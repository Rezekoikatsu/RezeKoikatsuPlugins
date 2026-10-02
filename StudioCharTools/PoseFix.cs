using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using HarmonyLib;
using Studio;

namespace StudioCharTools
{
    /// <summary>
    /// 換人 / 讀場景卡之後身體拉伸的修正。
    ///
    /// 診斷結果修正了先前的判斷：骨骼縮放不等於 1 是**正常的**，
    /// cf_j_bust01_L、cf_s_arm01_L 之類就是人物卡的體型資料，重設它們等於破壞角色。
    /// 真正壞掉的是 FK / IK 解算器的狀態，所以「隨便讀一個姿勢」才會修好。
    ///
    /// 正確做法：用 Studio.PauseCtrl.Save / Load 把角色**自己**的姿勢
    /// 存到暫存檔再讀回來。讀的是它原本的姿勢，所以不會有任何位移。
    /// </summary>
    public static class PoseFix
    {
        public static string LastMessage = "尚未執行";
        public static string LastReport = "";

        /// <summary>往返後是否保留暫存檔（除錯用；平常會自動刪除）。</summary>
        public static bool KeepTempFile = false;

        const string TempName = "__posefix_temp";

        // ===============================================================
        // 主要方案：自身姿勢往返
        // ===============================================================
        // ===============================================================
        // 拆開的存 / 讀，給「換角色前存、換完讀回」用
        // ===============================================================

        /// <summary>把角色目前姿勢存成暫存檔，回傳實際產生的檔案路徑；失敗回 null。</summary>
        public static string SavePoseTemp(OCIChar oci)
        {
            return SavePoseWithPrefix(oci, TempName, true);
        }

        /// <summary>
        /// 存成正式檔案，路徑鏡射目前場景的資料夾結構：
        ///   scene\(Scene)\Anna Anon\2025\2025_0524_1717_32_794.png
        /// → pose \(Scene)\Anna Anon\2025\2025_0524_1717_32_794_Spanks_&lt;日期&gt;.png
        /// </summary>
        public static string SavePoseNamed(OCIChar oci, int charIndex, bool isFemale)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return null; }

            string subDir = "";
            string sceneName = "Scene";

            string scenePath = LastScenePath;
            if (!string.IsNullOrEmpty(scenePath))
            {
                sceneName = Path.GetFileNameWithoutExtension(scenePath);

                string sceneRoot = SceneFolder();
                string full = Path.GetFullPath(scenePath);
                string rootFull = Path.GetFullPath(sceneRoot);
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(rootFull.Length).TrimStart('\\', '/');
                    subDir = Path.GetDirectoryName(rel) ?? "";
                }
            }

            // pose\<場景子路徑>\<場景名稱>\  ← 場景名稱本身也成為一層資料夾
            string dir = PoseFolder();
            if (subDir.Length > 0) dir = Path.Combine(dir, subDir);
            dir = Path.Combine(dir, SanitizeFileName(sceneName));

            string prefix = NamePrefix(oci, charIndex, isFemale);

            string path = SavePoseWithPrefix(oci, prefix, false, dir);

            if (path != null)
            {
                LastMessage = "已存出: " + Path.GetFileName(path);
                LastReport = "位置: " + Path.GetDirectoryName(path)
                           + (string.IsNullOrEmpty(scenePath)
                              ? "\n（沒有偵測到場景檔，用預設名稱與根目錄）"
                              : "\n來源場景: " + scenePath);
            }
            else
            {
                LastMessage = "存檔失敗，見報告";
            }
            return path;
        }

        // ---------------------------------------------------------------
        // 場景路徑追蹤
        // sceneInfo 上找不到可靠的路徑欄位，所以直接攔 Studio 的存讀場景方法。
        // ---------------------------------------------------------------
        public static string LastScenePath;

        public static void InstallSceneTracker(Harmony harmony)
        {
            if (harmony == null) { Debug.LogWarning("[PoseFix] harmony 是 null"); return; }

            try
            {
                var postfixInfo = typeof(PoseFix).GetMethod("RecordScenePath",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var hm = new HarmonyMethod(postfixInfo);

                string[] typeNames = { "Studio.Studio", "Studio.SceneInfo", "Studio.SceneLoadScene" };
                string[] methodNames = { "LoadScene", "SaveScene", "Load", "Save", "Import", "ImportScene" };

                var hit = new List<string>();

                foreach (var tn in typeNames)
                {
                    var t = FindType(tn);
                    if (t == null) continue;

                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (Array.IndexOf(methodNames, m.Name) < 0) continue;
                        var ps = m.GetParameters();
                        if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;

                        try
                        {
                            harmony.Patch(m, null, hm);      // 直接呼叫，不再反射找多載
                            hit.Add(tn + "." + m.Name + "(" + ps.Length + ")");
                        }
                        catch (Exception pe)
                        {
                            Debug.LogWarning("[PoseFix] 掛 " + tn + "." + m.Name + " 失敗: "
                                             + pe.GetBaseException().Message);
                        }
                    }
                }

                if (hit.Count == 0)
                    Debug.LogWarning("[PoseFix] 沒有掛上任何場景方法，檔名會用預設值 Scene");
                else
                    Debug.Log("[PoseFix] 場景路徑追蹤已掛上: " + string.Join(", ", hit.ToArray()));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PoseFix] 場景追蹤安裝失敗: " + e.GetBaseException().Message);
            }
        }

        /// <summary>統一的檔名前綴：(G) 1.Sana / [B] 2.Slime Fighter</summary>
        public static string NamePrefix(OCIChar oci, int charIndex, bool isFemale)
        {
            string tag = isFemale ? "(G) " : "[B] ";
            return tag + (charIndex > 0 ? charIndex + "." : "") + SanitizeFileName(GetCharName(oci));
        }

        /// <summary>KK 的時間戳格式：2026_0908_1233_21_875</summary>
        public static string TimeStamp()
        {
            return DateTime.Now.ToString("yyyy_MMdd_HHmm_ss_fff");
        }

        public static string GameRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        static void RecordScenePath(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                var p = __args[0] as string;
                if (string.IsNullOrEmpty(p)) return;

                // 只收 scene 資料夾底下的檔案，免得把人物卡、姿勢檔的路徑也記進來
                if (p.IndexOf(".png", StringComparison.OrdinalIgnoreCase) < 0) return;
                var root = Path.GetFullPath(SceneFolder());
                if (!Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

                LastScenePath = p;
                Debug.Log("[PoseFix] 記錄場景路徑: " + p);
            }
            catch { }
        }

        public static string GetCharName(OCIChar oci)
        {
            if (oci == null) return "Char";
            try
            {
                var tn = oci.treeNodeObject;
                if (tn != null && !string.IsNullOrEmpty(tn.textName)) return tn.textName;
            }
            catch { }
            try
            {
                var p = oci.charInfo.chaFile.parameter;
                string n = (p.lastname + p.firstname).Trim();
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return "Char";
        }

        static string SceneFolder()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, Path.Combine("UserData", Path.Combine("Studio", "scene")));
        }

        static string SavePoseWithPrefix(OCIChar oci, string prefix, bool sweepFirst, string targetDir = null)
        {
            if (oci == null) { LastReport = "沒有選取角色"; return null; }

            try
            {
                Type pauseCtrl = FindType("Studio.PauseCtrl");
                MethodInfo save = pauseCtrl == null ? null : FindMethod(pauseCtrl, "Save");
                if (save == null) { LastReport = "找不到 Studio.PauseCtrl.Save"; return null; }

                object inst = save.IsStatic ? null : UnityEngine.Object.FindObjectOfType(pauseCtrl);
                if (!save.IsStatic && inst == null) { LastReport = "找不到 PauseCtrl 實體"; return null; }

                string dir = targetDir ?? PoseFolder();
                Directory.CreateDirectory(dir);          // 子資料夾不存在時自動建立
                if (sweepFirst) SweepTemp(dir, null);

                DateTime before = DateTime.Now.AddSeconds(-2);
                save.Invoke(inst, new object[] { oci, Path.Combine(dir, prefix) });

                string made = NewestMatching(dir, prefix, before);
                if (made == null)
                {
                    save.Invoke(inst, new object[] { oci, prefix });   // 少數版本只吃檔名
                    made = NewestMatching(dir, prefix, before);
                }

                if (made == null) LastReport = "Save 沒有出錯但找不到以 " + prefix + " 開頭的新檔案\n資料夾: " + dir;
                return made;
            }
            catch (Exception e)
            {
                LastReport = "存檔失敗: " + e.GetBaseException();
                return null;
            }
        }

        /// <summary>讀回指定的姿勢檔。</summary>
        public static bool LoadPoseFile(OCIChar oci, string path)
        {
            if (oci == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                Type pauseCtrl = FindType("Studio.PauseCtrl");
                MethodInfo load = pauseCtrl == null ? null : FindMethod(pauseCtrl, "Load");
                if (load == null) { LastReport = "找不到 Studio.PauseCtrl.Load"; return false; }

                object inst = load.IsStatic ? null : UnityEngine.Object.FindObjectOfType(pauseCtrl);
                if (!load.IsStatic && inst == null) return false;

                load.Invoke(inst, new object[] { oci, path });
                return true;
            }
            catch (Exception e)
            {
                LastReport = "讀取失敗: " + e.GetBaseException();
                return false;
            }
        }

        /// <summary>換角色完成後呼叫：讀回換之前存的姿勢，然後刪掉暫存檔。</summary>
        public static bool RestoreAndCleanup(OCIChar oci, string tempPath)
        {
            bool ok = LoadPoseFile(oci, tempPath);
            if (!KeepTempFile)
            {
                try { SweepTemp(PoseFolder(), null); } catch { }
            }
            LastMessage = ok ? "已還原換角色前的姿勢" : "姿勢還原失敗";
            return ok;
        }

        static string NewestMatching(string dir, string prefix, DateTime notBefore)
        {
            string best = null;
            DateTime bestTime = notBefore;

            foreach (var f in Directory.GetFiles(dir, prefix + "*"))
            {
                var t = File.GetLastWriteTime(f);
                if (t <= bestTime) continue;
                bestTime = t;
                best = f;
            }
            return best;
        }

        /// <summary>目前場景名稱；抓不到就回 "Scene"。</summary>
        public static string GetSceneName()
        {
            try
            {
                object si = null;
                var studio = Studio.Studio.Instance;
                if (studio != null)
                {
                    var f = studio.GetType().GetField("sceneInfo",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) si = f.GetValue(studio);
                }

                if (si != null)
                {
                    // 找出裡面看起來像檔案路徑的字串欄位
                    foreach (var f in si.GetType().GetFields(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (f.FieldType != typeof(string)) continue;
                        var v = f.GetValue(si) as string;
                        if (string.IsNullOrEmpty(v)) continue;
                        if (v.IndexOf(".png", StringComparison.OrdinalIgnoreCase) < 0
                            && v.IndexOf('\\') < 0 && v.IndexOf('/') < 0) continue;
                        var n = Path.GetFileNameWithoutExtension(v);
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch { }
            return "Scene";
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Scene";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "Scene" : name;
        }

        public static bool ResetPoseRoundTrip(OCIChar oci)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return false; }

            var sb = new StringBuilder();
            string saved = null;

            try
            {
                Type pauseCtrl = FindType("Studio.PauseCtrl");
                if (pauseCtrl == null)
                {
                    LastMessage = "找不到 Studio.PauseCtrl";
                    LastReport = "這個遊戲版本沒有這個型別，請改用方案 2。";
                    return false;
                }

                MethodInfo save = FindMethod(pauseCtrl, "Save");
                MethodInfo load = FindMethod(pauseCtrl, "Load");
                if (save == null || load == null)
                {
                    LastMessage = "找不到 Save / Load";
                    LastReport = "PauseCtrl 存在但簽名對不上，請按「診斷」把輸出貼出來。";
                    return false;
                }

                object inst = null;
                if (!save.IsStatic || !load.IsStatic)
                {
                    inst = UnityEngine.Object.FindObjectOfType(pauseCtrl);
                    if (inst == null)
                    {
                        LastMessage = "PauseCtrl 是實體方法但場上找不到實體";
                        return false;
                    }
                }

                string dir = PoseFolder();
                Directory.CreateDirectory(dir);

                // Save 收的是「前綴」，它會自己接上時間戳與 .png，
                // 所以不能預設檔名，得存完再去撈最新的那一個。
                SweepTemp(dir, sb);

                string prefix = Path.Combine(dir, TempName);
                save.Invoke(save.IsStatic ? null : inst, new object[] { oci, prefix });

                saved = NewestTemp(dir);
                if (saved == null)
                {
                    // 少數版本只吃純檔名，路徑自己拼
                    save.Invoke(save.IsStatic ? null : inst, new object[] { oci, TempName });
                    saved = NewestTemp(dir);
                }

                if (saved == null)
                {
                    LastMessage = "存檔沒有產生檔案";
                    LastReport = sb + "資料夾: " + dir + "\n"
                               + "Save 沒有出錯但找不到以 " + TempName + " 開頭的檔案。\n"
                               + "請手動看一下這個資料夾裡實際被建立的檔名。";
                    return false;
                }

                sb.AppendLine("已存出: " + Path.GetFileName(saved));
                sb.AppendLine("大小: " + new FileInfo(saved).Length + " bytes");

                load.Invoke(load.IsStatic ? null : inst, new object[] { oci, saved });

                sb.AppendLine("讀回完成");
                LastMessage = "姿勢已往返重置（無位移）";
                LastReport = sb.ToString();
                return true;
            }
            catch (Exception e)
            {
                var baseEx = e.GetBaseException();
                LastMessage = "往返失敗: " + baseEx.Message;
                LastReport = sb + "\n" + baseEx;
                return false;
            }
            finally
            {
                if (!KeepTempFile)
                {
                    try { SweepTemp(PoseFolder(), null); } catch { }
                }
                else if (saved != null)
                {
                    LastReport += "\n（KeepTempFile 已開啟，暫存檔保留在 " + saved + "）";
                }
            }
        }

        /// <summary>找出資料夾裡以 TempName 開頭、最新寫入的那一個檔案。</summary>
        static string NewestTemp(string dir)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (var f in Directory.GetFiles(dir, TempName + "*"))
            {
                var t = File.GetLastWriteTime(f);
                if (t <= bestTime) continue;
                bestTime = t;
                best = f;
            }
            return best;
        }

        /// <summary>清掉所有暫存檔，包含前幾次失敗留下來的。</summary>
        static void SweepTemp(string dir, StringBuilder sb)
        {
            if (!Directory.Exists(dir)) return;

            int n = 0;
            foreach (var f in Directory.GetFiles(dir, TempName + "*"))
            {
                try { File.Delete(f); n++; } catch { }
            }
            if (n > 0 && sb != null) sb.AppendLine("清掉 " + n + " 個殘留暫存檔");
        }

        static string PoseFolder()
        {
            // CharaStudio 的 dataPath 是 <遊戲根>\CharaStudio_Data
            var root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, Path.Combine("UserData", Path.Combine("Studio", "pose")));
        }

        // ===============================================================
        // 次要方案：強制重新套用 FK / IK
        // 上一版失敗是因為我假設第一個參數是 bool，實際上是 BoneGroup / KinematicMode 列舉。
        // 這裡只做「強制重新啟用」，不先關閉，避免把姿勢弄丟。
        // ===============================================================
        public static bool ReapplyKinematics(OCIChar oci)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return false; }

            var sb = new StringBuilder();
            int done = 0;

            done += InvokeGroupToggle(oci, "ActiveFK", sb);
            done += InvokeGroupToggle(oci, "ActiveIK", sb);
            done += InvokeKinematicMode(oci, sb);

            LastReport = sb.Length == 0 ? "沒有找到可呼叫的方法" : sb.ToString();
            LastMessage = done > 0 ? "已重新套用 " + done + " 項" : "重新套用失敗，見報告";
            return done > 0;
        }

        static int InvokeGroupToggle(OCIChar oci, string name, StringBuilder sb)
        {
            // 簽名：void ActiveFK(BoneGroup _group, bool _active, bool _force)
            var m = FindMethod(oci.GetType(), name, 3);
            if (m == null) { sb.AppendLine("找不到 " + name); return 0; }

            var ps = m.GetParameters();
            if (!ps[0].ParameterType.IsEnum) { sb.AppendLine(name + " 第一參數不是列舉"); return 0; }

            int n = 0;
            foreach (var g in Enum.GetValues(ps[0].ParameterType))
            {
                try
                {
                    m.Invoke(oci, new object[] { g, true, true });   // _force = true
                    n++;
                }
                catch (Exception e)
                {
                    sb.AppendLine(name + "(" + g + ") 失敗: " + e.GetBaseException().Message);
                }
            }
            sb.AppendLine(name + " 已對 " + n + " 個群組強制重新套用");
            return n > 0 ? 1 : 0;
        }

        static int InvokeKinematicMode(OCIChar oci, StringBuilder sb)
        {
            // 簽名：void ActiveKinematicMode(KinematicMode _mode, bool _active, bool _force)
            var m = FindMethod(oci.GetType(), "ActiveKinematicMode", 3);
            if (m == null) { sb.AppendLine("找不到 ActiveKinematicMode"); return 0; }

            var ps = m.GetParameters();
            if (!ps[0].ParameterType.IsEnum) return 0;

            int n = 0;
            foreach (var mode in Enum.GetValues(ps[0].ParameterType))
            {
                try { m.Invoke(oci, new object[] { mode, true, true }); n++; }
                catch (Exception e)
                {
                    sb.AppendLine("ActiveKinematicMode(" + mode + ") 失敗: "
                                  + e.GetBaseException().Message);
                }
            }
            sb.AppendLine("ActiveKinematicMode 已對 " + n + " 個模式強制重新套用");
            return n > 0 ? 1 : 0;
        }

        // ===============================================================
        // 最後手段：縮放相關（保留，但預設不要碰）
        // 診斷已證實體型資料本來就不是 1，這兩個功能只在確定是殘值時才用。
        // ===============================================================
        class Snapshot { public Transform t; public Vector3 scale; }

        static readonly Dictionary<ChaControl, List<Snapshot>> _undo =
            new Dictionary<ChaControl, List<Snapshot>>();

        static Transform Root(OCIChar oci)
        {
            if (oci == null || oci.charInfo == null) return null;
            return oci.charInfo.objBodyBone != null
                ? oci.charInfo.objBodyBone.transform
                : oci.charInfo.transform;
        }

        /// <summary>只處理 NaN / Infinity / 極端值。正常體型資料不會被動到。</summary>
        public static int FixOutliers(OCIChar oci, float lo, float hi)
        {
            var root = Root(oci);
            if (root == null) { LastMessage = "找不到骨架"; return 0; }

            var snaps = new List<Snapshot>();
            var sb = new StringBuilder();
            int n = 0;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var s = t.localScale;
                bool broken = IsBroken(s.x) || IsBroken(s.y) || IsBroken(s.z);
                bool extreme = s.x < lo || s.x > hi || s.y < lo || s.y > hi || s.z < lo || s.z > hi;
                if (!broken && !extreme) continue;

                snaps.Add(new Snapshot { t = t, scale = s });
                if (n < 40) sb.AppendLine(t.name + "  " + s.ToString("F3") + "  -> 1");
                t.localScale = Vector3.one;
                n++;
            }

            if (n > 0) _undo[oci.charInfo] = snaps;

            LastReport = n == 0
                ? "沒有異常縮放。\n提醒：cf_j_bust01_L、cf_s_arm01_L 這類不等於 1 是正常的體型資料，不該重設。"
                : sb + (n > 40 ? "\n...（共 " + n + " 根，只列出前 40）" : "");
            LastMessage = n == 0 ? "沒有異常骨骼（正常）" : "已重設 " + n + " 根，若人物走樣請按復原";
            return n;
        }

        static bool IsBroken(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) || v == 0f;
        }

        public static int ResetAllScales(OCIChar oci)
        {
            var root = Root(oci);
            if (root == null) { LastMessage = "找不到骨架"; return 0; }

            var snaps = new List<Snapshot>();
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.localScale == Vector3.one) continue;
                snaps.Add(new Snapshot { t = t, scale = t.localScale });
                t.localScale = Vector3.one;
                n++;
            }
            if (n > 0) _undo[oci.charInfo] = snaps;

            LastMessage = "已把 " + n + " 根骨骼歸一";
            LastReport = "⚠️ 這會一併清掉人物卡的體型資料（胸型、手臂粗細等）。\n"
                       + "人物走樣是預期的，請按「復原上一次修正」。";
            return n;
        }

        public static bool Undo(OCIChar oci)
        {
            if (oci == null || oci.charInfo == null) return false;

            List<Snapshot> snaps;
            if (!_undo.TryGetValue(oci.charInfo, out snaps) || snaps.Count == 0)
            {
                LastMessage = "沒有可復原的紀錄";
                return false;
            }

            int n = 0;
            foreach (var s in snaps)
            {
                if (s.t == null) continue;
                s.t.localScale = s.scale;
                n++;
            }
            _undo.Remove(oci.charInfo);
            LastMessage = "已復原 " + n + " 根骨骼";
            return true;
        }

        public static bool HasUndo(OCIChar oci)
        {
            return oci != null && oci.charInfo != null && _undo.ContainsKey(oci.charInfo);
        }

        // ===============================================================
        // 診斷
        // ===============================================================
        public static void Dump(OCIChar oci)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== PoseFix 診斷 =====");
            sb.AppendLine("姿勢資料夾: " + PoseFolder());
            sb.AppendLine("資料夾存在: " + Directory.Exists(PoseFolder()));

            if (oci == null) { Debug.Log(sb + "沒有選取角色"); return; }
            sb.AppendLine("OCIChar 型別: " + oci.GetType().FullName);

            var pauseType = FindType("Studio.PauseCtrl");
            sb.AppendLine("--- Studio.PauseCtrl ---");
            if (pauseType == null) sb.AppendLine("  找不到");
            else
            {
                foreach (var m in pauseType.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var args = new List<string>();
                    foreach (var p in m.GetParameters()) args.Add(p.ParameterType.Name);
                    sb.AppendLine("  " + (m.IsStatic ? "static " : "") + m.Name
                                  + "(" + string.Join(", ", args.ToArray()) + ")");
                }
            }

            sb.AppendLine("--- 縮放異常（NaN / Infinity / 0）---");
            var root = Root(oci);
            int bad = 0;
            if (root != null)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var s = t.localScale;
                    if (!IsBroken(s.x) && !IsBroken(s.y) && !IsBroken(s.z)) continue;
                    sb.AppendLine("  " + t.name + " = " + s);
                    bad++;
                }
            }
            sb.AppendLine("  共 " + bad + " 根真正異常（不含正常體型資料）");
            sb.AppendLine("===== 診斷結束 =====");

            Debug.Log(sb.ToString());
            LastReport = "已輸出到 BepInEx 主控台";
            LastMessage = "診斷完成（" + bad + " 根真正異常）";
        }

        // ===============================================================
        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        static MethodInfo FindMethod(Type t, string name, int paramCount = -1)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                if (paramCount >= 0 && m.GetParameters().Length != paramCount) continue;
                return m;
            }
            return null;
        }
    }

    /// <summary>
    /// 存出人物卡與服裝卡。路徑一律由遊戲根目錄推導，不寫死磁碟機。
    ///   人物卡：UserData\chara\female|male\Temp
    ///   服裝卡：UserData\coordinate\Temp
    /// 檔名沿用姿勢那套：(G) 1.Sana_2026_0908_1233_21_875.png
    /// </summary>
    public static class CardSaver
    {
        public static string LastMessage = "";
        public static string LastReport = "";

        /// <summary>人物卡存到 Temp 子資料夾，關掉就存到 chara\female 根目錄。</summary>
        public static bool CharaToTemp = true;

        /// <summary>服裝卡存到 Temp 子資料夾，關掉就存到 coordinate 根目錄。</summary>
        public static bool CoordToTemp = true;

        public static string SaveCharaCard(OCIChar oci, int charIndex, bool isFemale, byte[] thumbPng)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return null; }

            try
            {
                var cha = oci.charInfo;
                var chaFile = cha.chaFile;
                if (chaFile == null) { LastMessage = "取不到 chaFile"; return null; }

                // 合卡流程的中繼檔跟手動存卡一樣放 Temp，用完由 CharTools 刪掉
                string baseDir = Path.Combine(PoseFix.GameRoot(),
                    Path.Combine("UserData", Path.Combine("chara", isFemale ? "female" : "male")));

                string dir = (CharToolsPlugin.mergeJobRunning || CharaToTemp)
                    ? Path.Combine(baseDir, "Temp")
                    : baseDir;
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir,
                    PoseFix.NamePrefix(oci, charIndex, isFemale) + "_" + PoseFix.TimeStamp() + ".png");

                byte sex = 0;
                try { sex = (byte)cha.sex; } catch { sex = (byte)(isFemale ? 1 : 0); }

                ApplyThumb(chaFile, thumbPng);

                var m = FindSave(chaFile.GetType(), "SaveCharaFile");
                if (m == null)
                {
                    LastMessage = "找不到 SaveCharaFile";
                    LastReport = DumpMethods(chaFile.GetType(), "Save");
                    return null;
                }

                object result = m.Invoke(chaFile, BuildArgs(m, path, sex));

                if (!File.Exists(path))
                {
                    LastMessage = "呼叫成功但檔案不存在";
                    LastReport = "路徑: " + path + "\n回傳: " + result
                               + "\n" + DumpMethods(chaFile.GetType(), "Save");
                    return null;
                }

                LastMessage = "人物卡已存出: " + Path.GetFileName(path);
                LastReport = "位置: " + dir;
                return path;
            }
            catch (Exception e)
            {
                LastMessage = "人物卡存檔失敗: " + e.GetBaseException().Message;
                LastReport = e.GetBaseException().ToString();
                return null;
            }
        }

        /// <summary>
        /// 存服裝卡，但路徑由呼叫端指定。
        ///
        /// 為什麼要有這個：外部腳本（VNGE 的 autocharamoments）要能指定輸出路徑，
        /// 但其餘步驟必須跟 F6 按鈕完全一樣，才不會又長出兩套行為。
        /// 實作上就是 SaveCoordinateCard 去掉「自己組路徑」那一段，其餘完全相同。
        ///
        /// （腳本存出來的卡缺 MaterialEditor 資料的那個老問題，
        ///   解法在 EnsureKkapiCoordinateData，兩條路都會經過。）
        /// </summary>
        public static string SaveCoordinateCardTo(OCIChar oci, string fullPath, byte[] thumbPng)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return null; }
            if (string.IsNullOrEmpty(fullPath)) { LastMessage = "沒有指定路徑"; return null; }

            try
            {
                var cha = oci.charInfo;
                object coord = CurrentCoordinate(cha);
                if (coord == null) { LastMessage = "取不到目前服裝資料"; return null; }

                try
                {
                    string d = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                }
                catch { }

                ApplyThumb(coord, thumbPng);
                EnsureKkapiCoordinateData(cha, coord);

                var m = FindSave(coord.GetType(), "SaveFile");
                if (m == null)
                {
                    LastMessage = "找不到 SaveFile";
                    LastReport = DumpMethods(coord.GetType(), "Save");
                    return null;
                }

                object result = m.Invoke(coord, BuildArgs(m, fullPath, 0));

                if (!File.Exists(fullPath))
                {
                    LastMessage = "呼叫成功但檔案不存在";
                    LastReport = "路徑: " + fullPath + "\n回傳: " + result;
                    return null;
                }

                LastMessage = "服裝卡已存出: " + Path.GetFileName(fullPath);
                LastReport = "位置: " + Path.GetDirectoryName(fullPath);
                return fullPath;
            }
            catch (Exception e)
            {
                LastMessage = "服裝卡存檔失敗: " + e.GetBaseException().Message;
                LastReport = e.GetBaseException().ToString();
                return null;
            }
        }

        public static string SaveCoordinateCard(OCIChar oci, int charIndex, bool isFemale, byte[] thumbPng)
        {
            if (oci == null) { LastMessage = "沒有選取角色"; return null; }

            try
            {
                var cha = oci.charInfo;
                object coord = CurrentCoordinate(cha);
                if (coord == null)
                {
                    LastMessage = "取不到目前服裝資料";
                    return null;
                }

                string coordBase = Path.Combine(PoseFix.GameRoot(),
                    Path.Combine("UserData", "coordinate"));
                string dir = CoordToTemp ? Path.Combine(coordBase, "Temp") : coordBase;
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir,
                    PoseFix.NamePrefix(oci, charIndex, isFemale) + "_" + PoseFix.TimeStamp() + ".png");

                ApplyThumb(coord, thumbPng);
                EnsureKkapiCoordinateData(cha, coord);

                var m = FindSave(coord.GetType(), "SaveFile");
                if (m == null)
                {
                    LastMessage = "找不到 SaveFile";
                    LastReport = DumpMethods(coord.GetType(), "Save");
                    return null;
                }

                object result = m.Invoke(coord, BuildArgs(m, path, 0));

                if (!File.Exists(path))
                {
                    LastMessage = "呼叫成功但檔案不存在";
                    LastReport = "路徑: " + path + "\n回傳: " + result
                               + "\n" + DumpMethods(coord.GetType(), "Save");
                    return null;
                }

                LastMessage = "服裝卡已存出: " + Path.GetFileName(path);
                LastReport = "位置: " + dir;
                return path;
            }
            catch (Exception e)
            {
                LastMessage = "服裝卡存檔失敗: " + e.GetBaseException().Message;
                LastReport = e.GetBaseException().ToString();
                return null;
            }
        }

        /// <summary>
        /// 讓 MaterialEditor、DBDE、KKABMX 這些外掛有機會把自己的服裝資料寫進去。
        ///
        /// 找了很久的那個「腳本存的服裝卡壞掉、F6 存的正常」的真正原因：
        ///
        /// 會寫這些延伸資料的不是存檔本身，是 KKAPI 的
        /// CharacterApi.OnCoordinateBeingSaved(ChaControl, ChaFileCoordinate) ——
        /// 它會叫每一個註冊在該角色身上的 CharaCustomFunctionController
        /// 把資料塞進這個 coordinate 物件的延伸資料表，之後 SaveFile 才把它寫進檔案。
        ///
        /// 而 KKAPI 自己唯一呼叫它的地方（Shared.Core/Chara/CharacterApi.cs 第 189 行）長這樣：
        ///
        ///     ExtendedSave.CoordinateBeingSaved += file =&gt;
        ///     {
        ///         var character = MakerAPI.GetCharacterControl();
        ///         if (character == null) { LogError("OnCoordinateBeingSaved fired outside chara maker..."); return; }
        ///         OnCoordinateBeingSaved(character, file);
        ///     };
        ///
        /// 而 GetCharacterControl() 是 `InsideMaker ? GetMakerBase()?.chaCtrl : null`。
        /// 也就是說：**在工作室裡，KKAPI 這條路本來就永遠走不通**，
        /// 跟我們交的是不是 nowCoordinate、KKAPI 有沒有在追這個角色，通通無關
        /// （診斷 log 也證實了：兩條路那一行完全一模一樣，連物件位址都相同）。
        ///
        /// 之所以 F6 那條有時候會通，是因為環境裡有別的外掛把工作室選取中的角色
        /// 補給了 GetCharacterControl；F6 存卡前會先把角色選起來（CharPicker），
        /// 腳本不會，所以同一個方法在兩條路上結果不同。
        ///
        /// 與其依賴那個外掛，這裡直接自己補上這一步：
        /// 判斷條件跟 KKAPI 內部那個 if 完全一樣 —— GetCharacterControl() 是 null
        /// 才自己呼叫，不是 null 就代表 KKAPI 待會自己會做，我們不插手，
        /// 所以本來就正常的那條路行為完全不變，不會變成寫兩次。
        /// </summary>
        static void EnsureKkapiCoordinateData(ChaControl cha, object coord)
        {
            if (cha == null || coord == null) return;
            try
            {
                Type api = FindType("KKAPI.Chara.CharacterApi");
                Type maker = FindType("KKAPI.Maker.MakerAPI");
                if (api == null || maker == null)
                {
                    UnityEngine.Debug.Log("[CardSaver] 找不到 KKAPI，跳過補寫服裝延伸資料");
                    return;
                }

                // KKAPI 自己待會就會做 —— 不要重複呼叫
                MethodInfo get = maker.GetMethod("GetCharacterControl",
                    BindingFlags.Public | BindingFlags.Static);
                if (get != null && get.Invoke(null, null) != null) return;

                MethodInfo onSaved = null;
                foreach (MethodInfo m in api.GetMethods(BindingFlags.NonPublic | BindingFlags.Public
                                                        | BindingFlags.Static))
                {
                    if (m.Name != "OnCoordinateBeingSaved") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(cha)) continue;
                    if (!ps[1].ParameterType.IsInstanceOfType(coord)) continue;
                    onSaved = m;
                    break;
                }

                if (onSaved == null)
                {
                    UnityEngine.Debug.LogWarning("[CardSaver] KKAPI 版本不同，"
                        + "找不到 OnCoordinateBeingSaved(ChaControl, ChaFileCoordinate)，"
                        + "服裝卡可能會缺 MaterialEditor 資料");
                    return;
                }

                onSaved.Invoke(null, new object[] { cha, coord });
                UnityEngine.Debug.Log("[CardSaver] 已代替 KKAPI 觸發 OnCoordinateBeingSaved"
                                      + "（工作室裡 KKAPI 自己不會做）");
            }
            catch (Exception e)
            {
                // 補寫失敗頂多是卡片少了外掛資料，不該讓整個存檔失敗
                UnityEngine.Debug.LogWarning("[CardSaver] 補寫服裝延伸資料失敗（仍會存檔）："
                                             + e.GetBaseException().Message);
            }
        }


        static Type FindType(string fullName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = asm.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                        if (t != null && t.FullName == fullName) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>把擷取到的畫面塞進卡片的縮圖欄位。</summary>
        static void ApplyThumb(object cardObj, byte[] png)
        {
            if (cardObj == null || png == null || png.Length == 0) return;

            int n = 0;
            foreach (var name in new[] { "pngData", "facePngData" })
            {
                try
                {
                    var f = cardObj.GetType().GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (f == null || f.FieldType != typeof(byte[])) continue;
                    f.SetValue(cardObj, png);
                    n++;
                }
                catch { }
            }

            if (n == 0)
                LastReport = "找不到 pngData 欄位，縮圖仍會是空白\n"
                           + DumpFields(cardObj.GetType(), "png");
        }

        static string DumpFields(Type t, string contains)
        {
            var sb = new StringBuilder();
            sb.AppendLine("含 \"" + contains + "\" 的欄位（" + t.FullName + "）：");
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                        sb.AppendLine("  " + f.FieldType.Name + " " + f.Name);
            return sb.ToString();
        }

        // -----------------------------------------------------------
        /// <summary>
        /// 目前穿在身上的那一套服裝資料。
        ///
        /// **一定要優先回 nowCoordinate，不能只看 chaFile.coordinate[idx]。**
        ///
        /// 實測（診斷 log）：換過裝之後 nowCoordinate 和 chaFile.coordinate[idx]
        /// 不是同一個實例，而身上真正穿著、外掛也認得的那一套是 nowCoordinate。
        /// 傳陣列裡那個等於存出一套沒人在用的資料。
        ///
        /// 注意：這裡**不是**在解決「卡片缺 MaterialEditor 資料」那個問題。
        /// 那個問題的原因寫在 EnsureKkapiCoordinateData 上面 ——
        /// 曾經以為 KKAPI 是靠比對 nowCoordinate 找擁有者，那是錯的。
        /// </summary>
        static object CurrentCoordinate(ChaControl cha)
        {
            try
            {
                // 先拿 nowCoordinate —— 那是遊戲和所有外掛都認定的「現在這一套」
                try
                {
                    var now = cha.nowCoordinate;
                    if (now != null) return now;
                }
                catch { }

                // 拿不到才退回陣列（舊行為）
                var coords = cha.chaFile.coordinate;
                if (coords == null) return null;

                int idx = 0;
                try { idx = cha.fileStatus.coordinateType; } catch { }
                if (idx < 0 || idx >= coords.Length) idx = 0;
                return coords[idx];
            }
            catch { return null; }
        }

        /// <summary>找出第一個參數是 string 的同名方法，參數數量少的優先。</summary>
        static MethodInfo FindSave(Type t, string name)
        {
            MethodInfo best = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                if (best == null || ps.Length < best.GetParameters().Length) best = m;
            }
            return best;
        }

        /// <summary>第一格填路徑，遇到 byte/int 型別的第二個參數就填性別，其餘給預設值。</summary>
        static object[] BuildArgs(MethodInfo m, string path, byte sex)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = path;

            for (int i = 1; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (i == 1 && (pt == typeof(byte) || pt == typeof(int)))
                    args[i] = pt == typeof(byte) ? (object)sex : (object)(int)sex;
                else if (pt == typeof(bool))
                    args[i] = true;                       // savePng 之類的旗標
                else
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            return args;
        }

        static string DumpMethods(Type t, string contains)
        {
            var sb = new StringBuilder();
            sb.AppendLine("可用的方法（" + t.FullName + "）：");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var args = new List<string>();
                foreach (var p in m.GetParameters()) args.Add(p.ParameterType.Name + " " + p.Name);
                sb.AppendLine("  " + m.ReturnType.Name + " " + m.Name
                              + "(" + string.Join(", ", args.ToArray()) + ")");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 換人時保留場景裡的表情。
    ///
    /// 工作室的 OCIChar.ChangeChara 會用 LoadCharaFile 把新卡整張讀進來，
    /// 連 ChaFileStatus（眉／眼／嘴的表情編號、開合上限、眨眼、臉紅、眼淚、
    /// 眼睛高光、視線／脖子方向）也一起換成新卡存檔時的狀態。
    /// 它之後只補回眼睛開合、眨眼、嘴巴開合三項，姿勢暫存（PauseCtrl）也不含表情編號，
    /// 所以換完人「表情不一樣」其實是被換成新卡自己的預設表情。
    ///
    /// 做法：ChangeChara 之前把這些欄位讀起來，換完再用 ChaControl / OCIChar
    /// 各自的 Change* 方法寫回去（找不到方法就直接寫 fileStatus）。全部走反射，
    /// 哪個遊戲版本少一個方法也只會略過那一項。
    /// </summary>
    public static class ExpressionFix
    {
        public static string LastReport = "";

        // statusMember, ChaControl 方法, OCIChar 方法（優先）
        static readonly string[][] Items =
        {
            new[] { "eyebrowPtn",       "ChangeEyebrowPtn",      null },
            new[] { "eyesPtn",          "ChangeEyesPtn",         null },
            new[] { "mouthPtn",         "ChangeMouthPtn",        null },
            new[] { "eyebrowOpenMax",   "ChangeEyebrowOpenMax",  null },
            new[] { "eyesOpenMax",      "ChangeEyesOpenMax",     "ChangeEyesOpen" },
            new[] { "mouthOpenMax",     "ChangeMouthOpenMax",    null },
            new[] { "eyesBlink",        "ChangeEyesBlinkFlag",   "ChangeBlink" },
            new[] { "eyesYure",         "ChangeEyesShaking",     null },
            new[] { "mouthFixed",       "ChangeMouthFixed",      null },
            new[] { "mouthAdjustWidth", "ChangeMouthAdjustWidth", null },
            new[] { "tongueState",      "ChangeTongueState",     null },
            new[] { "hohoAkaRate",      "ChangeHohoAkaRate",     null },
            new[] { "hideEyesHighlight","HideEyeHighlight",      null },
            new[] { "tearsLv",          null,                    "SetTearsLv" },
            new[] { "eyesLookPtn",      null,                    "ChangeLookEyesPtn" },
            new[] { "neckLookPtn",      null,                    "ChangeLookNeckPtn" },
        };

        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public class Snapshot
        {
            public readonly Dictionary<string, object> values = new Dictionary<string, object>();
        }

        static object StatusOf(OCIChar oci)
        {
            try { return oci.charInfo.fileStatus; } catch { return null; }
        }

        static object GetMember(object o, string name)
        {
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BF);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            FieldInfo f = t.GetField(name, BF);
            return f != null ? f.GetValue(o) : null;
        }

        static bool SetMember(object o, string name, object v)
        {
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BF);
            if (p != null && p.CanWrite) { p.SetValue(o, Convert.ChangeType(v, p.PropertyType), null); return true; }
            FieldInfo f = t.GetField(name, BF);
            if (f != null) { f.SetValue(o, Convert.ChangeType(v, f.FieldType)); return true; }
            return false;
        }

        /// <summary>呼叫第一個參數吃得下這個值的同名方法，其餘參數用預設值（沒有預設值的 bool 給 true）。</summary>
        static bool Call(object target, string method, object v)
        {
            if (target == null || string.IsNullOrEmpty(method)) return false;
            foreach (MethodInfo m in target.GetType().GetMethods(BF))
            {
                if (m.Name != method) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 0) continue;
                object a0;
                try { a0 = Convert.ChangeType(v, ps[0].ParameterType); } catch { continue; }
                object[] args = new object[ps.Length];
                args[0] = a0;
                bool ok = true;
                for (int i = 1; i < ps.Length; i++)
                {
                    if ((ps[i].Attributes & ParameterAttributes.HasDefault) != 0) args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType == typeof(bool)) args[i] = true;
                    else if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    else { ok = false; break; }
                }
                if (!ok) continue;
                try { m.Invoke(target, args); return true; }
                catch (Exception e) { Debug.LogWarning("[ExpressionFix] " + method + " failed: " + (e.InnerException ?? e).Message); }
            }
            return false;
        }

        public static Snapshot Capture(OCIChar oci)
        {
            if (oci == null) { LastReport = "沒有角色"; return null; }
            object st = StatusOf(oci);
            if (st == null) { LastReport = "讀不到 fileStatus"; return null; }
            var snap = new Snapshot();
            var sb = new StringBuilder();
            foreach (var it in Items)
            {
                try
                {
                    object v = GetMember(st, it[0]);
                    if (v == null) continue;
                    snap.values[it[0]] = v;
                    sb.Append(it[0]).Append('=').Append(v).Append(' ');
                }
                catch { }
            }
            LastReport = "表情 " + snap.values.Count + " 項：" + sb.ToString().TrimEnd();
            return snap.values.Count > 0 ? snap : null;
        }

        /// <summary>寫回表情，回傳成功的項數。</summary>
        public static int Restore(OCIChar oci, Snapshot snap)
        {
            if (oci == null || snap == null) return 0;
            ChaControl cha = oci.charInfo;
            object st = StatusOf(oci);
            int ok = 0;
            var miss = new List<string>();
            foreach (var it in Items)
            {
                object v;
                if (!snap.values.TryGetValue(it[0], out v)) continue;
                bool done = false;
                try
                {
                    done = Call(oci, it[2], v) || Call(cha, it[1], v);
                    if (!done && st != null) done = SetMember(st, it[0], v);
                }
                catch (Exception e) { Debug.LogWarning("[ExpressionFix] " + it[0] + ": " + e.Message); }
                if (done) ok++; else miss.Add(it[0]);
            }
            LastReport = "表情已寫回 " + ok + " 項" + (miss.Count > 0 ? "，略過：" + string.Join(", ", miss.ToArray()) : "");
            return ok;
        }
    }
}
