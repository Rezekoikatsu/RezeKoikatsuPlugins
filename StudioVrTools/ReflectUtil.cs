using System;
using System.Collections.Generic;
using System.Reflection;

namespace StudioVrTools
{
    /// <summary>
    /// 跨組件找型別。看起來只是小工具，但這裡有一個**讓整包 VR 功能全滅**的坑。
    ///
    /// 坑是什麼
    /// --------
    /// 原本每個檔案都自己寫一份這樣的迴圈：
    ///
    ///     foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
    ///     {
    ///         Type[] ts;
    ///         try { ts = asm.GetTypes(); }
    ///         catch { continue; }          // ← 這一行是災難
    ///         ...
    ///     }
    ///
    /// `Assembly.GetTypes()` 只要**有任何一個型別載不起來**就整個丟
    /// ReflectionTypeLoadException —— 不是回傳「載得起來的那些」，是整批不給。
    /// 而 VRGIN_KKCS.dll 正好就是這種組件（它參照了 Leap Motion 之類的東西，
    /// 這台機器上沒有），實測 log 裡 HarmonyX 自己也在抱怨同一件事：
    ///
    ///     [Warning: HarmonyX] AccessTools.GetTypesFromAssembly: assembly VRGIN_KKCS
    ///       => System.Reflection.ReflectionTypeLoadException:
    ///          The classes in the module cannot be loaded.
    ///
    /// 於是 `catch { continue; }` 把**整個 VRGIN_KKCS 跳過**，
    /// SteamVR_Controller、VRGIN.Core.VR、GUIQuadRegistry 一個都找不到。
    /// 表現出來就是：搖桿沒反應、Y 鍵沒反應、介面黑底沒出現、
    /// 而且每一項都「靜悄悄地不作用」，因為所有失敗都被 try/catch 吃掉了。
    /// 三個功能同時壞，其實只有這一個原因。
    ///
    /// （過場影片那條路不受影響，因為它是拿場上物件的元件名稱去比對，
    ///   沒有走 GetTypes —— 這也解釋了為什麼偏偏只有影片是好的。）
    ///
    /// 正確做法
    /// --------
    /// ReflectionTypeLoadException 身上有 `Types`：載得起來的是實體，
    /// 載不起來的是 null。把 null 濾掉就好，剩下的完全可用。
    /// </summary>
    public static class ReflectUtil
    {
        /// <summary>
        /// 拿一個組件裡「載得起來」的型別。
        /// 部分失敗不當成整個失敗 —— 這正是 VRGIN_KKCS 的情況。
        /// </summary>
        public static Type[] Types(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                Type[] partial = e.Types;
                if (partial == null) return new Type[0];

                var ok = new List<Type>(partial.Length);
                foreach (Type t in partial)
                    if (t != null) ok.Add(t);
                return ok.ToArray();
            }
            catch
            {
                return new Type[0];
            }
        }

        /// <summary>
        /// 用完整名稱（或單純的型別名）找型別。找不到回 null。
        ///
        /// 每次都掃全部組件很貴，所以**找到**的會快取起來。
        /// 找不到的不快取：VR 的組件是遊戲啟動之後才載入的，
        /// 太早問一次就把 null 記一輩子的話，後來載進來也永遠找不到。
        /// </summary>
        public static Type Find(string name)
        {
            Type cached;
            if (found.TryGetValue(name, out cached)) return cached;

            Assembly[] all;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return null; }

            foreach (Assembly asm in all)
            {
                foreach (Type t in Types(asm))
                {
                    if (t == null) continue;
                    if (t.FullName == name || t.Name == name)
                    {
                        found[name] = t;
                        return t;
                    }
                }
            }

            // 沒找到就**不要快取**。VR 的組件是遊戲啟動之後才載入的，
            // 太早問一次就把 null 記一輩子的話，後來載進來也永遠找不到 ——
            // 這種失敗完全沒有徵兆，是最難查的那一種。
            return null;
        }

        static readonly Dictionary<string, Type> found = new Dictionary<string, Type>();

    }
}
