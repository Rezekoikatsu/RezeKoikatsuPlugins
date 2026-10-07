using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 讀 VR 手柄。全部用反射，不參照 VRGIN_KKCS.dll。
    ///
    /// 為什麼不加參照
    /// --------------
    /// 跟 CutSceneBridge 同一個理由：加了參照的話，沒裝 VR 的人連這支插件都載不起來
    /// （TypeLoadException），桌面上的功能一起賠掉。全反射的話沒裝就只是靜靜地不作用。
    ///
    /// 這些名字都是用 dnfile 翻過 D:\Koikatu\BepInEx\VRGIN_KKCS.dll 確認的，不是猜的：
    ///   SteamVR_Controller（全域命名空間）  static Input(int) → Device
    ///   SteamVR_Controller.Device           GetPress / GetPressDown / GetAxis / connected / valid
    ///   VRGIN.Core.VR                       static Mode → VRGIN.Modes.ControlMode
    ///   VRGIN.Modes.ControlMode             Left / Right → VRGIN.Controls.Controller
    ///   VRGIN.Controls.Controller           欄位 Tracking → SteamVR_TrackedObject
    ///   SteamVR_TrackedObject               欄位 index（EIndex 列舉）
    ///   Valve.VR.EVRButtonId                k_EButton_ApplicationMenu 等等
    ///
    /// Quest 3 透過 SteamVR 的對應（傳統輸入）
    /// --------------------------------------
    ///   左 X / 右 A      → k_EButton_A          (7)
    ///   左 Y / 右 B      → k_EButton_ApplicationMenu (1)
    ///   握把            → k_EButton_Grip       (2)
    ///   搖桿（推）       → k_EButton_Axis0      (32) 的軸值
    ///   搖桿（按下去）    → k_EButton_Axis0 的按鍵
    ///   扳機            → k_EButton_Axis1      (33)
    ///
    /// 「左 Y = ApplicationMenu」這條是傳統 OpenVR 對應下的通例，但各家 runtime
    /// 有可能不一樣。所以面板上有一個**現在按著哪些鍵**的即時顯示（Diagnose）——
    /// 對不上的話看一眼就知道該綁哪個，不用再重建一次 dll 試。
    /// </summary>
    public static class VrInput
    {
        // OpenVR 的按鍵編號（Valve.VR.EVRButtonId）
        public const int BtnSystem = 0;
        public const int BtnAppMenu = 1;   // 左 Y / 右 B
        public const int BtnGrip = 2;
        public const int BtnA = 7;         // 左 X / 右 A
        public const int BtnStick = 32;    // k_EButton_Axis0：搖桿（軸 + 按下）
        public const int BtnTrigger = 33;  // k_EButton_Axis1

        public static string LastReport = "";

        /// <summary>這一幀兩手都讀得到嗎。UI 拿去顯示狀態。</summary>
        public static bool Ready { get { return leftDev != null || rightDev != null; } }

        // ---------------------------------------------------------- 反射快取

        static bool probed;
        static Type tSteamVrController, tDevice, tButtonEnum;
        static MethodInfo miInput;                       // SteamVR_Controller.Input(int)
        static MethodInfo miGetDeviceIndex;              // SteamVR_Controller.GetDeviceIndex(...)
        static MethodInfo miGetPress, miGetPressDown, miGetAxis;
        static bool pressTakesMask, downTakesMask;   // true = 只有吃 ulong 遮罩的版本
        static PropertyInfo piConnected, piValid;

        static Type tVr, tControlMode, tController;
        static PropertyInfo piMode, piLeft, piRight;
        static FieldInfo fiTracking, fiIndex;

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
                tSteamVrController = Find("SteamVR_Controller");
                if (tSteamVrController != null)
                {
                    miInput = tSteamVrController.GetMethod("Input",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    miGetDeviceIndex = tSteamVrController.GetMethod("GetDeviceIndex",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    tDevice = tSteamVrController.GetNestedType("Device",
                        BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (tDevice == null) tDevice = Find("SteamVR_Controller+Device");

                tButtonEnum = Find("Valve.VR.EVRButtonId");

                if (tDevice != null)
                {
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance;

                    // **這裡有個坑，而且第一版踩了。**
                    // SteamVR_Controller.Device 的 GetPress / GetPressDown / GetPressUp
                    // 各有**兩個**單參數多載：GetPress(ulong buttonMask) 和
                    // GetPress(EVRButtonId buttonId)。（用 dnfile 翻 VRGIN_KKCS.dll 確認，
                    // 這三個名字各出現兩次。）
                    //
                    // 第一版只判斷「參數數量 == 1」就收下，而且沒 break ——
                    // 於是留下來的是迴圈掃到的**最後一個**，可能是吃 ulong 的那個。
                    // 之後每次傳列舉進去都 ArgumentException，而我把例外吃掉回 false ——
                    // 結果就是「所有按鍵和搖桿完全沒反應，而且一行錯誤都沒有」。
                    //
                    // 所以要按**參數型別**挑，不是按數量。吃列舉的優先；
                    // 真的只有吃 ulong 的版本時就退回用位元遮罩（1 << 編號）。
                    miGetPress = PickButtonMethod(tDevice, any, "GetPress", out pressTakesMask);
                    miGetPressDown = PickButtonMethod(tDevice, any, "GetPressDown", out downTakesMask);

                    foreach (MethodInfo m in tDevice.GetMethods(any))
                    {
                        if (m.Name != "GetAxis") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsEnum) { miGetAxis = m; break; }
                        if (ps.Length == 1 && miGetAxis == null) miGetAxis = m;
                    }

                    piConnected = tDevice.GetProperty("connected", any);
                    piValid = tDevice.GetProperty("valid", any);
                }

                tVr = Find("VRGIN.Core.VR");
                if (tVr != null)
                    piMode = tVr.GetProperty("Mode",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                tControlMode = Find("VRGIN.Modes.ControlMode");
                if (tControlMode != null)
                {
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance;
                    piLeft = tControlMode.GetProperty("Left", any);
                    piRight = tControlMode.GetProperty("Right", any);
                }

                tController = Find("VRGIN.Controls.Controller");
                if (tController != null)
                    fiTracking = tController.GetField("Tracking",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                Type tracked = Find("SteamVR_TrackedObject");
                if (tracked != null)
                    fiIndex = tracked.GetField("index",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                LastReport = tSteamVrController == null
                    ? "找不到 SteamVR_Controller —— 這一版 VR 插件不是 VRGIN 系，或還沒載入"
                    : "手柄介面已接上";
            }
            catch (Exception e)
            {
                LastReport = Lang.T("接手柄介面時出錯：") + e.Message;
            }
        }

        /// <summary>
        /// 從同名多載裡挑「吃按鍵列舉」的那一個。
        /// 找不到就退回吃 ulong 的，並回報要改用位元遮罩。
        /// </summary>
        static MethodInfo PickButtonMethod(Type t, BindingFlags flags, string name, out bool takesMask)
        {
            takesMask = false;
            MethodInfo maskVersion = null;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType.IsEnum) return m;          // 要的就是這個
                if (ps[0].ParameterType == typeof(ulong)) maskVersion = m;
            }
            takesMask = maskVersion != null;
            return maskVersion;
        }

        /// <summary>把按鍵編號包成那個多載吃得下的型別。</summary>
        static object ButtonArg(int id, bool asMask)
        {
            if (asMask) return (ulong)1 << id;
            if (tButtonEnum == null) return id;
            try { return Enum.ToObject(tButtonEnum, id); }
            catch { return id; }
        }

        // ---------------------------------------------------------- 找左右手

        static object leftDev, rightDev;
        static float nextResolve;

        /// <summary>
        /// 找出左右手的 Device。
        ///
        /// 每 0.5 秒重找一次而不是找到就一直用：實測 log 裡手柄會斷線重連
        /// （「SteamVR controller reconnected on index 2」），index 會變，
        /// 抓死的話重連之後就整組失效，而且完全沒有徵兆。
        /// </summary>
        static void Resolve()
        {
            Probe();
            if (Time.realtimeSinceStartup < nextResolve) return;
            nextResolve = Time.realtimeSinceStartup + 0.5f;

            leftDev = rightDev = null;
            if (miInput == null) return;

            try
            {
                // VR.Mode 這個屬性**會丟例外**（VRGIN 還沒建好時會丟
                // 「VR Manager has not been created yet!」），而它是透過反射呼叫的，
                // 所以包成 TargetInvocationException 往外丟。
                //
                // 原本它在外層 try 裡面，一丟就把整個 Resolve 打斷 ——
                // 下面的 ByRelation 和 ScanDevices 兩條備援**一次都跑不到**，
                // 兩隻手都是 null，面板顯示「找手柄失敗: Exception has been thrown
                // by the target of an invocation.」，所有手柄功能同時死掉。
                // 實測面板截圖就是這一行。
                //
                // 這個屬性本來就允許失敗，所以它自己要有 try，不能連累別人。
                object mode = null;
                try { if (piMode != null) mode = piMode.GetValue(null, null); }
                catch { }

                if (mode != null)
                {
                    leftDev = DeviceOf(piLeft == null ? null : piLeft.GetValue(mode, null));
                    rightDev = DeviceOf(piRight == null ? null : piRight.GetValue(mode, null));
                }

                if (leftDev != null || rightDev != null)
                {
                    LastReport = Lang.T("走 VRGIN 的 Mode.Left/Right");
                    return;
                }

                // VRGIN 交不出手柄（或交出來的編號是壞的）。
                // 改用 SteamVR 自己的 GetDeviceIndex(Leftmost / Rightmost, Controller) ——
                // 那是 SteamVR 的 ControllerManager 自己在用的方法，
                // 它會依頭顯的座標系判斷哪一支在左邊，不需要 VRGIN 的狀態正確。
                if (ByRelation()) return;

                // 最後才用土法：掃編號。認不出左右，但至少讀得到。
                ScanDevices();

                // 備援：直接掃 SteamVR 的裝置編號。
                //
                // 為什麼需要：載入場景之後 VRGIN 會重建控制模式和手柄
                // （log 裡看得到「controller lifecycle」「reconnected on index 2」）。
                // 重建過程中 VR.Mode 可能是 null、或 Left/Right 還沒接上，
                // 這時候 Mode 那條路會回 null —— **而且是靜悄悄的**，
                // 表現出來就是「載入場景之後搖桿突然沒反應」，沒有任何錯誤訊息。
                // 掃編號不依賴 VRGIN 的狀態，重建期間照樣讀得到。
                ScanDevices();
            }
            catch (Exception e)
            {
                LastReport = Lang.T("找手柄失敗：") + e.Message;
            }
        }

        /// <summary>
        /// 用 SteamVR 的 GetDeviceIndex(Leftmost / Rightmost, Controller) 找左右手。
        ///
        /// 這是 SteamVR 自己的 ControllerManager 在用的做法：它拿頭顯的座標系
        /// 去判斷哪一支在左邊。好處是完全不依賴 VRGIN 的追蹤狀態 ——
        /// 就算 VRGIN 那邊的 index 是壞的（實測就是），這條路照樣對。
        /// </summary>
        static bool ByRelation()
        {
            try
            {
                if (miGetDeviceIndex == null) return false;

                object left = InvokeRelation(1);    // Leftmost
                object right = InvokeRelation(2);   // Rightmost
                if (left == null && right == null) return false;

                leftDev = left;
                rightDev = right;
                LastReport = Lang.T("VRGIN 的編號不可用，改用 SteamVR 的 Leftmost/Rightmost");
                return true;
            }
            catch { return false; }
        }

        static object InvokeRelation(int relation)
        {
            try
            {
                ParameterInfo[] ps = miGetDeviceIndex.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = ps[0].ParameterType.IsEnum
                    ? Enum.ToObject(ps[0].ParameterType, relation) : (object)relation;
                for (int i = 1; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    // 第二個參數是裝置類別，要指定「手柄」（ETrackedDeviceClass.Controller = 2）
                    if (pt.IsEnum && pt.Name == "ETrackedDeviceClass") args[i] = Enum.ToObject(pt, 2);
                    else if (pt == typeof(float)) args[i] = 0f;
                    else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }

                object r = miGetDeviceIndex.Invoke(null, args);
                int idx = Convert.ToInt32(r);
                if (idx <= 0) return null;

                object dev = miInput.Invoke(null, new object[] { idx });
                if (dev == null) return null;
                if (piConnected != null)
                {
                    object c = piConnected.GetValue(dev, null);
                    if (c is bool && !(bool)c) return null;
                }
                return dev;
            }
            catch { return null; }
        }

        /// <summary>
        /// 直接掃 SteamVR 的裝置編號，撿出連著的手柄。
        /// 編號 0 通常是頭顯，所以從 1 開始。
        /// </summary>
        static void ScanDevices()
        {
            object first = null, second = null;
            for (int i = 1; i < 16; i++)   // 從 1 開始：0 是頭顯
            {
                object dev;
                try { dev = miInput.Invoke(null, new object[] { i }); }
                catch { continue; }
                if (dev == null) continue;

                try
                {
                    if (piConnected != null)
                    {
                        object c = piConnected.GetValue(dev, null);
                        if (!(c is bool) || !(bool)c) continue;
                    }
                    if (piValid != null)
                    {
                        object v = piValid.GetValue(dev, null);
                        if (v is bool && !(bool)v) continue;
                    }
                }
                catch { continue; }

                if (first == null) first = dev;
                else if (second == null) { second = dev; break; }
            }

            if (first == null) { LastReport = Lang.T("手柄：一支都沒掃到"); return; }

            // 掃出來的順序不保證左先。以前這裡有個「左右對調」的開關，
            // 但前面兩條路（VRGIN 的 Mode.Left/Right、SteamVR 的 Leftmost/Rightmost）
            // 都是明確指定左右的，會掉到這條純屬例外，留一個開關反而讓人以為要常用。
            leftDev = first;
            rightDev = second;
            LastReport = Lang.T("手柄：VRGIN 沒給，改用掃裝置編號（")
                         + (second == null ? Lang.T("只找到一支") : Lang.T("兩支")) + Lang.T("），左右可能顛倒");
        }

        static object DeviceOf(object controller)
        {
            try
            {
                if (controller == null || fiTracking == null || fiIndex == null) return null;
                // MonoBehaviour 被銷毀之後 C# 參考還在，但 Unity 的 == null 會是 true
                var uo = controller as UnityEngine.Object;
                if (uo == null) return null;

                object tracking = fiTracking.GetValue(controller);
                if (tracking == null) return null;
                object idx = fiIndex.GetValue(tracking);
                if (idx == null) return null;

                int i = Convert.ToInt32(idx);

                // **編號 0 是頭顯，不是手柄。**
                // 這不是理論上的顧慮：實測 log 裡 Ermin 自己的插件就在喊
                //   「VR controller hierarchy is intact but a hand is still offline:
                //     left=left index mismatch 0 != 1; right=right index mismatch 0 != 2」
                // —— 兩隻手的 Tracking.index 都是 0。照收的話我們拿到的是頭顯的
                // Device，它沒有按鍵也沒有搖桿，讀出來永遠是 (0,0) 沒按鍵，
                // 而且完全看不出哪裡錯。所以這裡直接拒收，讓它掉到下面的備援。
                if (i <= 0) return null;

                object dev = miInput.Invoke(null, new object[] { i });
                if (dev == null) return null;

                // 沒連上的手柄回傳的 Device 仍然不是 null，只是所有讀值都是 0 ——
                // 不檢查的話會變成「按了沒反應而且沒有任何錯誤訊息」
                if (piConnected != null)
                {
                    object c = piConnected.GetValue(dev, null);
                    if (c is bool && !(bool)c) return null;
                }
                return dev;
            }
            catch { return null; }
        }

        static object Btn(int id)
        {
            if (tButtonEnum == null) return id;
            try { return Enum.ToObject(tButtonEnum, id); }
            catch { return id; }
        }

        static object Dev(bool left)
        {
            Resolve();
            return left ? leftDev : rightDev;
        }

        // ---------------------------------------------------------- 對外

        public static bool Press(bool left, int button)
        {
            object d = Dev(left);
            if (d == null || miGetPress == null) return false;
            try
            {
                object r = miGetPress.Invoke(d, new object[] { ButtonArg(button, pressTakesMask) });
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        public static bool Down(bool left, int button)
        {
            object d = Dev(left);
            if (d == null || miGetPressDown == null) return false;
            try
            {
                object r = miGetPressDown.Invoke(d, new object[] { ButtonArg(button, downTakesMask) });
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>
        /// 扳機有沒有按住。
        ///
        /// 兩條都看：GetPress(Axis1) 要推到「喀」一聲的死點才算，
        /// 有些 runtime 上 Quest 的扳機根本不回報那個 press。
        /// 所以另外讀扳機的類比值，過半就算 —— 兩條任一成立即可。
        ///
        /// 放在這裡而不是插件本體：綁定（VrBind）和擷取（VrCapture）都要用，
        /// 而「扳機算不算按住」必須三邊同一個定義，不然設定和實際會對不起來。
        /// </summary>
        public static bool TriggerHeld(bool left)
        {
            if (Press(left, BtnTrigger)) return true;
            Vector2 t = Axis(left, BtnTrigger);
            return t.x >= 0.6f;
        }

        public static Vector2 Axis(bool left, int button)
        {
            object d = Dev(left);
            if (d == null || miGetAxis == null) return Vector2.zero;
            try
            {
                object r = miGetAxis.Invoke(d, new object[] { Btn(button) });
                return r is Vector2 ? (Vector2)r : Vector2.zero;
            }
            catch { return Vector2.zero; }
        }

        /// <summary>
        /// 手柄現在的世界座標與朝向（給「介面黏在手上」用）。
        ///
        /// **用的是跟輸入完全同一支裝置**，不是 VRGIN 的物件。
        ///
        /// 為什麼繞這一圈：VRGIN 階層裡那兩個物件確實叫
        ///   VRGIN_Camera (origin)/Left Controller
        ///   VRGIN_Camera (origin)/Right Controller
        /// （log 裡兩個名字都在），但**名字不保證對應到實體的左右手** ——
        /// 同一份 log 裡 Ermin 的插件一直在喊
        ///   「left index mismatch 0 != 1; right index mismatch 0 != 2」
        /// 也就是 VRGIN 這一邊的左右指派本身就是壞的。抓那個名字，
        /// 設左手卻黏到右手上完全說得通，而且左右兩個設定會給出同一個結果。
        ///
        /// 而**輸入**那條路是好的（搖桿和 Y 鍵都正常），它走的是
        /// SteamVR 自己的 Leftmost / Rightmost。所以這裡改成問同一支 Device
        /// 要它的姿勢：只要輸入的左右是對的，黏手的左右就一定跟著對 ——
        /// 兩件事再也不會各自認一套。
        ///
        /// Device.transform 是 SteamVR_Utils.RigidTransform（欄位 pos / rot，
        /// 用 dnfile 確認過），座標在**追蹤空間**，所以要用 VR 原點換算到世界。
        /// </summary>
        public static bool HandPose(bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            try
            {
                object d = Dev(left);
                if (d == null || tDevice == null) return false;

                if (piDevTransform == null)
                    piDevTransform = tDevice.GetProperty("transform",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (piDevTransform == null) return false;

                object rt = piDevTransform.GetValue(d, null);
                if (rt == null) return false;

                Type trt = rt.GetType();
                if (fiRtPos == null) fiRtPos = trt.GetField("pos");
                if (fiRtRot == null) fiRtRot = trt.GetField("rot");
                if (fiRtPos == null || fiRtRot == null) return false;

                var lp = (Vector3)fiRtPos.GetValue(rt);
                var lr = (Quaternion)fiRtRot.GetValue(rt);

                Transform origin = VrLocomotion.Origin();
                if (origin == null) { pos = lp; rot = lr; return true; }

                pos = origin.TransformPoint(lp);
                rot = origin.rotation * lr;
                return true;
            }
            catch { return false; }
        }

        static PropertyInfo piDevTransform;
        static FieldInfo fiRtPos, fiRtRot;

        static readonly int[] DiagButtons = { BtnSystem, BtnAppMenu, BtnGrip, BtnA, BtnStick, BtnTrigger };
        static readonly string[] DiagNames =
        { "System", "AppMenu(左Y/右B)", "Grip", "A(左X/右A)", "搖桿按下", "扳機" };

        /// <summary>
        /// 現在按著哪些鍵、搖桿推到哪。
        ///
        /// 為什麼要有：「左 Y 對應到哪個 OpenVR 按鍵」各家 runtime 不保證一樣，
        /// 而戴著頭顯沒辦法邊看 log 邊試。有這一行的話按一下、脫下頭顯看一眼就知道，
        /// 不用為了確認一個編號重建一次 dll。
        /// </summary>
        public static string Diagnose()
        {
            Resolve();
            var sb = new StringBuilder();
            if (tSteamVrController == null) return LastReport;

            for (int h = 0; h < 2; h++)
            {
                bool left = h == 0;
                object d = left ? leftDev : rightDev;
                sb.Append(left ? Lang.T("左手：") : Lang.T("　　右手："));
                if (d == null) { sb.Append(Lang.T("沒連上")); continue; }

                Vector2 ax = Axis(left, BtnStick);
                sb.Append(Lang.T("搖桿(")).Append(ax.x.ToString("F2")).Append(',')
                  .Append(ax.y.ToString("F2")).Append(')');

                bool any = false;
                for (int i = 0; i < DiagButtons.Length; i++)
                {
                    if (!Press(left, DiagButtons[i])) continue;
                    sb.Append(any ? "+" : Lang.T(" 按著 ")).Append(Lang.T(DiagNames[i]));
                    any = true;
                }
                if (!any) sb.Append(Lang.T(" 沒按鍵"));
            }
            return sb.ToString();
        }
    }
}
