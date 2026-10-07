using System;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 用搖桿在場景裡移動 / 轉向。
    ///
    /// 動的是什麼
    /// ----------
    /// VRGIN 的相機階層是 VROrigin → …… → VRCamera（頭）。頭的位置是頭顯給的，
    /// 程式不該去動；要移動使用者就是移動 **Origin**。
    ///
    /// 這件事跟 KK_VR_CameraSync 是相容的，而且是它自己講明的：
    /// 它把工作室相機的**逐幀差值**套到 origin 上，並且「保留目前的 origin 偏移，
    /// 讓實體頭部移動和使用者移動都相對於運鏡後的相機」。也就是說我們在這裡加的位移
    /// 它會一路帶著走 —— 運鏡進行中照樣可以自己走動。
    /// 想回到相機原本的視角就按重置鍵（CameraSyncBridge.Realign）。
    ///
    /// 旋轉為什麼要繞著「頭」轉
    /// ------------------------
    /// 直接轉 Origin 的話，支點在 Origin 的原點而不是你眼睛所在的位置，
    /// 轉起來會像被綁在旋轉木馬上甩出去 —— 位置整個跑掉，而且很容易暈。
    /// 繞頭的世界座標轉，感覺才是「我站在原地轉身」。
    ///
    /// 上下轉（pitch）本來就是 VR 暈眩的主要來源之一，所以預設速度壓得比水平慢，
    /// 而且可以單獨關掉。
    /// </summary>
    public static class VrLocomotion
    {
        public static string LastReport = "";

        /// <summary>最近一次平移用到的數字。查「暫停時速度不一樣」用的。</summary>
        public static string LastMove = "";

        static Type tVr, tVrCamera;
        static PropertyInfo piCamera, piOrigin, piHead;
        static bool probed;

        // 一律走 ReflectUtil.Find —— 直接用 Assembly.GetTypes() 會把
        // VRGIN_KKCS 這種「部分型別載不起來」的組件整個跳過，
        // 那正是先前搖桿／介面全部靜悄悄失效的原因。詳見 ReflectUtil。
        static Type Find(string fullName) { return ReflectUtil.Find(fullName); }

        static void Probe()
        {
            if (probed) return;
            probed = true;
            try
            {
                tVr = Find("VRGIN.Core.VR");
                if (tVr != null)
                    piCamera = tVr.GetProperty("Camera",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                tVrCamera = Find("VRGIN.Core.VRCamera");
                if (tVrCamera != null)
                {
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance;
                    piOrigin = tVrCamera.GetProperty("Origin", any);
                    piHead = tVrCamera.GetProperty("Head", any);
                }
            }
            catch (Exception e) { LastReport = Lang.T("接 VRGIN 相機失敗：") + e.Message; }
        }

        /// <summary>
        /// VRGIN 真的跑起來了沒。
        ///
        /// **這是必要條件，不是保險。**
        ///
        /// VRGIN 的 `VR.Camera` 是「一讀就會生出來」的惰性單例：桌面模式下去讀它，
        /// VRGIN 會當場 new 一個 VRGIN_Camera 物件出來，而那個物件的 OnUpdate
        /// 每幀都會去拿 VRManager.Instance —— 沒有 VR 的情況下就是每幀丟一次
        ///
        ///     System.InvalidOperationException: VR Manager has not been created yet!
        ///
        /// 主控台會被洗爆，而且停不下來：物件已經生出來了，只能重開遊戲。
        ///
        /// 所以**任何會碰到 VR.Camera 的路徑，前面都必須先過這一關**。
        /// 以前沒踩到只是運氣好 —— Origin() 只有在你真的按了手柄按鍵、
        /// 或搖桿推過死區時才會被呼叫到，而桌面模式下那些永遠不成立。
        /// 後來「每 0.2 秒公布一次視角給 F7」是無條件跑的，就一頭撞上去了。
        /// </summary>
        static bool VrLive()
        {
            try { return VrScreen.VrRunning(); }
            catch { return false; }
        }

        static Transform AsTransform(object o)
        {
            Transform t = o as Transform;
            if (t != null) return t;
            var go = o as GameObject;
            if (go != null) return go.transform;
            var c = o as Component;
            return c == null ? null : c.transform;
        }

        /// <summary>VR 原點（要移動使用者就是移動這個）。</summary>
        public static Transform Origin()
        {
            if (!VrLive()) return null;      // 見 VrLive()：桌面模式下碰 VR.Camera 會出事
            Probe();
            try
            {
                if (piCamera != null && piOrigin != null)
                {
                    object camObj = piCamera.GetValue(null, null);
                    if (camObj != null)
                    {
                        Transform t = AsTransform(piOrigin.GetValue(camObj, null));
                        if (t != null) return t;
                    }
                }
            }
            catch { }

            // 備援：從我們自己挑到的 VR 相機往上爬到最上層。
            // VRGIN 換了 API 名稱時還能動，只是少了「就是那個 Origin」的保證。
            try
            {
                Transform head = Head();
                if (head != null)
                {
                    Transform t = head;
                    while (t.parent != null) t = t.parent;
                    return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 轉向的軸和支點用哪一個 transform 當「眼睛」。
        ///
        /// 一律用實際那台眼睛相機。滾轉軸必須**就是視線方向**，差一點都會變成斜著轉，
        /// 而 VRGIN 的 VRCamera.Head 屬性不保證跟眼睛相機同一個朝向（中間隔著
        /// SteamVR 的階層）。這裡原本有個「軸取自相機」的開關可以切到 Head，
        /// 但 log 量出來兩者在這台機器上就是同一個 transform，留著只是多一個會選錯的地方。
        /// 相機抓不到時才退回 Head。
        /// </summary>
        static Transform AxisHead()
        {
            Camera c = VrScreen.VrCamera();
            if (c != null) return c.transform;
            return Head();
        }

        /// <summary>頭（眼睛）的 transform。</summary>
        public static Transform Head()
        {
            // 同 Origin()：沒在 VR 裡就不要碰 VR.Camera。
            // 退回掃現有相機那條路 —— 那個只是看，不會生出任何東西。
            if (!VrLive())
            {
                Camera cd = VrScreen.VrCamera();
                return cd == null ? null : cd.transform;
            }
            Probe();
            try
            {
                if (piCamera != null && piHead != null)
                {
                    object camObj = piCamera.GetValue(null, null);
                    if (camObj != null)
                    {
                        Transform t = AsTransform(piHead.GetValue(camObj, null));
                        if (t != null) return t;
                    }
                }
            }
            catch { }

            Camera c = VrScreen.VrCamera();
            return c == null ? null : c.transform;
        }

        // ------------------------------------------------------------
        // 使用者自己移動出去的量，要能精準還原
        //
        // 為什麼不能只叫 CameraSync 的 RequestInitialAlignment
        // --------------------------------------------------
        // 讀過它的原始碼（CameraSyncDriver.cs）之後很清楚：那個旗標會讓它
        // 對齊到 `_initialAlignmentPose` —— **場景載入當下**擷取到的那個相機姿勢，
        // 不是「現在這一刻相機在哪」。而且要 `AlignInitialStudioCamera` 開著、
        // 還要再等兩幀才會生效。所以按下去之後跑到哪裡去，是它的狀態說了算，
        // 這就是你說的「重置好像不是重置到預設點」。
        //
        // 我們要的其實更單純：**把我自己移動出去的量原樣退回去**。
        // 退回去之後剩下的就是 CameraSync 當下把我們放的位置 —— 定義上就是
        // 「綁在相機上的那個視角」，而且不管相機這時候動到哪裡都成立。
        //
        // 怎麼做到精準
        // ------------
        // 把 origin 的姿勢看成兩段合成：origin = B ∘ U
        //   B = 「如果我完全沒動過」的姿勢（CameraSync 在管的那一段）
        //   U = 「我自己加上去的」那一段，記在 B 的座標系裡
        // 我們只知道 origin 和 U，但 B = origin ∘ U⁻¹ 是算得出來的。
        // 每次加一個世界空間的動作 W：
        //   origin' = W ∘ origin，而 B 不變 ⇒ U' = B⁻¹ ∘ W ∘ B ∘ U
        // 還原就是把 origin 設回 B，再把 U 清成單位。
        //
        // 關鍵在於 U 記在 B 的座標系裡：CameraSync 對 origin 做的都是剛體變換，
        // 那種變換會把 B 整個帶著走，而 U 在 B 底下的座標不受影響 ——
        // 所以相機運鏡到天涯海角，這個「退回去」都還是準的。
        // ------------------------------------------------------------

        struct Pose
        {
            public Vector3 p;
            public Quaternion r;
            public static Pose Identity { get { return new Pose { p = Vector3.zero, r = Quaternion.identity }; } }
        }

        static Pose Compose(Pose a, Pose b)
        {
            return new Pose { r = a.r * b.r, p = a.p + a.r * b.p };
        }

        static Pose Inverse(Pose a)
        {
            Quaternion ir = Quaternion.Inverse(a.r);
            return new Pose { r = ir, p = -(ir * a.p) };
        }

        static Pose user = Pose.Identity;

        /// <summary>使用者目前偏離「相機視角」多遠（公尺）。UI 拿去顯示。</summary>
        public static float OffsetDistance
        {
            get
            {
                float d = user.p.magnitude;
                return float.IsNaN(d) || float.IsInfinity(d) ? 0f : d;
            }
        }

        /// <summary>有沒有偏離過。沒偏離的話重置鍵不必做事。</summary>
        public static bool HasOffset
        {
            get { return user.p.sqrMagnitude > 1e-8f || Quaternion.Angle(user.r, Quaternion.identity) > 0.01f; }
        }

        static Pose OriginPose(Transform t)
        {
            return new Pose { p = t.position, r = t.rotation };
        }

        /// <summary>把一個世界空間的剛體動作套到 origin 上，同時記進 U。</summary>
        static void Apply(Transform origin, Pose w)
        {
            if (Bad(w)) return;              // 壞掉的動作不要套，也不要記

            Pose o = OriginPose(origin);
            if (Bad(o)) return;

            Pose b = Compose(o, Inverse(user));
            Pose wLocal = Compose(Inverse(b), Compose(w, b));
            Pose nextUser = Compose(wLocal, user);
            Pose o2 = Compose(w, o);

            // NaN 一旦進到 user 就再也出不去：之後每一幀都拿 NaN 去算，
            // 面板顯示「偏離相機 NaN m」，重置也算不回來 —— 實測 log 就是這樣。
            // 所以在寫回去之前擋一次，發現壞值就把 U 清掉重來，
            // 寧可少記一次位移，也不要讓整個追蹤永久壞掉。
            if (Bad(nextUser) || Bad(o2))
            {
                user = Pose.Identity;
                LastReport = Lang.T("位移計算出現無效值，已把偏移歸零重來");
                UnityEngine.Debug.LogWarning("[VrLocomotion] " + LastReport);
                return;
            }

            user = nextUser;
            origin.rotation = o2.r;
            origin.position = o2.p;
        }

        /// <summary>有沒有 NaN / 無限大。四元數長度異常也算壞。</summary>
        static bool Bad(Pose p)
        {
            if (float.IsNaN(p.p.x) || float.IsNaN(p.p.y) || float.IsNaN(p.p.z)) return true;
            if (float.IsInfinity(p.p.x) || float.IsInfinity(p.p.y) || float.IsInfinity(p.p.z)) return true;
            if (float.IsNaN(p.r.x) || float.IsNaN(p.r.y) || float.IsNaN(p.r.z) || float.IsNaN(p.r.w)) return true;

            float len = p.r.x * p.r.x + p.r.y * p.r.y + p.r.z * p.r.z + p.r.w * p.r.w;
            return len < 0.5f || len > 2f;
        }

        /// <summary>
        /// 回到「綁在相機上」的那個視角：把自己移動出去的量原樣退回去。
        /// 相機正在運鏡也沒關係 —— 退回去之後就是它這一刻放我們的位置。
        /// </summary>
        public static bool ResetToCamera()
        {
            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }
            if (!HasOffset) { LastReport = Lang.T("本來就在相機視角上，沒有東西要退"); return true; }

            float was = user.p.magnitude;
            Pose b = Compose(OriginPose(origin), Inverse(user));
            origin.rotation = b.r;
            origin.position = b.p;
            user = Pose.Identity;
            LastReport = Lang.T("已退回相機視角（原本偏離 ") + was.ToString("F2") + Lang.T(" m）");
            return true;
        }

        /// <summary>
        /// 平移。dt 乘在外面，所以速度是「公尺／秒」，不會因為幀率高就飛出去。
        /// 方向以頭的朝向為準（你面向哪就往哪走），但**壓平到水平面** ——
        /// 低頭看地板的時候不希望往前推就鑽進地底下。
        /// </summary>
        public static bool Move(Vector2 stick, float metersPerSecond, float dt, bool allowVertical)
        {
            Transform origin = Origin();
            Transform head = Head();
            if (origin == null || head == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Vector3 fwd = head.forward;
            Vector3 right = head.right;
            if (!allowVertical)
            {
                fwd.y = 0f; right.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f)
                {
                    // 完全抬頭或完全低頭：forward 壓平之後長度是 0，
                    // normalize 會得到 NaN，整個 origin 會消失（而且不會有任何錯誤）。
                    // 這種姿勢下改用頭頂的方向當前方。
                    fwd = -head.up; fwd.y = 0f;
                }
                fwd = fwd.sqrMagnitude < 1e-6f ? Vector3.forward : fwd.normalized;
                right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            }

            Vector3 delta = (fwd * stick.y + right * stick.x) * metersPerSecond * dt;
            if (delta.sqrMagnitude <= 0f) return false;
            Apply(origin, new Pose { p = delta, r = Quaternion.identity });

            // 「暫停之後移動變快很多」目前還沒有解釋。我們這條路乘的是
            // unscaledDeltaTime，timeScale 歸零不影響它，理論上兩種狀態一樣快。
            // 所以要把實際用到的數字記下來：dt 真的變了，就是幀率／deltaTime 的問題；
            // dt 沒變而人還是覺得快，那就是**另外有東西也在動你**（VRGIN 或 Ermin
            // 自己的搖桿位移），平常被我們蓋過去、暫停時才顯出來。
            LastMove = "dt=" + dt.ToString("F4")
                       + " 速度=" + metersPerSecond.ToString("F2")
                       + " 這一幀移動=" + delta.magnitude.ToString("F4") + "m"
                       + " timeScale=" + Time.timeScale.ToString("F2");
            return true;
        }

        /// <summary>
        /// 上下平移。給「扳機＋搖桿前後」用。
        ///
        /// 刻意沿**世界**的上下，不是頭頂的方向：低著頭想升高的時候，
        /// 沿頭頂方向會變成斜著飄出去，那不是「上下」該有的手感。
        /// </summary>
        /// <param name="amount">-1～1，搖桿前後的量</param>
        public static bool MoveVertical(float amount, float metersPerSecond, float dt)
        {
            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Vector3 delta = Vector3.up * (amount * metersPerSecond * dt);
            if (delta.sqrMagnitude <= 0f) return false;
            Apply(origin, new Pose { p = delta, r = Quaternion.identity });
            return true;
        }

        /// <summary>
        /// 繞頭旋轉。yaw = 水平（左右轉），pitch = 上下。
        /// 用 RotateAround 而不是改 origin.rotation：支點在眼睛，才不會被甩出去。
        /// </summary>
        public static bool Rotate(float yawDeg, float pitchDeg)
        {
            Transform origin = Origin();
            Transform head = AxisHead();
            if (origin == null || head == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Vector3 pivot = head.position;
            bool did = false;

            if (Mathf.Abs(yawDeg) > 0f)
            {
                // 世界的上方向，不是頭的上方向 —— 用頭的上方向的話，
                // 頭一歪水平轉就會變成斜的，地平線跟著歪，非常暈。
                Apply(origin, AroundPivot(pivot, Vector3.up, yawDeg));
                did = true;
            }
            if (Mathf.Abs(pitchDeg) > 0f)
            {
                Vector3 axis = head.right;
                axis.y = 0f;
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.right;
                Apply(origin, AroundPivot(pivot, axis.normalized, pitchDeg));
                did = true;
            }
            return did;
        }

        /// <summary>
        /// 繞 **VR 原點** 水平旋轉 —— 整顆頭跟著繞著那個點轉一圈。
        ///
        /// 跟上面的 Rotate 差在支點：
        ///   Rotate（握把＋搖桿）支點在眼睛 → 人站在原地轉身，位置不變，
        ///     看出去像是左右擺頭。
        ///   這一個支點在原點 → 頭會沿著圓周被帶著走，整個視角繞著場景轉，
        ///     位置也跟著變。
        ///
        /// 兩種都有用，所以不是二選一：轉身用前者，繞著看用後者。
        /// </summary>
        /// <param name="axisMode">
        /// 0 = 世界的上下軸（畫面水平繞圈）
        /// 1 = 視線的左右軸，壓平到水平（整個視角往上翻／往下翻）
        /// 2 = 視線的前後軸，**不壓平**（畫面順時針／逆時針轉）
        /// </param>
        /// <param name="pivotAtHead">
        /// true = 支點在眼睛　false = 支點在 VR 原點。
        ///
        /// 這個差別決定「會不會順便跑位」：
        ///   支點在眼睛 → 純粹轉方向，人不動
        ///   支點在原點 → 人被沿著圓周甩出去，位置跟著變
        ///
        /// 「畫面順時針轉」要的是前者。支點在原點的話，滾轉軸離你有一段距離，
        /// 轉起來就變成「從前方 45 度盪到後方 45 度」那種弧線 —— 那正是你看到的。
        /// </param>
        
        public static bool Orbit(float deg, int axisMode, bool pivotAtHead)
        {
            if (Mathf.Abs(deg) <= 0f) return false;
            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Transform head = AxisHead();
            Vector3 axis = Vector3.up;

            if (axisMode == 1)
            {
                Vector3 a = head != null ? head.right : Vector3.right;
                // 壓平：不壓的話頭一低，「往上翻」會變成斜著翻，地平線是歪的。
                a.y = 0f;
                axis = a.sqrMagnitude > 1e-6f ? a.normalized : Vector3.right;
            }
            else if (axisMode == 2)
            {
                // **不壓平。** 滾轉軸必須就是視線方向，壓平之後抬頭低頭時
                // 軸會偏離視線，轉出來就不是單純的畫面旋轉了。
                Vector3 a = head != null ? head.forward : Vector3.forward;
                axis = a.sqrMagnitude > 1e-6f ? a.normalized : Vector3.forward;
            }

            Vector3 pivot = (pivotAtHead && head != null) ? head.position : origin.position;

            // 繞轉一律走 Apply（user 追蹤）。
            //
            // 當初為了查「扳機＋左右滾轉怪怪的」做過三種機制對照
            // （Apply / RotateAround / origin 自身滾轉），面板上還放過一個三選一。
            // 實測確定 Apply 這條是對的，另外兩個各有問題：RotateAround 不記進 user，
            // 重置鍵退不回來；origin 自身滾轉跟視線無關，頭一轉就不是畫面滾轉了。
            // 對照組留著只會讓人不小心選到壞掉的那個，所以拿掉。
            Apply(origin, AroundPivot(pivot, axis, deg));
            return true;
        }

        // ------------------------------------------------------------ 記住一個視角

        static bool hasSpot;
        static Pose spotWorld;     // origin 的世界姿勢

        /// <summary>有沒有存過視角。</summary>
        public static bool HasSpot { get { return hasSpot; } }

        /// <summary>
        /// 記住現在這個視角 —— 記的是 **origin 的世界絕對姿勢**。
        ///
        /// 這裡原本有「絕對 / 相對相機」兩種模式可選，相對那個已經拿掉了。
        /// 相對模式記的是 user，也就是「這支外掛幫你移動出去的量」，
        /// 而實測 log 裡那個值幾乎永遠是 0.00 m —— 你實際的位移大半不經過我們這條路
        /// （CameraSync 重新對齊會吸收掉，Ermin 那支自己也會動 origin）。
        /// 記 0、回到 0，當然一點感覺都沒有，那就是當初「按了沒反應」的原因。
        /// 一個永遠是壞的選項留在面板上只會害人選到。
        ///
        /// 絕對模式的代價：存完之後工作室相機如果運鏡走了，這個點還是留在世界原處。
        /// 對「我設好的一個機位」來說，那通常正是要的。
        /// </summary>
        public static bool SaveSpot()
        {
            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點，沒有存"); return false; }

            Pose o = OriginPose(origin);
            if (Bad(o)) { LastReport = Lang.T("目前的姿勢是無效值，沒有存"); return false; }

            spotWorld = o;
            hasSpot = true;
            LastReport = string.Format(Lang.T("已記住這個視角（{0},{1},{2}）"), o.p.x.ToString("F1"),
                                       o.p.y.ToString("F1"), o.p.z.ToString("F1"));
            return true;
        }

        /// <summary>回到記住的視角。沒存過就什麼都不做。</summary>
        public static bool GoToSpot()
        {
            if (!hasSpot) { LastReport = Lang.T("還沒記住任何視角"); return false; }

            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Pose now = OriginPose(origin);
            Pose target = spotWorld;
            if (Bad(target)) { LastReport = Lang.T("記起來的視角是無效值，沒有移動"); return false; }

            // user 也要跟著換算，不然「偏離相機」那個數字和重置鍵會跟實際脫節：
            // origin = B ∘ user ⇒ B = now ∘ user⁻¹，換成 target 之後 user' = B⁻¹ ∘ target。
            Pose b = Compose(now, Inverse(user));
            Pose nextUser = Compose(Inverse(b), target);
            if (!Bad(nextUser)) user = nextUser;

            origin.rotation = target.r;
            origin.position = target.p;

            LastReport = Lang.T("已回到記住的視角（這一下移動了 ")
                         + (target.p - now.p).magnitude.ToString("F2") + Lang.T(" m）");
            return true;
        }

        // ---------------------------------------------------- 給 F7 存檔用的姿勢

        /// <summary>
        /// 現在的視角，攤平成 7 個數字（位置 xyz ＋ 四元數 xyzw）。
        /// 拿不到就回 null。
        ///
        /// 為什麼是 float[] 而不是 Pose：這個值要透過 AppDomain 那張共用表傳給 F7，
        /// 而 Pose 是我們自己的型別、F7 那邊編的是另一個同名型別，傳過去認不得。
        /// float[] 來自 mscorlib，兩邊是同一個型別。
        /// </summary>
        public static float[] PoseArray()
        {
            Transform origin = Origin();
            if (origin == null) return null;
            Pose o = OriginPose(origin);
            if (Bad(o)) return null;
            return new float[] { o.p.x, o.p.y, o.p.z, o.r.x, o.r.y, o.r.z, o.r.w };
        }

        /// <summary>
        /// 把視角換成這 7 個數字描述的姿勢。走的路跟 GoToSpot 的絕對模式一樣，
        /// 連 user 的換算都做，否則「偏離相機」那個數字和重置鍵會跟實際脫節。
        /// </summary>
        public static bool ApplyPoseArray(float[] a)
        {
            if (a == null || a.Length != 7) { LastReport = Lang.T("視角資料不完整，沒有套用"); return false; }

            Transform origin = Origin();
            if (origin == null) { LastReport = Lang.T("找不到 VR 原點"); return false; }

            Pose target = new Pose
            {
                p = new Vector3(a[0], a[1], a[2]),
                r = new Quaternion(a[3], a[4], a[5], a[6])
            };
            if (Bad(target)) { LastReport = Lang.T("視角資料是無效值，沒有套用"); return false; }

            Pose now = OriginPose(origin);
            if (Bad(now)) { LastReport = Lang.T("目前的姿勢是無效值，沒有套用"); return false; }

            Pose b = Compose(now, Inverse(user));
            Pose nextUser = Compose(Inverse(b), target);
            if (!Bad(nextUser)) user = nextUser;

            origin.rotation = target.r;
            origin.position = target.p;

            // 存進來的點也當成「記住的視角」，這樣扳機＋Y 立刻就能回到它，
            // 不用等你自己再按一次記住。
            spotWorld = target;
            hasSpot = true;

            LastReport = Lang.T("已套用設定檔裡的視角（移動了 ")
                         + (target.p - now.p).magnitude.ToString("F2") + Lang.T(" m）");
            return true;
        }

        /// <summary>忘掉記住的視角。</summary>
        public static void ClearSpot()
        {
            hasSpot = false;
            LastReport = Lang.T("已清掉記住的視角");
        }

        /// <summary>「繞著世界空間某一點轉」表示成一個剛體變換。</summary>
        static Pose AroundPivot(Vector3 pivot, Vector3 axis, float deg)
        {
            Quaternion q = Quaternion.AngleAxis(deg, axis);
            return new Pose { r = q, p = pivot - q * pivot };
        }
    }
}
