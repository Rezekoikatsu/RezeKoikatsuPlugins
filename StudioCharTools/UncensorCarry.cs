using System;
using System.Reflection;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 換人時把「場景原角色」在 UncensorSelector（身形選擇）選的三項帶到新角色：
    /// 身體（Body）、第二項（Penis）、第三項（Balls），連同兩個顯示開關。
    ///
    /// 場景卡的動作、碰撞、BetterPenetration 這些是配合原角色選的身形調的，
    /// 換上的人物卡自己存的身形不一樣時，位置和外觀就對不上。
    ///
    /// 做法：換人前記下原角色控制器上的 GUID 與開關；新角色讀完自己的卡之後寫回去，
    /// 再呼叫 UpdateUncensor() 重建。只用反射，沒裝 UncensorSelector 時整個功能安靜地不做事。
    /// </summary>
    public static class UncensorCarry
    {
        public static string LastReport = "";

        public class Snapshot
        {
            public string Body, Penis, Balls;
            public bool DisplayPenis, DisplayBalls;
            public bool Valid;
        }

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static Type ctrlType;
        static bool looked;

        static Type CtrlType
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    ctrlType = HarmonyLib.AccessTools.TypeByName("KK_Plugins.UncensorSelector+UncensorSelectorController");
                }
                return ctrlType;
            }
        }

        public static bool Available { get { return CtrlType != null; } }

        static object Ctrl(ChaControl cha)
        {
            if (cha == null || CtrlType == null) return null;
            return cha.GetComponent(CtrlType);
        }

        static object Get(object o, string prop)
        {
            PropertyInfo p = o.GetType().GetProperty(prop, Any);
            return p == null ? null : p.GetValue(o, null);
        }

        static bool Set(object o, string prop, object v)
        {
            PropertyInfo p = o.GetType().GetProperty(prop, Any);
            if (p == null) return false;
            MethodInfo s = p.GetSetMethod(true);
            if (s != null) { s.Invoke(o, new[] { v }); return true; }
            FieldInfo f = o.GetType().GetField("<" + prop + ">k__BackingField", Any);
            if (f == null) return false;
            f.SetValue(o, v);
            return true;
        }

        public static Snapshot Capture(ChaControl cha)
        {
            var s = new Snapshot();
            LastReport = "";
            try
            {
                object c = Ctrl(cha);
                if (c == null) { LastReport = "找不到 UncensorSelector 控制器"; return s; }
                s.Body = Get(c, "BodyGUID") as string;
                s.Penis = Get(c, "PenisGUID") as string;
                s.Balls = Get(c, "BallsGUID") as string;
                object dp = Get(c, "DisplayPenis"), db = Get(c, "DisplayBalls");
                s.DisplayPenis = dp is bool && (bool)dp;
                s.DisplayBalls = db is bool && (bool)db;
                s.Valid = true;
                LastReport = Describe(s);
            }
            catch (Exception e) { LastReport = "記錄失敗：" + e.Message; }
            return s;
        }

        /// <summary>寫回新角色並重建。回傳是否真的有改到（新角色本來就一樣時回傳 false）。</summary>
        public static bool Apply(ChaControl cha, Snapshot s)
        {
            LastReport = "";
            if (s == null || !s.Valid) return false;
            try
            {
                object c = Ctrl(cha);
                if (c == null) { LastReport = "新角色找不到 UncensorSelector 控制器"; return false; }
                bool same = Get(c, "BodyGUID") as string == s.Body
                            && Get(c, "PenisGUID") as string == s.Penis
                            && Get(c, "BallsGUID") as string == s.Balls
                            && Equals(Get(c, "DisplayPenis"), s.DisplayPenis)
                            && Equals(Get(c, "DisplayBalls"), s.DisplayBalls);
                if (same) { LastReport = "新角色本來就一樣：" + Describe(s); return false; }
                Set(c, "BodyGUID", s.Body);
                Set(c, "PenisGUID", s.Penis);
                Set(c, "BallsGUID", s.Balls);
                Set(c, "DisplayPenis", s.DisplayPenis);
                Set(c, "DisplayBalls", s.DisplayBalls);
                MethodInfo up = c.GetType().GetMethod("UpdateUncensor", Any, null, Type.EmptyTypes, null);
                if (up != null) up.Invoke(c, null);
                LastReport = "已套用：" + Describe(s);
                return true;
            }
            catch (Exception e)
            {
                LastReport = "套用失敗：" + (e.InnerException ?? e).Message;
                return false;
            }
        }

        static string Describe(Snapshot s)
        {
            return "Body=" + (string.IsNullOrEmpty(s.Body) ? "（預設）" : s.Body)
                   + "、2=" + (string.IsNullOrEmpty(s.Penis) ? "（預設）" : s.Penis)
                   + "（" + (s.DisplayPenis ? "顯示" : "隱藏") + "）"
                   + "、3=" + (string.IsNullOrEmpty(s.Balls) ? "（預設）" : s.Balls)
                   + "（" + (s.DisplayBalls ? "顯示" : "隱藏") + "）";
        }
    }

    /// <summary>
    /// 換人後讓 BetterPenetration 重新初始化。
    ///
    /// 換人時新角色換了網格與骨頭，但 BP 的控制器還抓著換人前建立的對位點與碰撞器，
    /// 要重新開卡才會重建 —— 存下來的設定完全一樣，重開就正常。
    /// 做法：對場景裡每個掛著 BetterPenetrationController 的角色呼叫一次 OnReload(Studio, false)，
    /// 等於讓它照卡上的設定重新建一次（雙方都要，因為對位是兩個角色之間算的）。
    /// 只用反射，沒裝 BP 時什麼都不做。
    /// </summary>
    public static class BpRefresh
    {
        public static string LastReport = "";

        static Type ctrlType;
        static MethodInfo reinit;
        static bool looked;

        /// <summary>
        /// 型別一定要從「有載入的那個 Studio 版 BP 外掛」的組件裡拿。
        /// 主遊戲版 KK_BetterPenetration.dll 在工作室會被略過，但組件本身還是載進來了，
        /// 裡面有同名的 Core_BetterPenetration.BetterPenetrationController ——
        /// 用 TypeByName 會拿到那個沒在用的，GetComponent 就一個都找不到（1.1.2 的 0 個角色就是這樣）。
        /// </summary>
        static void Look()
        {
            if (looked) return;
            looked = true;
            foreach (var pi in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
            {
                if (pi == null || pi.Instance == null) continue;
                Type pt = pi.Instance.GetType();
                if (!pt.Name.EndsWith("Studio_BetterPenetration")) continue;
                ctrlType = pt.Assembly.GetType("Core_BetterPenetration.BetterPenetrationController");
                reinit = pt.GetMethod("ReinitializeControllers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (ctrlType != null) return;
            }
        }

        public static bool Available { get { Look(); return ctrlType != null; } }

        /// <summary>沿著繼承鏈找指定名稱、指定參數個數的方法（避開多載造成的 AmbiguousMatchException）。</summary>
        static MethodInfo FindMethod(string name, int argc)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            for (Type t = ctrlType; t != null; t = t.BaseType)
                foreach (MethodInfo m in t.GetMethods(f))
                    if (m.Name == name && m.GetParameters().Length == argc) return m;
            return null;
        }

        /// <summary>
        /// 先讓每個控制器照卡上的設定重讀（OnReload），再呼叫 BP 外掛自己的 ReinitializeControllers
        /// （重建對位點並重新掛上 NodesConstraints 的約束）。回傳處理了幾個角色。
        /// </summary>
        public static int RefreshAll()
        {
            LastReport = "";
            Look();
            if (ctrlType == null) { LastReport = "找不到 Studio 版 BP"; return 0; }
            int n = 0, fail = 0;
            var sb = new System.Text.StringBuilder();
            try
            {
                // OnReload 在 KKAPI 的基底類別有兩個多載（1 個、2 個參數），GetMethod(名稱) 會丟
                // 「Ambiguous matching」—— 1.1.2 第二版就是死在這裡。指定兩個參數的那個。
                MethodInfo reload = FindMethod("OnReload", 2);
                MethodInfo initDan = FindMethod("InitializeDanAgent", 0);
                object studioMode = reload == null ? null
                    : Enum.ToObject(reload.GetParameters()[0].ParameterType, (int)KKAPI.GameMode.Studio);
                // 跟 BP 自己的 ReinitializeControllers 一樣用 FindObjectsOfType 找，
                // 不靠 charInfo.GetComponent（1.1.2 用那個一個都沒找到）。
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(ctrlType);
                sb.Append("（型別來自 ").Append(ctrlType.Assembly.GetName().Name)
                  .Append("，找到 ").Append(all == null ? 0 : all.Length).Append(" 個控制器）");
                if (all != null)
                    foreach (UnityEngine.Object o in all)
                    {
                        var c = o as Component;
                        if (c == null) continue;
                        try
                        {
                            if (reload != null) reload.Invoke(c, new[] { studioMode, (object)false });
                            if (initDan != null) initDan.Invoke(c, null);
                            n++;
                            sb.Append("\n  ✓ ").Append(c.gameObject.name);
                        }
                        catch (Exception e) { fail++; sb.Append("\n  ✗ ").Append(c.gameObject.name).Append("：").Append((e.InnerException ?? e).Message); }
                    }
                if (reinit != null)
                {
                    reinit.Invoke(null, null);
                    sb.Append("\n  已呼叫 BP 的 ReinitializeControllers（重新掛 NodesConstraints 約束）");
                }
            }
            catch (Exception e) { sb.Append("\n  ").Append((e.InnerException ?? e).Message); }
            LastReport = "重新初始化 " + n + " 個角色" + (fail > 0 ? "，失敗 " + fail + " 個" : "") + sb;
            return n;
        }
    }
}
