using System;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 把 KK_VR_CameraSync 的「重新對齊工作室相機」叫出來用。
    ///
    /// 為什麼需要
    /// ----------
    /// CameraSync 是把工作室相機的**逐幀差值**套到 VR origin 上的
    /// （`ApplyCameraPoseDelta`），而且它的註解寫得很清楚：
    /// 「The current origin offset is retained, so physical head motion and
    ///  user locomotion remain relative to the animated camera.」
    /// 也就是說你用手柄移動產生的那個位移，它**刻意保留** —— 這樣你才能
    /// 在運鏡進行中自己走動。
    ///
    /// 代價是回不去：絕對定位（SnapHeadToTarget）只在 _initialAlignmentPending
    /// 為 true 的時候做，而那個旗標只有載入場景時會被設起來。所以一旦你走開，
    /// 偏移就永遠跟著你，除非重載整個場景。
    ///
    /// 它裡面本來就有 RequestInitialAlignment(string) 可以把旗標設回 true，
    /// 只是沒有對外的快捷鍵或設定。這支橋接就是去叫它。
    ///
    /// 為什麼用反射而不是加參照
    /// ------------------------
    /// 加參照的話，沒裝 CameraSync 的人連這支插件都載不起來 —— 為了一個
    /// 附加功能賠掉主功能不划算。全反射的話它沒裝就只是靜靜地不作用。
    ///
    /// **這是在戳別人插件的私有成員，本質上就是脆弱的。**
    /// 所以：任何一步失敗都只記在 LastReport，不丟例外；
    /// 找不到就退而求其次直接寫 _initialAlignmentPending 欄位；
    /// 兩條路都斷了就老實說「這個版本對不上」，而不是靜靜地什麼都沒做。
    /// </summary>
    public static class CameraSyncBridge
    {
        public static string LastReport = "";

        static Type driverType;
        static object driver;             // MonoBehaviour 或單例物件
        static MethodInfo requestMethod;  // RequestInitialAlignment
        static bool requestTakesReason;
        static FieldInfo pendingField;    // _initialAlignmentPending（備援）
        static float nextTry;             // 找不到的時候不要每幀重掃

        /// <summary>找出 CameraSync 的型別。掃全部組件很貴，只做一次。</summary>
        static Type FindType()
        {
            if (driverType != null) return driverType;
            try
            {
                // 走 ReflectUtil：ReflectionTypeLoadException 要**濾掉壞的型別**，
                // 不是整個組件跳過。跳過的話 VRGIN_KKCS 那種組件裡的東西永遠找不到
                // （那個坑害搖桿和介面全滅過一次，詳見 ReflectUtil 的說明）。
                driverType = ReflectUtil.Find("CameraSyncDriver");
                if (driverType != null) return driverType;
            }
            catch { }
            return null;
        }

        static bool Alive(object o)
        {
            if (o == null) return false;
            // MonoBehaviour 被銷毀之後，C# 參考還在但 Unity 的 == null 會是 true
            var uo = o as UnityEngine.Object;
            if (uo != null) return true;
            return !(o is UnityEngine.Object);
        }

        static object FindDriver()
        {
            if (Alive(driver)) return driver;
            driver = null;

            Type t = FindType();
            if (t == null) return null;

            try
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(t))
                {
                    UnityEngine.Object o = UnityEngine.Object.FindObjectOfType(t);
                    if (o != null) { driver = o; return driver; }
                }

                // 不是 MonoBehaviour 就找靜態單例
                string[] names = { "Instance", "instance", "_instance", "Current", "Driver" };
                foreach (string n in names)
                {
                    PropertyInfo p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p != null)
                    {
                        object v = p.GetValue(null, null);
                        if (Alive(v)) { driver = v; return driver; }
                    }
                    FieldInfo f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null)
                    {
                        object v = f.GetValue(null);
                        if (Alive(v)) { driver = v; return driver; }
                    }
                }
            }
            catch (Exception e)
            {
                LastReport = "找 CameraSyncDriver 失敗：" + e.Message;
            }
            return null;
        }

        static void BindMembers(Type t)
        {
            if (requestMethod != null || pendingField != null) return;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                foreach (MethodInfo m in t.GetMethods(any))
                {
                    if (m.Name != "RequestInitialAlignment") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0) { requestMethod = m; requestTakesReason = false; break; }
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    { requestMethod = m; requestTakesReason = true; break; }
                }
                pendingField = t.GetField("_initialAlignmentPending", any);
            }
            catch { }
        }

        /// <summary>這台機器上到底能不能用。給 UI 決定按鈕要不要變灰。</summary>
        public static bool Available()
        {
            if (Time.realtimeSinceStartup < nextTry && driver == null && driverType == null) return false;
            Type t = FindType();
            if (t == null) { nextTry = Time.realtimeSinceStartup + 2f; return false; }
            BindMembers(t);
            return requestMethod != null || pendingField != null;
        }

        /// <summary>
        /// 要求 CameraSync 重新絕對對齊到目前的工作室相機。
        /// 回傳有沒有真的送出去。
        /// </summary>
        public static bool Realign(string reason)
        {
            Type t = FindType();
            if (t == null)
            {
                LastReport = "沒有 KK_VR_CameraSync（沒裝，或還沒載入）";
                nextTry = Time.realtimeSinceStartup + 2f;
                return false;
            }
            BindMembers(t);

            object d = FindDriver();
            if (d == null) { LastReport = "找不到 CameraSyncDriver 的實體（場景還沒開？）"; return false; }

            try
            {
                if (requestMethod != null)
                {
                    requestMethod.Invoke(d, requestTakesReason ? new object[] { reason } : new object[0]);
                    LastReport = "已要求重新對齊（" + reason + "）";
                    return true;
                }
                if (pendingField != null)
                {
                    // 備援：方法改名了就直接把旗標推回去。效果一樣，
                    // 只是少了它自己那套 reason 記錄。
                    pendingField.SetValue(d, true);
                    LastReport = "已直接設定重新對齊旗標（" + reason + "）";
                    return true;
                }
                LastReport = "這個版本的 CameraSync 找不到 RequestInitialAlignment，也沒有那個旗標";
            }
            catch (Exception e)
            {
                LastReport = "重新對齊失敗：" + e.Message;
            }
            return false;
        }
    }
}
