using System;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 在 Studio 左邊那排工具列加一顆按鈕（跟 Timeline 那顆同一排）。
    ///
    /// 那排按鈕是 KKAPI 提供的 CustomToolbarButtons。這裡**不**在 csproj 加參照，
    /// 而是用「指名道姓」的反射去拿那一個型別、呼叫那一個方法：
    ///
    ///   · 不加參照 → 沒裝 KKAPI 的人插件照樣能載入，只是少一顆按鈕
    ///   · 指名查找 → asm.GetType("完整名稱") 是直接查表，不會列舉型別、
    ///     不會碰到別人的屬性 getter。跟之前那個掃描整個行程的寫法完全不同，
    ///     那個寫法會把遊戲弄當，這個不會。
    ///
    /// 圖示是程式畫出來的 32×32，不需要外部檔案：一副 VR 頭顯，上面寫 VR，右下角一個 R。
    ///
    /// 這個檔案跟 StudioCharTools / StudioCutScene 那兩份幾乎一樣，是**刻意重複**的：
    /// 三支插件要能各自單獨安裝，共用就得加組件參照，缺一支就一起 TypeLoadException。
    /// 為了省這幾十行去換來那個風險不划算。不一樣的只有 MakeIcon。
    /// </summary>
    public static class ToolbarButton
    {
        static object toggle;             // KKAPI 的 ToolbarToggle
        static PropertyInfo valueProp;
        static bool tried;

        public static string Status = "（還沒建立）";

        /// <summary>建立按鈕。onChanged 會在使用者點按鈕時被呼叫。</summary>
        public static void Create(Action<bool> onChanged, bool initial)
        {
            if (tried) return;
            tried = true;
            try
            {
                Type t = FindType("KKAPI.Studio.UI.CustomToolbarButtons");
                if (t == null) { Status = "找不到 KKAPI，沒有工具列按鈕"; return; }

                MethodInfo mi = null;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "AddLeftToolbarToggle") continue;
                    if (m.GetParameters().Length >= 1) { mi = m; break; }
                }
                if (mi == null) { Status = "KKAPI 沒有 AddLeftToolbarToggle"; return; }

                ParameterInfo[] ps = mi.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(Texture2D))
                    {
                        // 留著這張圖的參照 —— 待會要靠它在 KKAPI 的按鈕清單裡
                        // 認出「哪一顆是我們的」，見 FindControl。
                        if (iconTex == null) iconTex = MakeIcon();
                        args[i] = iconTex;
                    }
                    else if (ps[i].ParameterType == typeof(bool)) args[i] = initial;
                    else if (typeof(Delegate).IsAssignableFrom(ps[i].ParameterType))
                        args[i] = Delegate.CreateDelegate(ps[i].ParameterType,
                                                          onChanged.Target, onChanged.Method);
                    else args[i] = ps[i].DefaultValue;
                }
                toggle = mi.Invoke(null, args);
                if (toggle != null)
                    valueProp = toggle.GetType().GetProperty("Value");
                Status = toggle != null ? "已建立" : "KKAPI 回傳 null";
            }
            catch (Exception e)
            {
                Status = "建立失敗：" + e.GetType().Name;
            }
        }

        /// <summary>面板用熱鍵開關時，把按鈕的狀態同步過去。</summary>
        public static void Sync(bool on)
        {
            try
            {
                if (toggle != null && valueProp != null && valueProp.CanWrite)
                {
                    object cur = valueProp.GetValue(toggle, null);
                    if (!(cur is bool) || (bool)cur != on)
                        valueProp.SetValue(toggle, on, null);
                }
            }
            catch { }
        }

        static Texture2D iconTex;
        static Type tMgr, tStore;

        /// <summary>
        /// 在 KKAPI 的按鈕清單裡找出「我們這一顆」。
        ///
        /// 用圖示的**參照**比對：AddLeftToolbarToggle 回傳的那個物件不一定
        /// 就是工具列在管的那個控制項（舊版是 ToolbarToggle，新版外面還包一層
        /// ToolbarControlAdapter），所以不能直接拿它去餵 ToolbarManager。
        /// 圖示是我們自己 new 出來的 Texture2D，參照相等就是我們的那一顆，不會認錯。
        /// </summary>
        static object FindControl()
        {
            if (iconTex == null) return null;
            try
            {
                if (tMgr == null) tMgr = FindType("KKAPI.Studio.UI.Toolbars.ToolbarManager");
                if (tMgr == null) return null;

                MethodInfo get = tMgr.GetMethod("GetAllButtons",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (get == null) return null;

                var list = get.Invoke(null, new object[] { true })
                           as System.Collections.IEnumerable;
                if (list == null) return null;

                foreach (object c in list)
                {
                    if (c == null) continue;
                    PropertyInfo p = c.GetType().GetProperty("IconTex",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p == null) continue;
                    if (ReferenceEquals(p.GetValue(c, null), iconTex)) return c;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 顯示 / 隱藏這顆按鈕。
        ///
        /// 優先走 KKAPI 自己的「隱藏」機制（ToolbarDataStorage.ToggleHidden +
        /// ToolbarManager.RequestToolbarRelayout）—— 那就是右鍵選單「隱藏按鈕」走的路，
        /// **後面的按鈕會自動遞補上來**，不會留一個空格。
        ///
        /// 只關 Visible 或 GameObject 做不到這件事：工具列的排版還是把那個位置
        /// 算給我們，看起來就是中間空一格。實測就是這個症狀。
        ///
        /// 舊版 KKAPI 沒有那一套的話，才退回原本的 Visible / GameObject 做法。
        /// </summary>
        public static void SetVisible(bool on)
        {
            if (toggle == null) return;

            // --- 首選：KKAPI 的隱藏 + 重新排版 ---
            try
            {
                object ctrl = FindControl();
                if (ctrl != null)
                {
                    if (tStore == null)
                        tStore = FindType("KKAPI.Studio.UI.Toolbars.ToolbarDataStorage");
                    if (tStore != null)
                    {
                        const BindingFlags anyStatic = BindingFlags.Public
                                                       | BindingFlags.NonPublic | BindingFlags.Static;
                        MethodInfo isHidden = tStore.GetMethod("IsHidden", anyStatic);
                        MethodInfo toggleHidden = tStore.GetMethod("ToggleHidden", anyStatic);
                        if (isHidden != null && toggleHidden != null)
                        {
                            object cur = isHidden.Invoke(null, new[] { ctrl });
                            bool hidden = cur is bool && (bool)cur;

                            // 想顯示就是「不隱藏」。狀態已經對了就不要再切，
                            // 不然每次重試都會翻一次，按鈕會一閃一閃。
                            if (hidden == on)
                            {
                                toggleHidden.Invoke(null, new[] { ctrl });
                                MethodInfo relayout = tMgr.GetMethod("RequestToolbarRelayout",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                if (relayout != null) relayout.Invoke(null, null);
                            }
                            return;
                        }
                    }
                }
            }
            catch { }

            // --- 退路：舊版 KKAPI ---
            try
            {
                PropertyInfo vp = toggle.GetType().GetProperty("Visible");
                if (vp != null)
                {
                    if (vp.PropertyType == typeof(bool) && vp.CanWrite)
                    {
                        vp.SetValue(toggle, on, null);
                        return;
                    }
                    object subj = vp.GetValue(toggle, null);
                    if (subj != null)
                    {
                        MethodInfo next = subj.GetType().GetMethod("OnNext",
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null);
                        if (next != null) { next.Invoke(subj, new object[] { on }); return; }
                    }
                }
            }
            catch { }

            try
            {
                foreach (string n in new[] { "ButtonObject", "ControlObject" })
                {
                    PropertyInfo p = toggle.GetType().GetProperty(n);
                    if (p == null) continue;
                    var go = p.GetValue(toggle, null) as GameObject;
                    if (go != null) { go.SetActive(on); return; }
                }
            }
            catch { }
        }

        /// <summary>
        /// 把「按下去變綠色」改成別的顏色。
        ///
        /// KKAPI 自己寫死了：UpdateVisualState 裡是
        ///   ButtonObject.GetComponent&lt;Button&gt;().image.color = Toggled ? green : white
        /// （反組譯確認過）。它只在狀態改變時跑一次，所以我們事後蓋上去是穩的，
        /// 只要每隔一小段時間補一次就好。
        ///
        /// 刻意不參照 UnityEngine.UI：用反射找「有 color 屬性而且現在是綠色」的元件。
        /// 加參照會讓這支插件多綁一個組件，為了改個顏色不划算。
        /// </summary>
        public static void TintToggled(Color want)
        {
            if (toggle == null) return;
            try
            {
                GameObject go = null;
                foreach (string n in new[] { "ButtonObject", "ControlObject" })
                {
                    PropertyInfo p = toggle.GetType().GetProperty(n);
                    if (p == null) continue;
                    go = p.GetValue(toggle, null) as GameObject;
                    if (go != null) break;
                }
                if (go == null) return;

                foreach (Component comp in go.GetComponentsInChildren(typeof(Component), true))
                {
                    if (comp == null) continue;
                    PropertyInfo cp = comp.GetType().GetProperty("color");
                    if (cp == null || cp.PropertyType != typeof(Color) || !cp.CanWrite) continue;

                    object v = cp.GetValue(comp, null);
                    if (!(v is Color)) continue;
                    Color c = (Color)v;

                    // 只動「正是 KKAPI 塗上去的那個綠」。白色是未啟用狀態，不要碰。
                    if (c.g > 0.7f && c.r < 0.3f && c.b < 0.3f)
                        cp.SetValue(comp, want, null);
                }
            }
            catch { }
        }

        /// <summary>指名查找，不列舉型別。</summary>
        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // 5×7 的點陣字。小尺寸下線條會糊，字形不會 —— 所以圖示直接用字。
        static readonly string[] GlyphV = {
            "10001",
            "10001",
            "10001",
            "10001",
            "10001",
            "01010",
            "00100",
        };

        /// <summary>
        /// 32×32 的圖示，純程式畫：一副頭顯輪廓，鏡片面上用點陣字寫 VR。
        /// 用程式畫而不是帶圖檔，是為了讓插件維持單一 dll、不用管資源路徑。
        /// </summary>
        static Texture2D MakeIcon()
        {
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;

            Color clear = new Color(0f, 0f, 0f, 0f);
            Color shell = new Color(0.45f, 0.80f, 1f, 1f);   // 頭顯外殼，淡藍
            Color face = new Color(0.10f, 0.13f, 0.20f, 1f); // 鏡片那一面，深色
            Color ink = Color.white;                         // VR 字

            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    Color c = clear;

                    // 頭顯本體：圓角矩形（四個角挖掉）
                    bool body = x >= 4 && x <= 27 && y >= 9 && y <= 25;
                    bool corner = (x <= 6 || x >= 25) && (y <= 11 || y >= 23);
                    if (body && !corner) c = shell;

                    // 兩側各伸出一小截綁帶
                    if ((x == 2 || x == 3 || x == 28 || x == 29) && y >= 14 && y <= 20) c = shell;

                    // 鏡片面：內縮一圈的深色底，字寫在上面才看得清楚
                    if (x >= 7 && x <= 24 && y >= 12 && y <= 23) c = face;

                    tex.SetPixel(x, y, c);
                }
            }

            // VR 兩個字，各放大兩倍（5×7 → 10×14）
            Blit(tex, GlyphV, 7, 15, 2, ink);
            Blit(tex, GlyphR, 18, 15, 2, ink);

            StampR(tex, N);
            tex.Apply();
            return tex;
        }

        /// <summary>把一個點陣字畫上去。x0/y0 是左下角，scale 是放大倍率。</summary>
        static void Blit(Texture2D tex, string[] glyph, int x0, int y0, int scale, Color c)
        {
            int h = glyph.Length, w = glyph[0].Length;
            for (int r = 0; r < h; r++)
            {
                // glyph[0] 是最上面那一列，貼圖的 y=0 在下面，所以要反過來取
                string row = glyph[h - 1 - r];
                for (int q = 0; q < w; q++)
                {
                    if (row[q] != '1') continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int px = x0 + q * scale + dx, py = y0 + r * scale + dy;
                            if (px >= 0 && py >= 0 && px < tex.width && py < tex.height)
                                tex.SetPixel(px, py, c);
                        }
                }
            }
        }

        /// <summary>
        /// 右下角蓋一個 R。
        ///
        /// 工具列上那排按鈕都是 32×32 的小圖，形狀差不多就分不出是誰的。
        /// 一個字母比「再畫細一點」有效得多 —— 在小尺寸下線條會糊掉，字形不會。
        /// 底下先鋪一塊深色，字才不會跟圖案本身混在一起。
        /// </summary>
        static readonly string[] GlyphR = {
            "11110",
            "10001",
            "10001",
            "11110",
            "10100",
            "10010",
            "10001",
        };

        public static void StampR(Texture2D tex, int n)
        {
            Color fg = Color.white;
            Color bg = new Color(0.08f, 0.08f, 0.10f, 1f);

            int w = GlyphR[0].Length, h = GlyphR.Length;
            int x0 = n - w - 2, y0 = 2;

            for (int y = y0 - 1; y <= y0 + h; y++)
                for (int x = x0 - 1; x <= x0 + w; x++)
                    if (x >= 0 && y >= 0 && x < n && y < n) tex.SetPixel(x, y, bg);

            // GlyphR[0] 是「上面那一列」，但貼圖的 y=0 在下面，所以要反過來取
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    if (GlyphR[h - 1 - r][c] == '1') tex.SetPixel(x0 + c, y0 + r, fg);
        }
    }
}
