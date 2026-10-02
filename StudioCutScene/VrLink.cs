using System;

namespace StudioCutScene
{
    /// <summary>
    /// F9（Studio VR Tools）和 F7（Studio CutScene）之間的細管子。
    ///
    /// 為什麼是 AppDomain 而不是反射或參照
    /// ----------------------------------
    /// 兩支插件要能各自單獨存在，所以不能互相參照（缺一邊就 TypeLoadException）。
    /// 反射也不好用：這個檔案兩邊各編一份，型別名字一樣但**是兩個不同的型別**，
    /// 用型別名字去找會找到自己那一份。VrSkin 當初就踩過這個。
    ///
    /// AppDomain.SetData/GetData 是整個行程共用的一張表，key 是字串，
    /// 所以只要兩邊講好 key 就通了。規則只有一條：
    /// **只傳 float[]、int、string 這些 mscorlib 的型別**，
    /// 不傳委派、不傳自訂類別 —— 那些會把型別識別的問題再帶回來。
    ///
    /// 三條訊息，都用「計數器變了就是有新的一筆」的方式，不用清除、不會漏：
    ///   1. 指令      F9 → F7   暫停／跳過過場
    ///   2. 存點位    F9 → F7   「把我現在的視角存進這張卡的設定檔」
    ///   3. 套點位    F7 → F9   「切到這一段了，視角換成這個」
    ///
    /// 姿勢一律是 float[7] = 位置 xyz + 四元數 xyzw，origin 的**世界絕對**姿勢。
    /// </summary>
    public static class VrLink
    {
        const string K_POSE = "reze.vr.view.pose";       // float[7]  F9 持續公布：現在的視角
        const string K_SAVE = "reze.vr.view.saveReq";    // int       F9 → F7：存點位
        const string K_GOTO = "reze.vr.view.gotoPose";   // float[7]  F7 → F9：要套用的視角
        const string K_GOTON = "reze.vr.view.gotoReq";   // int
        const string K_CMD = "reze.vr.cmd.name";         // string    F9 → F7：指令內容
        const string K_CMDN = "reze.vr.cmd.id";          // int

        static int seenSave = -1;
        static int seenCmd = -1;

        static int GetInt(string key)
        {
            try { object o = AppDomain.CurrentDomain.GetData(key); return o is int ? (int)o : 0; }
            catch { return 0; }
        }

        static float[] GetPose(string key)
        {
            try
            {
                float[] p = AppDomain.CurrentDomain.GetData(key) as float[];
                return (p != null && p.Length == 7) ? p : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------- F7 這一側

        /// <summary>F9 現在看到的視角。拿不到就是 null（沒裝 F9、或還沒公布）。</summary>
        public static float[] CurrentPose { get { return GetPose(K_POSE); } }

        /// <summary>
        /// 有沒有新的「存點位」請求。第一次呼叫會把目前的計數器吃掉 ——
        /// 不然插件一啟動就會被當成收到了一筆。
        /// </summary>
        public static bool TakeSaveRequest()
        {
            int n = GetInt(K_SAVE);
            if (seenSave < 0) { seenSave = n; return false; }
            if (n == seenSave) return false;
            seenSave = n;
            return true;
        }

        /// <summary>有沒有新的指令。沒有就回 null。</summary>
        public static string TakeCommand()
        {
            int n = GetInt(K_CMDN);
            if (seenCmd < 0) { seenCmd = n; return null; }
            if (n == seenCmd) return null;
            seenCmd = n;
            try { return AppDomain.CurrentDomain.GetData(K_CMD) as string; }
            catch { return null; }
        }

        /// <summary>叫 F9 把視角換成這個。pose 必須是 7 個數字，否則直接忽略。</summary>
        public static void RequestGoto(float[] pose)
        {
            if (pose == null || pose.Length != 7) return;
            try
            {
                AppDomain.CurrentDomain.SetData(K_GOTO, pose);
                AppDomain.CurrentDomain.SetData(K_GOTON, GetInt(K_GOTON) + 1);
            }
            catch { }
        }
    }
}
