using System;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 在 VR 頭顯裡播過場影片用的「虛擬螢幕」。
    ///
    /// 為什麼需要這個
    /// --------------
    /// CutOverlay 原本是用 IMGUI 畫的（OnGUI + GUI.DrawTexture 鋪滿 Screen）。
    /// 那在桌面上最省事：不依賴 uGUI、不用 RenderTexture、疊在所有東西最上層。
    /// 但 IMGUI 是畫進桌面／鏡像畫面的，**不會進到雙眼的貼圖** ——
    /// 戴上頭顯之後過場影片整個消失，只有音軌還在跑。
    ///
    /// 所以 VR 模式改成另一條路：在相機前面擺一塊世界空間的四邊形，
    /// 材質貼 VideoPlayer 的畫面。這樣兩隻眼睛都看得到，而且跟用哪一個
    /// VR 插件無關 —— 不用去對接 KK_VR 的 UI 系統（那樣綁死版本，它一改就壞）。
    ///
    /// 幾個刻意的選擇
    /// --------------
    /// 著色器用找的不是自己寫的：執行期沒辦法編譯著色器，而外掛又要維持單一 dll。
    /// 優先找 UI/Default（遊戲一定有 uGUI，不會被打包時剔除），它支援 _Color
    /// 淡入淡出，而且可以把 unity_GUIZTestMode 設成 Always，讓畫面穿過所有場景物件。
    ///
    /// 相機用挑的：Camera.main 在 VR 下不一定是眼睛的那台。優先挑
    /// stereoEnabled 的；順便跳過 cullingMask == 0 的相機 ——
    /// KK_VR 用那種相機做桌面遮罩，掛上去會什麼都看不到。
    /// </summary>
    public class VrScreen
    {
        public string Status = "";
        public bool Active;                 // 這一幀是不是走 VR 路徑
        public string ShaderName = "-";

        GameObject root, backGo, videoGo;
        Renderer backR, videoR;
        Material backM, videoM;
        Camera cam;
        float nextPick;
        bool placed;

        // ---------------------------------------------------------- VR 偵測

        static PropertyInfo enabledProp, presentProp;
        static bool probed;

        /// <summary>
        /// Unity 5.6 是 UnityEngine.VR.VRSettings，2017.2 之後改名 UnityEngine.XR.XRSettings。
        /// 用反射兩個都試：同一份 dll 在不同版本都能跑，csproj 也不用加參照。
        /// </summary>
        static void Probe()
        {
            if (probed) return;
            probed = true;
            string[] names = {
                "UnityEngine.VR.VRSettings, UnityEngine",
                "UnityEngine.XR.XRSettings, UnityEngine",
                "UnityEngine.XR.XRSettings, UnityEngine.XRModule",
                "UnityEngine.VR.VRDevice, UnityEngine",
                "UnityEngine.XR.XRDevice, UnityEngine",
            };
            foreach (string n in names)
            {
                Type t = null;
                try { t = Type.GetType(n, false); }
                catch { }
                if (t == null) continue;
                if (enabledProp == null)
                    enabledProp = t.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static);
                if (presentProp == null)
                    presentProp = t.GetProperty("isPresent", BindingFlags.Public | BindingFlags.Static);
            }
        }

        static float vrginCheckAt = -999f;
        static bool vrginCached;

        /// <summary>
        /// VRGIN 有沒有在跑。
        ///
        /// 為什麼不只看 VRSettings.enabled：VRGIN 是「事後注入」的 ——
        /// 它在遊戲啟動之後自己建相機、接管渲染。有些版本／有些啟動路徑下
        /// VRSettings.enabled 不會翻成 true，但頭顯其實已經在顯示了。
        /// 只看那個旗標的話，模式 0（自動）就永遠走桌面路徑，VR 裡一片空白。
        /// 所以多一條線索：場上有沒有 VRGIN 的 VRCamera。
        ///
        /// 這個檢查要掃全部相機，每幀做太貴（Tick 每幀都會叫），所以快取一秒。
        /// </summary>
        static bool VrginPresent()
        {
            if (Time.realtimeSinceStartup - vrginCheckAt < 1f) return vrginCached;
            vrginCheckAt = Time.realtimeSinceStartup;
            vrginCached = PickVrgin() != null;
            return vrginCached;
        }

        public static bool VrRunning()
        {
            Probe();
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
            return VrginPresent();
        }

        // ---------------------------------------------------------- 相機

        /// <summary>
        /// VRGIN 自己認定的頭部相機。
        ///
        /// 這台機器上的 CharaStudio VR 是 KKCharaStudioVRPlugin + VRGIN_KKCS，
        /// 屬於 VRGIN 那一系。VRGIN 會蓋一整套相機階層（VROrigin → VRCamera →
        /// SteamVR 的眼睛相機），其中**不只一台 stereoEnabled 會是 true** ——
        /// 單純比 depth 挑，很容易挑到只拿來做鏡像輸出的那台，結果頭顯裡還是空的。
        ///
        /// 所以直接找「身上掛著 VRCamera 或 SteamVR_Camera 的那台」。
        /// 用元件的型別名稱比對而不是參照 VRGIN.dll：版本換了、改用別的 VR 插件
        /// （只要同樣是 VRGIN 系）都還能對上，csproj 也不必多一個參照。
        /// </summary>
        static Camera PickVrgin()
        {
            try
            {
                // 先看相機自己身上有沒有掛
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || !c.isActiveAndEnabled || c.cullingMask == 0) continue;
                    foreach (Component comp in c.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        string n = comp.GetType().Name;
                        if (n == "VRCamera" || n == "SteamVR_Camera") return c;
                    }
                }

                // 有些版本 VRCamera 掛在父物件上，真正的 Camera 在子物件
                foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string n = mb.GetType().Name;
                    if (n != "VRCamera" && n != "SteamVR_Camera") continue;
                    Camera c = mb.GetComponentInChildren<Camera>();
                    if (c != null && c.isActiveAndEnabled && c.cullingMask != 0) return c;
                }
            }
            catch { }
            return null;
        }

        /// <summary>給其他模組用的：目前這一幀我們認定的 VR 相機（頭）。</summary>
        public static Camera VrCamera()
        {
            Camera c = PickVrgin();
            return c != null ? c : Pick();
        }

        static Camera Pick()
        {
            Camera vrgin = PickVrgin();
            if (vrgin != null) return vrgin;

            Camera stereo = null, top = null;
            Camera[] all;
            try { all = Camera.allCameras; }
            catch { return Camera.main; }

            foreach (Camera c in all)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                if (c.cullingMask == 0) continue;      // 遮罩用的相機，掛上去等於看不到
                bool st = false;
                try { st = c.stereoEnabled; } catch { }
                if (st && (stereo == null || c.depth > stereo.depth)) stereo = c;
                if (top == null || c.depth > top.depth) top = c;
            }
            if (stereo != null) return stereo;
            if (Camera.main != null && Camera.main.isActiveAndEnabled) return Camera.main;
            return top;
        }

        static int FirstLayer(int mask)
        {
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        // ---------------------------------------------------------- 獨佔模式

        static int soloLayer = -1;

        /// <summary>
        /// 挑一個場上沒東西在用的層給影片專用。
        ///
        /// 不能直接用第 0 層（Default）—— 地圖就在那裡，只留那一層等於沒關。
        /// 所以從高往低找一個「沒名字、而且場上沒有任何 Renderer 在用」的層。
        /// 兩個條件都要：沒名字表示遊戲沒規劃它，沒 Renderer 在用表示沒有外掛偷用。
        /// </summary>
        static int SoloLayer()
        {
            if (soloLayer >= 0) return soloLayer;

            bool[] used = new bool[32];
            try
            {
                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null) continue;
                    int l = r.gameObject.layer;
                    if (l >= 0 && l < 32) used[l] = true;
                }
            }
            catch { }

            for (int i = 31; i >= 8; i--)
            {
                if (used[i]) continue;
                string n = null;
                try { n = LayerMask.LayerToName(i); }
                catch { }
                if (!string.IsNullOrEmpty(n)) continue;
                soloLayer = i;
                Debug.Log("[VrScreen] 獨佔模式使用第 " + i + " 層（沒名字，場上也沒人用）");
                return soloLayer;
            }

            // 真的找不到就退回原本的做法：不獨佔，靠黑底擋。
            soloLayer = -1;
            Debug.LogWarning("[VrScreen] 找不到空的層，獨佔模式停用，改用黑底擋");
            return FirstLayer(Camera.main != null ? Camera.main.cullingMask : 1);
        }

        class CamState
        {
            public Camera cam;
            public int mask;
            public CameraClearFlags clear;
            public Color back;
            public System.Collections.Generic.List<MonoBehaviour> fx;
        }

        /// <summary>
        /// 播放時要不要順便把相機上的後製特效關掉。
        ///
        /// 為什麼需要：獨佔模式只改 cullingMask（哪幾層進渲染）和清除色，
        /// **後製是在那之後才跑的** —— 畫面已經是純黑加影片了，泛光、色彩校正、
        /// 景深還是照樣往上疊。影片本身很亮，泛光就把亮部整片糊開；
        /// 顏色又被校正曲線拉過一次。桌面上看起來正常是因為桌面那一份走的是
        /// 另一條路（IMGUI 的 overlay），根本沒經過這些相機。
        /// </summary>
        public static bool KillPostFx = true;

        /// <summary>
        /// 這個元件是不是「影像後製」。
        ///
        /// 判斷方式是它有沒有自己實作 OnRenderImage —— 那是 Unity 給影像後製的
        /// 唯一入口，泛光、色彩校正、景深、暈影全都靠它。用方法有沒有存在來判斷，
        /// 比列一張外掛名單可靠：xukmi、PostProcessing、AmplifyColor、
        /// 還沒出現的第三方，一視同仁都抓得到。
        ///
        /// VRGIN / SteamVR 自己的東西一律跳過 —— 它們也用 OnRenderImage 做
        /// 雙眼合成，關掉的話頭顯裡會整片黑，那比偏紅嚴重得多。
        /// </summary>
        static bool IsImageEffect(MonoBehaviour mb)
        {
            if (mb == null || !mb.enabled) return false;
            Type t = mb.GetType();

            string fn = t.FullName ?? "";
            if (fn.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (fn.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (fn.IndexOf("Valve", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // 第一版只看 OnRenderImage，實測在這台機器上「找到 0 個」——
            // 因為 Unity Post Processing Stack v2（KK 的 PostProcessingEffects 外掛用的那套）
            // 走的是 CommandBuffer，不是 OnRenderImage，所以完全抓不到。
            // 名稱比對補這個漏，兩條任一成立就算。
            string sn = t.Name ?? "";
            if (sn.IndexOf("PostProcess", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (sn.IndexOf("Bloom", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (sn.IndexOf("AmplifyColor", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (sn.IndexOf("ColorGrading", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (sn.IndexOf("SunShafts", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            try
            {
                MethodInfo mi = t.GetMethod("OnRenderImage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return mi != null && mi.DeclaringType != typeof(MonoBehaviour);
            }
            catch { return false; }
        }

        static readonly System.Collections.Generic.List<CamState> soloed =
            new System.Collections.Generic.List<CamState>();

        /// <summary>
        /// 把所有會畫到畫面上的相機改成「只畫影片那一層」＋清成黑色。
        ///
        /// 刻意跳過有 targetTexture 的相機：VRGIN 就是用那種相機把桌面介面
        /// 畫進一張貼圖再貼到浮空板子上的。把它也關掉的話，介面會整塊變黑 ——
        /// 過場中介面本來就該收起來，但那應該是使用者自己決定，不是被我們順手弄壞。
        /// </summary>
        static void EnterSolo(int layer)
        {
            if (soloLayer < 0) return;              // 沒有可用的層，不做
            if (soloed.Count > 0) return;           // 已經在獨佔了

            int killedFx = 0, soloedCams = 0;
            try
            {
                var dump = new System.Text.StringBuilder();

                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || !c.isActiveAndEnabled) continue;
                    if (c.cullingMask == 0) continue;

                    // 有 targetTexture 的相機**不**動 cullingMask —— VRGIN 就是用那種
                    // 相機把桌面介面畫進一張貼圖再貼到浮空板子上，改了介面會整塊變黑。
                    // 但後製還是要關：頭顯的眼睛相機正是這一種，第一版把它整台跳過，
                    // 結果「找到 0 個特效」，偏紅泛光當然沒好。
                    bool soloThis = c.targetTexture == null;

                    var st = new CamState
                    {
                        cam = soloThis ? c : null,
                        mask = c.cullingMask,
                        clear = c.clearFlags,
                        back = c.backgroundColor,
                    };

                    int here = 0;
                    if (KillPostFx)
                    {
                        foreach (MonoBehaviour mb in c.GetComponents<MonoBehaviour>())
                        {
                            if (!IsImageEffect(mb)) continue;
                            if (st.fx == null)
                                st.fx = new System.Collections.Generic.List<MonoBehaviour>();
                            st.fx.Add(mb);
                            mb.enabled = false;
                            killedFx++;
                            here++;
                            dump.Append("　　").Append(c.name).Append(" → ")
                                .Append(mb.GetType().Name).Append('\n');
                        }
                    }

                    if (soloThis || here > 0) soloed.Add(st);
                    if (soloThis) soloedCams++;

                    if (soloThis)
                    {
                        c.cullingMask = 1 << layer;
                        c.clearFlags = CameraClearFlags.SolidColor;
                        c.backgroundColor = Color.black;
                    }
                }

                // 數量**一律**印出來，0 也要印。上一版只有大於 0 才印，
                // 結果「沒印」到底是沒開這個功能、還是開了但一個都沒找到，分不出來。
                Debug.Log("[VrScreen] 獨佔模式開啟：" + soloedCams
                          + " 台相機只畫第 " + layer + " 層　｜ 後製特效 "
                          + (KillPostFx ? "關掉 " + killedFx + " 個" : "（功能關閉）"));
                if (dump.Length > 0) Debug.Log("[VrScreen] 關掉的後製特效：\n" + dump);
                else if (KillPostFx) Debug.Log("[VrScreen] " + CameraDump());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrScreen] 獨佔模式開啟失敗：" + e.Message);
                ExitSolo();
            }
        }

        /// <summary>
        /// 一個都沒抓到的時候，把場上所有相機和它們身上的元件列出來。
        ///
        /// 沒有這份清單就只能猜「特效到底掛在哪」—— 上一輪就是這樣浪費了一輪。
        /// </summary>
        static string CameraDump()
        {
            var sb = new System.Text.StringBuilder("一個特效都沒抓到。場上的相機和元件：\n");
            try
            {
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null) continue;
                    sb.Append("　　").Append(c.name)
                      .Append(c.isActiveAndEnabled ? "" : "（停用）")
                      .Append("　targetTexture=").Append(c.targetTexture == null ? "無" : "有")
                      .Append("　depth=").Append(c.depth)
                      .Append("　元件：");
                    foreach (MonoBehaviour mb in c.GetComponents<MonoBehaviour>())
                        if (mb != null) sb.Append(mb.GetType().Name)
                                          .Append(mb.enabled ? " " : "(off) ");
                    sb.Append('\n');
                }
            }
            catch (Exception e) { sb.Append("（列舉中斷：").Append(e.Message).Append("）"); }
            return sb.ToString();
        }

        public static void ExitSolo()
        {
            if (soloed.Count == 0) return;
            foreach (CamState s in soloed)
            {
                try
                {
                    // 特效先還原：相機可能已經被銷毀，但特效元件是獨立物件，
                    // 掛在同一個 GameObject 上，各自判斷 null 比較穩。
                    if (s.fx != null)
                        foreach (MonoBehaviour mb in s.fx)
                            if (mb != null) mb.enabled = true;

                    // cam 是 null 表示這一筆只記了特效（有 targetTexture 的相機
                    // 我們不動它的 cullingMask），特效還原完就沒事了。
                    if (s.cam == null) continue;
                    s.cam.cullingMask = s.mask;
                    s.cam.clearFlags = s.clear;
                    s.cam.backgroundColor = s.back;
                }
                catch { }
            }
            soloed.Clear();
            Debug.Log("[VrScreen] 獨佔模式關閉，相機已還原");
        }

        // ---------------------------------------------------------- 建立

        /// <summary>
        /// 獨佔模式：播過場的時候，除了影片什麼都不畫。
        ///
        /// 為什麼這樣做比「把黑底放大」對
        /// ------------------------------
        /// 黑底是一塊實體板子，還是要跟場景比誰近 —— 地圖只要有一面牆卡在中間就穿幫，
        /// 這跟黑底多大沒有關係。你說得對，那不是黑底的問題。
        ///
        /// 真正乾淨的做法是**不要畫**：把相機的 cullingMask 改成只剩影片那一層，
        /// 清除色設成黑色。這樣地圖、角色、特效通通不進渲染，畫面就是純黑加影片。
        ///
        /// 為什麼不去關場景物件本身
        /// ------------------------
        /// 那會動到你的場景 —— 要逐一記住誰本來是開的、過場中途當掉就回不來，
        /// 而且 Timeline 還可能在過場期間繼續改那些物件的顯示狀態，兩邊會打架。
        /// cullingMask 只影響「這台相機畫不畫」，一個整數存起來就能還原，
        /// 場景資料完全沒被碰到。
        /// </summary>
        public static bool Solo = true;

        /// <summary>
        /// 挑一個真的畫得出來的著色器。
        ///
        /// **`Shader.Find` 回傳非 null 不代表它能用。** 這是這次踩到的坑：
        /// 實測 `UI/Default` 找得到、材質也建得起來、Unity 連 `isVisible` 都回報 true，
        /// 但畫面上什麼都沒有 —— 因為遊戲打包時把那個著色器的變體剔掉了，
        /// 只剩下名字。從外面完全看不出差別。
        ///
        /// 所以這裡多兩道關卡：
        ///   1. 檢查 `isSupported`（變體被剔掉或硬體不支援時會是 false）
        ///   2. 候選名單放寬，優先放「遊戲自己一定在用」的那幾個 ——
        ///      Sprites/Default 和 Particles 系列在 KK 的 UI 與特效裡到處都是，
        ///      比 UI/Default 更可能完整留在包裡。
        /// </summary>
        static readonly string[] ShaderCandidates =
        {
            "Sprites/Default",
            "Particles/Alpha Blended",
            "Particles/Alpha Blended Premultiply",
            "Unlit/Transparent",
            "UI/Default",
            "Unlit/Texture",
            "Mobile/Particles/Alpha Blended",
            "Legacy Shaders/Transparent/Diffuse",
        };

        /// <summary>給其他模組共用：挑一個真的畫得出來的著色器。</summary>
        public static Shader PickShader(out string used) { return FindShader(out used); }

        static Shader FindShader(out string used)
        {
            foreach (string n in ShaderCandidates)
            {
                Shader s = Try(n);
                if (s != null) { used = n; return s; }
            }

            // 全部都不行的話，退而求其次：從遊戲**已經載入**的著色器裡找一個
            // 名字看起來像無光照／透明的。這些一定是包在遊戲裡而且正在用的，
            // 不會有「只剩名字」的問題。
            try
            {
                foreach (Shader s in Resources.FindObjectsOfTypeAll<Shader>())
                {
                    if (s == null || !s.isSupported) continue;
                    string n = s.name;
                    if (n.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    used = n + Lang.T("（從場上找到的）");
                    return s;
                }
            }
            catch { }

            used = Lang.T("（找不到可用的著色器）");
            return null;
        }

        static Shader Try(string n)
        {
            try
            {
                Shader s = Shader.Find(n);
                // isSupported 是關鍵的那一道 —— 見上面的說明
                if (s != null && s.isSupported) return s;
            }
            catch { }
            return null;
        }

        static Mesh Quad(bool flipY)
        {
            var m = new Mesh();
            m.vertices = new[] {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f),
            };
            m.uv = flipY
                ? new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) }
                : new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            // **一定要給頂點色。**
            // UI/Default 的片段著色器是 tex2D(_MainTex, uv) * IN.color，
            // 而 IN.color = 頂點色 × _Color。網格沒有 color 通道的時候，
            // 那個頂點串流是未繫結的，拿到的值不保證是白色 —— 拿到 0 就整塊全黑／全透明，
            // 看起來就跟「板子根本沒畫出來」一模一樣，而且從 Status 完全看不出差別。
            // 第一版漏了這個。
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.RecalculateNormals();
            return m;
        }

        GameObject MakePlane(string name, Shader sh, int queue, out Renderer r, out Material mat, bool flipY)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.mesh = Quad(flipY);
            r = go.AddComponent<MeshRenderer>();
            mat = new Material(sh);
            mat.renderQueue = queue;
            try
            {
                // 兩個名字都設：UI/Default 看 unity_GUIZTestMode（平常由 Canvas 設定），
                // 其他有暴露 _ZTest 的著色器看 _ZTest。設成 Always 就不會被場景擋住。
                // 著色器沒有這個屬性的話 SetInt 不會報錯、也不會有效果 ——
                // 所以這只是「設得到就賺到」，真正的保險是把板子放得夠近（見擺位那一段）。
                int always = (int)UnityEngine.Rendering.CompareFunction.Always;
                mat.SetInt("unity_GUIZTestMode", always);
                mat.SetInt("_ZTest", always);
                mat.SetInt("_ZWrite", 0);
            }
            catch { }
            r.material = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        bool Build(bool flipY)
        {
            string used;
            Shader sh = FindShader(out used);
            ShaderName = used;
            if (sh == null) { Status = "找不到可用的著色器，VR 畫面做不出來"; return false; }

            root = new GameObject("CutScene VR Screen");
            UnityEngine.Object.DontDestroyOnLoad(root);
            backGo = MakePlane("back", sh, 4000, out backR, out backM, false);
            videoGo = MakePlane("video", sh, 4001, out videoR, out videoM, flipY);
            return true;
        }

        // ---------------------------------------------------------- 每幀

        /// <summary>
        /// mode 0 = 自動（偵測到 VR 才用）、1 = 一律使用、2 = 一律不用。
        /// 回傳值沒有意義，狀態看 Active / Status。
        /// </summary>
        public void Tick(bool visible, float alpha, Texture tex, float aspect,
                         int mode, float dist, float width, bool follow, bool flipY)
        {
            bool want = mode == 1 || (mode == 0 && VrRunning());
            Active = want;
            if (!want || !visible || tex == null)
            {
                // 沒在播就一定要把相機還原 —— 這是整個獨佔模式最重要的一行。
                // 漏了它，過場結束之後整個工作室會變成一片黑，而且看不出是誰幹的。
                ExitSolo();
                if (root != null && root.activeSelf) root.SetActive(false);
                placed = false;
                if (!want) Status = mode == 2 ? "已關閉（設定成一律不用）" : "沒偵測到 VR，走桌面";
                return;
            }

            if (root == null && !Build(flipY)) return;

            if (cam == null || !cam.isActiveAndEnabled || Time.realtimeSinceStartup > nextPick)
            {
                cam = Pick();
                nextPick = Time.realtimeSinceStartup + 1f;
            }
            if (cam == null) { Status = "找不到相機"; root.SetActive(false); return; }

            // 獨佔模式要把影片放到一個「場上沒別人用」的層，才能只留它；
            // 不獨佔就沿用相機本來就在畫的第一層。
            int layer = Solo ? SoloLayer() : FirstLayer(cam.cullingMask);
            if (root.layer != layer)
            {
                root.layer = layer;
                backGo.layer = layer;
                videoGo.layer = layer;
            }

            if (Solo) EnterSolo(layer); else ExitSolo();

            if (!root.activeSelf) root.SetActive(true);

            // 擺位：跟頭就每幀更新；不跟頭的話擺一次就不動，
            // **但板子跑到視線外時要重擺**。
            //
            // 為什麼需要這個看門狗：開場過場是在場景載入完的當下自動開始的，
            // 而 KK_VR_CameraSync 會在那前後把 VR 視角絕對對齊到工作室相機
            // （log：「Initial VR view aligned to Studio camera」）。
            // 板子先擺好、視角再跳走 —— 板子就留在原地，人在頭顯裡什麼都看不到，
            // 而且 Status 會顯示一切正常，因為它**確實**畫出來了，只是不在你面前。
            // 這也是「桌面測試看得到、進 VR 就沒有」的原因。
            bool replace = follow || !placed;
            if (!replace && placed)
            {
                Transform ct0 = cam.transform;
                Vector3 to = root.transform.position - ct0.position;
                // 夾角超過 55 度就當作它已經不在視野裡了（單眼水平視野大約 100 度）
                if (to.sqrMagnitude < 1e-6f
                    || Vector3.Angle(ct0.forward, to) > 55f
                    || to.magnitude > Mathf.Max(0.3f, dist) * 3f)
                {
                    replace = true;
                    Debug.Log("[VrScreen] 板子不在視野裡了，重新擺到眼前"
                              + "（視角被重新對齊過，或你轉開了）");
                }
            }
            if (replace)
            {
                Transform ct = cam.transform;
                root.transform.position = ct.position + ct.forward * Mathf.Max(0.3f, dist);
                root.transform.rotation = ct.rotation;
                placed = true;
            }

            float w = Mathf.Max(0.2f, width);
            float h = w / Mathf.Max(0.1f, aspect);
            videoGo.transform.localScale = new Vector3(w, h, 1f);

            // 黑底的用途是把場景蓋掉，讓影片像浮在虛空裡。
            //
            // 為什麼是「放大黑底 + 拉近距離」而不是「關掉深度測試」：
            // 關深度測試要靠著色器支援（UI/Default 的 unity_GUIZTestMode、
            // 或有 _ZTest 屬性的著色器），但我們能用的著色器是**執行期找**出來的，
            // 遊戲打包時剔掉了哪些變體無法預先知道 —— 之前就是栽在
            // 「Shader.Find 找得到但畫不出來」。所以那兩個設定照設（設得到就賺到），
            // 真正靠得住的是幾何：板子放得比場景裡任何東西都近，就沒有東西擋得住。
            // 想要純黑虛空就把距離拉到 1 公尺以內、黑底倍率開大。
            // 黑底固定 24 倍。以前這是面板上的滑桿，但那是「獨佔模式」還沒做出來之前，
            // 只能靠一塊夠大的板子去擋場景的權宜之計 —— 而它本來就擋不住比它更近的東西。
            // 現在正常情況走獨佔模式（相機只畫影片那一層），黑底只是它不可用時的退路，
            // 不需要再調。
            const float bs = 24f;
            backGo.transform.localScale = new Vector3(w * bs, h * bs, 1f);
            backGo.transform.localPosition = new Vector3(0f, 0f, 0.02f);

            if (videoM.mainTexture != tex) videoM.mainTexture = tex;
            videoM.color = new Color(1f, 1f, 1f, alpha);
            backM.color = new Color(0f, 0f, 0f, alpha);

            // Status 要能分辨「沒畫出來」的兩種原因，不然只能猜：
            //   isVisible=False → 被剔除／擺錯位置／層不對（幾何問題）
            //   isVisible=True 但看不到 → 著色器或顏色問題（例如頂點色是 0）
            // 貼圖尺寸一起印，0×0 就是影片還沒 Prepare 好。
            bool vis = false;
            try { vis = videoR != null && videoR.isVisible; } catch { }
            Vector3 p = root.transform.position;
            Status = Lang.T("VR 畫面：") + cam.name + Lang.T("　層 ") + layer + "　" + ShaderName
                     + (follow ? Lang.T("　跟頭") : Lang.T("　固定"))
                     + Lang.T("　可見=") + vis
                     + Lang.T("　貼圖 ") + tex.width + "×" + tex.height
                     + Lang.T("　位置 ") + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1")
                     + Lang.T("　大小 ") + w.ToString("F1") + "×" + h.ToString("F1");

            // 戴著頭顯的時候看不到面板，出了問題只能事後看 log。
            // 之前那一輪就是因為 log 裡**一行 VrScreen 都沒有**，
            // 「有沒有畫」「畫在哪」全都只能猜。兩秒一行不會洗版。
            if (Time.realtimeSinceStartup >= nextLog)
            {
                nextLog = Time.realtimeSinceStartup + 2f;
                Debug.Log("[VrScreen] " + Status);
            }
        }

        float nextLog;

        public void Hide()
        {
            placed = false;
            ExitSolo();
            if (root != null) root.SetActive(false);
        }

        public void Destroy()
        {
            ExitSolo();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            backGo = videoGo = null;
            backR = videoR = null;
            backM = videoM = null;
            placed = false;
        }
    }
}
