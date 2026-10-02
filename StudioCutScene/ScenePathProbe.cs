using System;
using System.IO;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 想辦法問出「現在載入的是哪張場景卡」，但**絕不碰遊戲本身的程式碼**。
    ///
    /// 歷史：第一版用反射掃描所有組件的所有型別，讀每個像路徑的靜態成員。
    /// 那是個危險的設計 —— 讀屬性等於執行別人的 getter，讀欄位可能觸發型別初始化，
    /// 在一個掛了兩百個外掛的 Unity 5.6 行程裡，這足以引發原生層的存取違規。
    /// 就算某次當機事後證明不是它造成的，這種「對整個行程亂按按鈕」的做法本身就不該留著。
    ///
    /// 現在只做一件安全的事：讀 BepInEx/config 底下那些外掛自己寫的 .data 檔。
    /// 有些「上一張／下一張場景」的外掛會把目前的場景路徑記在那裡。
    /// 純檔案讀取，不執行任何遊戲程式碼，最壞情況就是找不到。
    ///
    /// 呼叫端一定要有「問不到也能運作」的退路（比對時間軸總長度）。
    /// </summary>
    public static class ScenePathProbe
    {
        public static string Source = "（還沒找過）";

        static string dataFile;          // null = 還沒找過；"" = 找過但沒有
        static string cached;
        static long cachedStamp;

        public static void Reset()
        {
            dataFile = null;
            cached = null;
            cachedStamp = 0;
            Source = "（還沒找過）";
        }

        static bool Valid(string v)
        {
            return !string.IsNullOrEmpty(v)
                   && v.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                   && File.Exists(v);
        }

        /// <summary>回傳目前場景卡的完整路徑，問不到就回 null。</summary>
        public static string Detect()
        {
            // 優先用 SceneLoadWatch —— 它是直接從載入事件拿到的路徑，一定是現在這張。
            // 下面讀 *.data 那條路留著當退路（Harmony 掛不上時才會用到），
            // 但它其實很不可靠：實測 StudioSceneNavigation 記的是裸檔名而不是完整路徑，
            // 而且是歷史清單，最後一筆不等於現在載入的那張。
            if (SceneLoadWatch.Active && Valid(SceneLoadWatch.LastPath))
            {
                Source = "載入事件";
                return SceneLoadWatch.LastPath;
            }

            try
            {
                if (dataFile == null) FindDataFile();
                if (dataFile.Length == 0) return null;

                // 檔案沒動過就用上次的結果，不用每秒重讀
                long stamp = File.GetLastWriteTimeUtc(dataFile).Ticks;
                if (stamp == cachedStamp) return cached;
                cachedStamp = stamp;
                cached = ScanText(File.ReadAllText(dataFile));
                Source = cached == null ? "（檔案裡沒有有效路徑）"
                                        : Path.GetFileName(dataFile);
                return cached;
            }
            catch
            {
                return null;
            }
        }

        static void FindDataFile()
        {
            dataFile = "";
            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string cfg = Path.Combine(root, Path.Combine("BepInEx", "config"));
            if (!Directory.Exists(cfg)) { Source = "（找不到 BepInEx/config）"; return; }
            string[] files;
            try { files = Directory.GetFiles(cfg, "*.data"); }
            catch { Source = "（BepInEx/config 讀不了）"; return; }
            foreach (string f in files)
                if (Path.GetFileName(f).IndexOf("scene", StringComparison.OrdinalIgnoreCase) >= 0)
                { dataFile = f; return; }
            Source = "（沒有外掛在記錄目前的場景）";
        }

        /// <summary>
        /// 格式各家不同，所以只把整個檔案當文字，撈出看起來像場景卡路徑的片段，
        /// 取最後一個真的存在的 —— 這類檔案通常是「愈後面愈新」。
        /// </summary>
        static string ScanText(string txt)
        {
            string best = null;
            int i = 0;
            while (true)
            {
                int k = txt.IndexOf(".png", i, StringComparison.OrdinalIgnoreCase);
                if (k < 0) break;
                int s = k;
                while (s > 0)
                {
                    char c = txt[s - 1];
                    if (c < ' ' || c == '"' || c == '\n' || c == '\r' || c == '\0') break;
                    s--;
                }
                string cand = txt.Substring(s, k + 4 - s).Trim();
                if (Valid(cand)) best = cand;
                i = k + 4;
            }
            return best;
        }
    }
}
