using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 合併場景卡：KKPE 的碰撞器只作用在自己那一段的角色／物件身上。
    ///
    /// 為什麼要有這個
    /// --------------
    /// kkscenebridge 合併出來的卡，每一段是一個最上層資料夾，沒輪到的段落停在同一個遠處。
    /// KKPE 的碰撞器（例如 Dynamic Bone Collider 道具）對「清單裡沒寫到」的動骨，
    /// 載入時照預設值決定吃不吃 —— 但用飾品做的頭髮之類，載入那一刻還沒啟用，
    /// KKPE 根本沒把它們列進清單，於是別段角色的頭髮被這顆碰撞器吸走（兩段停在同一個地方，飛天吸附）。
    /// 合併工具在卡片裡也補不到：來源卡存檔時同樣沒列，卡片裡沒有這些骨頭的路徑。
    ///
    /// 做法
    /// ----
    /// 載入場景卡、載入結束之後（再等一下讓 KKPE 讀完自己的資料），
    /// 對每一顆碰撞器：**不在同一個最上層資料夾**的角色／物件，所有動骨（連沒啟用的）一律設成不吃；
    /// 同一段裡只留作者在清單裡明確開啟的那幾根，其餘也設成不吃（跟 F6「一鍵修復」的結果一樣 ——
    /// 同一段的飾品頭髮一樣沒被列進清單，一樣會被吸）。之後每 2 秒檢查一次播放中才冒出來的動骨。
    /// 用 KKPE 自己的 SetIgnoreDynamicBone，內部清單和 DynamicBone 身上的碰撞器清單會一起改，
    /// 存檔時也會照實寫進卡片。同一段裡的設定完全不動。
    ///
    /// 只處理看得出是合併卡的場景：最上層有一個叫「(CAM)」的資料夾（kkscenebridge 合併後的形狀）。
    /// 一般作者的卡常常把碰撞器和角色放在不同的最上層資料夾，那是故意的，不能動。
    ///
    /// 全部走反射，沒有 KKPE 就什麼都不做。
    /// </summary>
    internal class ColliderIsolate : MonoBehaviour
    {
        internal static bool Enabled = true;
        internal static string LastReport = "";
        private static ManualLogSource _log;

        private int seenStamp;
        private bool waiting;
        private float doneLoadingAt = -1f;
        private int passes;

        internal static void Create(GameObject host)
        {
            _log = BepInEx.Logging.Logger.CreateLogSource("碰撞器分段");
            host.AddComponent<ColliderIsolate>();
        }

        void Start() { seenStamp = SceneLoadWatch.Stamp; }

        void Update()
        {
            if (!Enabled || !SceneLoadWatch.Active) return;
            if (SceneLoadWatch.Stamp != seenStamp)
            {
                seenStamp = SceneLoadWatch.Stamp;
                waiting = true;
                guarding = false;
                boneCache.Clear();
                passes = 0;
                doneLoadingAt = -1f;
            }
            if (!waiting) return;
            if (SceneLoadWatch.IsLoading) { doneLoadingAt = -1f; return; }
            if (doneLoadingAt < 0f) { doneLoadingAt = Time.realtimeSinceStartup; return; }
            // KKPE 讀碰撞器資料是延後幾格才做的；飾品頭髮也可能晚一點才建好。
            // 載入結束後 1 秒做一次、4 秒再補一次。
            float wait = passes == 0 ? 1f : 4f;
            if (Time.realtimeSinceStartup - doneLoadingAt < wait) return;
            passes++;
            try { Run(); }
            catch (Exception e) { if (_log != null) _log.LogWarning("處理失敗：" + e.Message); }
            if (passes >= 2) { waiting = false; guarding = true; nextGuard = Time.realtimeSinceStartup + 2f; }
        }

        // 讀完之後還會有新的動骨冒出來（飾品頭髮之類、播放中才建好或才啟用），
        // KKPE 照樣可能把碰撞器掛上去。之後每 2 秒檢查一次：
        // 只看「身上真的掛著碰撞器、而且不是作者明確開啟」的動骨，有才動手，平常幾乎不做事。
        private bool guarding;
        private float nextGuard;

        void LateUpdate()
        {
            if (!guarding || !Enabled || SceneLoadWatch.IsLoading || waiting) return;
            if (Time.realtimeSinceStartup < nextGuard) return;
            nextGuard = Time.realtimeSinceStartup + 2f;
            try { Apply(true); }
            catch (Exception e) { guarding = false; if (_log != null) _log.LogWarning("檢查失敗，停止檢查：" + e.Message); }
        }

        // =============================================================
        // 反射
        // =============================================================
        private static bool inited, available;
        private static Type tPoseController, tCollidersEditor, tDynamicBone, tDynamicBoneVer02;
        private static FieldInfo fPoseControllers, fPcCollidersEditor, fPcTarget, fTargetOci;
        private static FieldInfo fDirtyColliders, fCeTarget, fAllDynamicBones;
        private static MethodInfo mSetIgnore;

        private static void Init()
        {
            if (inited) return;
            inited = true;
            tPoseController = FindType("HSPE.PoseController");
            tCollidersEditor = FindType("HSPE.AMModules.CollidersEditor");
            tDynamicBone = FindType("DynamicBone");
            tDynamicBoneVer02 = FindType("DynamicBone_Ver02");
            if (tPoseController == null || tCollidersEditor == null) return;
            fPoseControllers = Fld(tPoseController, "_poseControllers");
            fPcCollidersEditor = Fld(tPoseController, "_collidersEditor");
            fPcTarget = Fld(tPoseController, "_target");
            fDirtyColliders = Fld(tCollidersEditor, "_dirtyColliders");
            fCeTarget = Fld(tCollidersEditor, "_target");
            Type tTarget = FindType("HSPE.AMModules.GenericOCITarget");
            if (tTarget != null) fTargetOci = Fld(tTarget, "oci");
            foreach (MethodInfo m in tCollidersEditor.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "SetIgnoreDynamicBone" && m.GetParameters().Length == 4) { mSetIgnore = m; break; }
            }
            available = fPoseControllers != null && fPcCollidersEditor != null && fPcTarget != null
                        && fDirtyColliders != null && fTargetOci != null && mSetIgnore != null;
            if (!available && _log != null)
                _log.LogWarning("KKPE 內部結構跟預期不同（版本不同？），合併卡的碰撞器分段沒有啟用");
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(name); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        private static FieldInfo Fld(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        // =============================================================
        // 主流程
        // =============================================================
        private static void Run() { Apply(false); }

        /// <summary>
        /// 每顆碰撞器 × 每個 PoseController × 每根動骨：
        ///   別段的 → 一律不吃；
        ///   同一段的 → 作者在清單裡明確開啟（true）的照舊，其餘不吃（跟 F6「一鍵修復」用其他角色當範本的結果一樣）。
        /// guardOnly：只處理「身上真的掛著這顆碰撞器」的動骨，給播放中的定期檢查用。
        /// </summary>
        private static void Apply(bool guardOnly)
        {
            Init();
            if (!available) return;

            Studio.Studio st = Studio.Studio.Instance;
            if (st == null || st.dicObjectCtrl == null) return;
            bool merged = false;
            foreach (Studio.ObjectCtrlInfo oci in st.dicObjectCtrl.Values)
            {
                Studio.TreeNodeObject r = RootOf(oci);
                if (r != null && r.textName == "(CAM)") { merged = true; break; }
            }
            if (!merged) return;

            List<object> pcs = new List<object>();
            try
            {
                IEnumerable set = fPoseControllers.GetValue(null) as IEnumerable;
                if (set != null) foreach (object pc in set) if (pc != null) pcs.Add(pc);
            }
            catch { }

            // 每個 PoseController 屬於哪一段（最上層資料夾）
            Dictionary<object, Studio.TreeNodeObject> segOf = new Dictionary<object, Studio.TreeNodeObject>();
            foreach (object pc in pcs) segOf[pc] = SegmentOf(TargetOci(fPcTarget, pc));

            int nCol = 0, nBones = 0, nPc = 0;
            List<object> seenCol = new List<object>();
            foreach (object pcOwner in pcs)
            {
                object editor = null;
                try { editor = fPcCollidersEditor.GetValue(pcOwner); } catch { }
                if (editor == null) continue;
                IDictionary dirty = null;
                try { dirty = fDirtyColliders.GetValue(editor) as IDictionary; } catch { }
                if (dirty == null || dirty.Count == 0) continue;
                Studio.TreeNodeObject colSeg = SegmentOf(TargetOci(fCeTarget, editor)) ?? segOf[pcOwner];
                if (colSeg == null) continue;

                foreach (DictionaryEntry de in dirty)
                {
                    object collider = de.Key;
                    if (!(collider is Component) || de.Value == null || seenCol.Contains(collider)) continue;
                    seenCol.Add(collider);
                    bool touched = false;
                    foreach (object pc in pcs)
                    {
                        Studio.TreeNodeObject s = segOf[pc];
                        if (s == null) continue;                       // 不在任何一段（例如 (CAM) 底下）：不動
                        int n = IgnoreAll(editor, collider, pc, de.Value, s == colSeg, guardOnly);
                        if (n > 0) { nBones += n; nPc++; touched = true; }
                    }
                    if (touched) nCol++;
                }
            }
            if (nCol == 0) return;
            LastReport = string.Format(guardOnly
                ? "合併卡：播放中又有 {2} 根動骨被碰撞器掛上，已拿掉（{0} 顆碰撞器、{1} 筆）"
                : "合併卡：{0} 顆碰撞器只留作者開啟的動骨，別段全部不吃（{1} 筆，共 {2} 根動骨）", nCol, nPc, nBones);
            if (_log != null) _log.LogInfo(LastReport);
        }

        /// <summary>這顆碰撞器對這個 PoseController 底下每一根動骨（含沒啟用的）都設成不吃。回傳實際改掉的根數。</summary>
        private static readonly Dictionary<object, List<object>> boneCache = new Dictionary<object, List<object>>();
        private static float cacheTime;

        private static int IgnoreAll(object editor, object collider, object pc, object colliderData,
                                     bool sameSeg, bool guardOnly)
        {
            IDictionary inner = null;
            try
            {
                if (fAllDynamicBones == null || !fAllDynamicBones.DeclaringType.IsInstanceOfType(colliderData))
                    fAllDynamicBones = Fld(colliderData.GetType(), "allDynamicBones");
                IDictionary all = fAllDynamicBones == null ? null : fAllDynamicBones.GetValue(colliderData) as IDictionary;
                if (all != null && all.Contains(pc)) inner = all[pc] as IDictionary;
            }
            catch { }

            // 動骨清單：KKPE 記的 + 物件樹上實際有的（連沒啟用的）。後者每 10 秒才重抓一次。
            if (Time.realtimeSinceStartup - cacheTime > 10f) { boneCache.Clear(); cacheTime = Time.realtimeSinceStartup; }
            List<object> found;
            if (!boneCache.TryGetValue(pc, out found))
            {
                found = new List<object>();
                Component pcc = pc as Component;
                if (pcc != null)
                {
                    AddComponents(found, pcc, tDynamicBone);
                    AddComponents(found, pcc, tDynamicBoneVer02);
                }
                boneCache[pc] = found;
            }
            List<object> bones = new List<object>(found);
            if (inner != null)
                foreach (DictionaryEntry e in inner) if (e.Key != null && !bones.Contains(e.Key)) bones.Add(e.Key);

            int changed = 0;
            foreach (object bone in bones)
            {
                if (bone == null || (bone is UnityEngine.Object && (UnityEngine.Object)bone == null)) continue;
                bool listedOn = false, listedOff = false;
                if (inner != null && inner.Contains(bone))
                {
                    object v = inner[bone];
                    listedOn = v is bool && (bool)v;
                    listedOff = v is bool && !(bool)v;
                }
                if (sameSeg && listedOn) continue;              // 作者在這一段明確開啟的：照舊
                bool attached = HasCollider(bone, collider);
                if (guardOnly ? !attached : (listedOff && !attached)) continue;
                try
                {
                    mSetIgnore.Invoke(editor, new object[] { collider, pc, bone, true });
                    changed++;
                }
                catch { }
            }
            return changed;
        }

        private static readonly Dictionary<Type, FieldInfo> colliderListField = new Dictionary<Type, FieldInfo>();

        /// <summary>這根動骨身上的碰撞器清單裡有沒有這顆。查不到就當作有（寧可多設一次）。</summary>
        private static bool HasCollider(object bone, object collider)
        {
            try
            {
                Type t = bone.GetType();
                FieldInfo f;
                if (!colliderListField.TryGetValue(t, out f))
                {
                    f = Fld(t, "m_Colliders") ?? Fld(t, "Colliders");
                    colliderListField[t] = f;
                }
                IList list = f == null ? null : f.GetValue(bone) as IList;
                return list == null || list.Contains(collider);
            }
            catch { return true; }
        }

        private static void AddComponents(List<object> outList, Component root, Type t)
        {
            if (t == null) return;
            try
            {
                Component[] found = root.GetComponentsInChildren(t, true);
                if (found == null) return;
                foreach (Component c in found) if (c != null && !outList.Contains(c)) outList.Add(c);
            }
            catch { }
        }

        private static Studio.ObjectCtrlInfo TargetOci(FieldInfo targetField, object owner)
        {
            try
            {
                object target = targetField == null ? null : targetField.GetValue(owner);
                return target == null ? null : fTargetOci.GetValue(target) as Studio.ObjectCtrlInfo;
            }
            catch { return null; }
        }

        private static Studio.TreeNodeObject RootOf(Studio.ObjectCtrlInfo oci)
        {
            if (oci == null || oci.treeNodeObject == null) return null;
            Studio.TreeNodeObject n = oci.treeNodeObject;
            int guard = 0;
            while (n.parent != null && guard++ < 256) n = n.parent;
            return n;
        }

        /// <summary>這個物件屬於哪一段：最上層資料夾。在「(CAM)」底下、或自己就在最上層的，不算任何一段。</summary>
        private static Studio.TreeNodeObject SegmentOf(Studio.ObjectCtrlInfo oci)
        {
            Studio.TreeNodeObject r = RootOf(oci);
            if (r == null || r == oci.treeNodeObject || r.textName == "(CAM)") return null;
            return r;
        }
    }
}
