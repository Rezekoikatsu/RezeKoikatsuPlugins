using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 讓「寫死寬度」的控制項在字比較長的語言裡自己變寬。
    ///
    /// 為什麼需要
    /// ----------
    /// 面板是照中文排的：按鈕、勾選框、標籤很多都給了固定寬度（GUILayout.Width(80)）。
    /// 換成英文或日文之後同一句話長很多，IMGUI 不會幫你加寬，只會把字切掉 ——
    /// 「Change clothes」變成「ange clot」，而且切掉的是中間以外的部分，根本猜不出原文。
    ///
    /// 做法
    /// ----
    /// 原本寫 GUILayout.Width(80) 的地方改成 Fit.WB(80, 文字)：
    /// 量一下這句話用這個樣式畫出來多寬，比 80 寬就用量到的，否則維持 80。
    /// 中文介面量出來都不超過原本的寬度，所以排版跟以前一樣。
    ///
    /// 量文字（CalcSize）不便宜，而且 OnGUI 一幀跑好幾遍，所以結果按「樣式 + 文字」快取。
    /// 三支插件各自編一份這個檔案（跟 Lang、VrSkin 一樣）。
    /// </summary>
    internal static class Fit
    {
        const int MAX_PER_STYLE = 600;

        static readonly Dictionary<GUIStyle, Dictionary<string, float>> cache =
            new Dictionary<GUIStyle, Dictionary<string, float>>();
        static readonly GUIContent tmp = new GUIContent();
        static int lastLang = -1;

        /// <summary>
        /// 視窗底寬的倍率。中文 1；其他語言的字比較長，整個視窗先放寬一點，
        /// 捲動清單裡的列才不會動不動就冒出水平捲軸。
        /// </summary>
        public static float Wide
        {
            get { return Lang.Current == Lang.ZH ? 1f : 1.25f; }
        }

        /// <summary>
        /// 語言換了沒（每個呼叫點各自記，所以用 ref）。換了就順便清快取。
        /// 用來把視窗寬度重設回「這個語言的底寬」—— 視窗只會被內容撐大、不會自己縮回來。
        /// </summary>
        public static bool LanguageChanged(ref int seen)
        {
            if (lastLang != Lang.Current)
            {
                lastLang = Lang.Current;
                cache.Clear();
            }
            if (seen == Lang.Current) return false;
            seen = Lang.Current;
            return true;
        }

        /// <summary>這句話用這個樣式畫出來多寬（含樣式自己的內距）。只能在 OnGUI 裡呼叫。</summary>
        public static float TextWidth(string text, GUIStyle st)
        {
            if (string.IsNullOrEmpty(text) || st == null) return 0f;
            Dictionary<string, float> d;
            if (!cache.TryGetValue(st, out d))
            {
                d = new Dictionary<string, float>();
                cache[st] = d;
            }
            float w;
            if (d.TryGetValue(text, out w)) return w;
            tmp.text = text;
            // CalcSize 不考慮自動換行，回傳的是「一行排完」的寬度，正是這裡要的。
            w = Mathf.Ceil(st.CalcSize(tmp).x) + 2f;
            // 帶數字的句子（「共 3 段」）每個數字都是一筆；設個上限，滿了就整批丟掉重來。
            if (d.Count >= MAX_PER_STYLE) d.Clear();
            d[text] = w;
            return w;
        }

        /// <summary>至少 w，文字放不下就加寬。</summary>
        public static GUILayoutOption W(float w, string text, GUIStyle st)
        {
            float need = w;
            try
            {
                float t = TextWidth(text, st);
                if (t > need) need = t;
            }
            catch { }
            // 上限：萬一丟進來的是一整段話或檔案路徑，不要讓它把整列撐爆。
            float cap = w * 4f + 60f;
            if (need > cap) need = cap;
            return GUILayout.Width(need);
        }

        public static GUILayoutOption WB(float w, string text) { return W(w, text, GUI.skin.button); }
        public static GUILayoutOption WL(float w, string text) { return W(w, text, GUI.skin.label); }
        public static GUILayoutOption WT(float w, string text) { return W(w, text, GUI.skin.toggle); }
        public static GUILayoutOption WX(float w, string text) { return W(w, text, GUI.skin.box); }

        // ------------------------------------------------------------ 介面縮放
        //
        // 整個面板（連文字）放大縮小。做法是畫視窗之前把 GUI.matrix 乘上倍率 ——
        // IMGUI 的視窗會記住建立當下的矩陣，滑鼠座標也會照著換算，所以拖曳、點擊、
        // 捲動清單都不用另外處理。倍率是 1 的時候完全不碰矩陣，跟沒有這個功能一樣。
        //
        // 三支插件**各自**設定、互不影響（每支面板的大小和內容量差很多，
        // 共用一個倍率反而每支都不合適）。這個類別三支各編一份，所以 Scale 本來就是各自的。

        public const float SCALE_MIN = 0.6f, SCALE_MAX = 2f;

        /// <summary>這支插件目前的倍率。</summary>
        public static float Scale = 1f;

        static float Snap(float s)
        {
            if (float.IsNaN(s) || s <= 0f) s = 1f;
            s = Mathf.Clamp(s, SCALE_MIN, SCALE_MAX);
            return Mathf.Round(s * 20f) / 20f;            // 0.05 一格
        }

        public static void SetScale(float s)
        {
            Scale = Snap(s);
        }

        /// <summary>畫視窗之前呼叫，回傳原本的矩陣；畫完交給 EndScale 還原。</summary>
        public static Matrix4x4 BeginScale()
        {
            Matrix4x4 old = GUI.matrix;
            if (Mathf.Abs(Scale - 1f) > 0.001f)
                GUI.matrix = old * Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
            return old;
        }

        public static void EndScale(Matrix4x4 old)
        {
            GUI.matrix = old;
        }

        /// <summary>縮放之後，視窗座標系裡的螢幕寬高（要把視窗置中、貼邊時用這個，不要用 Screen.width）。</summary>
        public static float ScreenW { get { return Screen.width / Scale; } }
        public static float ScreenH { get { return Screen.height / Scale; } }

        /// <summary>
        /// 設置裡的那一列：「介面縮放 100%  [－] [＋] [100%]」。
        /// 有改的話回傳 true，呼叫端要把 Fit.Scale 寫回自己的設定檔。
        /// </summary>
        public static bool ScaleRow()
        {
            float want = Scale;
            GUILayout.BeginHorizontal();
            string label = Lang.T("介面縮放") + " " + Mathf.RoundToInt(Scale * 100f) + "%";
            GUILayout.Label(label, WL(110f, label));
            if (GUILayout.Button("-", GUILayout.Width(30f), GUILayout.Height(22f))) want = Scale - 0.1f;
            if (GUILayout.Button("+", GUILayout.Width(30f), GUILayout.Height(22f))) want = Scale + 0.1f;
            if (GUILayout.Button("100%", GUILayout.Width(52f), GUILayout.Height(22f))) want = 1f;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            want = Snap(want);
            if (Mathf.Abs(want - Scale) < 0.001f) return false;
            SetScale(want);
            return true;
        }

        // ------------------------------------------------------------ 自動換列
        //
        // 一列放很多顆「不指定寬度」的按鈕時，IMGUI 不會自己換列：
        // 放不下就整列超出視窗（在捲動清單裡就是冒出水平捲軸）。
        // 這裡自己記目前這一列用掉多寬，下一顆放不下就先換列。
        //
        // Layout 和 Repaint 兩遍量到的寬度一樣，所以兩遍換列的位置也一樣，
        // 不會有控制項數量對不上的問題。

        static float flowAvail, flowX, flowIndent;

        public static void FlowBegin(float avail, float indent)
        {
            flowAvail = avail;
            flowIndent = indent;
            flowX = indent;
            GUILayout.BeginHorizontal();
            if (indent > 0f) GUILayout.Space(indent);
        }

        /// <summary>畫下一顆之前呼叫：告訴它這顆的文字和樣式，放不下就換列。</summary>
        public static void FlowItem(string text, GUIStyle st, float minWidth)
        {
            float w = minWidth;
            try
            {
                float t = TextWidth(text, st);
                if (t > w) w = t;
            }
            catch { }
            w += 6f;                               // 控制項之間的間距
            if (flowX > flowIndent + 0.5f && flowX + w > flowAvail)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (flowIndent > 0f) GUILayout.Space(flowIndent);
                flowX = flowIndent;
            }
            flowX += w;
        }

        public static void FlowEnd()
        {
            GUILayout.EndHorizontal();
        }
    }
    /// <summary>
    /// 畫自己的面板時，請 XUnity.AutoTranslator 先不要動手。
    ///
    /// 為什麼需要
    /// ----------
    /// AutoTranslator 會攔 IMGUI 的文字，拿去查它自己的翻譯檔。玩家如果裝了
    /// 「英文 → 簡體中文」的翻譯包（HF Patch 的中文化就是），這三支面板切到英文之後，
    /// 剛好在它翻譯檔裡的短句（Show / Hide / Close / Settings / Accessories…）
    /// 會被它再翻回簡體中文 —— 畫面上就變成英文夾著簡體字，看起來像「沒翻完」。
    /// 面板的語言由面板自己管，不需要它幫忙。
    ///
    /// AutoTranslator 自己有一份「這些外掛的 IMGUI 不要翻」的名單（BlacklistedIMGUIPlugins），
    /// 它的做法就是在視窗函式前後呼叫 DisableAutoTranslator / EnableAutoTranslator。
    /// 這裡直接做同一件事，玩家就不用自己去改它的設定檔。
    ///
    /// 型別和方法名稱是用實際安裝的 XUnity.AutoTranslator.Plugin.Core.dll（5.4.3 和 5.5.0）
    /// 翻出來確認的。沒裝、或哪天改了名字，這裡就什麼都不做。
    /// </summary>
    internal static class Xua
    {
        static bool probed;
        static FieldInfo fCurrent, fDisabled;
        static MethodInfo mOff, mOn;
        static object plugin;
        static readonly Dictionary<GUI.WindowFunction, GUI.WindowFunction> wrapped =
            new Dictionary<GUI.WindowFunction, GUI.WindowFunction>();

        static void Probe()
        {
            if (probed) return;
            probed = true;
            try
            {
                Type t = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "XUnity.AutoTranslator.Plugin.Core") continue;
                    t = a.GetType("XUnity.AutoTranslator.Plugin.Core.AutoTranslationPlugin", false);
                    break;
                }
                if (t == null) return;
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                fCurrent = t.GetField("Current", S);
                fDisabled = t.GetField("_temporarilyDisabled", I);
                mOff = t.GetMethod("DisableAutoTranslator", I, null, Type.EmptyTypes, null);
                mOn = t.GetMethod("EnableAutoTranslator", I, null, Type.EmptyTypes, null);
                if (fCurrent == null || mOff == null || mOn == null) { fCurrent = null; }
            }
            catch { fCurrent = null; }
        }

        /// <summary>
        /// 暫停翻譯。回傳 true 表示「是我關的」，要交給 End 開回去。
        /// 本來就被別人關著的話不動它（回傳 false），免得幫別人提早開回來。
        /// </summary>
        public static bool Begin()
        {
            try
            {
                Probe();
                if (fCurrent == null) return false;
                if (plugin == null) plugin = fCurrent.GetValue(null);
                if (plugin == null) return false;
                if (fDisabled != null && (bool)fDisabled.GetValue(plugin)) return false;
                mOff.Invoke(plugin, null);
                return true;
            }
            catch { return false; }
        }

        public static void End(bool mine)
        {
            if (!mine) return;
            try { mOn.Invoke(plugin, null); } catch { }
        }

        /// <summary>
        /// 把視窗函式包一層。視窗的內容不是在 GUILayout.Window 那一行畫的，
        /// 而是 OnGUI 結束之後才被叫回來，所以光在 OnGUI 裡前後包住不夠。
        /// 同一個函式只包一次（記在表裡），不會每幀配新的委派。
        /// </summary>
        public static GUI.WindowFunction Wrap(GUI.WindowFunction f)
        {
            GUI.WindowFunction w;
            if (wrapped.TryGetValue(f, out w)) return w;
            w = delegate(int id)
            {
                bool mine = Begin();
                try { f(id); }
                finally { End(mine); }
            };
            wrapped[f] = w;
            return w;
        }
    }
}
