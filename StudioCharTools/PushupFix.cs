using System;
using System.Reflection;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 完美換人之後「一改穿脫狀態，胸部就跳回新卡的形狀」的修復。
    ///
    /// 【問題本體】（對著 KK_Pushup.dll 反編譯確認）
    ///
    /// KK_Pushup 的 PushupController 身上有三份資料：
    ///     BaseData          這個角色「沒有胸托時」的原始胸部 12 個數值
    ///     LoadedBaseData    讀卡時的那一份（離開捏人/ 還原用）
    ///     CurrentPushupData 算完胸托之後、實際寫進 chaFile 的那一份
    /// 加上每套換裝各一份的 BraDataDictionary / TopDataDictionary（胸托參數）。
    ///
    /// 只要穿脫狀態一變，遊戲的 SetClothesState 會被 Hooks.SetClothesStatePostfix
    /// 攔到，走 ClothesStateChangeEvent() → RecalculateBody(false, false)，
    /// 拿 BaseData + 當前換裝的胸托參數重算，再 MapBodyInfoToChaFile 寫回
    /// chaFile 的胸部滑桿。也就是說 —— **BaseData 才是胸型的真正來源**，
    /// chaFile 上的胸部滑桿隨時會被它覆蓋掉。
    ///
    /// 完美換人的流程是：ChangeChara 讀新卡 → PushupController.OnReload 把
    /// BaseData 換成新卡的 → 我們再把舊角色的 shapeValueBody 一百格寫回去。
    /// 畫面第一眼是對的（我們寫的是最後一手），但 BaseData 還是新卡的，
    /// 所以下一次穿脫（半脫、全脫、穿回去都算）一重算，胸部就跳回新卡的形狀。
    /// 場景角色本身沒有全脫狀態也一樣中 —— 觸發點是「狀態改變」，不是某個特定狀態。
    ///
    /// 【修法】換人前把舊角色的 BaseData 複製一份存起來，還原身材之後寫回
    /// 新的 PushupController，再主動呼叫一次 RecalculateBody(false, false)。
    /// 這樣胸型的來源就是舊角色的原始胸部，而胸托參數仍然跟著新卡的服裝走
    /// （新卡穿什麼內衣、托多少，是新卡的事）。
    ///
    /// 特別注意不能拿 chaFile 上的胸部滑桿當 BaseData —— 如果舊角色當下穿著
    /// 有胸托的內衣，那個值已經是「托過」的，拿去當基準會被托第二次。
    /// 所以一定是複製 BaseData 本身。
    ///
    /// 只鎖身高模式不做這件事：那個模式本來就是要新卡自己的身材。
    ///
    /// 全程反射，沒裝 KK_Pushup 就整段跳過。
    /// </summary>
    internal static class PushupFix
    {
        private static bool inited;
        private static bool available;
        private static string initError = "";

        private static Type tPushup;            // KK_Plugins.Pushup
        private static Type tController;        // Pushup+PushupController
        private static Type tBodyData;          // Pushup+BodyData
        private static MethodInfo mGetCharaController;   // static PushupController GetCharaController(ChaControl)
        private static FieldInfo fBaseData;
        private static FieldInfo fLoadedBaseData;
        private static MethodInfo mCopyTo;               // BodyData.CopyTo(BodyData)
        private static MethodInfo mRecalculateBody;      // RecalculateBody(bool, bool)

        internal static string LastReport = "";

        internal static bool Available { get { Init(); return available; } }
        internal static string Unavailable { get { Init(); return initError; } }

        private static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type t = a.GetType("KK_Plugins.Pushup");
                        if (t != null) { tPushup = t; break; }
                    }
                    catch { }
                }
                if (tPushup == null) { initError = "沒有裝 Pushup 外掛，胸托修復跳過"; return; }

                BindingFlags nf = BindingFlags.Public | BindingFlags.NonPublic;
                tController = tPushup.GetNestedType("PushupController", nf);
                tBodyData = tPushup.GetNestedType("BodyData", nf);
                if (tController == null || tBodyData == null)
                {
                    initError = "Pushup 外掛內部型別與預期不符（版本不同？）";
                    return;
                }

                mGetCharaController = tPushup.GetMethod("GetCharaController",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                fBaseData = tController.GetField("BaseData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                fLoadedBaseData = tController.GetField("LoadedBaseData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                mCopyTo = tBodyData.GetMethod("CopyTo", new Type[] { tBodyData });

                foreach (MethodInfo m in tController.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "RecalculateBody") continue;
                    if (m.GetParameters().Length != 2) continue;
                    mRecalculateBody = m;
                    break;
                }

                if (mGetCharaController == null || fBaseData == null || mCopyTo == null)
                {
                    initError = "Pushup 外掛缺少 GetCharaController / BaseData / CopyTo";
                    return;
                }
                available = true;
            }
            catch (Exception e)
            {
                initError = "胸托修復初始化失敗: " + e.Message;
                available = false;
            }
        }

        private static object Controller(ChaControl cha)
        {
            if (cha == null) return null;
            try { return mGetCharaController.Invoke(null, new object[] { cha }); }
            catch { return null; }
        }

        /// <summary>
        /// 換人前呼叫：把這個角色目前的 BaseData（沒有胸托的原始胸型）複製一份。
        /// 回傳 null 代表沒東西可存（沒裝插件、或這個角色還沒有 controller）。
        /// </summary>
        internal static object Capture(Studio.OCIChar oci)
        {
            Init();
            if (!available) { LastReport = initError; return null; }
            try
            {
                ChaControl cha = oci != null ? oci.charInfo : null;
                object ctrl = Controller(cha);
                if (ctrl == null) { LastReport = "這個角色沒有 PushupController"; return null; }

                object live = fBaseData.GetValue(ctrl);
                if (live == null) { LastReport = "BaseData 是空的"; return null; }

                // 一定要複製。OnReload 會對同一個物件 CopyTo，留參照等於沒存。
                object clone = Activator.CreateInstance(tBodyData);
                mCopyTo.Invoke(live, new object[] { clone });
                LastReport = "已記下原始胸型";
                return clone;
            }
            catch (Exception e)
            {
                LastReport = "記錄胸型失敗: " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// 換完人、身材還原完之後呼叫：把舊角色的原始胸型寫回新的 PushupController，
        /// 並立刻重算一次，讓畫面與 BaseData 一致。
        /// </summary>
        internal static bool Apply(Studio.OCIChar oci, object savedBaseData)
        {
            Init();
            if (!available || savedBaseData == null)
            {
                LastReport = available ? "沒有胸型記錄" : initError;
                return false;
            }
            try
            {
                ChaControl cha = oci != null ? oci.charInfo : null;
                object ctrl = Controller(cha);
                if (ctrl == null) { LastReport = "換完人後找不到 PushupController"; return false; }

                if (!WriteInto(ctrl, fBaseData, savedBaseData)) return false;
                WriteInto(ctrl, fLoadedBaseData, savedBaseData);   // 失敗不算致命

                // RecalculateBody(false, false) 就是穿脫狀態改變時走的那一條
                // （Hooks.SetClothesStatePostfix → ClothesStateChangeEvent）。
                if (mRecalculateBody != null)
                {
                    try { mRecalculateBody.Invoke(ctrl, new object[] { false, false }); }
                    catch (Exception e)
                    {
                        LastReport = "胸型已寫回，但重算失敗（改一次穿脫狀態就會正常）: " + e.Message;
                        return true;
                    }
                }
                LastReport = "原始胸型已寫回並重算";
                return true;
            }
            catch (Exception e)
            {
                LastReport = "寫回胸型失敗: " + e.Message;
                return false;
            }
        }

        private static bool WriteInto(object ctrl, FieldInfo field, object savedBaseData)
        {
            if (field == null) return false;
            try
            {
                object live = field.GetValue(ctrl);
                if (live == null)
                {
                    object fresh = Activator.CreateInstance(tBodyData);
                    mCopyTo.Invoke(savedBaseData, new object[] { fresh });
                    field.SetValue(ctrl, fresh);
                    return true;
                }
                mCopyTo.Invoke(savedBaseData, new object[] { live });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PushupFix] 寫入 " + field.Name + " 失敗: " + e.Message);
                return false;
            }
        }
    }
}
