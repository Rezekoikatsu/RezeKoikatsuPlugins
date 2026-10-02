using System;

namespace StudioVrTools
{
    /// <summary>
    /// F9（Studio VR Tools）這一側的細管子，對面是 F7（Studio CutScene）的同名類別。
    ///
    /// 說明見 F7 那一份。重點只有一句：兩邊各編一份、型別**不共用**，
    /// 所以只能靠 AppDomain 這張共用表上的字串 key 溝通，
    /// 而且只傳 float[]、int、string 這些 mscorlib 的型別。
    ///
    /// key 兩邊必須逐字一致，改一邊就斷了 —— 所以兩份都寫在這個註解底下。
    /// </summary>
    public static class VrLink
    {
        const string K_POSE = "reze.vr.view.pose";       // float[7]  F9 → 持續公布
        const string K_SAVE = "reze.vr.view.saveReq";    // int       F9 → F7
        const string K_GOTO = "reze.vr.view.gotoPose";   // float[7]  F7 → F9
        const string K_GOTON = "reze.vr.view.gotoReq";   // int
        const string K_CMD = "reze.vr.cmd.name";         // string    F9 → F7
        const string K_CMDN = "reze.vr.cmd.id";          // int

        static int seenGoto = -1;

        static int GetInt(string key)
        {
            try { object o = AppDomain.CurrentDomain.GetData(key); return o is int ? (int)o : 0; }
            catch { return 0; }
        }

        /// <summary>公布「我現在的視角」，讓 F7 要存的時候拿得到。</summary>
        public static void PublishPose(float[] pose)
        {
            if (pose == null || pose.Length != 7) return;
            try { AppDomain.CurrentDomain.SetData(K_POSE, pose); }
            catch { }
        }

        /// <summary>請 F7 把目前的視角寫進這張卡的設定檔。</summary>
        public static void RequestSave()
        {
            try { AppDomain.CurrentDomain.SetData(K_SAVE, GetInt(K_SAVE) + 1); }
            catch { }
        }

        /// <summary>送一個指令給 F7。目前用到的："toggle-pause"、"skip-cut"。</summary>
        public static void Send(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            try
            {
                AppDomain.CurrentDomain.SetData(K_CMD, cmd);
                AppDomain.CurrentDomain.SetData(K_CMDN, GetInt(K_CMDN) + 1);
            }
            catch { }
        }

        /// <summary>
        /// F7 有沒有叫我們換視角。沒有就回 null。
        /// 第一次呼叫會把目前的計數器吃掉，免得剛啟動就被當成收到一筆。
        /// </summary>
        public static float[] TakeGoto()
        {
            int n = GetInt(K_GOTON);
            if (seenGoto < 0) { seenGoto = n; return null; }
            if (n == seenGoto) return null;
            seenGoto = n;
            try
            {
                float[] p = AppDomain.CurrentDomain.GetData(K_GOTO) as float[];
                return (p != null && p.Length == 7) ? p : null;
            }
            catch { return null; }
        }
    }
}
