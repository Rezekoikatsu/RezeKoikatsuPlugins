using System;
using UnityEngine;

/// <summary>
/// 把自己這幾支外掛的 IMGUI 視窗換成**不透明**的外觀，讓它在 VR 裡讀得清楚。
///
/// 為什麼是這個做法（有根據，不是又一次試）
/// ----------------------------------------
/// 你指出 VideoExport 在 VR 裡很清楚。翻了它的原始碼（HSPlugins/VideoExport.Core/
/// Styles.cs）之後才發現關鍵：**它也是 IMGUI，不是 uGUI。**
/// 它做的事只有一件 —— 用 `Object.Instantiate(GUI.skin)` 複製一份 skin，
/// 然後把 window / button / toggle / textField 的 `normal.background`
/// 全部換成它自己的**不透明**圖，再指定文字顏色。
///
/// 這正好解釋了前面幾輪為什麼一直白費力氣：
/// Unity 內建 skin 的視窗底圖 alpha 不是 1，VRGIN 把整個桌面畫進一張
/// RenderTexture 再貼到板子上，於是視窗底色跟板子後面的東西混在一起。
/// 在板子**後面**補底板改變不了這件事 —— 合成是 src×a + dst×(1−a)，
/// 文字和底圖的 alpha 接近時，對比只剩 a×|文字−底圖|，跟 dst 無關。
/// 唯一有效的是把 a 變成 1，而那要在**來源**做：換掉底圖本身。
///
/// 所以這裡不再碰板子、不碰字級（你說不要動字體，就不動），只做一件事：
/// 給視窗、按鈕、輸入框一張**純色不透明**的底，並把文字顏色拉開。
///
/// 為什麼用程式生成 1×1 純色而不是像 VideoExport 那樣內嵌圖片：
/// 一張 1×1 的圖拉伸之後就是純色塊，效果一樣，而且不必在 dll 裡塞資源、
/// 不必處理 ImageConversion 在不同遊戲版本上的差異。
///
/// GUI.skin 是**全域**的，所以 Begin 一定要配一個 End —— 不還原的話
/// 別人的外掛也會被我們換皮，那是把自己的問題丟給別人。
/// </summary>
public static class VrSkin
{
    /// <summary>要不要換皮。</summary>
    public static bool Enabled = true;

    /// <summary>只在 VR 裡換。桌面上本來就看得清楚，換了反而跟別的外掛不一致。</summary>
    public static bool OnlyInVr = true;

    // ------------------------------------------------ 三支插件共用同一份設定

    /// <summary>
    /// 設定的唯一來源是 F9。F6 / F7 跟著走，自己不再各留一份。
    ///
    /// 為什麼用 AppDomain 的資料槽，不用反射去讀對方的欄位
    /// ------------------------------------------------
    /// 這個 VrSkin 類別三個專案各編一份（刻意的：三支插件要能各自單獨存在，
    /// 加組件參照的話缺一邊就 TypeLoadException，三支一起死）。
    /// 也就是說執行時有**三個同名型別**。用 ReflectUtil 去找「VrSkin」
    /// 會拿到先掃到的那一個 —— 可能是自己，那就等於什麼都沒做，而且完全沒有徵兆。
    ///
    /// AppDomain.SetData / GetData 是整個行程共用的一張表，用字串當鍵，
    /// 不牽涉型別識別，也不需要任何參照。F9 沒裝的時候 GetData 回 null，
    /// F6 / F7 就退回自己的預設值 —— 單獨安裝照樣能用。
    ///
    /// 值故意存成 "1" / "0" 字串而不是 bool：跨組件傳 boxed 值型別沒問題，
    /// 但字串連「哪個組件定義的型別」這個問題都不存在，省得以後踩到。
    /// </summary>
    const string KeyEnabled = "reze.studio.vrskin.enabled";
    const string KeyOnlyVr = "reze.studio.vrskin.onlyvr";

    /// <summary>設定是自己的還是 F9 給的，面板上顯示用。</summary>
    public static string SharedReport = "尚未同步";

    /// <summary>F9 專用：設定改了就公布出去，F6 / F7 下一幀就跟上。</summary>
    public static void Publish(bool enabled, bool onlyInVr)
    {
        Enabled = enabled;
        OnlyInVr = onlyInVr;
        SharedReport = "這裡是設定來源（F6 / F7 跟著走）";
        try
        {
            AppDomain.CurrentDomain.SetData(KeyEnabled, enabled ? "1" : "0");
            AppDomain.CurrentDomain.SetData(KeyOnlyVr, onlyInVr ? "1" : "0");
        }
        catch { }
    }

    /// <summary>F6 / F7 專用：跟著 F9 的設定走。F9 沒裝就用自己的預設值。</summary>
    public static void Follow()
    {
        try
        {
            object e = AppDomain.CurrentDomain.GetData(KeyEnabled);
            object o = AppDomain.CurrentDomain.GetData(KeyOnlyVr);
            if (e is string)
            {
                Enabled = (string)e == "1";
                OnlyInVr = (o is string) ? (string)o == "1" : OnlyInVr;
                SharedReport = "跟著 F9 的設定";
                return;
            }
        }
        catch { }
        SharedReport = "沒偵測到 F9，用本地預設";
    }

    /// <summary>視窗底色。</summary>
    public static Color Window = new Color(0.13f, 0.14f, 0.17f, 1f);

    /// <summary>按鈕 / 輸入框底色（未啟用）。</summary>
    public static Color Control = new Color(0.16f, 0.17f, 0.21f, 1f);

    /// <summary>未啟用時的外框。低調就好，只是把控制項跟視窗底分開。</summary>
    public static Color ControlEdge = new Color(0.38f, 0.40f, 0.46f, 1f);

    /// <summary>
    /// 按下去 / 打開時的底色。
    ///
    /// 原本是 (0.20, 0.42, 0.58) —— 跟未啟用的 (0.22, 0.24, 0.28) 亮度幾乎一樣，
    /// 在頭顯裡（解析度低、又隔了一層 RenderTexture）根本分不出開沒開。
    /// 改成明顯更亮、更飽和的藍，亮度差拉到 2 倍以上。
    /// </summary>
    public static Color ControlOn = new Color(0.10f, 0.50f, 0.92f, 1f);

    /// <summary>啟用時的外框：純白。這是最直接的「有沒有選到」訊號。</summary>
    public static Color ControlOnEdge = Color.white;

    /// <summary>文字顏色（未啟用）。</summary>
    public static Color Text = new Color(0.82f, 0.84f, 0.88f, 1f);

    /// <summary>文字顏色（啟用）。比未啟用亮，跟著底色一起把對比拉開。</summary>
    public static Color TextOn = Color.white;

    // ---------------------------------------------------------- VR 偵測

    static System.Reflection.PropertyInfo enabledProp, presentProp;
    static bool probed;
    static float checkAt = -999f;
    static bool cached;

    static bool VrRunning()
    {
        // OnGUI 一幀會被叫好幾次，每次都掃相機太貴，快取一秒
        if (Time.realtimeSinceStartup - checkAt < 1f) return cached;
        checkAt = Time.realtimeSinceStartup;
        cached = Detect();
        return cached;
    }

    static bool Detect()
    {
        if (!probed)
        {
            probed = true;
            // Unity 5.6 是 UnityEngine.VR.VRSettings，之後改名 XRSettings，兩個都試
            foreach (string n in new[] {
                         "UnityEngine.VR.VRSettings, UnityEngine",
                         "UnityEngine.XR.XRSettings, UnityEngine",
                         // Unity 2019（Koikatsu Sunshine）：型別實際在 VRModule 裡，
                         // UnityEngine.dll 只是轉發。轉發沒接上的話直接指名真正的組件。
                         "UnityEngine.XR.XRSettings, UnityEngine.VRModule" })
            {
                Type t = null;
                try { t = Type.GetType(n, false); }
                catch { }
                if (t == null) continue;
                if (enabledProp == null)
                    enabledProp = t.GetProperty("enabled",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (presentProp == null)
                    presentProp = t.GetProperty("isPresent",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
        }

        try
        {
            if (enabledProp != null)
            {
                object v = enabledProp.GetValue(null, null);
                if (v is bool && (bool)v) return true;
            }
            if (presentProp != null)
            {
                object v = presentProp.GetValue(null, null);
                if (v is bool && (bool)v) return true;
            }
        }
        catch { }

        // VRGIN 是事後注入的，有些啟動路徑下 VRSettings.enabled 不會翻成 true，
        // 但頭顯其實已經在顯示了。多看一條線索：場上有沒有 VRGIN 的相機。
        try
        {
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                foreach (Component comp in c.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string n = comp.GetType().Name;
                    if (n == "VRCamera" || n == "SteamVR_Camera") return true;
                }
            }
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------- 換皮

    static GUISkin skin;
    static Color builtWindow, builtControl, builtOn, builtText;
    static Color builtEdge, builtOnEdge, builtTextOn;

    static bool Active
    {
        get { return Enabled && (!OnlyInVr || VrRunning()); }
    }

    /// <summary>換上不透明版的 skin，回傳原本那個。一定要拿去餵 End。</summary>
    public static GUISkin Begin()
    {
        GUISkin saved = GUI.skin;
        if (!Active) return saved;

        if (skin == null || builtWindow != Window || builtControl != Control
            || builtOn != ControlOn || builtText != Text
            || builtEdge != ControlEdge || builtOnEdge != ControlOnEdge
            || builtTextOn != TextOn)
        {
            skin = Build(saved);
            builtWindow = Window; builtControl = Control;
            builtOn = ControlOn; builtText = Text;
            builtEdge = ControlEdge; builtOnEdge = ControlOnEdge; builtTextOn = TextOn;
        }
        if (skin != null) GUI.skin = skin;
        return saved;
    }

    public static void End(GUISkin saved)
    {
        if (saved != null) GUI.skin = saved;
    }

    static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
        t.SetPixel(0, 0, c);
        t.Apply();
        t.filterMode = FilterMode.Point;
        t.wrapMode = TextureWrapMode.Clamp;
        UnityEngine.Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>外框粗細（像素）。</summary>
    const int EdgeW = 2;

    /// <summary>
    /// 中間純色、四周一圈外框的小圖。
    ///
    /// 為什麼要外框：在 VR 裡「這個選項到底有沒有啟用」只靠底色深淺分辨很吃力 ——
    /// 畫面經過 VRGIN 的 RenderTexture 再貼到板子上，細微的亮度差幾乎全丟掉。
    /// 一圈純白的框是形狀上的差別，不是顏色上的差別，低解析度下照樣看得出來。
    ///
    /// 搭配 border = EdgeW 的九宮格拉伸，框的粗細不會因為按鈕大小而變形。
    /// </summary>
    static Texture2D Framed(Color fill, Color edge)
    {
        const int S = EdgeW * 2 + 2;          // 兩圈框 + 中間至少 2 px
        var t = new Texture2D(S, S, TextureFormat.ARGB32, false);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
                t.SetPixel(x, y,
                    (x < EdgeW || y < EdgeW || x >= S - EdgeW || y >= S - EdgeW) ? edge : fill);
        t.Apply();
        t.filterMode = FilterMode.Point;
        t.wrapMode = TextureWrapMode.Clamp;
        UnityEngine.Object.DontDestroyOnLoad(t);
        return t;
    }

    static GUISkin Build(GUISkin src)
    {
        try
        {
            GUISkin s = UnityEngine.Object.Instantiate(src) as GUISkin;
            if (s == null) return null;
            UnityEngine.Object.DontDestroyOnLoad(s);

            Texture2D win = Solid(Window);
            Texture2D ctl = Framed(Control, ControlEdge);
            Texture2D on = Framed(ControlOn, ControlOnEdge);

            // 視窗和捲動區是大面積的底，不要外框
            Paint(s.window, win, win, Text, Text, false);
            Paint(s.box, win, win, Text, Text, false);
            Paint(s.scrollView, win, win, Text, Text, false);

            // 可以「按」「選」的東西才要外框，而且啟用時是白框
            Paint(s.button, ctl, on, Text, TextOn, true);
            Paint(s.toggle, ctl, on, Text, TextOn, true);
            Paint(s.textField, ctl, ctl, Text, TextOn, true);
            Paint(s.textArea, ctl, ctl, Text, TextOn, true);
            Paint(s.horizontalSlider, ctl, ctl, Text, TextOn, true);
            Paint(s.horizontalSliderThumb, on, on, TextOn, TextOn, true);
            Paint(s.verticalSlider, ctl, ctl, Text, TextOn, true);
            Paint(s.verticalSliderThumb, on, on, TextOn, TextOn, true);

            // label 沒有底圖（本來就該透出視窗底色），只要文字夠亮就好
            Tint(s.label, Text);

            return s;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VrSkin] 建不透明 skin 失敗，維持原樣：" + e.Message);
            return null;
        }
    }

    static void Paint(GUIStyle st, Texture2D normal, Texture2D active,
                      Color text, Color textOn, bool framed)
    {
        if (st == null) return;
        st.normal.background = normal;
        st.hover.background = normal;
        st.focused.background = normal;
        st.active.background = active;
        st.onNormal.background = active;
        st.onHover.background = active;
        st.onFocused.background = active;
        st.onActive.background = active;

        st.normal.textColor = text;
        st.hover.textColor = textOn;
        st.focused.textColor = textOn;
        st.active.textColor = textOn;
        st.onNormal.textColor = textOn;
        st.onHover.textColor = textOn;
        st.onFocused.textColor = textOn;
        st.onActive.textColor = textOn;

        // 九宮格：四邊各留 EdgeW 不拉伸，框的粗細才不會跟著按鈕大小變。
        // 沒有框的（視窗、捲動區）就歸零，不然唯一的那幾個畫素會被切碎。
        st.border = framed
            ? new RectOffset(EdgeW, EdgeW, EdgeW, EdgeW)
            : new RectOffset(0, 0, 0, 0);
    }

    static void Tint(GUIStyle st, Color text)
    {
        if (st == null) return;
        st.normal.textColor = text;
        st.hover.textColor = text;
        st.focused.textColor = text;
        st.active.textColor = text;
        st.onNormal.textColor = text;
        st.onHover.textColor = text;
        st.onFocused.textColor = text;
        st.onActive.textColor = text;
    }

}
