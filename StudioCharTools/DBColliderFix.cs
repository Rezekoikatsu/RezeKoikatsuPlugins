using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 換人物之後 KKPE(HSPE) 的 Dynamic Bone Collider 綁定會爆掉的修復。
    ///
    /// 【問題本體】（實際反編譯 KKPE.dll + 比對場景卡 kkpe/sceneInfo 得到的結論）
    ///
    /// HSPE 的碰撞器資料長這樣：
    ///     CollidersEditor._dirtyColliders : Dictionary&lt;DynamicBoneCollider, ColliderDataBase&gt;
    ///     ColliderDataBase.allDynamicBones: Dictionary&lt;PoseController, Dictionary&lt;object, bool&gt;&gt;
    /// 也就是「每一顆碰撞器 → 每一個角色/物件 → 每一根動骨 → 要不要吃這顆碰撞器」。
    /// 存進場景卡就是 &lt;collider&gt; 底下那一堆 &lt;dynamicBoneData root="…" enabled="…" /&gt;。
    ///
    /// 換人時 HSPE 走的是：
    ///     CharaPoseController.OnCharacterReplaced()
    ///       → DynamicBonesEditor.RefreshDynamicBoneList()
    ///           對「場上每一顆已被編輯過的碰撞器」×「這個角色的每一根新動骨」：
    ///           如果這根骨頭還不在 allDynamicBones 裡，就用碰撞器所屬
    ///           CollidersEditor._addNewDynamicBonesAsDefault 當預設值加進去，
    ///           並且直接把碰撞器塞進 DynamicBone.m_Colliders。
    ///
    /// 而 _addNewDynamicBonesAsDefault 的預設值是 true（面板上的
    /// "Enable New Dynamic Bones"）。換完人，舊的 DynamicBone 物件被銷毀、
    /// 原本那份「只有兩三根骨頭是 true」的設定被 CollidersEditor.Update() 清掉，
    /// 取而代之的是「新角色全部的動骨都 = true」。
    ///
    /// 實測（J694問題 那兩張卡）：
    ///     原卡     J694#1 對該角色只啟用 2 根：cm_J_dan_Pivot_f_L / _R，J694#2 啟用 0 根
    ///     換人後   兩顆 J694 各啟用 101 根（陰道 pivot、頭髮 yure、屁股、胸部全中）
    /// 所以那顆半徑 0.005 的小碰撞器變成在吸整顆頭跟下半身 —— 就是你看到的吸附。
    /// CollidersEditor 自己沒有覆寫 OnCharacterReplaced，所以 HSPE 根本沒有
    /// 「換人後把碰撞器綁定接回去」這條路，這不是設定跑掉，是它沒做。
    ///
    /// 【修法】
    ///   1. 換人前：把該角色目前的綁定（以骨頭路徑為 key）記下來，
    ///      並把場上所有 CollidersEditor 的 _addNewDynamicBonesAsDefault 暫時關掉，
    ///      這樣 RefreshDynamicBoneList 加進來的新骨頭一律是 false（= 不吸）。
    ///   2. 換人後：對每一顆碰撞器、該角色的每一根動骨，呼叫 HSPE 自己的
    ///      CollidersEditor.SetIgnoreDynamicBone(collider, pc, bone, !要啟用)，
    ///      按記錄把原本該開的那幾根重新打開。用它自己的 API 的好處是
    ///      內部字典與 DynamicBone.m_Colliders 會同時一致，不用手動去戳。
    ///   3. 因為 RefreshDynamicBoneList 在 OnCharacterReplaced 裡會被叫兩次，
    ///      加上 _headlessReconstructionTimeout 還會延遲重建，所以分幾次套用。
    ///
    /// 整份都走反射，沒有 KKPE 也不會影響其他功能。
    /// </summary>
    internal static class DBColliderFix
    {
        // =============================================================
        // 反射快取
        // =============================================================
        private static bool inited;
        private static bool available;

        private static Type tPoseController;        // HSPE.PoseController
        private static Type tCollidersEditor;       // HSPE.AMModules.CollidersEditor
        private static Type tDynamicBone;           // DynamicBone
        private static Type tDynamicBoneVer02;      // DynamicBone_Ver02

        private static FieldInfo fPoseControllers;  // static HashSet<PoseController>
        private static FieldInfo fPcCollidersEditor;// PoseController._collidersEditor
        private static FieldInfo fPcTarget;         // PoseController._target
        private static FieldInfo fTargetOciChar;    // GenericOCITarget.ociChar
        private static FieldInfo fTargetOci;        // GenericOCITarget.oci
        private static FieldInfo fLoneColliders;    // static Dictionary<DynamicBoneCollider, CollidersEditor>
        private static FieldInfo fDirtyColliders;   // CollidersEditor._dirtyColliders
        private static FieldInfo fAddNewDefault;    // CollidersEditor._addNewDynamicBonesAsDefault
        private static FieldInfo fAllDynamicBones;  // ColliderDataBase.allDynamicBones
        private static FieldInfo fDbRoot;           // DynamicBone.m_Root
        private static FieldInfo fDb2Root;          // DynamicBone_Ver02.m_Root / Root
        private static MethodInfo mSetIgnore;       // SetIgnoreDynamicBone

        internal static string LastReport = "";

        internal static bool Available { get { Init(); return available; } }

        /// <summary>沒裝 KKPE 時給 UI 用的說明。</summary>
        internal static string Unavailable { get { Init(); return available ? "" : initError; } }

        private static string initError = "";

        private static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                tPoseController = FindType("HSPE.PoseController");
                tCollidersEditor = FindType("HSPE.AMModules.CollidersEditor");
                tDynamicBone = FindType("DynamicBone");
                tDynamicBoneVer02 = FindType("DynamicBone_Ver02");

                if (tPoseController == null || tCollidersEditor == null)
                {
                    initError = "找不到 KKPE（HSPE）的型別，確認 KKPE.dll 有載入";
                    return;
                }

                fPoseControllers = Fld(tPoseController, "_poseControllers");
                fPcCollidersEditor = Fld(tPoseController, "_collidersEditor");
                fPcTarget = Fld(tPoseController, "_target");
                fLoneColliders = Fld(tCollidersEditor, "_loneColliders");
                fDirtyColliders = Fld(tCollidersEditor, "_dirtyColliders");
                fAddNewDefault = Fld(tCollidersEditor, "_addNewDynamicBonesAsDefault");

                Type tTarget = FindType("HSPE.AMModules.GenericOCITarget");
                if (tTarget != null)
                {
                    fTargetOciChar = Fld(tTarget, "ociChar");
                    fTargetOci = Fld(tTarget, "oci");
                }

                // ColliderDataBase 是 CollidersEditor 裡面的 nested class，
                // 在 metadata 裡 namespace 是空的，Assembly.GetType("ColliderDataBase")
                // 找不到，一定要走 GetNestedType 或 "外層+內層" 的寫法。
                Type tColliderData =
                    tCollidersEditor.GetNestedType("ColliderDataBase",
                        BindingFlags.Public | BindingFlags.NonPublic)
                    ?? FindType("HSPE.AMModules.CollidersEditor+ColliderDataBase")
                    ?? FindType("ColliderDataBase");
                if (tColliderData != null) fAllDynamicBones = Fld(tColliderData, "allDynamicBones");

                if (tDynamicBone != null) fDbRoot = Fld(tDynamicBone, "m_Root");
                if (tDynamicBoneVer02 != null)
                    fDb2Root = Fld(tDynamicBoneVer02, "m_Root") ?? Fld(tDynamicBoneVer02, "Root");

                foreach (MethodInfo m in tCollidersEditor.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "SetIgnoreDynamicBone") continue;
                    if (m.GetParameters().Length != 4) continue;
                    mSetIgnore = m;
                    break;
                }

                // allDynamicBones 不列入必要條件 —— 它可以在跑的時候直接從
                // 實際物件的型別上拿（見 AllDbField），不必先認得型別名字。
                List<string> missing = new List<string>();
                if (fPoseControllers == null) missing.Add("PoseController._poseControllers");
                if (fPcTarget == null) missing.Add("PoseController._target");
                if (fPcCollidersEditor == null) missing.Add("PoseController._collidersEditor");
                if (fDirtyColliders == null) missing.Add("CollidersEditor._dirtyColliders");
                if (mSetIgnore == null) missing.Add("CollidersEditor.SetIgnoreDynamicBone");
                if (missing.Count > 0)
                {
                    initError = "KKPE 內部結構與預期不符（版本不同？）缺少："
                              + string.Join("、", missing.ToArray());
                    return;
                }
                available = true;
            }
            catch (Exception e)
            {
                initError = "初始化失敗: " + e.Message;
                available = false;
            }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static FieldInfo Fld(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        // =============================================================
        // 快照
        // =============================================================

        internal class Snapshot
        {
            /// <summary>碰撞器 instanceID → (骨頭路徑 → 要不要啟用)。</summary>
            public Dictionary<int, Dictionary<string, bool>> byCollider =
                new Dictionary<int, Dictionary<string, bool>>();
            public string charLabel = "";
            public int enabledCount;
            public int totalCount;
            public string source = "";      // 「換人前記錄」/「其他角色範本」/「全部關閉」
        }

        /// <summary>
        /// 這個 session 記下來的「原始」綁定。key 是 PoseController 的 instanceID。
        /// 第一次換人之前抓到的那份最有價值（那是場景卡原作者的設定），
        /// 所以預設不覆寫，要覆寫得由使用者按「重新記錄」。
        /// </summary>
        private static readonly Dictionary<int, Snapshot> remembered = new Dictionary<int, Snapshot>();

        internal static bool HasRemembered(Studio.OCIChar oci)
        {
            Init();
            if (!available) return false;
            object pc = FindPoseController(oci);
            return pc != null && remembered.ContainsKey(((Component)pc).GetInstanceID());
        }

        internal static Snapshot GetRemembered(Studio.OCIChar oci)
        {
            Init();
            if (!available) return null;
            object pc = FindPoseController(oci);
            if (pc == null) return null;
            Snapshot s;
            remembered.TryGetValue(((Component)pc).GetInstanceID(), out s);
            return s;
        }

        internal static void ForgetAll()
        {
            remembered.Clear();
        }

        /// <summary>
        /// 記錄該角色目前的碰撞器綁定。force = false 時已經有記錄就直接回傳舊的，
        /// 這樣連續換人（例如「保持服裝換人」會換兩次）不會把第一次的正確設定洗掉。
        /// </summary>
        internal static Snapshot Capture(Studio.OCIChar oci, bool force)
        {
            Init();
            if (!available) { LastReport = Unavailable; return null; }

            object pc = FindPoseController(oci);
            if (pc == null) { LastReport = "找不到這個角色的 HSPE PoseController"; return null; }
            int pcId = ((Component)pc).GetInstanceID();

            if (!force && remembered.ContainsKey(pcId)) return remembered[pcId];

            Snapshot snap = new Snapshot();
            snap.source = "換人前記錄";
            snap.charLabel = Label(oci);

            foreach (KeyValuePair<object, object> pair in EachDirtyCollider())
            {
                object collider = pair.Key;
                object editor = pair.Value;
                IDictionary inner = InnerMap(editor, collider, pc);
                Dictionary<string, bool> map = new Dictionary<string, bool>();
                if (inner != null)
                {
                    foreach (DictionaryEntry de in inner)
                    {
                        if (de.Key == null) continue;
                        string key = BoneKey(de.Key, ((Component)pc).transform);
                        if (key == null) continue;
                        bool on = de.Value is bool && (bool)de.Value;
                        bool had;
                        // 路徑撞名（同一個 root 掛了兩個 DynamicBone）時取聯集，
                        // 寧可留著原本會動的那根，也不要把該吸的關掉。
                        if (map.TryGetValue(key, out had)) map[key] = had || on;
                        else map[key] = on;
                        snap.totalCount++;
                        if (on) snap.enabledCount++;
                    }
                }
                snap.byCollider[((Component)collider).GetInstanceID()] = map;
            }

            remembered[pcId] = snap;
            LastReport = "已記錄 " + snap.byCollider.Count + " 顆碰撞器，"
                       + snap.enabledCount + "/" + snap.totalCount + " 根骨頭是啟用的";
            return snap;
        }

        /// <summary>
        /// 拿場上「其他角色」在同一顆碰撞器上啟用了哪些骨頭當範本。
        /// 用來救已經壞掉、沒有記錄可還原的卡：同一張場景卡裡其他角色通常
        /// 是同一套設定（例如每個女角都只開 cm_J_dan_Pivot_f_L/R）。
        /// </summary>
        internal static Snapshot TemplateFromOthers(Studio.OCIChar oci)
        {
            Init();
            if (!available) { LastReport = Unavailable; return null; }
            object pc = FindPoseController(oci);
            if (pc == null) { LastReport = "找不到這個角色的 HSPE PoseController"; return null; }

            Snapshot snap = new Snapshot();
            snap.source = "其他角色範本";
            snap.charLabel = Label(oci);

            int donors = 0;
            foreach (KeyValuePair<object, object> pair in EachDirtyCollider())
            {
                object collider = pair.Key;
                object editor = pair.Value;
                Dictionary<string, bool> map = new Dictionary<string, bool>();

                object data = ColliderData(editor, collider);
                FieldInfo fAll = AllDbField(data);
                IDictionary all = fAll == null ? null : fAll.GetValue(data) as IDictionary;
                if (all != null)
                {
                    foreach (DictionaryEntry pcEntry in all)
                    {
                        if (pcEntry.Key == null) continue;
                        if (ReferenceEquals(pcEntry.Key, pc)) continue;          // 跳過自己
                        Component donor = pcEntry.Key as Component;
                        if (donor == null) continue;
                        IDictionary inner = pcEntry.Value as IDictionary;
                        if (inner == null) continue;
                        bool used = false;
                        foreach (DictionaryEntry de in inner)
                        {
                            if (de.Key == null) continue;
                            if (!(de.Value is bool) || !(bool)de.Value) continue;
                            string key = BoneKey(de.Key, donor.transform);
                            if (key == null) continue;
                            map[key] = true;
                            used = true;
                        }
                        if (used) donors++;
                    }
                }
                foreach (KeyValuePair<string, bool> kv in map) if (kv.Value) snap.enabledCount++;
                snap.byCollider[((Component)collider).GetInstanceID()] = map;
            }

            LastReport = "範本取自 " + donors + " 個其他角色/物件，共 "
                       + snap.enabledCount + " 條要啟用的骨頭路徑";
            return snap;
        }

        /// <summary>整個角色對所有碰撞器都不吸（最保守的救援）。</summary>
        internal static Snapshot EmptySnapshot(Studio.OCIChar oci)
        {
            Snapshot snap = new Snapshot();
            snap.source = "全部關閉";
            snap.charLabel = Label(oci);
            return snap;
        }

        // =============================================================
        // 套用
        // =============================================================

        /// <summary>
        /// 把快照套回去。回傳實際被改掉的骨頭數。
        /// 快照裡沒提到的骨頭一律設成「不吸」—— 換人後被自動加進來的那一百多根
        /// 就是靠這一條關掉的。
        /// </summary>
        internal static int Apply(Studio.OCIChar oci, Snapshot snap)
        {
            Init();
            if (!available) { LastReport = Unavailable; return 0; }
            if (snap == null) { LastReport = "沒有可以套用的綁定記錄"; return 0; }

            object pc = FindPoseController(oci);
            if (pc == null) { LastReport = "找不到這個角色的 HSPE PoseController"; return 0; }
            Transform pcRoot = ((Component)pc).transform;

            int changed = 0, touched = 0, on = 0, colliders = 0;
            foreach (KeyValuePair<object, object> pair in EachDirtyCollider())
            {
                object collider = pair.Key;
                object editor = pair.Value;
                colliders++;

                Dictionary<string, bool> want;
                if (!snap.byCollider.TryGetValue(((Component)collider).GetInstanceID(), out want))
                    want = new Dictionary<string, bool>();

                IDictionary inner = InnerMap(editor, collider, pc);

                foreach (object bone in CollectBones(pc, inner))
                {
                    string key = BoneKey(bone, pcRoot);
                    if (key == null) continue;
                    bool desired;
                    if (!want.TryGetValue(key, out desired)) desired = false;

                    bool current = false;
                    if (inner != null && inner.Contains(bone))
                    {
                        object v = inner[bone];
                        current = v is bool && (bool)v;
                    }

                    try
                    {
                        mSetIgnore.Invoke(editor, new object[] { collider, pc, bone, !desired });
                        touched++;
                        if (desired) on++;
                        if (current != desired) changed++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[DBColliderFix] SetIgnoreDynamicBone 失敗: " + e.Message);
                    }
                }
            }

            LastReport = "（" + snap.source + "）" + colliders + " 顆碰撞器 / 處理 " + touched
                       + " 根骨頭，啟用 " + on + " 根，變更 " + changed + " 根";
            return changed;
        }

        /// <summary>照記錄還原；沒有記錄就用其他角色當範本；再不行就全部關掉。</summary>
        internal static int AutoFix(Studio.OCIChar oci)
        {
            Init();
            if (!available) { LastReport = Unavailable; return 0; }
            Snapshot snap = GetRemembered(oci);
            if (snap == null) snap = TemplateFromOthers(oci);
            if (snap == null) snap = EmptySnapshot(oci);
            return Apply(oci, snap);
        }

        // =============================================================
        // "Enable New Dynamic Bones" 的臨時關閉
        // =============================================================

        internal class AutoAddGuard
        {
            internal List<object> editors = new List<object>();
            internal List<bool> old = new List<bool>();
        }

        /// <summary>
        /// 換人前呼叫。把場上每一個 CollidersEditor 的「自動把新動骨加進來並啟用」
        /// 關掉，這樣 RefreshDynamicBoneList 塞進來的新骨頭一律不吸。
        /// </summary>
        internal static AutoAddGuard SuppressAutoAdd()
        {
            Init();
            AutoAddGuard g = new AutoAddGuard();
            if (!available || fAddNewDefault == null) return g;
            foreach (object editor in AllCollidersEditors())
            {
                try
                {
                    object v = fAddNewDefault.GetValue(editor);
                    g.editors.Add(editor);
                    g.old.Add(v is bool && (bool)v);
                    fAddNewDefault.SetValue(editor, false);
                }
                catch { }
            }
            return g;
        }

        internal static void RestoreAutoAdd(AutoAddGuard g)
        {
            if (g == null || !available || fAddNewDefault == null) return;
            for (int i = 0; i < g.editors.Count; i++)
            {
                try { fAddNewDefault.SetValue(g.editors[i], g.old[i]); }
                catch { }
            }
            g.editors.Clear();
            g.old.Clear();
        }

        /// <summary>
        /// 永久把場上所有碰撞器的「自動加入新動骨」關掉（這個值會存進場景卡的
        /// collidersEditor addNewDynamicBonesAsDefault）。回傳改了幾個。
        /// </summary>
        internal static int DisableAutoAddPermanently()
        {
            Init();
            if (!available || fAddNewDefault == null) { LastReport = Unavailable; return 0; }
            int n = 0;
            foreach (object editor in AllCollidersEditors())
            {
                try
                {
                    object v = fAddNewDefault.GetValue(editor);
                    if (v is bool && (bool)v) { fAddNewDefault.SetValue(editor, false); n++; }
                }
                catch { }
            }
            LastReport = "已關閉 " + n + " 個碰撞器物件的「自動加入新動骨」（存檔後生效）";
            return n;
        }

        /// <summary>場上還有幾個碰撞器物件是「自動加入新動骨 = 開」的。</summary>
        internal static int CountAutoAddOn()
        {
            Init();
            if (!available || fAddNewDefault == null) return 0;
            int n = 0;
            foreach (object editor in AllCollidersEditors())
            {
                try
                {
                    object v = fAddNewDefault.GetValue(editor);
                    if (v is bool && (bool)v) n++;
                }
                catch { }
            }
            return n;
        }

        // =============================================================
        // 診斷
        // =============================================================

        /// <summary>列出每一顆碰撞器對這個角色開了幾根骨頭，以及開了哪些。</summary>
        internal static string Describe(Studio.OCIChar oci)
        {
            Init();
            if (!available) return Unavailable;
            object pc = FindPoseController(oci);
            if (pc == null) return "找不到這個角色的 HSPE PoseController（該角色可能還沒被 KKPE 碰過）";
            Transform pcRoot = ((Component)pc).transform;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Snapshot snap = GetRemembered(oci);
            sb.Append("對象：").Append(Label(oci)).Append("\n");
            sb.Append(snap == null
                ? "綁定記錄：無（這個場景還沒在這裡換過人）\n"
                : "綁定記錄：有，" + snap.enabledCount + " 根啟用（" + snap.source + "）\n");
            sb.Append("自動加入新動骨仍開著的碰撞器：").Append(CountAutoAddOn()).Append(" 個\n");
            int pcCount = 0;
            foreach (object ignore in AllPoseControllers()) pcCount++;
            int edCount = 0;
            foreach (object ignore in AllCollidersEditors()) edCount++;
            sb.Append("掃到 PoseController ").Append(pcCount)
              .Append(" 個 / CollidersEditor ").Append(edCount).Append(" 個\n\n");

            int idx = 0;
            foreach (KeyValuePair<object, object> pair in EachDirtyCollider())
            {
                idx++;
                object collider = pair.Key;
                object editor = pair.Value;
                IDictionary inner = InnerMap(editor, collider, pc);
                int total = 0, on = 0;
                List<string> names = new List<string>();
                if (inner != null)
                {
                    foreach (DictionaryEntry de in inner)
                    {
                        if (de.Key == null) continue;
                        total++;
                        if (de.Value is bool && (bool)de.Value)
                        {
                            on++;
                            if (names.Count < 14)
                            {
                                string k = BoneKey(de.Key, pcRoot);
                                if (k != null)
                                {
                                    int slash = k.LastIndexOf('/');
                                    names.Add(slash >= 0 ? k.Substring(slash + 1) : k);
                                }
                            }
                        }
                    }
                }
                sb.Append(idx).Append(". ").Append(ColliderLabel(collider, editor))
                  .Append("  啟用 ").Append(on).Append(" / ").Append(total).Append("\n");
                if (on > 0)
                {
                    sb.Append("     ").Append(string.Join(", ", names.ToArray()));
                    if (on > names.Count) sb.Append(" …（另外 ").Append(on - names.Count).Append(" 根）");
                    sb.Append("\n");
                }
            }
            if (idx == 0) sb.Append("場上沒有被編輯過的 Dynamic Bone Collider。\n");
            return sb.ToString();
        }

        // =============================================================
        // 內部工具
        // =============================================================

        /// <summary>
        /// 場上每一顆「被編輯過」的碰撞器 → 它所屬的 CollidersEditor。
        /// 直接走每個 CollidersEditor 的 _dirtyColliders，比只看 _loneColliders
        /// 完整（掛在角色或物件身上、不是獨立碰撞器物件的那些也會收到）。
        /// </summary>
        private static IEnumerable<KeyValuePair<object, object>> EachDirtyCollider()
        {
            List<KeyValuePair<object, object>> outList = new List<KeyValuePair<object, object>>();
            List<object> seen = new List<object>();
            foreach (object editor in AllCollidersEditors())
            {
                IDictionary dirty = null;
                try { dirty = fDirtyColliders.GetValue(editor) as IDictionary; }
                catch { }
                if (dirty == null) continue;
                foreach (DictionaryEntry de in dirty)
                {
                    object collider = de.Key;
                    if (!(collider is Component)) continue;
                    if (de.Value == null) continue;
                    if (Has(seen, collider)) continue;
                    seen.Add(collider);
                    outList.Add(new KeyValuePair<object, object>(collider, editor));
                }
            }
            return outList;
        }

        private static IEnumerable<object> AllCollidersEditors()
        {
            List<object> outList = new List<object>();
            IDictionary lone = null;
            try { lone = fLoneColliders == null ? null : fLoneColliders.GetValue(null) as IDictionary; }
            catch { }
            if (lone != null)
            {
                foreach (DictionaryEntry de in lone)
                    if (de.Value != null && !Has(outList, de.Value)) outList.Add(de.Value);
            }
            // 以防有 collider 還沒註冊到 _loneColliders，補上所有 PoseController 身上那一份
            foreach (object pc in AllPoseControllers())
            {
                try
                {
                    object ce = fPcCollidersEditor == null ? null : fPcCollidersEditor.GetValue(pc);
                    if (ce != null && !Has(outList, ce)) outList.Add(ce);
                }
                catch { }
            }
            return outList;
        }

        private static bool Has(List<object> list, object o)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], o)) return true;
            return false;
        }

        private static IEnumerable<object> AllPoseControllers()
        {
            List<object> outList = new List<object>();
            try
            {
                IEnumerable set = fPoseControllers.GetValue(null) as IEnumerable;
                if (set != null)
                    foreach (object pc in set)
                        if (pc != null) outList.Add(pc);
            }
            catch { }
            return outList;
        }

        /// <summary>
        /// ColliderDataBase.allDynamicBones 的 FieldInfo。直接從實際物件的型別上找，
        /// 這樣就算 KKPE 換版把 nested class 改名也照樣抓得到。
        /// </summary>
        private static FieldInfo AllDbField(object data)
        {
            if (data == null) return null;
            if (fAllDynamicBones != null
                && fAllDynamicBones.DeclaringType != null
                && fAllDynamicBones.DeclaringType.IsInstanceOfType(data))
                return fAllDynamicBones;
            FieldInfo f = Fld(data.GetType(), "allDynamicBones");
            if (f != null) fAllDynamicBones = f;
            return f;
        }

        private static object ColliderData(object editor, object collider)
        {
            try
            {
                IDictionary dirty = fDirtyColliders.GetValue(editor) as IDictionary;
                if (dirty == null || !dirty.Contains(collider)) return null;
                return dirty[collider];
            }
            catch { return null; }
        }

        /// <summary>某顆碰撞器對某個 PoseController 的「骨頭 → 啟用」字典。</summary>
        private static IDictionary InnerMap(object editor, object collider, object pc)
        {
            object data = ColliderData(editor, collider);
            if (data == null) return null;
            try
            {
                FieldInfo f = AllDbField(data);
                if (f == null) return null;
                IDictionary all = f.GetValue(data) as IDictionary;
                if (all == null) return null;
                if (all.Contains(pc)) return all[pc] as IDictionary;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 這個 PoseController 底下所有的動骨。以 HSPE 自己記錄的那一份為主
        /// （換人後 RefreshDynamicBoneList 已經把新骨頭全部登記進去了），
        /// 再補上實際掛在物件樹上的元件，避免漏掉還沒被登記的。
        /// </summary>
        private static List<object> CollectBones(object pc, IDictionary inner)
        {
            List<object> outList = new List<object>();
            if (inner != null)
            {
                foreach (DictionaryEntry de in inner)
                    if (de.Key != null && !Has(outList, de.Key)) outList.Add(de.Key);
            }
            Component pcc = pc as Component;
            if (pcc != null)
            {
                AddComponents(outList, pcc, tDynamicBone);
                AddComponents(outList, pcc, tDynamicBoneVer02);
            }
            return outList;
        }

        private static void AddComponents(List<object> outList, Component root, Type t)
        {
            if (t == null) return;
            try
            {
                Component[] found = root.GetComponentsInChildren(t, true);
                if (found == null) return;
                for (int i = 0; i < found.Length; i++)
                    if (found[i] != null && !Has(outList, found[i])) outList.Add(found[i]);
            }
            catch { }
        }

        /// <summary>
        /// 骨頭的穩定識別字串：型別 + 從 PoseController 往下的 root 骨骼路徑。
        /// 換人之後物件參照全部換新，只有這條路徑是同一條
        /// （HSPE 存場景卡也是存這個路徑，見 &lt;dynamicBoneData root="…"&gt;）。
        /// </summary>
        private static string BoneKey(object bone, Transform pcRoot)
        {
            Component c = bone as Component;
            if (c == null) return null;
            Transform t = null;
            try
            {
                if (tDynamicBoneVer02 != null && tDynamicBoneVer02.IsInstanceOfType(bone))
                {
                    if (fDb2Root != null) t = fDb2Root.GetValue(bone) as Transform;
                    if (t == null) t = c.transform;
                    return "v2:" + PathFrom(t, pcRoot);
                }
                if (fDbRoot != null) t = fDbRoot.GetValue(bone) as Transform;
            }
            catch { }
            if (t == null) t = c.transform;
            return "v1:" + PathFrom(t, pcRoot);
        }

        private static string PathFrom(Transform t, Transform root)
        {
            if (t == null) return "?";
            string p = t.name;
            Transform cur = t.parent;
            int guard = 0;
            while (cur != null && cur != root && guard++ < 64)
            {
                p = cur.name + "/" + p;
                cur = cur.parent;
            }
            return p;
        }

        private static object FindPoseController(Studio.OCIChar oci)
        {
            if (oci == null) return null;
            foreach (object pc in AllPoseControllers())
            {
                try
                {
                    object target = fPcTarget == null ? null : fPcTarget.GetValue(pc);
                    if (target == null) continue;
                    object oc = fTargetOciChar == null ? null : fTargetOciChar.GetValue(target);
                    if (oc != null && ReferenceEquals(oc, oci)) return pc;
                }
                catch { }
            }
            // 退路：直接找掛在角色物件上的 PoseController
            try
            {
                if (oci.charInfo != null && tPoseController != null)
                {
                    Component c = oci.charInfo.GetComponent(tPoseController);
                    if (c != null) return c;
                }
            }
            catch { }
            return null;
        }

        private static string ColliderLabel(object collider, object editor)
        {
            string itemName = null;
            try
            {
                FieldInfo f = Fld(tCollidersEditor, "_target");
                object t = f == null ? null : f.GetValue(editor);
                object o = (t != null && fTargetOci != null) ? fTargetOci.GetValue(t) : null;
                if (o != null)
                {
                    PropertyInfo p = o.GetType().GetProperty("treeNodeObject",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object node = p != null ? p.GetValue(o, null) : null;
                    if (node == null)
                    {
                        FieldInfo nf = Fld(o.GetType(), "treeNodeObject");
                        node = nf != null ? nf.GetValue(o) : null;
                    }
                    if (node != null)
                    {
                        PropertyInfo tn = node.GetType().GetProperty("textName",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (tn != null) itemName = tn.GetValue(node, null) as string;
                    }
                }
            }
            catch { }
            Component cc = collider as Component;
            string colName = cc != null ? cc.gameObject.name : "Collider";
            return string.IsNullOrEmpty(itemName) ? colName : itemName + " / " + colName;
        }

        private static string Label(Studio.OCIChar oci)
        {
            try
            {
                if (oci != null && oci.charInfo != null && oci.charInfo.chaFile != null)
                {
                    string n = oci.charInfo.chaFile.parameter.fullname;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return "角色";
        }
    }
}
