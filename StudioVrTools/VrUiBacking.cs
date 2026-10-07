using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 把 VR 裡的外掛介面從「半透明」變回看得清楚。
    ///
    /// 為什麼會半透明
    /// --------------
    /// VRGIN 不是把 IMGUI 直接畫進眼睛，而是先把整個桌面介面畫進一張 RenderTexture，
    /// 再把那張貼圖貼到一塊浮在空中的板子上（VRGIN.Visuals.GUIQuad）。
    ///
    /// 問題出在那張 RenderTexture 的 **alpha**：畫面上沒有介面的地方 alpha 是 0，
    /// 而 Unity 內建 skin 的視窗／方塊底圖本身 alpha 也不是 1（大約 0.8，邊角更低）。
    /// 板子用的是 alpha 混色，於是「視窗底色」是跟**板子後面的場景**混在一起，
    /// 而不是跟黑色混 —— 場景愈亮愈花，字就愈看不清楚。
    /// 這跟外掛怎麼寫沒有關係，所以每一支外掛都一樣半透明。
    ///
    /// 做法
    /// ----
    /// 不去改 VRGIN 的材質（那要看它用哪一個著色器，而且不同版本不一樣），
    /// 而是在**每一塊 GUIQuad 正後方**貼一塊不透明的黑板子。
    /// 介面於是跟黑色混色，等同於桌面上黑底的效果，字立刻清楚。
    /// 這樣做的好處是它跟「誰畫的介面」無關 —— F6、F7、F9、RuntimeUnityEditor、
    /// 別人的外掛，全部一起受惠。
    ///
    /// 黑板子刻意**不設成 GUIQuad 的子物件**：VRGIN 會自己改那塊板子的縮放和顯示狀態
    /// （UpdateAspect / AlphaConcealer），塞進它的階層裡容易被一起動到或一起關掉。
    /// 改成每幀跟著擺，貴一點點，但不會跟它的邏輯打架。
    ///
    /// GUIQuad / GUIQuadRegistry 這些名字是用 dnfile 翻過實際安裝的
    /// D:\Koikatu\BepInEx\VRGIN_KKCS.dll 確認的，不是猜的。
    ///
    /// 已經放棄的做法（不要再加回來）
    /// ------------------------------
    /// 曾經試過「把同一張介面貼圖疊畫 N 次，用 (1-a)^N 把 alpha 補滿」。
    /// 數學上成立，實測**沒有改善而且更難看**。
    /// 原因後來從 log 查清楚了：介面貼圖是 1920×1080，灰字的筆畫在來源就只有
    /// 一兩個畫素寬，縮進頭顯之後不足一個畫素 —— 疊幾層都不會讓筆畫多出畫素。
    /// 那條路已整組移除。
    /// </summary>
    public static class VrUiBacking
    {
        public static bool Enabled = true;

        /// <summary>
        /// 底板顏色。固定白色。
        ///
        /// 顏色調了好幾輪（黑、白、深藍、可調 RGB）都沒有明顯差別，原因是數學上
        /// 本來就沒差：合成是 src×a + dst×(1−a)，文字和視窗底圖的 alpha 接近時，
        /// 對比只剩 a×|文字−底圖|，**跟 dst 無關**。
        /// 真正有效的是把 a 變成 1，而那要在來源做 —— 見 VrSkin（換掉視窗底圖本身）。
        /// 所以這裡不再提供顏色選項，只留「要不要擋住後面的場景」這個用途。
        /// </summary>
        public static readonly Color Tint = Color.white;

        /// <summary>底板的不透明度。1 = 完全不透，調低就看得到一點後面的場景。</summary>
        public static float Opacity = 1f;

        /// <summary>底板比介面板子大多少。稍微大一點才不會看到邊緣漏光。</summary>
        public static float Margin = 1.04f;

        public static string LastReport = "";

        // ---------------------------------------------------------- 介面黏在手上

        /// <summary>
        /// 把主介面板子貼到手柄上（像手環選單那樣）。
        ///
        /// 為什麼主介面會離得遠：VRGIN 把它當成一塊「螢幕」擺在世界裡，
        /// 位置由它的 GUIMonitor（Distance / Angle 設定）決定，不跟著手。
        /// 手環選單則是 Ermin 自己另外做的 uGUI，本來就掛在手柄底下 ——
        /// 這也是為什麼手環選單不半透明而主介面半透明：兩者根本不是同一套東西。
        ///
        /// 這裡不去改 VRGIN 的設定（那是全域的、而且會被它自己覆寫），
        /// 而是每幀直接把板子的 transform 擺到手柄前面。要關掉隨時放手就好。
        /// </summary>
        public static bool MountToHand;

        /// <summary>
        /// 把主介面整個藏起來（跟 Ermin 那支 VR 插件右手 A 鍵一樣的效果）。
        ///
        /// 做法是關掉 GUIQuad 上 Renderer 的 enabled，不是 SetActive(false)：
        /// VRGIN 自己會管那些物件的啟用狀態，我們把它停用掉會跟它打架，
        /// 而且下一次它重新啟用時我們也不知道。只關 Renderer 是純顯示層的事，
        /// 放回去就是把 enabled 設回 true，記得住也還原得回來。
        /// </summary>
        public static bool HideMain;

        /// <summary>貼在哪一手。</summary>
        public static bool MountLeft = true;

        /// <summary>板子中心離手柄多遠（公尺）。</summary>
        public static float MountDistance = 0.12f;

        /// <summary>整體縮放。手邊的板子要縮小，不然會糊在臉上。</summary>
        public static float MountScale = 0.25f;

        /// <summary>繞手柄的 X 軸傾斜幾度（正值 = 板子往上翻，像看手錶）。</summary>
        public static float MountTilt = 0f;

        /// <summary>往上偏移多少（公尺），避免擋住手本身。</summary>
        public static float MountLift = 0.02f;

        /// <summary>
        /// 板子正反翻轉。**預設是開的。**
        ///
        /// GUIQuad 的正面朝它自己的 +Z 還是 -Z 是 VRGIN 建網格時決定的，從外面看不出來。
        /// 猜錯就會看到背面，畫面左右相反 —— 實測兩種都試過，這一邊才是對的。
        /// 換了 VR 插件版本萬一相反，這一格就切回來，不用重建 dll。
        /// </summary>
        public static bool MountFlip = true;


        static Type tRegistry;
        static PropertyInfo piQuads;
        static bool probed;

        // 一塊 GUIQuad 配一塊底板
        class Backing
        {
            public Component quad;
            public GameObject go;
            public Material mat;
            public Renderer rend;

            // 板子本來多大。掛到手上要縮小，放開要還原成原本的大小，
            // 所以第一次看到它的時候就記起來 —— 不記的話「還原」只能用猜的。
            public Vector3 origScale;
            public bool mounted;
        }

        static readonly System.Collections.Generic.List<Backing> backings =
            new System.Collections.Generic.List<Backing>();

        // 一律走 ReflectUtil.Find —— 直接用 Assembly.GetTypes() 會把
        // VRGIN_KKCS 這種「部分型別載不起來」的組件整個跳過，
        // 那正是先前搖桿／介面全部靜悄悄失效的原因。詳見 ReflectUtil。
        static Type Find(string fullName) { return ReflectUtil.Find(fullName); }

        static void Probe()
        {
            if (probed) return;
            probed = true;
            tRegistry = Find("VRGIN.Visuals.GUIQuadRegistry");
            if (tRegistry != null)
                piQuads = tRegistry.GetProperty("Quads",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            LastReport = piQuads == null
                ? "找不到 VRGIN 的 GUIQuadRegistry（不是 VRGIN 系的 VR 插件？）"
                : "已接上 VRGIN 的介面板子";
        }

        static Mesh quadMesh;

        static Mesh QuadMesh()
        {
            if (quadMesh != null) return quadMesh;
            var m = new Mesh();
            m.vertices = new[] {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            // 沒有頂點色的話，乘上未繫結頂點串流會拿到不保證是白的值 ——
            // VrScreen 第一版就是栽在這裡，整塊變全黑／全透明卻查不出原因。
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.RecalculateNormals();
            quadMesh = m;
            UnityEngine.Object.DontDestroyOnLoad(m);
            return m;
        }

        static Backing Make(Component quad)
        {
            string used;
            Shader sh = VrScreen.PickShader(out used);
            if (sh == null) return null;
            shaderInfo = Lang.T("　著色器 ") + used;

            var go = new GameObject("VrTools UI Backing");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = quad.gameObject.layer;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = QuadMesh();
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(sh);
            mat.color = Tint;
            r.material = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            return new Backing
            {
                quad = quad, go = go, mat = mat, rend = r,
                origScale = quad.transform.localScale,
            };
        }

        static Backing Get(Component quad)
        {
            for (int i = 0; i < backings.Count; i++)
                if (ReferenceEquals(backings[i].quad, quad)) return backings[i];

            Backing b = Make(quad);
            if (b != null) backings.Add(b);
            return b;
        }

        static void Prune()
        {
            for (int i = backings.Count - 1; i >= 0; i--)
            {
                Backing b = backings[i];
                if (b.quad == null || b.go == null)
                {
                    if (b.go != null) UnityEngine.Object.Destroy(b.go);
                    backings.RemoveAt(i);
                }
            }
        }

        public static void HideAll()
        {
            for (int i = 0; i < backings.Count; i++)
            {
                Backing b = backings[i];
                if (b.go != null && b.go.activeSelf) b.go.SetActive(false);
            }
        }

        static int seen;

        // 介面貼圖的實際解析度。要回答「字糊是不是解析度問題」就得先知道這個數字 ——
        // 貼圖只有 1920×1080、而板子在頭顯裡只佔一小塊視野的話，
        // 細字被縮到不足一個畫素，那是**放大再多層也救不回來**的。
        static string texInfo = "";

        // 底板實際用到哪一個著色器。
        // 「畫面被洗成半透明的淡色」最可能的原因就是挑到了混色方式不對的著色器
        // （例如相加型），那從外面完全看不出來 —— VrScreen 就栽過一模一樣的坑。
        // 印出來就不用猜了。
        static string shaderInfo = "";

        /// <summary>
        /// 把一塊板子擺到手柄前方。
        ///
        /// 刻意**不設成手柄的子物件**：VRGIN 會自己管 GUIQuad 的階層
        /// （抓取、重建、切換模式都會動它），塞進手柄底下的話它重建時就掉了，
        /// 而且我們也不好還原。每幀擺位雖然笨，但不會跟它的邏輯打架，
        /// 關掉的瞬間板子就回到它自己的位置。
        /// </summary>
        static void MountOnHand(Backing b)
        {
            Vector3 hp;
            Quaternion hr;
            if (!VrInput.HandPose(MountLeft, out hp, out hr)) return;
            Transform qt = b.quad.transform;

            // 手柄的三個軸。從 Device 的姿勢算，不是從 VRGIN 的物件拿 ——
            // 那邊的左右指派本身就是壞的（見 VrInput.HandPose）。
            Vector3 hFwd = hr * Vector3.forward;
            Vector3 hUp = hr * Vector3.up;
            Vector3 hRight = hr * Vector3.right;

            // 板子要**平行於手柄**，像貼在手腕上的平板，不是插在手柄前端的牌子。
            //
            // 上一版錯在這裡：我把板子的法線設成手柄的指向，
            // 那等於讓板子的平面**垂直**切過手柄 —— 你說的「跟手柄指向完全垂直」。
            //
            // 正確的擺法要指定兩個方向：
            //   法線  = 手背朝外的方向（手柄的 up）→ 舉起手就正對著你
            //   板子的上 = 手柄的指向（forward）    → 文字沿著手柄跑
            // LookRotation(法線, 板子的上) 剛好就是這個意思。
            Quaternion tilt = Quaternion.AngleAxis(MountTilt, hRight);
            Vector3 along = tilt * hFwd;              // 沿著手柄 = 板子的「上」
            Vector3 back = tilt * hUp;                // 手背朝外 = 你看過來的那一側

            // 板子的 +Z 要朝哪，取決於 VRGIN 那塊網格哪一面是正面 —— 從外面看不出來。
            // 猜錯的時候看到的是**背面**，畫面會左右相反（就是你這次看到的）。
            //
            // 注意這裡是**旋轉**不是鏡射：LookRotation(-back, along) 相當於
            // 繞著 along 軸轉 180 度，上下不變、左右回正。
            // 位置的偏移一律用 back，不跟著翻 —— 板子要往手背外側抬，
            // 跟著翻的話會沉進手裡。
            Vector3 face = MountFlip ? -back : back;

            qt.position = hp + along * MountDistance + back * MountLift;
            qt.rotation = Quaternion.LookRotation(face, along);

            // 等比例縮 —— 長寬比是 VRGIN 依畫面解析度算的，改掉字會變形
            qt.localScale = b.origScale * Mathf.Max(0.05f, MountScale);
            b.mounted = true;
        }

        /// <summary>放開：板子的大小還原，位置交還給 VRGIN（它下一幀就會自己擺回去）。</summary>
        static void Unmount(Backing b)
        {
            if (!b.mounted) return;
            b.mounted = false;
            try { if (b.quad != null) b.quad.transform.localScale = b.origScale; }
            catch { }
        }

        // 被我們關掉 Renderer 的那些。只還原自己關過的，不去動別人的狀態。
        static readonly System.Collections.Generic.List<Renderer> hiddenByUs =
            new System.Collections.Generic.List<Renderer>();

        static void HideQuads()
        {
            try
            {
                var en = piQuads.GetValue(null, null) as IEnumerable;
                if (en == null) return;
                foreach (object o in en)
                {
                    var quad = o as Component;
                    if (quad == null) continue;
                    foreach (Renderer r in quad.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || !r.enabled) continue;
                        r.enabled = false;
                        hiddenByUs.Add(r);
                    }
                }
            }
            catch { }
        }

        static void RestoreQuads()
        {
            if (hiddenByUs.Count == 0) return;
            for (int i = 0; i < hiddenByUs.Count; i++)
            {
                Renderer r = hiddenByUs[i];
                if (r != null) r.enabled = true;
            }
            hiddenByUs.Clear();
        }

        /// <summary>每幀叫一次。</summary>
        public static void Tick()
        {
            Probe();
            if (piQuads == null) { if (!Enabled && !MountToHand) HideAll(); return; }

            // 隱藏優先於其他一切：介面都不畫了，底板和黏手都沒有意義
            if (HideMain)
            {
                HideQuads();
                HideAll();
                LastReport = Lang.T("主介面已隱藏（") + hiddenByUs.Count + Lang.T(" 塊）");
                return;
            }
            RestoreQuads();

            // 只開「黏在手上」而沒開黑底時也要跑 —— 兩件事都在這一趟裡做
            if (!Enabled && !MountToHand) { HideAll(); return; }
            if (!Enabled) HideAll();

            try
            {
                object list = piQuads.GetValue(null, null);
                var en = list as IEnumerable;
                if (en == null) { LastReport = Lang.T("GUIQuadRegistry.Quads 不是可列舉的"); return; }

                Prune();
                Camera head = VrScreen.VrCamera();
                seen = 0;

                foreach (object o in en)
                {
                    var quad = o as Component;
                    if (quad == null) continue;
                    // 介面板子沒顯示的時候底板也要跟著收起來，
                    // 否則關掉介面之後會留下一塊黑板浮在空中
                    if (!quad.gameObject.activeInHierarchy) continue;

                    Renderer qr = quad.GetComponentInChildren<Renderer>();
                    if (qr == null || !qr.enabled) continue;

                    seen++;
                    Transform qt = quad.transform;

                    Backing b = Get(quad);
                    if (b == null) continue;

                    // 先把板子擺到手上（如果有開），底板才會跟著貼對位置
                    if (MountToHand) MountOnHand(b); else Unmount(b);

                    // 要貼在**背面**。哪一面是背面取決於使用者在哪 ——
                    // 板子可以被抓著轉，寫死正負號會有一半機率擋在介面前面，
                    // 那比半透明還糟。所以每幀用頭的位置判斷。
                    Vector3 away = qt.forward;
                    if (head != null && Vector3.Dot(qt.position - head.transform.position, away) < 0f)
                        away = -away;

                    Vector3 scale = qt.lossyScale;
                    float thick = Mathf.Max(0.002f, Mathf.Max(scale.x, scale.y) * 0.004f);

                    b.go.transform.position = qt.position + away * thick;
                    b.go.transform.rotation = qt.rotation;
                    b.go.transform.localScale = new Vector3(
                        Mathf.Abs(scale.x) * Margin, Mathf.Abs(scale.y) * Margin, 1f);
                    b.go.layer = quad.gameObject.layer;

                    if (!Enabled) continue;      // 只黏手、不加黑底

                    b.mat.color = new Color(Tint.r, Tint.g, Tint.b, Mathf.Clamp01(Opacity));

                    // 底板一定要比 VRGIN 那塊介面板**先**畫，
                    // 不然介面混色的對象還是後面的場景，補了也是白補。
                    int baseQ = 3000;
                    try { baseQ = qr.sharedMaterial.renderQueue; }
                    catch { }
                    b.mat.renderQueue = Mathf.Max(1, baseQ - 1);

                    if (!b.go.activeSelf) b.go.SetActive(true);
                }

                // 這一輪沒被碰到的（介面關掉了）收起來
                for (int i = 0; i < backings.Count; i++)
                {
                    Backing b = backings[i];
                    if (b.quad == null || !b.quad.gameObject.activeInHierarchy)
                        if (b.go != null && b.go.activeSelf) b.go.SetActive(false);
                }

                LastReport = Lang.T("介面板子 ") + seen + Lang.T(" 塊")
                             + (Enabled ? Lang.T("，底板 ") + Tint.r.ToString("F2") : Lang.T("，只黏手不加底"))
                             + texInfo + shaderInfo;
            }
            catch (Exception e)
            {
                LastReport = Lang.T("加黑底失敗：") + e.Message;
            }
        }
    }
}
