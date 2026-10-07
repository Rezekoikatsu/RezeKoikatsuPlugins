using System.Collections.Generic;
using UnityEngine;

namespace StudioVrTools
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
}
