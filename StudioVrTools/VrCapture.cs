using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 手柄版的「Press any key」。
    ///
    /// 為什麼是輪詢，不是事件
    /// ----------------------
    /// 桌面版可以讀 Event.current，鍵盤事件是 Unity 送進 OnGUI 的。
    /// 手柄沒有這種東西 —— VrInput 走的是 SteamVR_Controller.Device 的
    /// GetPress / GetAxis，那是**問現在的狀態**，不是事件佇列。
    /// 所以只能每幀掃一遍兩隻手的六顆鍵加搖桿，自己判斷邊緣。
    ///
    /// 怎麼決定使用者「按完了」
    /// ------------------------
    /// 不能一按到就收 —— 使用者要設「握把＋扳機＋Y」時，握把一定比 Y 早按到，
    /// 一按到就收的話收到的是「握把」。所以規則是：
    ///
    ///   1. 只要有東西按著，就持續更新「目前這一組」，並且**只往更豐富的方向記**
    ///      （修飾鍵更多、或從沒有主鍵變成有主鍵）。
    ///   2. 全部放開之後才定案。
    ///
    /// 這樣不管先按握把還是先按 Y，最後記到的都是完整的那一組。
    ///
    /// 主鍵怎麼從修飾鍵裡分出來
    /// ------------------------
    /// 握把和扳機是修飾鍵，其餘是主鍵。全程只按了握把／扳機的話，
    /// 那它自己就是主鍵（「扳機」本身也可以是一個綁定）；兩顆都按而沒有別的，
    /// 就記成「握把＋扳機」。
    /// </summary>
    public static class VrCapture
    {
        /// <summary>正在等使用者按。</summary>
        public static bool Active { get { return active; } }

        /// <summary>正在設定哪一個功能（顯示用）。</summary>
        public static string Label = "";

        /// <summary>目前按到什麼（即時顯示，讓人知道它有在讀）。</summary>
        public static string Preview = "";

        static bool active;
        static VrBind best;
        static float startedAt;
        static bool sawSomething;

        /// <summary>搖桿推到多少算數。跟播放控制那邊同一個門檻的精神。</summary>
        public const float StickThreshold = 0.7f;

        public static void Begin(string label)
        {
            active = true;
            Label = label ?? "";
            Preview = "";
            best = null;
            sawSomething = false;
            startedAt = Time.realtimeSinceStartup;
        }

        public static void Cancel()
        {
            active = false;
            Label = "";
            Preview = "";
            best = null;
        }

        /// <summary>幾秒沒有任何輸入就自己收掉，免得忘了關卡在那裡吃輸入。</summary>
        public const float TimeoutSec = 15f;

        /// <summary>
        /// 每幀呼叫。收完回傳那一組綁定，還沒收完回 null。
        /// </summary>
        public static VrBind Tick()
        {
            if (!active) return null;

            if (!sawSomething && Time.realtimeSinceStartup - startedAt > TimeoutSec)
            {
                Cancel();
                return null;
            }

            VrBind now = Sample();

            if (now != null)
            {
                sawSomething = true;
                if (best == null || Score(now) >= Score(best)) best = now;
                Preview = best.Display();
                return null;                 // 還按著，等放開
            }

            // 全部放開了
            if (best == null) return null;   // 還沒按過任何東西，繼續等

            VrBind done = best;
            active = false;
            Label = "";
            Preview = "";
            best = null;
            return done;
        }

        /// <summary>修飾鍵越多、有主鍵的越「完整」，用來決定要不要蓋掉先前記到的那一組。</summary>
        static int Score(VrBind b)
        {
            int s = 0;
            if (b.Grip) s += 2;
            if (b.Trigger) s += 2;
            if (!b.IsEmpty) s += 1;
            if (b.IsDir || (b.Button != VrInput.BtnGrip && b.Button != VrInput.BtnTrigger)) s += 4;
            return s;
        }

        static readonly int[] MainButtons =
        { VrInput.BtnAppMenu, VrInput.BtnA, VrInput.BtnStick, VrInput.BtnSystem };

        /// <summary>這一幀按著什麼。沒有任何輸入就回 null。</summary>
        static VrBind Sample()
        {
            for (int h = 0; h < 2; h++)
            {
                bool left = h == 0;
                bool grip = VrInput.Press(left, VrInput.BtnGrip);
                bool trig = VrInput.TriggerHeld(left);

                int main = -1;
                for (int i = 0; i < MainButtons.Length; i++)
                    if (VrInput.Press(left, MainButtons[i])) { main = MainButtons[i]; break; }

                int dir = VrBind.DirNone;
                if (main < 0)
                {
                    Vector2 ax = VrInput.Axis(left, VrInput.BtnStick);
                    if (ax.magnitude >= StickThreshold)
                        dir = Mathf.Abs(ax.x) > Mathf.Abs(ax.y)
                              ? (ax.x > 0f ? VrBind.DirRight : VrBind.DirLeft)
                              : (ax.y > 0f ? VrBind.DirUp : VrBind.DirDown);
                }

                if (main < 0 && dir == VrBind.DirNone && !grip && !trig) continue;

                var b = new VrBind { Left = left };
                if (main >= 0)
                {
                    b.Grip = grip; b.Trigger = trig; b.Button = main;
                }
                else if (dir != VrBind.DirNone)
                {
                    b.Grip = grip; b.Trigger = trig; b.Dir = dir;
                }
                else if (grip && trig)
                {
                    // 只按了握把＋扳機 —— 那就是這一組本身
                    b.Grip = true; b.Button = VrInput.BtnTrigger;
                }
                else if (trig) b.Button = VrInput.BtnTrigger;
                else b.Button = VrInput.BtnGrip;

                return b;
            }
            return null;
        }
    }
}
