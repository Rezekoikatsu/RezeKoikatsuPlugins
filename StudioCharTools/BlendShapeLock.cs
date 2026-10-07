using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using BepInEx;
using HarmonyLib;      // BepInEx 5.4 = HarmonyX；舊版請改成 using Harmony;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// BlendShape 鎖定 / 範圍限制模組。
    /// 在所有 LateUpdate 之後、蒙皮烘焙之前把權重寫回去，
    /// 因此 Timeline、KKPE、FBSCtrl 誰改都無所謂，最後一手是我們的。
    /// </summary>
    public static class BlendShapeLock
    {
        // ---------------------------------------------------------------
        // 資料結構
        // ---------------------------------------------------------------
        public enum LockMode
        {
            Fixed,   // 固定成單一數值
            Clamp,   // 只限制上下限，範圍內仍可自由跑動
            Scale    // 比例：別人（表情、KKPE、Timeline）寫多少，就乘上倍率再輸出
        }

        public class LockEntry
        {
            public string smrName;      // 例如 "cf_O_face"
            public string shapeName;    // 例如 "kuti_face.f00_neko_cl"
            public LockMode mode = LockMode.Fixed;
            public float value;         // Fixed 用
            public float min = 0f;      // Clamp 用
            public float max = 100f;    // Clamp 用
            public bool enabled = true;
            public float scale = 1f;    // Scale 用（輸出上限共用 max）

            [NonSerialized] public SkinnedMeshRenderer smr;
            // Scale 用：上一幀我們寫出去的值和當時的原值。
            // 這一幀讀到的值跟上次寫出去的一樣 = 沒人重寫，原值沿用上次的，
            // 否則每幀都乘一次會一路縮到 0（沒有每幀重設的鍵就會這樣）。
            [NonSerialized] public bool hasLast;
            [NonSerialized] public float lastOut, lastBase;
            [NonSerialized] public int index = -1;
            [NonSerialized] public int nextResolveFrame;
        }

        static readonly Dictionary<ChaControl, List<LockEntry>> _locks =
            new Dictionary<ChaControl, List<LockEntry>>();

        static bool _inited;
        static GameObject _enforcerGo;

        // ---------------------------------------------------------------
        // 執行時機
        // ---------------------------------------------------------------
        /// <summary>
        /// DefaultExecutionOrder 給很大的值，確保排在 KKPE / Timeline / ChaControl
        /// （預設順序 0）之後，但仍早於 PostLateUpdate 的蒙皮烘焙。
        /// </summary>
        [DefaultExecutionOrder(31000)]
        public class LateEnforcer : MonoBehaviour
        {
            void LateUpdate() { Apply(); }
        }

        public static void Init()
        {
            if (_inited) return;
            _inited = true;

            _enforcerGo = new GameObject("BlendShapeLockEnforcer");
            _enforcerGo.hideFlags = HideFlags.HideAndDontSave;
            GameObject.DontDestroyOnLoad(_enforcerGo);
            _enforcerGo.AddComponent<LateEnforcer>();
        }

        public static void Dispose()
        {
            if (_enforcerGo != null) GameObject.Destroy(_enforcerGo);
            _enforcerGo = null;
            _inited = false;
        }

        // ---------------------------------------------------------------
        // 核心
        // ---------------------------------------------------------------
        public static void Apply()
        {
            List<ChaControl> dead = null;

            foreach (var kv in _locks)
            {
                var cha = kv.Key;
                if (cha == null || cha.objBodyBone == null)
                {
                    (dead ?? (dead = new List<ChaControl>())).Add(cha);
                    continue;
                }

                var list = kv.Value;
                SkinnedMeshRenderer[] smrCache = null;

                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (!e.enabled) continue;

                    if (e.smr == null || e.index < 0)
                    {
                        if (Time.frameCount < e.nextResolveFrame) continue;
                        if (smrCache == null)
                            smrCache = cha.GetComponentsInChildren<SkinnedMeshRenderer>(true);

                        Resolve(smrCache, e);
                        if (e.index < 0) { e.nextResolveFrame = Time.frameCount + 120; continue; }
                    }

                    if (e.mode == LockMode.Fixed)
                    {
                        e.smr.SetBlendShapeWeight(e.index, e.value);
                    }
                    else if (e.mode == LockMode.Scale)
                    {
                        float cur = e.smr.GetBlendShapeWeight(e.index);
                        float baseV = (e.hasLast && Mathf.Abs(cur - e.lastOut) < 0.0005f) ? e.lastBase : cur;
                        float outV = Mathf.Clamp(baseV * e.scale, 0f, e.max);
                        if (outV != cur) e.smr.SetBlendShapeWeight(e.index, outV);
                        e.hasLast = true; e.lastBase = baseV; e.lastOut = outV;
                    }
                    else
                    {
                        // 讀取這一幀別人寫進去的值，超出範圍才拉回來
                        float cur = e.smr.GetBlendShapeWeight(e.index);
                        float clamped = Mathf.Clamp(cur, e.min, e.max);
                        if (cur != clamped) e.smr.SetBlendShapeWeight(e.index, clamped);
                    }
                }
            }

            if (dead != null)
                foreach (var c in dead) _locks.Remove(c);
        }

        static void Resolve(ChaControl cha, LockEntry e)
        {
            if (cha == null) return;
            Resolve(cha.GetComponentsInChildren<SkinnedMeshRenderer>(true), e);
        }

        static void Resolve(SkinnedMeshRenderer[] smrs, LockEntry e)
        {
            e.smr = null;
            e.index = -1;
            e.hasLast = false;

            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (smr.name != e.smrName) continue;

                int idx = smr.sharedMesh.GetBlendShapeIndex(e.shapeName);
                if (idx < 0) continue;

                e.smr = smr;
                e.index = idx;
                return;
            }
        }

        // ---------------------------------------------------------------
        // 查詢 / 編輯
        // ---------------------------------------------------------------
        public class ShapeInfo
        {
            public SkinnedMeshRenderer smr;
            public int index;
            public string shapeName;
            public string smrName;
            public float Current { get { return smr.GetBlendShapeWeight(index); } }
            public void SetNow(float v) { smr.SetBlendShapeWeight(index, v); }
        }

        public static List<ShapeInfo> Enumerate(ChaControl cha)
        {
            var result = new List<ShapeInfo>();
            if (cha == null) return result;

            foreach (var smr in cha.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    result.Add(new ShapeInfo
                    {
                        smr = smr,
                        index = i,
                        shapeName = mesh.GetBlendShapeName(i),
                        smrName = smr.name
                    });
                }
            }
            return result;
        }

        public static List<LockEntry> GetLocks(ChaControl cha)
        {
            List<LockEntry> list;
            if (!_locks.TryGetValue(cha, out list))
            {
                list = new List<LockEntry>();
                _locks[cha] = list;
            }
            return list;
        }

        public static LockEntry Find(ChaControl cha, string smrName, string shapeName)
        {
            if (cha == null) return null;
            return GetLocks(cha).FirstOrDefault(x => x.smrName == smrName && x.shapeName == shapeName);
        }

        /// <summary>建立或取得一筆鎖定。value 傳 null 代表沿用畫面上的現值。</summary>
        public static LockEntry SetLock(ChaControl cha, string smrName, string shapeName, float? value)
        {
            var e = Find(cha, smrName, shapeName);
            if (e == null)
            {
                e = new LockEntry { smrName = smrName, shapeName = shapeName };
                GetLocks(cha).Add(e);
            }

            if (value.HasValue)
            {
                e.value = Mathf.Clamp(value.Value, 0f, 100f);
            }
            else
            {
                Resolve(cha, e);
                e.value = (e.smr != null && e.index >= 0) ? e.smr.GetBlendShapeWeight(e.index) : 0f;
            }

            e.enabled = true;
            return e;
        }

        /// <summary>獨佔模式：開啟後，每次建立新鎖定會先把其他鎖定歸零並移除。</summary>
        public static bool Exclusive;

        /// <summary>建立鎖定並套用獨佔模式規則，同時直接指定為「固定」模式的數值。</summary>
        public static LockEntry SetFixedLock(ChaControl cha, string smrName, string shapeName, float? value)
        {
            if (cha == null) return null;
            if (Exclusive) ClearOthers(cha, smrName, shapeName);

            var e = SetLock(cha, smrName, shapeName, value);
            if (e != null) e.mode = LockMode.Fixed;
            return e;
        }

        /// <summary>建立或改成「比例」模式。已經有的鎖會被改成比例，倍率換成新的。</summary>
        public static LockEntry SetScale(ChaControl cha, string smrName, string shapeName, float scale, float cap)
        {
            if (cha == null) return null;
            var e = Find(cha, smrName, shapeName);
            if (e == null)
            {
                e = new LockEntry { smrName = smrName, shapeName = shapeName };
                GetLocks(cha).Add(e);
            }
            e.mode = LockMode.Scale;
            e.scale = Mathf.Clamp(scale, 0f, 3f);
            e.min = 0f;
            e.max = Mathf.Clamp(cap, 0f, 100f);
            e.enabled = true;
            e.hasLast = false;
            return e;
        }

        /// <summary>把「除了指定項目以外」的鎖定全部歸零後移除。</summary>
        public static void ClearOthers(ChaControl cha, string smrName, string shapeName)
        {
            if (cha == null) return;
            var list = GetLocks(cha);
            if (list.Count == 0) return;

            SkinnedMeshRenderer[] smrs = null;

            foreach (var e in list)
            {
                if (e.smrName == smrName && e.shapeName == shapeName) continue;

                if (e.smr == null || e.index < 0)
                {
                    if (smrs == null) smrs = cha.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    Resolve(smrs, e);
                }
                if (e.smr != null && e.index >= 0)
                    e.smr.SetBlendShapeWeight(e.index, 0f);
            }

            list.RemoveAll(x => !(x.smrName == smrName && x.shapeName == shapeName));
        }

        public static void RemoveLock(ChaControl cha, string smrName, string shapeName)
        {
            if (cha == null) return;
            GetLocks(cha).RemoveAll(x => x.smrName == smrName && x.shapeName == shapeName);
        }

        public static void ClearAll(ChaControl cha)
        {
            if (cha != null) GetLocks(cha).Clear();
        }

        public static int LockCount(ChaControl cha)
        {
            return cha == null ? 0 : GetLocks(cha).Count;
        }

        // ---------------------------------------------------------------
        // 序列化（場景卡與預設檔共用）
        // 格式：smrName \t shapeName \t mode \t value \t min \t max \t enabled \t scale
        // （第 8 欄是後來加的；舊版讀到會忽略，新版讀舊檔當 1）
        // ---------------------------------------------------------------
        static string F(float v) { return v.ToString("F3", CultureInfo.InvariantCulture); }
        static float P(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        public static string Serialize(ChaControl cha)
        {
            if (cha == null) return "";
            return string.Join("\n", GetLocks(cha)
                .Select(x => string.Join("\t", new[]
                {
                    x.smrName, x.shapeName, ((int)x.mode).ToString(),
                    F(x.value), F(x.min), F(x.max), x.enabled ? "1" : "0", F(x.scale)
                }))
                .ToArray());
        }

        public static void Deserialize(ChaControl cha, string data)
        {
            if (cha == null) return;
            var list = GetLocks(cha);
            list.Clear();
            if (string.IsNullOrEmpty(data)) return;

            foreach (var line in data.Split('\n'))
            {
                if (line.Length == 0) continue;
                var p = line.Split('\t');

                // 舊格式相容：smrName \t shapeName \t value \t enabled
                if (p.Length == 4)
                {
                    list.Add(new LockEntry
                    {
                        smrName = p[0],
                        shapeName = p[1],
                        mode = LockMode.Fixed,
                        value = P(p[2]),
                        min = 0f,
                        max = 100f,
                        enabled = p[3] == "1"
                    });
                    continue;
                }

                if (p.Length < 7) continue;
                int m;
                int.TryParse(p[2], out m);
                list.Add(new LockEntry
                {
                    smrName = p[0],
                    shapeName = p[1],
                    mode = (LockMode)m,
                    value = P(p[3]),
                    min = P(p[4]),
                    max = P(p[5]),
                    enabled = p[6] == "1",
                    scale = p.Length >= 8 ? P(p[7]) : 1f
                });
            }
        }

        // ---------------------------------------------------------------
        // 預設檔（存到 BepInEx\config\BlendShapeLock\*.txt）
        // ---------------------------------------------------------------
        public static string PresetDir
        {
            get { return Path.Combine(Paths.ConfigPath, "BlendShapeLock"); }
        }

        static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// 角色的名字，整理成可以當檔名的樣子。
        /// 「換完人自動帶入」就是靠這個對名字 —— 預設檔叫什麼、角色叫什麼，一樣就帶。
        /// </summary>
        public static string NameOf(ChaControl cha)
        {
            try
            {
                if (cha != null && cha.chaFile != null && cha.chaFile.parameter != null)
                {
                    string last = cha.chaFile.parameter.lastname ?? "";
                    string first = cha.chaFile.parameter.firstname ?? "";
                    return SanitizeName((last + first).Trim());
                }
            }
            catch { }
            return null;
        }

        public static string PresetPathFor(string name)
        {
            string n = SanitizeName(name);
            return n == null ? null : Path.Combine(PresetDir, n + ".txt");
        }

        /// <summary>有沒有跟這個角色同名的預設檔。</summary>
        public static bool HasPresetFor(ChaControl cha)
        {
            string path = PresetPathFor(NameOf(cha));
            try { return path != null && File.Exists(path); }
            catch { return false; }
        }

        /// <summary>
        /// 換完人之後自動帶入同名的預設。回傳有沒有真的帶入。
        ///
        /// 刻意用 replace = true：自動帶入的語意是「這個角色就該長這樣」，
        /// 附加上去的話會跟前一個角色殘留的鎖疊在一起，變成沒人看得懂的狀態。
        /// </summary>
        public static bool AutoApply(ChaControl cha)
        {
            string name = NameOf(cha);
            string path = PresetPathFor(name);
            if (path == null) return false;
            try { if (!File.Exists(path)) return false; }
            catch { return false; }
            return LoadPreset(name, cha, true);
        }

        public static List<string> ListPresets()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(PresetDir)) return result;
                foreach (var f in Directory.GetFiles(PresetDir, "*.txt"))
                    result.Add(Path.GetFileNameWithoutExtension(f));
                result.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Debug.LogWarning("[BlendShapeLock] 讀取預設清單失敗：" + ex.Message); }
            return result;
        }

        public static bool SavePreset(string name, ChaControl cha)
        {
            name = SanitizeName(name);
            if (name == null || cha == null) return false;
            try
            {
                Directory.CreateDirectory(PresetDir);
                File.WriteAllText(Path.Combine(PresetDir, name + ".txt"), Serialize(cha));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BlendShapeLock] 儲存預設失敗：" + ex.Message);
                return false;
            }
        }

        /// <summary>套用預設。replace = true 取代現有鎖定，false 則合併（同名覆寫）。</summary>
        public static bool LoadPreset(string name, ChaControl cha, bool replace)
        {
            name = SanitizeName(name);
            if (name == null || cha == null) return false;

            var path = Path.Combine(PresetDir, name + ".txt");
            if (!File.Exists(path)) return false;

            try
            {
                var text = File.ReadAllText(path);

                if (replace)
                {
                    Deserialize(cha, text);
                }
                else
                {
                    var backup = Serialize(cha);
                    Deserialize(cha, text);              // 先載入預設
                    var incoming = GetLocks(cha).ToList();
                    Deserialize(cha, backup);            // 還原原本的
                    var list = GetLocks(cha);
                    foreach (var e in incoming)
                    {
                        list.RemoveAll(x => x.smrName == e.smrName && x.shapeName == e.shapeName);
                        list.Add(e);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BlendShapeLock] 載入預設失敗：" + ex.Message);
                return false;
            }
        }

        public static void DeletePreset(string name)
        {
            name = SanitizeName(name);
            if (name == null) return;
            try
            {
                var path = Path.Combine(PresetDir, name + ".txt");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { Debug.LogWarning("[BlendShapeLock] 刪除預設失敗：" + ex.Message); }
        }
    }

    // ===================================================================
    // 選用：連「表情圖樣」一起鎖住
    // 場景卡的貓嘴多半是呼叫 ChangeMouthPtn 切換口型圖樣，
    // 再由 FBSCtrl 依開合度驅動 kuti_face.f00_neko_* 這組鍵。
    // ※ 方法簽名請先用 dnSpy 確認，KK / KKS 之間有差異。
    // ===================================================================
    public static class FacePtnLock
    {
        public static readonly HashSet<ChaControl> LockMouth = new HashSet<ChaControl>();
        public static readonly HashSet<ChaControl> LockEyes = new HashSet<ChaControl>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaControl), "ChangeMouthPtn")]
        static bool PreChangeMouthPtn(ChaControl __instance)
        {
            return !LockMouth.Contains(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaControl), "ChangeEyesPtn")]
        static bool PreChangeEyesPtn(ChaControl __instance)
        {
            return !LockEyes.Contains(__instance);
        }
    }

    // ===================================================================
    // KKPE 形態鍵編輯器裡「調整過」（紫色）的項目
    //
    // KKPE 的 BlendShapesEditor 把改過的鍵記在每個 BlendRenderer 的 _dirtyBlends，
    // 介面上紫色就是看這個（渲染器清單：_dirtyBlends.Count > 0；單一鍵：在裡面）。
    // 這支 KKPE 是用「鍵名」當 key；原版 HSPE 是 _dirtySkinnedMeshRenderers +
    // dirtyBlendShapes（用索引當 key），兩種都認。全部走反射，沒裝 KKPE 就回 null。
    // ===================================================================
    public static class KkpeBlendDirty
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Dictionary<ChaControl, Component> _pcCache = new Dictionary<ChaControl, Component>();
        static ChaControl _setFor;
        static HashSet<string> _set;
        static float _setTime = -10f;

        public static string Key(SkinnedMeshRenderer smr, string shapeName)
        {
            return smr.GetInstanceID() + "/" + shapeName;
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BF | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        static Component FindPoseController(ChaControl cha)
        {
            Component pc;
            if (_pcCache.TryGetValue(cha, out pc) && pc != null) return pc;
            pc = null;
            // 先找角色本體與往上兩層，再往下找（HSPE 版本不同掛的位置不同）
            Transform t = cha.transform;
            for (int up = 0; up < 3 && t != null && pc == null; up++, t = t.parent)
                foreach (var c in t.GetComponents<MonoBehaviour>())
                    if (c != null && c.GetType().Name.EndsWith("PoseController")) { pc = c; break; }
            if (pc == null)
                foreach (var c in cha.GetComponentsInChildren<MonoBehaviour>(true))
                    if (c != null && c.GetType().Name.EndsWith("PoseController")) { pc = c; break; }
            _pcCache[cha] = pc;
            return pc;
        }

        /// <summary>
        /// KKPE 標紫色的鍵（Key(smr, 名稱)）。沒有 KKPE / 找不到編輯器時回 null。
        /// 半秒重算一次，IMGUI 每幀叫也不會卡。
        /// </summary>
        public static HashSet<string> For(ChaControl cha)
        {
            if (cha == null) return null;
            if (_setFor == cha && Time.realtimeSinceStartup - _setTime < 0.5f) return _set;
            _setFor = cha;
            _setTime = Time.realtimeSinceStartup;
            _set = Build(cha);
            return _set;
        }

        public static void Invalidate() { _setTime = -10f; }

        /// <summary>診斷用：KKPE 每個渲染器調過的鍵原始資料（渲染器 → _dirtyBlends）。沒有 KKPE 回 null。</summary>
        public static List<KeyValuePair<SkinnedMeshRenderer, IDictionary>> Raw(ChaControl cha)
        {
            try
            {
                Component pc = FindPoseController(cha);
                object ed = Get(pc, "_blendShapesEditor");
                if (ed == null) return null;
                var list = new List<KeyValuePair<SkinnedMeshRenderer, IDictionary>>();
                var brs = Get(ed, "_blendRenderers") as IDictionary;
                if (brs != null)
                {
                    foreach (DictionaryEntry de in brs)
                        list.Add(new KeyValuePair<SkinnedMeshRenderer, IDictionary>(
                            Get(de.Value, "_renderer") as SkinnedMeshRenderer, Get(de.Value, "_dirtyBlends") as IDictionary));
                    return list;
                }
                var old = Get(ed, "_dirtySkinnedMeshRenderers") as IDictionary;
                if (old != null)
                {
                    foreach (DictionaryEntry de in old)
                        list.Add(new KeyValuePair<SkinnedMeshRenderer, IDictionary>(
                            de.Key as SkinnedMeshRenderer, Get(de.Value, "dirtyBlendShapes") as IDictionary));
                    return list;
                }
                return null;
            }
            catch (Exception e) { Debug.LogWarning("[BlendShapeLock] 讀 KKPE 原始資料失敗：" + e.Message); return null; }
        }

        /// <summary>把一個物件的欄位（數字、布林、字串）攤成 "名=值" 字串，診斷用。</summary>
        public static string Describe(object o)
        {
            if (o == null) return "";
            var parts = new List<string>();
            for (Type t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BF | BindingFlags.DeclaredOnly))
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v is float) parts.Add(f.Name + "=" + ((float)v).ToString("0.###", CultureInfo.InvariantCulture));
                    else if (v is int || v is bool || v is string || v is double) parts.Add(f.Name + "=" + Convert.ToString(v, CultureInfo.InvariantCulture));
                }
            return string.Join(" ", parts.ToArray());
        }

        static HashSet<string> Build(ChaControl cha)
        {
            try
            {
                Component pc = FindPoseController(cha);
                object ed = Get(pc, "_blendShapesEditor");
                if (ed == null) return null;
                var set = new HashSet<string>();

                // 這支 KKPE：_blendRenderers → BlendRenderer { _renderer, _dirtyBlends(string → data) }
                var brs = Get(ed, "_blendRenderers") as IDictionary;
                if (brs != null)
                {
                    foreach (DictionaryEntry de in brs)
                    {
                        var smr = Get(de.Value, "_renderer") as SkinnedMeshRenderer;
                        var dirty = Get(de.Value, "_dirtyBlends") as IDictionary;
                        Add(set, smr, dirty);
                    }
                    return set;
                }

                // 原版 HSPE：_dirtySkinnedMeshRenderers(SMR → { dirtyBlendShapes(int → data) })
                var old = Get(ed, "_dirtySkinnedMeshRenderers") as IDictionary;
                if (old != null)
                {
                    foreach (DictionaryEntry de in old)
                        Add(set, de.Key as SkinnedMeshRenderer, Get(de.Value, "dirtyBlendShapes") as IDictionary);
                    return set;
                }
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BlendShapeLock] 讀 KKPE 調整清單失敗：" + e.Message);
                return null;
            }
        }

        static void Add(HashSet<string> set, SkinnedMeshRenderer smr, IDictionary dirty)
        {
            if (smr == null || dirty == null || dirty.Count == 0 || smr.sharedMesh == null) return;
            foreach (object k in dirty.Keys)
            {
                string name = k as string;
                if (name == null && k is int)
                {
                    int i = (int)k;
                    if (i >= 0 && i < smr.sharedMesh.blendShapeCount) name = smr.sharedMesh.GetBlendShapeName(i);
                }
                if (name != null) set.Add(Key(smr, name));
            }
        }
    }

    // ===================================================================
    // 診斷：把角色目前的形態鍵倒成文字檔
    //   1) KKPE 調過（紫色）的鍵：渲染器、鍵名、目前權重、KKPE 記的資料
    //   2) 頭上所有渲染器目前非零的鍵（表情系統 FBS / Timeline / 鎖定 寫進去的都算）
    //   3) 目前的表情編號（嘴、眼、眉）
    // 存在 UserData\StudioCharTools\diag\，路徑同時寫進 log 並複製到剪貼簿。
    // ===================================================================
    public static class BlendShapeDump
    {
        public static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Paths.GameRootPath, "UserData"), "StudioCharTools"), "diag"); }
        }

        static string RelPath(Transform t, Transform root)
        {
            var names = new List<string>();
            for (; t != null && t != root; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        static string CharName(ChaControl cha)
        {
            try { return cha.fileParam.fullname; } catch { return cha.name; }
        }

        static object TryGet(Func<object> f) { try { return f(); } catch { return "?"; } }

        public static void Write(System.Text.StringBuilder sb, ChaControl cha)
        {
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine("==================================================");
            sb.AppendLine("角色: " + CharName(cha) + "   物件: " + cha.name);
            sb.AppendLine("頭: headId=" + TryGet(() => cha.fileFace.headId) + "  objHead=" + TryGet(() => cha.objHead != null ? cha.objHead.name : "null"));
            sb.AppendLine("表情編號: 眉=" + TryGet(() => cha.fileStatus.eyebrowPtn) + "  眼=" + TryGet(() => cha.fileStatus.eyesPtn)
                + "  嘴=" + TryGet(() => cha.fileStatus.mouthPtn) + "  眼睜開=" + TryGet(() => cha.fileStatus.eyesOpenMax)
                + "  嘴張開(目前)=" + TryGet(() => cha.fileStatus.mouthOpenMax));

            sb.AppendLine();
            sb.AppendLine("--- KKPE 調過的鍵（紫色）---");
            var raw = KkpeBlendDirty.Raw(cha);
            if (raw == null) sb.AppendLine("（找不到 KKPE 的形態鍵編輯器：沒裝 KKPE、或這個角色還沒打開過 KKPE）");
            else
            {
                int total = 0;
                foreach (var kv in raw)
                {
                    var smr = kv.Key; var dirty = kv.Value;
                    if (smr == null || dirty == null || dirty.Count == 0) continue;
                    Mesh m = smr.sharedMesh;
                    sb.AppendLine("[" + RelPath(smr.transform, cha.transform) + "]  mesh=" + (m != null ? m.name + " (" + m.blendShapeCount + " 鍵)" : "null"));
                    foreach (DictionaryEntry de in dirty)
                    {
                        string name = de.Key as string; int idx = -1;
                        if (name == null && de.Key is int) { idx = (int)de.Key; if (m != null && idx >= 0 && idx < m.blendShapeCount) name = m.GetBlendShapeName(idx); }
                        else if (m != null && name != null) idx = m.GetBlendShapeIndex(name);
                        string cur = idx >= 0 ? smr.GetBlendShapeWeight(idx).ToString("0.##", inv) : "（這個網格沒有這個鍵）";
                        sb.AppendLine("  " + (name ?? Convert.ToString(de.Key)) + "\t目前=" + cur + "\t" + KkpeBlendDirty.Describe(de.Value));
                        total++;
                    }
                }
                sb.AppendLine("共 " + total + " 個");
            }

            sb.AppendLine();
            sb.AppendLine("--- 頭上所有非零的形態鍵（目前畫面上的值）---");
            Transform head = cha.objHead != null ? cha.objHead.transform : cha.transform;
            foreach (var smr in head.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh m = smr.sharedMesh;
                if (m == null || m.blendShapeCount == 0) continue;
                var rows = new List<string>();
                for (int i = 0; i < m.blendShapeCount; i++)
                {
                    float w = smr.GetBlendShapeWeight(i);
                    if (Mathf.Abs(w) > 0.01f) rows.Add("  " + m.GetBlendShapeName(i) + "\t" + w.ToString("0.##", inv));
                }
                sb.AppendLine("[" + RelPath(smr.transform, cha.transform) + "]  mesh=" + m.name + " (" + m.blendShapeCount + " 鍵，非零 " + rows.Count + ")");
                foreach (var r in rows) sb.AppendLine(r);
            }
            sb.AppendLine();
        }

        /// <summary>倒出指定角色（null = 場景裡全部角色）。回傳檔案路徑。</summary>
        public static string Dump(ChaControl only)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("StudioCharTools 形態鍵診斷  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            var list = only != null ? new List<ChaControl> { only } : UnityEngine.Object.FindObjectsOfType<ChaControl>().ToList();
            foreach (var cha in list) { try { Write(sb, cha); } catch (Exception e) { sb.AppendLine("（" + cha.name + " 失敗：" + e.Message + "）"); } }
            Directory.CreateDirectory(Folder);
            string tag = only != null ? CharName(only) : "all";
            foreach (char c in Path.GetInvalidFileNameChars()) tag = tag.Replace(c, '_');
            string file = Path.Combine(Folder, "blendshapes_" + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
            try { GUIUtility.systemCopyBuffer = file; } catch { }
            Debug.Log("[StudioCharTools] 形態鍵診斷已存到 " + file);
            return file;
        }
    }

    // ===================================================================
    // UI
    // ===================================================================
    public static class BlendShapeLockUI
    {
        const int MaxRows = 120;          // 單頁最多畫幾列，避免 IMGUI 排版爆掉

        // 有鎖定的項目一律用黃字，掃一眼就知道哪裡動過
        static readonly Color Marked = new Color(1f, 0.85f, 0.25f);

        // ---- 分組 ----
        class Group
        {
            public string smrName;
            public string label;
            public List<string> subKeys = new List<string>();
            public Dictionary<string, List<BlendShapeLock.ShapeInfo>> subs =
                new Dictionary<string, List<BlendShapeLock.ShapeInfo>>();
        }

        // KK 的網格物件名 → 好讀的中文標籤。找不到就直接顯示原名。
        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "o_body_a",        "身體" },
            { "cf_O_face",       "眼睛/嘴巴" },
            { "cf_O_mayuge",     "眉毛" },
            { "cf_O_noseline",   "鼻線" },
            { "cf_O_tooth",      "牙齒" },
            { "cf_O_canine",     "虎牙" },
            { "cf_O_tang",       "舌頭" },
            { "cf_O_eyeline_up", "上睫毛" },
            { "cf_O_eyeline_low","下睫毛" },
            { "cf_O_namida_L",   "眼淚 L" },
            { "cf_O_namida_M",   "眼淚 M" },
            { "cf_O_namida_S",   "眼淚 S" },
            { "cf_O_eyebase_L",  "左眼白" },
            { "cf_O_eyebase_R",  "右眼白" },
        };

        static string LabelOf(string smrName)
        {
            string v;
            return Labels.TryGetValue(smrName, out v) ? Lang.T(v) : smrName;
        }

        static readonly List<Group> _groups = new List<Group>();
        static int _selGroup;
        static string _selSub = "";

        // ---- 狀態 ----
        static string _search = "";
        // 以前是「只顯示非零」；現在改成只顯示 KKPE 裡調過（紫色）的鍵。
        // 沒裝 KKPE / 讀不到的時候自動退回「非零」。
        static bool _filterKkpe;
        static HashSet<string> _dirty;                       // KKPE 紫色（本幀）
        static readonly HashSet<string> _dirtyGroups = new HashSet<string>();   // smrName、smrName|sub
        static readonly List<BlendShapeLock.ShapeInfo> _viewAll = new List<BlendShapeLock.ShapeInfo>();
        static float _bulkScale = 1f;
        static readonly Color Kkpe = new Color(1f, 0.45f, 1f);
        static bool _filterLocked;

        static Vector2 _scroll, _presetScroll, _groupScroll;

        static ChaControl _cachedFor;
        static bool _scanned;

        static string _presetName = "";
        static List<string> _presets = new List<string>();
        static bool _presetsLoaded;

        class Row
        {
            public BlendShapeLock.ShapeInfo info;
            public BlendShapeLock.LockEntry entry;
            public float current;
        }
        static readonly List<Row> _rows = new List<Row>();
        static int _hiddenCount;

        static string _pendingLoad, _pendingDelete;
        static bool _pendingLoadReplace;

        static readonly Dictionary<string, BlendShapeLock.LockEntry> _lockMap =
            new Dictionary<string, BlendShapeLock.LockEntry>();

        static readonly GUILayoutOption W24 = GUILayout.Width(24);
        static readonly GUILayoutOption W30 = GUILayout.Width(30);
        static readonly GUILayoutOption W40 = GUILayout.Width(40);
        static readonly GUILayoutOption W50 = GUILayout.Width(50);
        static readonly GUILayoutOption W60 = GUILayout.Width(60);
        static readonly GUILayoutOption W70 = GUILayout.Width(70);

        // ===============================================================
        public static void Draw(ChaControl cha)
        {
            if (cha == null) { GUILayout.Label(Lang.T("未選取角色")); return; }

            if (!_presetsLoaded) { _presets = BlendShapeLock.ListPresets(); _presetsLoaded = true; }

            if (Event.current.type == EventType.Layout)
            {
                if (_pendingLoad != null)
                {
                    BlendShapeLock.LoadPreset(_pendingLoad, cha, _pendingLoadReplace);
                    _pendingLoad = null;
                }
                if (_pendingDelete != null)
                {
                    BlendShapeLock.DeletePreset(_pendingDelete);
                    _pendingDelete = null;
                    _presets = BlendShapeLock.ListPresets();
                }

                if (_cachedFor != cha || !_scanned) Scan(cha);
                RebuildRows(cha);
            }

            DrawSearch();
            DrawGroups(cha);
            DrawBulkScale(cha);
            DrawRows(cha);
            DrawPresets(cha);
            DrawFooter(cha);
        }

        // ===============================================================
        static void Scan(ChaControl cha)
        {
            _groups.Clear();
            _cachedFor = cha;
            _scanned = true;

            var bySmr = new Dictionary<string, Group>();

            foreach (var s in BlendShapeLock.Enumerate(cha))
            {
                Group g;
                if (!bySmr.TryGetValue(s.smrName, out g))
                {
                    g = new Group { smrName = s.smrName, label = LabelOf(s.smrName) };
                    bySmr[s.smrName] = g;
                    _groups.Add(g);
                }

                // 次分類：取名稱第一個點之前的前綴，例如 eye_face / kuti_face
                int dot = s.shapeName.IndexOf('.');
                string sub = dot > 0 ? s.shapeName.Substring(0, dot) : Lang.T("其他");

                List<BlendShapeLock.ShapeInfo> bucket;
                if (!g.subs.TryGetValue(sub, out bucket))
                {
                    bucket = new List<BlendShapeLock.ShapeInfo>();
                    g.subs[sub] = bucket;
                    g.subKeys.Add(sub);
                }
                bucket.Add(s);
            }

            if (_selGroup >= _groups.Count) _selGroup = 0;
            _selSub = "";
        }

        static void RebuildRows(ChaControl cha)
        {
            _rows.Clear();
            _viewAll.Clear();
            _hiddenCount = 0;

            _dirty = KkpeBlendDirty.For(cha);
            _dirtyGroups.Clear();
            if (_dirty != null && _dirty.Count > 0)
            {
                foreach (var g in _groups)
                    foreach (var sub in g.subKeys)
                        foreach (var s in g.subs[sub])
                            if (s.smr != null && _dirty.Contains(KkpeBlendDirty.Key(s.smr, s.shapeName)))
                            { _dirtyGroups.Add(g.smrName); _dirtyGroups.Add(g.smrName + "|" + sub); break; }
            }

            // 一次建好查表，取代原本每列一次 LINQ 查詢
            _lockMap.Clear();
            foreach (var e in BlendShapeLock.GetLocks(cha))
                _lockMap[e.smrName + "/" + e.shapeName] = e;

            bool searching = !string.IsNullOrEmpty(_search);

            for (int gi = 0; gi < _groups.Count; gi++)
            {
                // 沒在搜尋時只處理選中的分組，這是效能的關鍵
                if (!searching && gi != _selGroup) continue;
                var g = _groups[gi];

                for (int si = 0; si < g.subKeys.Count; si++)
                {
                    var sub = g.subKeys[si];
                    if (!searching && _selSub.Length > 0 && sub != _selSub) continue;

                    var bucket = g.subs[sub];
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        var s = bucket[i];

                        if (searching &&
                            s.shapeName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        BlendShapeLock.LockEntry e;
                        _lockMap.TryGetValue(s.smrName + "/" + s.shapeName, out e);

                        if (_filterLocked && e == null) continue;

                        float cur;
                        try { cur = s.Current; }
                        catch { continue; }

                        if (_filterKkpe && e == null)
                        {
                            if (_dirty != null)
                            {
                                if (!_dirty.Contains(KkpeBlendDirty.Key(s.smr, s.shapeName))) continue;
                            }
                            else if (Mathf.Abs(cur) < 0.05f) continue;
                        }

                        _viewAll.Add(s);
                        if (_rows.Count >= MaxRows) { _hiddenCount++; continue; }
                        _rows.Add(new Row { info = s, entry = e, current = cur });
                    }
                }
            }
        }

        // ===============================================================
        static void DrawSearch()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("搜索"), W40);
            _search = GUILayout.TextField(_search);
            if (GUILayout.Button("X", W24)) _search = "";
            if (GUILayout.Button(Lang.T("重新掃描"), W70)) _scanned = false;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _filterKkpe = GUILayout.Toggle(_filterKkpe,
                _dirty != null ? Lang.T("只顯示 KKPE 調過的") : Lang.T("只顯示非零"), GUI.skin.button);
            _filterLocked = GUILayout.Toggle(_filterLocked, Lang.T("只顯示已鎖定"), GUI.skin.button);
            GUILayout.EndHorizontal();
        }

        static void DrawGroups(ChaControl cha)
        {
            if (!string.IsNullOrEmpty(_search))
            {
                GUILayout.Label(Lang.T("搜尋模式：跨所有分組"));
                return;
            }

            _groupScroll = GUILayout.BeginScrollView(_groupScroll, GUILayout.Height(76));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                bool has = false;
                foreach (var kv in _lockMap)
                    if (kv.Value.smrName == g.smrName) { has = true; break; }

                if (i > 0 && i % 4 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }

                Color prev = GUI.color;
                if (has) GUI.color = Marked;
                else if (_dirtyGroups.Contains(g.smrName)) GUI.color = Kkpe;
                bool sel = GUILayout.Toggle(i == _selGroup, has ? g.label + " *" : g.label,
                                            GUI.skin.button);
                GUI.color = prev;
                if (sel && i != _selGroup) { _selGroup = i; _selSub = ""; }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            if (_selGroup < _groups.Count)
            {
                var g = _groups[_selGroup];
                if (g.subKeys.Count > 1)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Toggle(_selSub.Length == 0, Lang.T("全部"), GUI.skin.button, W50))
                        _selSub = "";
                    for (int i = 0; i < g.subKeys.Count; i++)
                    {
                        var k = g.subKeys[i];
                        bool subHas = false;
                        foreach (var kv in _lockMap)
                            if (kv.Value.smrName == g.smrName
                                && kv.Value.shapeName != null
                                && kv.Value.shapeName.StartsWith(k))
                            { subHas = true; break; }

                        Color prevSub = GUI.color;
                        if (subHas) GUI.color = Marked;
                        else if (_dirtyGroups.Contains(g.smrName + "|" + k)) GUI.color = Kkpe;
                        bool hit = GUILayout.Toggle(_selSub == k, subHas ? k + " *" : k,
                                                    GUI.skin.button);
                        GUI.color = prevSub;
                        if (hit) _selSub = k;
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        /// <summary>
        /// 目前畫面上這一批（分組／子分類／搜尋／篩選後的全部，不受 120 列限制）一次設比例。
        /// 臉型 mod 的表情鍵整組偏強或偏弱時用：例如 kuti_face 全部 ×0.8。
        /// </summary>
        static void DrawBulkScale(ChaControl cha)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("這頁 ") + _viewAll.Count + Lang.T(" 個：倍率"), Fit.WL(110, Lang.T("這頁 ") + _viewAll.Count + Lang.T(" 個：倍率")));
            _bulkScale = GUILayout.HorizontalSlider(_bulkScale, 0f, 2f);
            GUILayout.Label("×" + _bulkScale.ToString("F2"), W50);
            if (GUILayout.Button("-", W24)) _bulkScale = Mathf.Max(0f, _bulkScale - 0.05f);
            if (GUILayout.Button("+", W24)) _bulkScale = Mathf.Min(2f, _bulkScale + 0.05f);
            GUI.enabled = _viewAll.Count > 0;
            if (GUILayout.Button(Lang.T("套用比例"), W70))
                foreach (var s in _viewAll)
                {
                    var old = BlendShapeLock.Find(cha, s.smrName, s.shapeName);
                    // 已經是固定／範圍鎖的不動，免得一鍵把手調的鎖洗掉
                    if (old != null && old.mode != BlendShapeLock.LockMode.Scale) continue;
                    BlendShapeLock.SetScale(cha, s.smrName, s.shapeName, _bulkScale, old != null ? old.max : 100f);
                }
            if (GUILayout.Button(Lang.T("清除比例"), W70))
                foreach (var s in _viewAll)
                {
                    var old = BlendShapeLock.Find(cha, s.smrName, s.shapeName);
                    if (old != null && old.mode == BlendShapeLock.LockMode.Scale)
                        BlendShapeLock.RemoveLock(cha, s.smrName, s.shapeName);
                }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        static void DrawRows(ChaControl cha)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);

            for (int r = 0; r < _rows.Count; r++)
            {
                var row = _rows[r];
                var s = row.info;
                var e = row.entry;
                bool locked = e != null && e.enabled;

                Color rowPrev = GUI.color;
                if (locked) GUI.color = Marked;

                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                bool purple = !locked && _dirty != null && s.smr != null
                              && _dirty.Contains(KkpeBlendDirty.Key(s.smr, s.shapeName));
                if (purple) GUI.color = Kkpe;
                GUILayout.Label(locked ? s.shapeName + " *" : s.shapeName);
                if (purple) GUI.color = rowPrev;
                GUILayout.FlexibleSpace();
                GUILayout.Label(row.current.ToString("F0"), W30);

                if (locked)
                {
                    var label = Lang.T(e.mode == BlendShapeLock.LockMode.Fixed ? "固定"
                                     : e.mode == BlendShapeLock.LockMode.Clamp ? "範圍" : "比例");
                    if (GUILayout.Button(label, W40))
                    {
                        e.mode = e.mode == BlendShapeLock.LockMode.Fixed ? BlendShapeLock.LockMode.Clamp
                               : e.mode == BlendShapeLock.LockMode.Clamp ? BlendShapeLock.LockMode.Scale
                               : BlendShapeLock.LockMode.Fixed;
                        e.hasLast = false;
                        if (e.mode == BlendShapeLock.LockMode.Scale && e.scale <= 0f) e.scale = 1f;
                    }
                }

                if (GUILayout.Button(Lang.T("最大"), W40))
                    BlendShapeLock.SetFixedLock(cha, s.smrName, s.shapeName, 100f);
                if (GUILayout.Button(Lang.T("最小"), W40))
                    BlendShapeLock.SetFixedLock(cha, s.smrName, s.shapeName, 0f);
                if (!locked && GUILayout.Button(Lang.T("比例"), W40))
                    BlendShapeLock.SetScale(cha, s.smrName, s.shapeName, 1f, 100f);

                bool want = GUILayout.Toggle(locked, Lang.T("鎖定"), GUI.skin.button, W50);
                if (want != locked)
                {
                    if (want)
                    {
                        if (BlendShapeLock.Exclusive)
                            BlendShapeLock.ClearOthers(cha, s.smrName, s.shapeName);
                        BlendShapeLock.SetLock(cha, s.smrName, s.shapeName, null);
                    }
                    else BlendShapeLock.RemoveLock(cha, s.smrName, s.shapeName);
                }
                GUILayout.EndHorizontal();

                if (locked && e.mode == BlendShapeLock.LockMode.Fixed)
                {
                    GUILayout.BeginHorizontal();
                    float v = GUILayout.HorizontalSlider(e.value, 0f, 100f);
                    if (GUILayout.Button("-1", W30)) v = e.value - 1f;
                    if (GUILayout.Button("+1", W30)) v = e.value + 1f;
                    if (GUILayout.Button(Lang.T("取現值"), W60)) v = row.current;
                    if (GUILayout.Button(Lang.T("重置"), W40)) v = 0f;
                    e.value = Mathf.Clamp(v, 0f, 100f);
                    GUILayout.EndHorizontal();
                }
                else if (locked && e.mode == BlendShapeLock.LockMode.Scale)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Lang.T("倍率 ") + "×" + e.scale.ToString("F2"), W70);
                    float sc = GUILayout.HorizontalSlider(e.scale, 0f, 2f);
                    if (GUILayout.Button("-", W24)) sc = e.scale - 0.05f;
                    if (GUILayout.Button("+", W24)) sc = e.scale + 0.05f;
                    if (GUILayout.Button("×1", W30)) sc = 1f;
                    e.scale = Mathf.Clamp(sc, 0f, 3f);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Lang.T("上限 ") + e.max.ToString("F0"), W70);
                    e.max = GUILayout.HorizontalSlider(e.max, 0f, 100f);
                    GUILayout.EndHorizontal();
                }
                else if (locked)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Lang.T("下限 ") + e.min.ToString("F0"), W60);
                    e.min = GUILayout.HorizontalSlider(e.min, 0f, 100f);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Lang.T("上限 ") + e.max.ToString("F0"), W60);
                    e.max = GUILayout.HorizontalSlider(e.max, 0f, 100f);
                    GUILayout.EndHorizontal();

                    if (e.min > e.max) e.min = e.max;
                }

                GUILayout.EndVertical();
                GUI.color = rowPrev;
            }

            if (_hiddenCount > 0)
                GUILayout.Label(Lang.T("另有 ") + _hiddenCount + Lang.T(" 筆未顯示，請用搜尋或分組縮小範圍"));

            GUILayout.EndScrollView();
        }

        static void DrawPresets(ChaControl cha)
        {
            GUILayout.Label(Lang.T("預設"));
            GUILayout.BeginHorizontal();
            _presetName = GUILayout.TextField(_presetName);
            string charName = BlendShapeLock.NameOf(cha);
            GUI.enabled = !string.IsNullOrEmpty(charName);
            if (GUILayout.Button(Lang.T("用角色名"), W60))
                _presetName = charName;
            GUI.enabled = true;
            if (GUILayout.Button(Lang.T("儲存"), W60))
            {
                if (BlendShapeLock.SavePreset(_presetName, cha))
                    _presets = BlendShapeLock.ListPresets();
            }
            GUILayout.EndHorizontal();

            // 提示：檔名跟角色同名，換人之後才有辦法自動帶入
            bool willAuto = !string.IsNullOrEmpty(charName)
                            && string.Equals(charName, (_presetName ?? "").Trim(),
                                             StringComparison.OrdinalIgnoreCase);
            GUI.color = willAuto ? new Color(0.6f, 1f, 0.7f) : new Color(0.8f, 0.8f, 0.8f);
            GUILayout.Label(willAuto
                ? Lang.T("✓ 檔名＝角色名「") + charName + Lang.T("」，換人後會自動帶入（可在設置關閉）")
                : Lang.T("存成角色名「") + (charName ?? "?") + Lang.T("」的話，換人後會自動帶入這份鎖"));
            GUI.color = Color.white;

            _presetScroll = GUILayout.BeginScrollView(_presetScroll, GUILayout.Height(90));
            for (int i = 0; i < _presets.Count; i++)
            {
                var name = _presets[i];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(name))
                {
                    _pendingLoad = name; _pendingLoadReplace = true; _presetName = name;
                }
                if (GUILayout.Button(Lang.T("附加"), W40))
                {
                    _pendingLoad = name; _pendingLoadReplace = false;
                }
                if (GUILayout.Button("X", W24)) _pendingDelete = name;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        static void DrawFooter(ChaControl cha)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("全部解鎖"))) BlendShapeLock.ClearAll(cha);
            BlendShapeLock.Exclusive = GUILayout.Toggle(
                BlendShapeLock.Exclusive, Lang.T("只鎖一個"), GUI.skin.button, Fit.W(80, Lang.T("只鎖一個"), GUI.skin.button));
            GUILayout.FlexibleSpace();
            GUILayout.Label(Lang.T("目前鎖定 ") + BlendShapeLock.LockCount(cha) + Lang.T(" 筆"));
            GUILayout.EndHorizontal();

            // 診斷：倒出目前的形態鍵（KKPE 調過的 + 所有非零的）
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("診斷"), W40);
            if (GUILayout.Button(Lang.T("倒出形態鍵（這個角色）"))) _dumpMsg = SafeDump(cha);
            if (GUILayout.Button(Lang.T("倒出形態鍵（全部角色）"))) _dumpMsg = SafeDump(null);
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_dumpMsg)) GUILayout.Label(_dumpMsg);
        }

        static string _dumpMsg;
        static string SafeDump(ChaControl cha)
        {
            try { return Lang.T("已存到（路徑已複製）：") + BlendShapeDump.Dump(cha); }
            catch (Exception e) { return Lang.T("倒出失敗：") + e.Message; }
        }
    }
}