using System.Text;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 一個手柄按鍵綁定：哪一隻手、要按著哪些修飾鍵、主鍵是什麼。
    ///
    /// 為什麼要有這個型別
    /// ------------------
    /// 以前每一個功能各自存兩個 int（哪隻手、哪個鍵），修飾鍵則是寫死在程式裡
    /// （「記住視角一定要按著扳機」「播放控制一定是握把＋扳機」）。
    /// 於是一加新功能就得手工處理一次衝突 —— 「扳機＋Y＝回到記住的視角」和
    /// 「握把＋扳機時 Y＝暫停」就撞過，只能在程式裡塞 `!(comboActive && …)` 這種補丁。
    ///
    /// 把修飾鍵變成綁定的一部分之後，比對規則只剩一條、而且是硬的：
    /// **修飾鍵要剛好一樣**。沒寫握把的綁定，握把按著時就不算數。
    /// 這跟桌面版 BepInEx 的 KeyboardShortcut 是同一套規矩（Ctrl+R 不會被 Shift+Ctrl+R 觸發），
    /// 所有「一顆鍵做兩件事」的衝突就自己消失了，不必再一個一個擋。
    ///
    /// 存進 .cfg 的樣子是人看得懂的字串，手改也行：
    ///     L:Stick                  左手搖桿按下
    ///     L:Trigger+A              左手 扳機＋X
    ///     L:Grip+Trigger+AppMenu   左手 握把＋扳機＋Y
    ///     L:Grip+Trigger+Stick&gt;Right   左手 握把＋扳機＋搖桿推右
    ///     -                        沒有綁
    ///
    /// 刻意用字串而不是自訂型別＋TomlTypeConverter：ConfigEntry&lt;自訂型別&gt; 要先註冊
    /// 轉換器，註冊失敗是在載入時丟例外（整支插件掛掉），而字串永遠不會有這個問題。
    /// </summary>
    public sealed class VrBind
    {
        public const int DirNone = 0, DirRight = 1, DirLeft = 2, DirUp = 3, DirDown = 4;

        public bool Left = true;      // true = 左手
        public bool Grip;             // 要按著握把
        public bool Trigger;          // 要按著扳機
        public int Button = -1;       // 主鍵的 OpenVR 編號，-1 = 沒有主鍵
        public int Dir = DirNone;     // 主鍵是搖桿方向時用這個（此時 Button 應為 -1）

        public bool IsEmpty { get { return Button < 0 && Dir == DirNone; } }
        public bool IsDir { get { return Dir != DirNone; } }

        public VrBind Clone()
        {
            return new VrBind { Left = Left, Grip = Grip, Trigger = Trigger,
                                Button = Button, Dir = Dir };
        }

        // ------------------------------------------------------------ 字串

        static string BtnKey(int id)
        {
            if (id == VrInput.BtnAppMenu) return "AppMenu";
            if (id == VrInput.BtnA) return "A";
            if (id == VrInput.BtnStick) return "Stick";
            if (id == VrInput.BtnTrigger) return "Trigger";
            if (id == VrInput.BtnGrip) return "Grip";
            if (id == VrInput.BtnSystem) return "System";
            return id.ToString();
        }

        static int BtnId(string key)
        {
            switch (key)
            {
                case "AppMenu": return VrInput.BtnAppMenu;
                case "A": return VrInput.BtnA;
                case "Stick": return VrInput.BtnStick;
                case "Trigger": return VrInput.BtnTrigger;
                case "Grip": return VrInput.BtnGrip;
                case "System": return VrInput.BtnSystem;
            }
            int n;
            return int.TryParse(key, out n) ? n : -1;
        }

        static string DirKey(int d)
        {
            if (d == DirRight) return "Right";
            if (d == DirLeft) return "Left";
            if (d == DirUp) return "Up";
            if (d == DirDown) return "Down";
            return "";
        }

        static int DirId(string k)
        {
            switch (k)
            {
                case "Right": return DirRight;
                case "Left": return DirLeft;
                case "Up": return DirUp;
                case "Down": return DirDown;
            }
            return DirNone;
        }

        public override string ToString()
        {
            if (IsEmpty) return "-";
            var sb = new StringBuilder();
            sb.Append(Left ? "L:" : "R:");
            if (Grip) sb.Append("Grip+");
            if (Trigger) sb.Append("Trigger+");
            if (IsDir) sb.Append("Stick>").Append(DirKey(Dir));
            else sb.Append(BtnKey(Button));
            return sb.ToString();
        }

        public static VrBind Parse(string s)
        {
            var b = new VrBind();
            if (string.IsNullOrEmpty(s)) return b;
            s = s.Trim();
            if (s == "-" || s.Length == 0) return b;

            int c = s.IndexOf(':');
            if (c > 0)
            {
                b.Left = s.Substring(0, c).Trim().ToUpperInvariant() != "R";
                s = s.Substring(c + 1);
            }

            string[] parts = s.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                bool last = i == parts.Length - 1;

                // 修飾鍵只有在「後面還有東西」時才算修飾鍵。
                // 最後一段一律是主鍵 —— 不然 "L:Trigger" 會被讀成「按著扳機但沒有主鍵」。
                if (!last && p == "Grip") { b.Grip = true; continue; }
                if (!last && p == "Trigger") { b.Trigger = true; continue; }

                if (p.StartsWith("Stick>"))
                {
                    b.Dir = DirId(p.Substring(6).Trim());
                    b.Button = -1;
                    continue;
                }
                b.Button = BtnId(p);
                b.Dir = DirNone;
            }
            return b;
        }

        // ------------------------------------------------------------ 顯示

        static string BtnName(int id)
        {
            if (id == VrInput.BtnAppMenu) return Lang.T("Y/B");
            if (id == VrInput.BtnA) return Lang.T("X/A");
            if (id == VrInput.BtnStick) return Lang.T("搖桿按下");
            if (id == VrInput.BtnTrigger) return Lang.T("扳機");
            if (id == VrInput.BtnGrip) return Lang.T("握把");
            if (id == VrInput.BtnSystem) return Lang.T("System");
            return "#" + id;
        }

        static string DirName(int d)
        {
            if (d == DirRight) return Lang.T("搖桿右");
            if (d == DirLeft) return Lang.T("搖桿左");
            if (d == DirUp) return Lang.T("搖桿上");
            if (d == DirDown) return Lang.T("搖桿下");
            return "";
        }

        /// <summary>面板上顯示的那一行，例如「左手　握把＋扳機＋Y/B」。</summary>
        public string Display()
        {
            if (IsEmpty) return Lang.T("（未設定）");
            var sb = new StringBuilder();
            sb.Append(Left ? Lang.T("左手　") : Lang.T("右手　"));
            if (Grip) sb.Append(Lang.T("握把＋"));
            if (Trigger) sb.Append(Lang.T("扳機＋"));
            sb.Append(IsDir ? DirName(Dir) : BtnName(Button));
            return sb.ToString();
        }

        // ------------------------------------------------------------ 比對

        /// <summary>
        /// 修飾鍵狀態剛好符合嗎。
        ///
        /// 「剛好」是重點：沒寫握把的綁定，握把按著時就**不算數**。
        /// 少了這一條，「搖桿按下＝回到相機視角」在握把＋扳機的遙控器模式裡會一起觸發，
        /// 於是快轉的同時視角被重置 —— 以前就是靠一堆 `!(comboActive && …)` 在擋。
        ///
        /// 主鍵本身是握把或扳機時，那一顆不再當成修飾鍵檢查（不然永遠不成立）。
        /// </summary>
        public bool ModsOk()
        {
            bool mainIsGrip = !IsDir && Button == VrInput.BtnGrip;
            bool mainIsTrig = !IsDir && Button == VrInput.BtnTrigger;

            if (!mainIsGrip && VrInput.Press(Left, VrInput.BtnGrip) != Grip) return false;
            if (!mainIsTrig && VrInput.TriggerHeld(Left) != Trigger) return false;
            return true;
        }

        /// <summary>整個綁定現在按著（主鍵是按鍵時才有意義）。</summary>
        public bool Held()
        {
            if (IsEmpty || IsDir) return false;
            if (!ModsOk()) return false;
            if (Button == VrInput.BtnTrigger) return VrInput.TriggerHeld(Left);
            return VrInput.Press(Left, Button);
        }

        /// <summary>
        /// 這一幀剛按下。
        ///
        /// 不用 VrInput.Down（GetPressDown）——修飾鍵和主鍵誰先按下不一定，
        /// 先按主鍵再按修飾鍵的話 GetPressDown 那一幀 ModsOk 還不成立，就漏掉了。
        /// 改成自己記上一幀的 Held()，順序怎麼按都抓得到。
        /// </summary>
        public bool Down()
        {
            bool now = Held();
            bool fire = now && !wasHeld;
            wasHeld = now;
            return fire;
        }

        bool wasHeld;

        /// <summary>搖桿方向綁定：這一幀推到位了嗎（含修飾鍵檢查）。</summary>
        public bool DirActive(float threshold)
        {
            if (!IsDir || !ModsOk()) return false;
            Vector2 ax = VrInput.Axis(Left, VrInput.BtnStick);
            if (ax.magnitude < threshold) return false;
            int d = Mathf.Abs(ax.x) > Mathf.Abs(ax.y)
                    ? (ax.x > 0f ? DirRight : DirLeft)
                    : (ax.y > 0f ? DirUp : DirDown);
            return d == Dir;
        }
    }
}
