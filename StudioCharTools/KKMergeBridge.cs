using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 工作室 → kkbridge.exe 的橋接。
    ///
    /// 做法是把工單寫成 json 丟進一個資料夾，kkbridge 監看到就處理，
    /// 處理完在旁邊產出同名的 .done.json。這邊輪詢那個檔案。
    ///
    /// 不直接啟動行程，是因為這樣工具可以一直開著、進度看得到，
    /// 而且工具沒開時工單會留在資料夾裡，下次開起來會自動補做。
    /// 逾時就當失敗處理，不會無限等下去。
    /// </summary>
    public static class KKMerge
    {
        /// <summary>工單資料夾。空字串就用 DefaultJobFolder()。</summary>
        public static string JobFolderOverride = "";

        /// <summary>保留給直接呼叫 exe 的用法，目前的流程沒用到。</summary>
        public static string ExeOverride = "";

        public static string GameRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        public static string DefaultJobFolder()
        {
            return Path.Combine(GameRoot(), @"UserData\chara\female\Temp");
        }

        public static string JobFolder()
        {
            string d = string.IsNullOrEmpty(JobFolderOverride)
                ? DefaultJobFolder() : JobFolderOverride;
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static string TempFolder()
        {
            string dir = Path.Combine(GameRoot(), @"UserData\_kkmerge_tmp");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public class Task
        {
            public bool Done;
            public bool Ok;
            public string Result;     // 產出的卡片路徑
            public string Error;
            public string Json;       // 原始結果 json
            public string JobFile;
            public string DoneFile;
            public string OutPath;
            public float StartTime;

            /// <summary>每幀呼叫；結果檔出現就收工。</summary>
            public void Poll()
            {
                if (Done) return;
                try
                {
                    if (!File.Exists(DoneFile)) return;
                    Json = File.ReadAllText(DoneFile, Encoding.UTF8);
                }
                catch { return; }          // 還在寫，下一幀再看

                Ok = Json.Contains("\"ok\": true") || Json.Contains("\"ok\":true");
                if (Ok && !File.Exists(OutPath))
                {
                    Ok = false;
                    Error = "工具回報成功，但找不到產出的檔案：" + OutPath;
                }
                if (!Ok && string.IsNullOrEmpty(Error))
                {
                    Error = ExtractError(Json);
                    if (string.IsNullOrEmpty(Error)) Error = "工具回報失敗";
                }
                Result = Ok ? OutPath : null;
                Done = true;
                CleanUp();
            }

            public void Fail(string why)
            {
                Error = why;
                Ok = false;
                Done = true;
            }

            public void CleanUp()
            {
                foreach (var f in new[] { JobFile, DoneFile })
                    try { if (!string.IsNullOrEmpty(f) && File.Exists(f)) File.Delete(f); }
                    catch { }
            }
        }

        // ------------------------------------------------------------------
        // 三個動作
        // ------------------------------------------------------------------
        public static Task Append(string charaPath, string coordPath, int outfit, string outPath)
        {
            var f = new List<string>
            {
                Pair("op", "append"), Pair("chara", charaPath), Pair("coord", coordPath),
                "\"outfit\": " + Mathf.Max(0, outfit), Pair("out", outPath)
            };
            return Submit(f, outPath);
        }

        public static Task Transplant(string srcPath, int srcOutfit,
                                      string dstPath, int dstOutfit, string outPath,
                                      string pushup, string skinOverlay)
        {
            var f = new List<string>
            {
                Pair("op", "transplant"),
                Pair("src", srcPath), "\"src_outfit\": " + Mathf.Max(0, srcOutfit),
                Pair("dst", dstPath), "\"dst_outfit\": " + Mathf.Max(0, dstOutfit),
                Pair("out", outPath)
            };
            if (!string.IsNullOrEmpty(pushup)) f.Add(Pair("pushup", pushup));
            if (!string.IsNullOrEmpty(skinOverlay)) f.Add(Pair("skin_overlay", skinOverlay));
            return Submit(f, outPath);
        }

        public static Task Clean(string charaPath, string outPath)
        {
            var f = new List<string>
            {
                Pair("op", "clean"), Pair("chara", charaPath), Pair("out", outPath)
            };
            return Submit(f, outPath);
        }

        // ------------------------------------------------------------------
        static string Esc(string v)
        {
            if (v == null) return "";
            var sb = new StringBuilder();
            foreach (char c in v)
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Pair(string k, string v)
        {
            return "\"" + k + "\": \"" + Esc(v) + "\"";
        }

        static Task Submit(List<string> fields, string outPath)
        {
            var task = new Task { OutPath = outPath, StartTime = Time.realtimeSinceStartup };
            try
            {
                string dir = JobFolder();
                string id = "kk_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                task.JobFile = Path.Combine(dir, id + ".job.json");
                task.DoneFile = Path.Combine(dir, id + ".done.json");

                // 先寫暫存檔再改名，避免 kkbridge 撿到寫到一半的工單
                string tmp = task.JobFile + ".tmp";
                File.WriteAllText(tmp, "{" + string.Join(", ", fields.ToArray()) + "}",
                                  new UTF8Encoding(false));
                if (File.Exists(task.JobFile)) File.Delete(task.JobFile);
                File.Move(tmp, task.JobFile);
            }
            catch (Exception e)
            {
                task.Fail("寫不出工單：" + e.Message);
            }
            return task;
        }

        /// <summary>從結果 json 撈出 "error"，不值得為這個拉一個 json 函式庫進來。</summary>
        public static string ExtractError(string json) { return Field(json, "error"); }

        /// <summary>撈出 "warnings" 陣列的原文，空的回 null。</summary>
        public static string ExtractWarnings(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf("\"warnings\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int a = json.IndexOf('[', i);
            int b = a < 0 ? -1 : json.IndexOf(']', a);
            if (a < 0 || b < 0) return null;
            string body = json.Substring(a + 1, b - a - 1).Trim();
            return body.Length == 0 ? null : body.Replace("\"", "").Replace("\n", " ");
        }

        static string Field(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = json.IndexOf(':', i);
            if (c < 0) return null;
            int q1 = json.IndexOf('"', c + 1);
            if (q1 < 0) return null;
            var sb = new StringBuilder();
            for (int k = q1 + 1; k < json.Length; k++)
            {
                if (json[k] == '\\' && k + 1 < json.Length) { sb.Append(json[k + 1]); k++; continue; }
                if (json[k] == '"') break;
                sb.Append(json[k]);
            }
            return sb.ToString();
        }
    }
}
