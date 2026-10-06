using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 直接聽「工作室載入了哪張場景卡」。
    ///
    /// 為什麼改成這樣
    /// --------------
    /// 原本是 ScenePathProbe：去讀別的外掛寫在 BepInEx/config 下的 *.data 檔。
    /// 那個設計的初衷是「絕不碰遊戲程式碼」，但實測下來它根本問不到答案 ——
    /// StudioSceneNavigation 記的是**裸檔名**（2025_0227_2338_59_146.png）而不是完整路徑，
    /// 而且是一份歷史清單，最後一筆也不等於現在載入的那張。
    /// 結果就是 Detect() 永遠回 null，「同名優先」那條規則永遠沒機會執行，
    /// 一路掉到比對總長度 —— 於是兩張長度一樣的卡（例如 Charcard 和 Charcard_VR）就分不出來。
    ///
    /// 所以改成掛 Harmony。這確實違反了當初那條「不碰遊戲程式碼」的原則，但差別在於：
    /// 舊的 ScenePathProbe 第一版是**掃描所有組件的所有型別、讀每個像路徑的靜態成員**，
    /// 那等於對整個行程亂按按鈕；這裡是掛兩個名字明確、簽章明確的方法，
    /// 而且用 TypeByName 找型別（不參照 Assembly-CSharp），掛不上就退回舊路。
    ///
    /// 附帶好處：自動載入不用再每秒輪詢了。載入事件一來就處理一次，
    /// 沒載入的時候完全不做事。
    /// </summary>
    public static class SceneLoadWatch
    {
        /// <summary>最後一次載入的場景卡完整路徑。問不到就是 null。</summary>
        public static string LastPath;

        /// <summary>每載入一次 +1。外面比對這個數字就知道「換卡了」，不用自己輪詢。</summary>
        public static int Stamp;

        /// <summary>Harmony 有沒有掛成功。false 的話呼叫端要走舊的輪詢路。</summary>
        public static bool Active;

        public static string LastReport = "（還沒掛上）";

        static readonly List<string> patched = new List<string>();

        // ---------------------------------------------------------- 正在載入嗎

        static bool loadProbed;
        static bool flagsStatic;
        static PropertyInfo instProp;
        static FieldInfo instField;
        static PropertyInfo nowLoading, nowLoadingFade;

        static void Probe()
        {
            if (loadProbed) return;
            loadProbed = true;
            try
            {
                Type t = AccessTools.TypeByName("Manager.Scene");
                if (t == null) return;

                // 兩款遊戲的寫法不一樣：
                //   Koikatsu           Manager.Scene 繼承 Singleton<Scene>，旗標是「實例」屬性
                //   Koikatsu Sunshine  繼承 SingletonInitializer<Scene>，旗標是「靜態」屬性
                // 所以先不分靜態 / 實例把屬性找出來，再看它的 getter 是哪一種。
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Static | BindingFlags.Instance
                                         | BindingFlags.FlattenHierarchy;
                nowLoading = t.GetProperty("IsNowLoading", any);
                nowLoadingFade = t.GetProperty("IsNowLoadingFade", any);

                MethodInfo g = nowLoading == null ? null : nowLoading.GetGetMethod(true);
                flagsStatic = g != null && g.IsStatic;

                if (!flagsStatic)
                {
                    // Instance 在基底類別上，所以一定要帶 FlattenHierarchy，不然找不到。
                    const BindingFlags st = BindingFlags.Public | BindingFlags.NonPublic
                                            | BindingFlags.Static | BindingFlags.FlattenHierarchy;
                    instProp = t.GetProperty("Instance", st);
                    if (instProp == null) instField = t.GetField("Instance", st);
                }
            }
            catch { }
        }

        static object SceneInstance()
        {
            Probe();
            try
            {
                if (instProp != null) return instProp.GetValue(null, null);
                if (instField != null) return instField.GetValue(null);
            }
            catch { }
            return null;
        }

        static bool Flag(PropertyInfo p, object target)
        {
            if (p == null) return false;
            object v = p.GetValue(target, null);
            return v is bool && (bool)v;
        }

        /// <summary>
        /// 遊戲現在是不是正在載入場景。
        ///
        /// 這是遊戲自己的旗標（`Manager.Scene.IsNowLoading` / `IsNowLoadingFade`），
        /// 不是我們猜的。KK_VR_CameraSync 判斷「可以對齊相機了沒」用的也是這兩個 ——
        /// 它的 `IsSceneLoading()` 就是 `scene.IsNowLoading || scene.IsNowLoadingFade`。
        ///
        /// 有了它就不用靠輪詢猜時機：載入中一律不動作，載入結束（true → false 的那一刻）
        /// 才是真正該去比對設定檔的時間點。
        ///
        /// 問不到（遊戲版本不同、還沒初始化）就回 false —— 當成「沒在載入」，
        /// 呼叫端的其他保險還在，不會因此卡死。
        /// </summary>
        public static bool IsLoading
        {
            get
            {
                try
                {
                    Probe();
                    if (nowLoading == null) return false;
                    object target = null;
                    if (!flagsStatic)
                    {
                        target = SceneInstance();
                        if (target == null) return false;
                    }
                    return Flag(nowLoading, target) || Flag(nowLoadingFade, target);
                }
                catch { }
                return false;
            }
        }

        /// <summary>這台機器上問不問得到載入旗標。問不到就退回原本的做法。</summary>
        public static bool LoadFlagAvailable
        {
            get { Probe(); return nowLoading != null; }
        }

        public static void Apply()
        {
            if (Active) return;
            try
            {
                var h = new Harmony("reze.studio.cutscene.sceneload");
                MethodInfo post = AccessTools.Method(typeof(SceneLoadWatch), "Post");

                // 兩個都掛：一般「載入場景」走 Studio.LoadScene，
                // SceneInfo.Load 則是實際讀檔的那一層（匯入、讀取都會經過）。
                // 重複觸發沒關係 —— Stamp 多加一次只是多比對一次，結果一樣。
                Hook(h, post, "Studio.SceneInfo", "Load");
                Hook(h, post, "Studio.SceneInfo", "Import");
                Hook(h, post, "Studio.Studio", "LoadScene");
                // LoadScene 有可能只是「開一個協程」，真正吃到路徑的是協程本身。
                // 實測第一版只掛前兩個時，postfix 一次路徑都沒收到，所以把這個也掛上。
                Hook(h, post, "Studio.Studio", "LoadSceneCoroutine");
                Hook(h, post, "Studio.Studio", "ImportScene");

                Active = patched.Count > 0;
                LastReport = Active
                    ? "已掛上：" + string.Join("、", patched.ToArray())
                    : "找不到可掛的載入方法（遊戲版本不同？），改用舊的輪詢";
            }
            catch (Exception e)
            {
                Active = false;
                LastReport = "掛載失敗：" + e.Message + "，改用舊的輪詢";
            }
        }

        static void Hook(Harmony h, MethodInfo post, string typeName, string methodName)
        {
            try
            {
                // 用 TypeByName 而不是參照 Assembly-CSharp：
                // csproj 不必多一個參照，遊戲換版本也只是找不到而已，不會編譯不過。
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) return;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static
                                                      | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != methodName) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    // 只掛「第一個參數是字串」的多載 —— 那個字串就是路徑
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    try
                    {
                        h.Patch(m, null, new HarmonyMethod(post));
                        patched.Add(typeName + "." + methodName + "(" + ps.Length + ")");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[SceneLoadWatch] 掛 " + typeName + "." + methodName
                                         + " 失敗: " + e.Message);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Postfix。用 __args 而不是 __0：這樣不管是哪一個多載、參數名字叫什麼都收得到。
        ///
        /// 這裡**只記東西，不做任何事**。載入場景的當下遊戲正在忙，
        /// 在這裡去掃資料夾、讀 json 會直接卡住載入畫面；真正的比對留給 Update 那邊做。
        /// </summary>
        static int logged;

        static void Post(object[] __args)
        {
            try
            {
                // 前 12 次無論成不成功都記一行。
                // 上一版只在成功時記錄，結果「postfix 到底有沒有被呼叫」和
                // 「呼叫了但字串不合用」這兩種完全不同的失敗看起來一模一樣，
                // 只能靠猜。這幾行 log 就是用來把那兩者分開的。
                string raw = (__args != null && __args.Length > 0) ? (__args[0] as string) : null;
                string full = string.IsNullOrEmpty(raw) ? null : Resolve(raw);

                if (logged < 12)
                {
                    logged++;
                    Debug.Log("[SceneLoadWatch] 收到載入呼叫：參數數="
                              + (__args == null ? "null" : __args.Length.ToString())
                              + "　第一個字串=" + (raw == null ? "（不是字串或沒有）" : "\"" + raw + "\"")
                              + "　解析結果=" + (full == null ? "失敗" : full));
                }

                if (full == null) return;
                LastPath = full;
                Stamp++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SceneLoadWatch] postfix 例外：" + e.Message);
            }
        }

        /// <summary>
        /// 把拿到的字串變成真的存在的完整路徑。
        /// 有些呼叫點傳的是相對於 UserData/Studio/scene 的路徑，所以要補。
        /// </summary>
        static string Resolve(string p)
        {
            try
            {
                if (p.IndexOf(".png", StringComparison.OrdinalIgnoreCase) < 0) return null;
                if (File.Exists(p)) return Path.GetFullPath(p);

                string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
                string baseDir = Path.Combine(root,
                    Path.Combine("UserData", Path.Combine("Studio", "scene")));
                string cand = Path.Combine(baseDir, p);
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
            catch { }
            return null;
        }
    }
}
