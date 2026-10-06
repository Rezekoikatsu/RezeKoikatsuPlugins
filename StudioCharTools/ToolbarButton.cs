using System;
using System.Reflection;
using UnityEngine;

namespace StudioCharTools
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
    /// 圖示是程式畫出來的 32×32，不需要外部檔案：一個白色小人，右下角一個 R。
    ///
    /// 這個檔案跟 StudioCutScene 那一份幾乎一樣，是**刻意重複**的：
    /// 兩支插件要能各自單獨安裝，共用就得加組件參照，缺一邊就兩支一起 TypeLoadException。
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

            // 有些版本的 KKAPI（Sunshine 用的 KKSAPI 1.45 就是）AddLeftToolbarToggle 回傳的
            // 那個包裝物件，Value 改了**不會**傳到工具列上真正的那顆按鈕：顏色不變，
            // 下一次點按鈕送出來的還是舊狀態的反相，要點兩下才有反應。
            // 所以上面寫完之後再看一眼真正的控制項，沒跟上就直接推它的 Toggled。
            // 會傳過去的版本這裡一定已經一致，什麼都不會做。
            try
            {
                object subj = GetSubject(FindControl(), "Toggled");
                if (subj != null) PushBool(subj, on);
            }
            catch { }
        }

        /// <summary>拿控制項上某個 BehaviorSubject&lt;bool&gt; 屬性（Toggled / Visible）。</summary>
        static object GetSubject(object ctrl, string name)
        {
            if (ctrl == null) return null;
            PropertyInfo p = ctrl.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(ctrl, null);
        }

        /// <summary>把值推進 BehaviorSubject&lt;bool&gt;。已經是那個值就不推。回傳 true = 真的推了。</summary>
        static bool PushBool(object subj, bool v)
        {
            PropertyInfo vp = subj.GetType().GetProperty("Value");
            object cur = vp == null ? null : vp.GetValue(subj, null);
            if (cur is bool && (bool)cur == v) return false;
            MethodInfo next = subj.GetType().GetMethod("OnNext",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            if (next == null) return false;
            next.Invoke(subj, new object[] { v });
            return true;
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
            if (foundCtrl != null) return foundCtrl;
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

                    // 一顆一顆包起來：原版按鈕的 IconTex 會丟例外（它的圖示取得函式回傳 null），
                    // 不包的話碰到一顆原版按鈕整個迴圈就結束，排在它後面的我們永遠找不到。
                    object tex;
                    try { tex = p.GetValue(c, null); } catch { continue; }
                    if (ReferenceEquals(tex, iconTex)) { foundCtrl = c; return c; }
                }
            }
            catch { }
            return null;
        }

        // 找到之後就記住。按鈕建立後不會換物件，不用每次都把整個清單翻一遍。
        static object foundCtrl;

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

            // --- 沒有 IsHidden / ToggleHidden 的版本（KKSAPI 1.45 等）---
            // 改工具列上真正那顆控制項自己的 Visible：排版只算 Visible 為 true 的按鈕，
            // 所以後面的一樣會遞補上來。寫包裝物件的 Visible 做不到這件事（只是把按鈕關掉，
            // 位置還在）。按鈕的 GameObject 還沒建立時沒有人在聽這個值，所以自己補叫一次重新排版。
            try
            {
                object vis = GetSubject(FindControl(), "Visible");
                if (vis != null)
                {
                    if (PushBool(vis, on) && tMgr != null)
                    {
                        MethodInfo relayout = tMgr.GetMethod("RequestToolbarRelayout",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (relayout != null) relayout.Invoke(null, null);
                    }
                    return;
                }
            }
            catch { }

            // --- 退路：更舊的 KKAPI ---
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

        /// <summary>
        /// 32×32 的圖示，純程式畫：一個白色小人（頭 + 肩）。
        /// 用程式畫而不是帶圖檔，是為了讓插件維持單一 dll、不用管資源路徑。
        /// </summary>
        static Texture2D MakeIcon()
        {
            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;

            Color clear = new Color(0f, 0f, 0f, 0f);
            Color body = Color.white;

            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    Color c = clear;
                    float dx = x - 15.5f;

                    // 頭：圓形，中心偏上
                    float dy = y - 23.5f;
                    if (dx * dx + dy * dy <= 5.2f * 5.2f) c = body;

                    // 肩：越往下越寬的梯形，跟頭之間留一條縫才看得出是脖子
                    if (y >= 5 && y <= 17)
                    {
                        float half = 4.5f + (17 - y) * 0.62f;
                        if (Mathf.Abs(dx) <= half) c = body;
                    }

                    tex.SetPixel(x, y, c);
                }
            }
            StampR(tex, N);
            tex.Apply();
            return tex;
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
