using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 合併卡的畫面效果 / 角色燈光切換（跟 MapSwitch 同一套做法）。
    ///
    /// 畫面效果（調色、AO、景深、暈影、霧、光暈、光束…）和角色燈光都是整張卡一份的設定。
    /// 合併工具在每段的包裝資料夾底下放一個記號資料夾：
    ///
    ///     [ENV] v=3 ac=2 ab=0.25 cli=0.58 …               新版：每段各自完整一套 ——
    ///                                                     短代號，跟固定預設一樣的欄位不寫（見 Table）
    ///           ac.g=<GUID> / rp.g=<GUID>                 模組的調色 / Ramp，問 UAR 換成本機編號
    ///     [ENV] v=2 aceNo=2 aceBlend=0.25 …               v=2：長欄位名、全部都寫
    ///     [ENV] base / [ENV] aceNo=2 …                    舊版：卡片載入時的值 + 差異
    ///
    /// 播到哪一段就套那一段：先回到「卡片載入時」的那一套（基準），再蓋上記號寫的欄位，
    /// 最後照 Studio 載入場景時的做法呼叫 SystemButtonCtrl.UpdateInfo() 和
    /// CameraLightCtrl.Reflect() 讓它生效。新版記號每個欄位都有寫，基準完全被蓋掉，
    /// 所以在 Studio 裡播到哪一段存檔（卡片本身的值變成那一段的）都不會影響其他段。
    ///
    /// 記號資料夾本身或上面任何一層（包裝資料夾、再外層）沒打勾 → 這一段不套，用卡片本身存的。
    /// </summary>
    internal static class EnvSwitch
    {
        class Marker
        {
            public Studio.ObjectCtrlInfo wrap;
            public Studio.OCIFolder folder;
            public Dictionary<string, string> kv = new Dictionary<string, string>();
        }

        /// <summary>卡片載入時的那一套（第 1 張的值，已經由 Studio / UAR 正確換算過）。</summary>
        class Snapshot
        {
            public int aceNo, sunCaster, rampG;
            public float aceBlend, aoeRadius, bloomIntensity, bloomBlur, bloomThreshold,
                         depthFocalSize, depthAperture, fogHeight, fogStartDistance,
                         lineColorG, lineWidthG, ambientShadowG;
            public bool enableAOE, enableBloom, enableDepth, enableVignette, enableFog,
                        enableSunShafts, enableShadow, faceNormal, faceShadow;
            public Color aoeColor, fogColor, sunThresholdColor, sunColor, ambientShadow;
            public Color clColor;
            public float clIntensity, clRx, clRy;
            public bool clShadow;
        }

        // [ENV] v=3 的欄位表：{ 欄位, 短代號, 固定預設 }。
        // 跟合併工具 kkscenemerge.py 的 ENV_TABLE 一模一樣，兩邊要一起改。
        // 記號沒寫的欄位 = 固定預設（常數，不是卡片本身存的值）。
        static readonly string[,] Table = {
            { "aceNo", "ac", "0" },
            { "aceBlend", "ab", "0" },
            { "enableAOE", "ao", "1" },
            { "aoeColor", "aoc", "#B4B4B4FF" },
            { "aoeRadius", "aor", "0.1" },
            { "enableBloom", "bl", "1" },
            { "bloomIntensity", "bli", "0.4" },
            { "bloomThreshold", "blt", "0.6" },
            { "bloomBlur", "blb", "0.8" },
            { "enableDepth", "dp", "0" },
            { "depthFocalSize", "dpf", "0.95" },
            { "depthAperture", "dpa", "0.6" },
            { "enableVignette", "vg", "1" },
            { "enableFog", "fg", "0" },
            { "fogColor", "fgc", "#89C1DDFF" },
            { "fogHeight", "fgh", "1" },
            { "fogStartDistance", "fgs", "0" },
            { "enableSunShafts", "ss", "0" },
            { "sunThresholdColor", "sst", "#808080FF" },
            { "sunColor", "ssc", "#FFFFFFFF" },
            { "sunCaster", "sso", "-1" },
            { "enableShadow", "sh", "1" },
            { "faceNormal", "fn", "0" },
            { "faceShadow", "fs", "0" },
            { "lineColorG", "lc", "0.65043" },
            { "ambientShadow", "asc", "#808080FF" },
            { "lineWidthG", "lw", "0.37838" },
            { "rampG", "rp", "1" },
            { "ambientShadowG", "as", "0.25889" },
            { "cl.col", "clc", "#FFFFFFFF" },
            { "cl.int", "cli", "1" },
            { "cl.rx", "clx", "0" },
            { "cl.ry", "cly", "0" },
            { "cl.sh", "cls", "1" }
        };

        static readonly Regex RX = new Regex(@"^\[ENV\](.*)$");
        static readonly Regex KV = new Regex(@"([\w\.]+)\s*=\s*(\S+)");

        static List<Marker> markers;
        static Snapshot baseline;
        static Marker applied;
        static bool appliedOn;
        static float rescanUntil, nextRetry;
        static bool forceScan;

        public static string Status = "";

        public static void Reset()
        {
            // 換卡時不必還原：新卡載入會自己寫一整套
            markers = null;
            baseline = null;
            applied = null;
            rescanUntil = Time.realtimeSinceStartup + 10f;
            nextRetry = 0f;
            Status = "";
        }

        public static void Rescan() { forceScan = true; }

        public static void Tick()
        {
            Studio.Studio st;
            try { st = Singleton<Studio.Studio>.Instance; } catch { return; }
            if (st == null || st.dicObjectCtrl == null || st.sceneInfo == null) return;

            if (markers == null || forceScan
                || (markers.Count == 0 && Time.realtimeSinceStartup < rescanUntil
                    && Time.realtimeSinceStartup >= nextRetry))
            {
                Scan(st);
                forceScan = false;
                nextRetry = Time.realtimeSinceStartup + 1f;
            }
            if (markers == null || markers.Count == 0) return;
            // 記號資料夾已經不在場景裡（初始化、刪除）→ 作廢，不要拿著舊記號繼續套
            int dead = markers.RemoveAll(m => !MapSwitch.Alive(st, m.folder) || !MapSwitch.Alive(st, m.wrap));
            if (dead > 0)
            {
                if (applied != null && !markers.Contains(applied)) applied = null;
                if (markers.Count == 0) { baseline = null; Status = ""; return; }
            }
            if (baseline == null) baseline = Take(st);      // 還沒動過任何東西 → 這就是卡片載入的值

            var cand = new List<Marker>();
            foreach (var m in markers)
                if (MapSwitch.NotParkedChain(m.folder)) cand.Add(m);
            Marker act = null;
            if (cand.Count == 1) act = cand[0];
            else
                foreach (var m in cand)
                    if (MapSwitch.Visible(m.wrap)) { act = m; break; }
            if (act == null) return;

            bool on = MapSwitch.Visible(act.folder);
            if (act == applied && on == appliedOn) return;
            Apply(st, act, on);
        }

        static void Scan(Studio.Studio st)
        {
            var list = new List<Marker>();
            foreach (var kv in st.dicObjectCtrl)
            {
                var f = kv.Value as Studio.OCIFolder;
                if (f == null || f.parentInfo == null) continue;
                var m = RX.Match(f.name ?? "");
                if (!m.Success) continue;
                Marker mk = markers == null ? null : markers.Find(x => x.folder == f);
                if (mk == null) mk = new Marker();
                mk.folder = f;
                mk.wrap = MapSwitch.RootOf(f);      // 段落 = 最外層的包裝資料夾（見 MapSwitch）
                mk.kv.Clear();
                foreach (Match p in KV.Matches(m.Groups[1].Value))
                    mk.kv[p.Groups[1].Value] = p.Groups[2].Value;
                list.Add(mk);
            }
            if (applied != null && !list.Contains(applied)) applied = null;
            markers = list;
        }

        static void Apply(Studio.Studio st, Marker m, bool on)
        {
            var si = st.sceneInfo;
            Put(si, baseline);
            int n = 0, miss = 0;
            if (on)
            {
                foreach (var e in Fields(m.kv))
                {
                    string v = e.Value;
                    string g;
                    if ((e.Key == "aceNo" || e.Key == "rampG") && m.kv.TryGetValue(GuidKey(m.kv, e.Key), out g))
                    {
                        // 模組的調色 / Ramp：記號上是模組自己的編號，換成本機編號
                        int slot, loc = -1;
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                        {
                            string guid = Uar.Unescape(g);
                            loc = e.Key == "aceNo" ? Uar.StudioLocal(guid, slot) : Uar.RampLocal(guid, slot);
                            if (loc < 0)
                                Debug.LogWarning("[CutScene] 畫面效果：本機找不到模組 " + guid + " #" + slot
                                                 + "（" + e.Key + " 這一項不切換）");
                        }
                        if (loc < 0) { miss++; continue; }
                        v = loc.ToString(CultureInfo.InvariantCulture);
                    }
                    if (Set(si, e.Key, v)) n++;
                }
            }
            try { st.systemButtonCtrl.UpdateInfo(); }
            catch (Exception ex) { Debug.LogWarning("[CutScene] 畫面效果套用失敗：" + ex.Message); }
            try { st.cameraLightCtrl.Reflect(); }
            catch (Exception ex) { Debug.LogWarning("[CutScene] 角色燈光套用失敗：" + ex.Message); }
            applied = m;
            appliedOn = on;
            bool full = m.kv.ContainsKey("v");
            Status = !on ? "畫面效果：記號資料夾或上層沒打勾（用卡片本身存的）"
                   : full ? "畫面效果：這一段的（完整 " + n + " 項" + (miss > 0 ? "，" + miss + " 項找不到模組" : "") + "）"
                   : n == 0 ? "畫面效果：卡片本身存的（舊版記號）"
                   : "畫面效果：這一段的（舊版記號，" + n + " 項）";
            Debug.Log("[CutScene] " + Status);
        }

        /// <summary>記號 → 要套的（欄位, 值）。v=3 會展開成完整一套（沒寫的補固定預設）。</summary>
        static IEnumerable<KeyValuePair<string, string>> Fields(Dictionary<string, string> kv)
        {
            string ver;
            kv.TryGetValue("v", out ver);
            if (ver == "3")
            {
                for (int i = 0; i < Table.GetLength(0); i++)
                {
                    string v;
                    if (!kv.TryGetValue(Table[i, 1], out v)) v = Table[i, 2];
                    yield return new KeyValuePair<string, string>(Table[i, 0], v);
                }
                yield break;
            }
            foreach (var e in kv) yield return e;      // v=2 / 舊版：本來就是長欄位名
        }

        static string GuidKey(Dictionary<string, string> kv, string field)
        {
            string ver;
            kv.TryGetValue("v", out ver);
            if (ver == "3") return field == "aceNo" ? "ac.g" : "rp.g";
            return field + ".g";
        }

        // ------------------------------------------------------------ 基準

        static Snapshot Take(Studio.Studio st)
        {
            var si = st.sceneInfo;
            var s = new Snapshot
            {
                aceNo = si.aceNo, aceBlend = si.aceBlend,
                enableAOE = si.enableAOE, aoeColor = si.aoeColor, aoeRadius = si.aoeRadius,
                enableBloom = si.enableBloom, bloomIntensity = si.bloomIntensity,
                bloomBlur = si.bloomBlur, bloomThreshold = si.bloomThreshold,
                enableDepth = si.enableDepth, depthFocalSize = si.depthFocalSize,
                depthAperture = si.depthAperture, enableVignette = si.enableVignette,
                enableFog = si.enableFog, fogColor = si.fogColor, fogHeight = si.fogHeight,
                fogStartDistance = si.fogStartDistance, enableSunShafts = si.enableSunShafts,
                sunThresholdColor = si.sunThresholdColor, sunColor = si.sunColor,
                sunCaster = si.sunCaster, enableShadow = si.enableShadow,
                faceNormal = si.faceNormal, faceShadow = si.faceShadow,
                lineColorG = si.lineColorG, ambientShadow = si.ambientShadow,
                lineWidthG = si.lineWidthG, rampG = si.rampG, ambientShadowG = si.ambientShadowG,
            };
            try
            {
                var cl = si.charaLight;
                s.clColor = cl.color;
                s.clIntensity = cl.intensity;
                s.clRx = cl.rot != null && cl.rot.Length > 0 ? cl.rot[0] : 0f;
                s.clRy = cl.rot != null && cl.rot.Length > 1 ? cl.rot[1] : 0f;
                s.clShadow = cl.shadow;
            }
            catch { }
            return s;
        }

        static void Put(Studio.SceneInfo si, Snapshot s)
        {
            si.aceNo = s.aceNo; si.aceBlend = s.aceBlend;
            si.enableAOE = s.enableAOE; si.aoeColor = s.aoeColor; si.aoeRadius = s.aoeRadius;
            si.enableBloom = s.enableBloom; si.bloomIntensity = s.bloomIntensity;
            si.bloomBlur = s.bloomBlur; si.bloomThreshold = s.bloomThreshold;
            si.enableDepth = s.enableDepth; si.depthFocalSize = s.depthFocalSize;
            si.depthAperture = s.depthAperture; si.enableVignette = s.enableVignette;
            si.enableFog = s.enableFog; si.fogColor = s.fogColor; si.fogHeight = s.fogHeight;
            si.fogStartDistance = s.fogStartDistance; si.enableSunShafts = s.enableSunShafts;
            si.sunThresholdColor = s.sunThresholdColor; si.sunColor = s.sunColor;
            si.sunCaster = s.sunCaster; si.enableShadow = s.enableShadow;
            si.faceNormal = s.faceNormal; si.faceShadow = s.faceShadow;
            si.lineColorG = s.lineColorG; si.ambientShadow = s.ambientShadow;
            si.lineWidthG = s.lineWidthG; si.rampG = s.rampG; si.ambientShadowG = s.ambientShadowG;
            try
            {
                var cl = si.charaLight;
                cl.color = s.clColor;
                cl.intensity = s.clIntensity;
                if (cl.rot == null || cl.rot.Length < 2) cl.rot = new float[2];
                cl.rot[0] = s.clRx;
                cl.rot[1] = s.clRy;
                cl.shadow = s.clShadow;
            }
            catch { }
        }

        // ------------------------------------------------------------ 記號 → 欄位

        static bool Set(Studio.SceneInfo si, string k, string v)
        {
            try
            {
                switch (k)
                {
                    case "aceNo": si.aceNo = I(v); return true;
                    case "aceBlend": si.aceBlend = F(v); return true;
                    case "enableAOE": si.enableAOE = B(v); return true;
                    case "aoeColor": si.aoeColor = C(v); return true;
                    case "aoeRadius": si.aoeRadius = F(v); return true;
                    case "enableBloom": si.enableBloom = B(v); return true;
                    case "bloomIntensity": si.bloomIntensity = F(v); return true;
                    case "bloomBlur": si.bloomBlur = F(v); return true;
                    case "bloomThreshold": si.bloomThreshold = F(v); return true;
                    case "enableDepth": si.enableDepth = B(v); return true;
                    case "depthFocalSize": si.depthFocalSize = F(v); return true;
                    case "depthAperture": si.depthAperture = F(v); return true;
                    case "enableVignette": si.enableVignette = B(v); return true;
                    case "enableFog": si.enableFog = B(v); return true;
                    case "fogColor": si.fogColor = C(v); return true;
                    case "fogHeight": si.fogHeight = F(v); return true;
                    case "fogStartDistance": si.fogStartDistance = F(v); return true;
                    case "enableSunShafts": si.enableSunShafts = B(v); return true;
                    case "sunThresholdColor": si.sunThresholdColor = C(v); return true;
                    case "sunColor": si.sunColor = C(v); return true;
                    case "sunCaster": si.sunCaster = I(v); return true;
                    case "enableShadow": si.enableShadow = B(v); return true;
                    case "faceNormal": si.faceNormal = B(v); return true;
                    case "faceShadow": si.faceShadow = B(v); return true;
                    case "lineColorG": si.lineColorG = F(v); return true;
                    case "ambientShadow": si.ambientShadow = C(v); return true;
                    case "lineWidthG": si.lineWidthG = F(v); return true;
                    case "rampG": si.rampG = I(v); return true;
                    case "ambientShadowG": si.ambientShadowG = F(v); return true;
                    case "cl.col": si.charaLight.color = C(v); return true;
                    case "cl.int": si.charaLight.intensity = F(v); return true;
                    case "cl.rx": Rot(si)[0] = F(v); return true;
                    case "cl.ry": Rot(si)[1] = F(v); return true;
                    case "cl.sh": si.charaLight.shadow = B(v); return true;
                }
            }
            catch { }
            return false;
        }

        static float[] Rot(Studio.SceneInfo si)
        {
            var cl = si.charaLight;
            if (cl.rot == null || cl.rot.Length < 2) cl.rot = new float[2];
            return cl.rot;
        }

        static int I(string v) { return int.Parse(v, CultureInfo.InvariantCulture); }
        static float F(string v) { return float.Parse(v, CultureInfo.InvariantCulture); }
        static bool B(string v) { return v == "1" || v.ToLowerInvariant() == "true"; }

        static Color C(string v)
        {
            v = v.TrimStart('#');
            if (v.Length < 6) throw new FormatException(v);
            Func<int, float> h = i => int.Parse(v.Substring(i, 2), NumberStyles.HexNumber) / 255f;
            return new Color(h(0), h(2), h(4), v.Length >= 8 ? h(6) : 1f);
        }
    }
}
