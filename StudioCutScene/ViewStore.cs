using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 每張卡、每一段場景各記一個 VR 視角。
    ///
    /// 為什麼是另外一個檔案，不是寫進 .cutscene.json
    /// ---------------------------------------------
    /// cutscene.json 是**你手改的**檔案 —— 裡面有註解式的排版、有我們沒有建模的欄位、
    /// 還有你自己調過的數值。要寫回去就得把整份重新序列化，任何我們沒讀進來的欄位
    /// 都會在那一刻消失。為了存七個數字去冒「把設定檔改壞」的險不划算。
    ///
    /// 所以視角存成旁邊的 <卡片檔名>.view.json，格式由我們自己決定、我們自己寫，
    /// 壞掉最多就是視角沒了，重存一次就好。附帶好處：**沒有 cutscene.json 的卡片
    /// 也能存視角** —— 這種卡佔多數。
    ///
    /// 檔名用卡片名（不含副檔名），跟 cutscene.json 的命名規則一致。
    ///
    /// 「場景」＝面板上那幾列「場景 N 開始」，也就是 cfg.tracks 的索引。
    /// 合併場景卡的 (1) (2) 這些段落在時間軸上就是一段一段的音軌，
    /// 所以用音軌索引當 key，正好一段一個視角。
    /// </summary>
    public static class ViewStore
    {
        /// <summary>場景索引 → 7 個數字（位置 xyz ＋ 四元數 xyzw）。</summary>
        public static readonly Dictionary<int, float[]> Views = new Dictionary<int, float[]>();

        public static string File_ = "";        // 目前這張卡對應的檔案，"" = 還沒認出卡片
        public static string LastReport = "（還沒載入）";

        static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        public static void Clear()
        {
            Views.Clear();
            File_ = "";
            LastReport = "（沒有卡片）";
        }

        /// <summary>
        /// 換卡時叫一次。cardPath 是場景卡的完整路徑，dir 是要放 view.json 的資料夾。
        /// 認不出卡片就整個關掉（File_ 留空，存的時候會說原因）。
        /// </summary>
        public static void Bind(string cardPath, string dir)
        {
            Views.Clear();
            File_ = "";

            if (string.IsNullOrEmpty(cardPath)) { LastReport = Lang.T("認不出現在是哪張卡，視角不會存"); return; }
            if (string.IsNullOrEmpty(dir)) { LastReport = Lang.T("找不到可以放視角檔的資料夾"); return; }

            try
            {
                File_ = Path.Combine(dir, Path.GetFileNameWithoutExtension(cardPath) + ".view.json");
            }
            catch (Exception e) { LastReport = "組不出檔名：" + e.Message; return; }

            if (!System.IO.File.Exists(File_)) { LastReport = Lang.T("這張卡還沒有存過視角"); return; }

            try
            {
                JNode root = MiniJson.Parse(System.IO.File.ReadAllText(File_));
                JNode arr = root == null ? null : root.Get("views");
                int n = arr == null ? 0 : arr.Count;
                for (int i = 0; i < n; i++)
                {
                    JNode e = arr.At(i);
                    if (e == null) continue;
                    int scene = e.I("scene", -1);
                    JNode p = e.Get("pose");
                    if (scene < 0 || p == null || p.Count != 7) continue;
                    float[] a = new float[7];
                    bool ok = true;
                    for (int k = 0; k < 7; k++)
                    {
                        JNode v = p.At(k);
                        if (v == null || v.Kind != JNode.NUM) { ok = false; break; }
                        a[k] = (float)v.Num;
                    }
                    if (ok) Views[scene] = a;
                }
                LastReport = string.Format(Lang.T("已載入 {0} 個視角："), Views.Count) + Path.GetFileName(File_);
            }
            catch (Exception e)
            {
                LastReport = Lang.T("讀視角檔失敗：") + e.Message;
            }
        }

        public static bool TryGet(int scene, out float[] pose)
        {
            return Views.TryGetValue(scene, out pose);
        }

        /// <summary>存一個場景的視角並立刻寫檔。</summary>
        public static bool Set(int scene, float[] pose)
        {
            if (pose == null || pose.Length != 7) { LastReport = Lang.T("視角資料不完整，沒有存"); return false; }
            if (string.IsNullOrEmpty(File_)) { LastReport = Lang.T("認不出現在是哪張卡，視角沒有存檔"); return false; }

            Views[scene] = pose;
            return Save();
        }

        public static bool Remove(int scene)
        {
            if (!Views.Remove(scene)) return false;
            return Save();
        }

        /// <summary>
        /// 自己組字串寫出去，不用序列化器。
        ///
        /// 格式固定、欄位就這幾個，手寫比拉一個序列化器進來簡單也好查 ——
        /// 出問題時你直接打開檔案就看得懂。數字一律用 InvariantCulture：
        /// 繁中 Windows 的小數點還是「.」，但這個習慣不能省，
        /// 換個地區設定就會寫出逗號而整份讀不回來。
        /// </summary>
        static bool Save()
        {
            if (string.IsNullOrEmpty(File_)) return false;
            try
            {
                var keys = new List<int>(Views.Keys);
                keys.Sort();

                var sb = new StringBuilder();
                sb.Append("{\n  \"views\": [\n");
                for (int i = 0; i < keys.Count; i++)
                {
                    float[] a = Views[keys[i]];
                    sb.Append("    { \"scene\": ").Append(keys[i]).Append(", \"pose\": [");
                    for (int k = 0; k < 7; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        sb.Append(a[k].ToString("R", INV));
                    }
                    sb.Append("] }");
                    if (i < keys.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]\n}\n");

                string dir = Path.GetDirectoryName(File_);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // 先寫暫存檔再換掉：中途當掉的話舊的那份還在，不會變成半個檔案讀不回來。
                string tmp = File_ + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString());
                if (System.IO.File.Exists(File_)) System.IO.File.Delete(File_);
                System.IO.File.Move(tmp, File_);

                LastReport = string.Format(Lang.T("已存 {0} 個視角："), Views.Count) + Path.GetFileName(File_);
                return true;
            }
            catch (Exception e)
            {
                LastReport = Lang.T("寫視角檔失敗：") + e.Message;
                Debug.LogWarning("[CutScene] " + LastReport);
                return false;
            }
        }
    }
}
