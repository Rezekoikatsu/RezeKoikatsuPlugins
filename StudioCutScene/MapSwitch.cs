using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 合併卡的內建地圖切換。
    ///
    /// 內建地圖（左邊「地圖」清單選的那個）是整張卡一個的設定，Timeline 也沒有
    /// 能切換它的軌道。合併工具在每段的包裝資料夾底下放了一個記號資料夾：
    ///
    ///     [MAPINFO] map=12 sun=0 opt=1                  位置/旋轉 = 那一段的地圖位置/旋轉
    ///     [MAPINFO] map=7239 sun=0 opt=1 guid=<模組GUID>  模組地圖：編號是模組自己的，
    ///                                                     這裡問 UAR 換成本機編號
    ///
    /// 每段的記號各自完整，不靠「卡片本身存的那張地圖」—— 在 Studio 裡播到哪一段存檔都不會壞。
    /// （舊版記號的 scene=1 還是認得：用卡片載入時 Studio 載好的那張。）
    ///
    /// 這裡每幀看「現在輪到哪一段」—— 包裝資料夾打勾、而且沒被停放到外太空
    /// （合併工具寫的啟用軌道和停放軌道會負責這件事）—— 然後照那一段的記號：
    ///
    ///   map = -1           把地圖藏起來（只是 SetActive，瞬間完成）
    ///   map = 目前這張      顯示回來、套回那一段的位置/旋轉
    ///   map = 別張          重新載入地圖（跟在地圖清單點一下一樣），會卡一下
    ///
    /// 記號資料夾同時就是地圖的「開關」和「把手」：
    ///   ・取消打勾 → 這一段的地圖關掉；打勾 → 打開
    ///   ・移動 / 旋轉資料夾 → 地圖跟著動；用 Studio 的地圖工具動地圖 → 位置寫回資料夾
    ///
    /// 卡裡沒有記號就完全不做事。換卡（開始載入）時會先把藏起來的地圖打開，
    /// 不然新卡如果剛好是同一張地圖，Studio 不會重新載入，地圖就一直是藏著的。
    /// </summary>
    internal static class MapSwitch
    {
        class Marker
        {
            public Studio.ObjectCtrlInfo wrap;
            public Studio.OCIFolder folder;      // 記號資料夾本身：打勾 = 地圖開、位置 = 地圖位置
            public Vector3 lastPos, lastRot;     // 上一次同步時的值，用來判斷是誰動了
            public int map = -1;
            public int sun = -1;
            public int opt = -1;
            public bool scene;          // 舊版 scene=1：就是卡片本身存的那張地圖
            public string guid;         // 模組地圖的 GUID（新版記號）
            public int local = int.MinValue;   // guid 換算出來的本機編號（快取）
            public string label;
        }

        static readonly Regex RX = new Regex(@"^\[MAPINFO\](.*)$");
        static readonly Regex KV = new Regex(@"(\w+)\s*=\s*(-?\d+)");
        static readonly Regex GUID = new Regex(@"(?:^|\s)guid=(\S+)");
        const int NoResolve = int.MinValue + 1;    // 有 GUID 但本機找不到那個模組

        static List<Marker> markers;
        // 只在「載入新場景」之後掃，跟 .cutscene.json 的自動載入同一個時機。
        // Reset() 會把 markers 清成 null，下一幀就掃一次。
        // 例外：剛載入完的那幾秒如果一個記號都沒掃到，再補掃幾次 ——
        // 載入事件偶爾比物件建好還早一點點到，掃太早會是空的，之後就再也不掃了。
        static float rescanUntil;
        static float nextRetry;
        static bool forceScan;
        static Marker applied;
        static bool hiddenByUs;
        static bool busy;
        // 你在 Studio 裡手動換掉 / 刪掉地圖之後，這一段就不再自動切回來（換段或重新載入才會再套用）。
        // 以前 Sync 每幀看到「地圖跟記號不一樣」就重新載入 —— 地圖怎麼刪都刪不掉。
        static bool userOverride;
        // 載入卡片時 Studio 載好的那張地圖，在本機的編號。
        // 模組地圖（sideloader）卡裡存的是模組自己的編號，載入時 UAR 會換成本機編號，
        // 兩者不一樣 —— 所以標了 scene=1 的記號一律用這個，不拿記號上的數字去載。
        static int sceneLocal = int.MinValue;

        /// <summary>給面板顯示的一句話；沒有記號時是空字串。</summary>
        public static string Status = "";

        /// <summary>
        /// 手動重掃（F7 的「立刻重找」）：在 Studio 裡自己新增、改名記號資料夾之後用。
        /// 已經套用的狀態保留，只是重新認一次記號。
        /// </summary>
        public static void Rescan()
        {
            forceScan = true;     // 不清 markers：同一個資料夾要沿用同一個物件
        }

        /// <summary>換卡／清掉設定時叫。藏起來的地圖一定要先打開。</summary>
        public static void Reset()
        {
            if (hiddenByUs) SetRootActive(true);
            hiddenByUs = false;
            markers = null;
            rescanUntil = Time.realtimeSinceStartup + 10f;
            nextRetry = 0f;
            sceneLocal = int.MinValue;
            applied = null;
            Status = "";
        }

        public static void Tick(MonoBehaviour host)
        {
            if (busy) return;
            Studio.Studio st;
            try { st = Singleton<Studio.Studio>.Instance; } catch { return; }
            if (st == null || st.dicObjectCtrl == null) return;
            var map = Singleton<Studio.Map>.Instance;
            if (map == null || map.isLoading) return;

            if (markers == null || forceScan
                || (markers.Count == 0 && Time.realtimeSinceStartup < rescanUntil
                    && Time.realtimeSinceStartup >= nextRetry))
            {
                Scan(st);
                forceScan = false;
                nextRetry = Time.realtimeSinceStartup + 1f;
            }
            if (markers == null || markers.Count == 0) return;
            // 記號資料夾已經不在場景裡（Studio「初始化」、刪掉物件…不會觸發載入事件）：
            // 那些記號作廢。以前會拿著已經刪掉的資料夾繼續運作，初始化之後地圖還被一直載回來。
            if (Prune(st))
            {
                if (markers.Count == 0)
                {
                    if (hiddenByUs) SetRootActive(true);
                    hiddenByUs = false;
                    applied = null;
                    sceneLocal = int.MinValue;
                    Status = "";
                    return;
                }
            }
            // 第一次看到記號、我們還沒動過地圖的時候，記下卡片本身那張的本機編號
            if (sceneLocal == int.MinValue && applied == null && map.no >= 0) sceneLocal = map.no;

            Marker act = null;
            // 輪到哪一段：主要看停放（沒輪到的包裝資料夾被移到外太空）。
            // 不看打勾 —— 輪到的那段你手動把包裝資料夾（或更上層）取消打勾時，
            // 要能把地圖關掉，而不是「找不到輪到的段落」就什麼都不做。
            // 只有好幾段都沒被停放（合併時關了停放）才用打勾來分。
            var cand = new List<Marker>();
            foreach (var m in markers)
                if (NotParkedChain(m.folder)) cand.Add(m);
            if (cand.Count == 1) act = cand[0];
            else
                foreach (var m in cand)
                    if (Visible(m.wrap)) { act = m; break; }
            if (act == null) return;
            if (act != applied) { Apply(act, st, map, host); return; }
            Sync(act, st, map, host);
        }

        static void Scan(Studio.Studio st)
        {
            var list = new List<Marker>();
            foreach (var kv in st.dicObjectCtrl)
            {
                var f = kv.Value as Studio.OCIFolder;
                if (f == null) continue;
                string nm = f.name ?? "";
                var m = RX.Match(nm);
                if (!m.Success || f.parentInfo == null) continue;
                // 同一個資料夾沿用同一個物件 —— 不然每 3 秒重掃一次，
                // 「已經套用的是這一個」的判斷就會失效，整組設定被重套一次，
                // 你手動調的東西會被蓋回去。
                Marker mk = markers == null ? null : markers.Find(x => x.folder == f);
                if (mk == null) mk = new Marker();
                // 段落 = 最外層的那個資料夾（包裝資料夾）。不能只看直接上層 ——
                // 記號被搬進 (MAP) 之類的子資料夾時，直接上層不會被停放、也可能被你取消打勾，
                // 以前就因此「兩段都判斷成沒輪到」，地圖開關像燈泡一樣要兩邊一起關才有用。
                mk.wrap = RootOf(f);
                mk.folder = f;
                mk.label = nm;
                mk.map = -1; mk.sun = -1; mk.opt = -1; mk.scene = false;
                string g = null;
                var gm = GUID.Match(m.Groups[1].Value);
                if (gm.Success) g = Uar.Unescape(gm.Groups[1].Value);
                mk.guid = g;
                mk.local = int.MinValue;       // 重掃就重新換算（編號 / GUID 可能被改過）
                foreach (Match p in KV.Matches(m.Groups[1].Value))
                {
                    int v;
                    if (!int.TryParse(p.Groups[2].Value, out v)) continue;
                    string k = p.Groups[1].Value.ToLowerInvariant();
                    if (k == "map") mk.map = v;
                    else if (k == "sun") mk.sun = v;
                    else if (k == "opt") mk.opt = v;
                    else if (k == "scene") mk.scene = v == 1;
                }
                list.Add(mk);
            }
            // 物件被刪掉、記號換了 → 之前套用的那一份不再算數
            if (applied != null && !list.Contains(applied))
                applied = null;
            markers = list;
            if (list.Count == 0) Status = "";
        }

        /// <summary>這個物件還在目前的場景裡（dicObjectCtrl 裡同一個物件）。</summary>
        internal static bool Alive(Studio.Studio st, Studio.ObjectCtrlInfo o)
        {
            try
            {
                if (o == null || o.objectInfo == null) return false;
                Studio.ObjectCtrlInfo cur;
                return st.dicObjectCtrl.TryGetValue(o.objectInfo.dicKey, out cur) && ReferenceEquals(cur, o);
            }
            catch { return false; }
        }

        /// <summary>把已經不在場景裡的記號拿掉；有拿掉任何一個回傳 true。</summary>
        static bool Prune(Studio.Studio st)
        {
            int n = markers.RemoveAll(m => !Alive(st, m.folder) || !Alive(st, m.wrap));
            if (n > 0 && applied != null && !markers.Contains(applied)) applied = null;
            return n > 0;
        }

        /// <summary>往上找到最外層的節點（段落的包裝資料夾）。</summary>
        internal static Studio.ObjectCtrlInfo RootOf(Studio.ObjectCtrlInfo o)
        {
            Studio.ObjectCtrlInfo r = o;
            try
            {
                for (int guard = 0; r != null && r.parentInfo != null && guard < 64; guard++)
                    r = r.parentInfo;
            }
            catch { }
            return r;
        }

        /// <summary>這個節點往上每一層都沒被停放到外太空（停放軌道把包裝資料夾移到 100,100,100）。</summary>
        internal static bool NotParkedChain(Studio.ObjectCtrlInfo o)
        {
            try
            {
                Studio.ObjectCtrlInfo p = o == null ? null : o.parentInfo;
                for (int guard = 0; p != null && guard < 64; guard++, p = p.parentInfo)
                    if (!NotParked(p)) return false;
                return o != null;
            }
            catch { return false; }
        }

        /// <summary>包裝資料夾沒被停放到外太空（合併工具的停放軌道移到 100,100,100）。</summary>
        internal static bool NotParked(Studio.ObjectCtrlInfo w)
        {
            try
            {
                if (w == null || w.objectInfo == null) return false;
                Vector3 p = w.objectInfo.changeAmount.pos;
                return p.sqrMagnitude < 50f * 50f;
            }
            catch { return false; }
        }

        /// <summary>這個節點自己，以及往上每一層，全部都有打勾。</summary>
        internal static bool Visible(Studio.ObjectCtrlInfo o)
        {
            try
            {
                for (int guard = 0; o != null && guard < 64; guard++, o = o.parentInfo)
                    if (o.treeNodeObject != null && !o.treeNodeObject.visible) return false;
            }
            catch { }
            return true;
        }

        /// <summary>這個記號在本機要載哪個編號。</summary>
        static int Want(Marker m)
        {
            if (m.map < 0) return -1;
            if (!string.IsNullOrEmpty(m.guid))
            {
                if (m.local == int.MinValue)
                {
                    int loc = Uar.StudioLocal(m.guid, m.map);
                    m.local = loc >= 0 ? loc : NoResolve;
                    if (loc < 0)
                        Debug.LogWarning("[CutScene] 地圖：本機找不到模組地圖 " + m.guid + " #" + m.map
                                         + (Uar.Available ? "" : "（找不到 Sideloader）"));
                }
                if (m.local != NoResolve) return m.local;
                if (m.scene && sceneLocal != int.MinValue) return sceneLocal;
                return NoResolve;
            }
            if (m.scene && sceneLocal != int.MinValue) return sceneLocal;
            return m.map;
        }

        static void Apply(Marker m, Studio.Studio st, Studio.Map map, MonoBehaviour host)
        {
            userOverride = false;
            int want = Want(m);
            if (want == NoResolve)
            {
                applied = m;
                m.lastPos = Pos(m); m.lastRot = Rot(m);
                Status = "地圖：本機找不到模組地圖 " + m.guid + "（不切換）";
                Debug.Log("[CutScene] " + Status);
                return;
            }
            if (want < 0 || !FolderOn(m))
            {
                SetRootActive(false);
                hiddenByUs = true;
                applied = m;
                m.lastPos = Pos(m); m.lastRot = Rot(m);
                Status = want < 0 ? "地圖：這一段沒有地圖（已隱藏）"
                                  : "地圖：記號資料夾或上層沒打勾（已隱藏）";
                Debug.Log("[CutScene] " + Status);
                return;
            }
            if (map.no == want && map.mapRoot != null)
            {
                SetRootActive(true);
                hiddenByUs = false;
                Restore(m, st, map);
                applied = m;
                Status = "地圖：#" + m.map;
                return;
            }
            host.StartCoroutine(LoadRoutine(m, want, st, map));
        }

        static IEnumerator LoadRoutine(Marker m, int want, Studio.Studio st, Studio.Map map)
        {
            busy = true;
            float t0 = Time.realtimeSinceStartup;
            Status = "地圖：載入 #" + want + "…";
            try { st.AddMap(want, false, false, true); }
            catch (Exception e)
            {
                Status = "地圖：載入 #" + m.map + " 失敗：" + e.Message;
                applied = m;            // 不要每幀重試
                busy = false;
                yield break;
            }
            // 等 Studio 自己的旗標：載完、而且真的是這一張
            while (Time.realtimeSinceStartup - t0 < 30f)
            {
                yield return null;
                if (!map.isLoading && map.no == want && map.mapRoot != null) break;
            }
            yield return null;
            hiddenByUs = false;
            SetRootActive(true);
            Restore(m, st, map);
            applied = m;
            Status = "地圖：#" + m.map + "（切換花了 "
                     + (Time.realtimeSinceStartup - t0).ToString("F1") + " 秒）";
            Debug.Log("[CutScene] " + Status);
            busy = false;
        }

        static bool FolderOn(Marker m)
        {
            // 記號資料夾本身，或上面任何一層（包裝資料夾、更外層）沒打勾 → 地圖關
            return m.folder == null || Visible(m.folder);
        }

        static Vector3 Pos(Marker m)
        {
            try { return m.folder.objectInfo.changeAmount.pos; } catch { return Vector3.zero; }
        }

        static Vector3 Rot(Marker m)
        {
            try { return m.folder.objectInfo.changeAmount.rot; } catch { return Vector3.zero; }
        }

        /// <summary>
        /// 已經輪到這一段之後，每幀跟記號資料夾保持同步：
        ///   ・資料夾打勾 / 取消 → 地圖開 / 關（地圖是別張而且還沒載的話就去載）
        ///   ・資料夾被移動 / 旋轉 → 地圖跟著動
        ///   ・反過來，用 Studio 的地圖工具移動地圖 → 位置寫回資料夾，存檔就留得住
        /// </summary>
        static void Sync(Marker m, Studio.Studio st, Studio.Map map, MonoBehaviour host)
        {
            int want = Want(m);
            if (want == NoResolve) return;
            bool on = want >= 0 && FolderOn(m);
            if (on && map.no != want)
            {
                // 輪到這一段之後地圖變成別張 / 沒有了 = 你手動換的（F7 自己載的會等載完才算套用）。
                // 尊重手動的選擇，不要每幀載回來。
                if (!userOverride)
                {
                    userOverride = true;
                    hiddenByUs = false;
                    Status = "地圖：已手動更換／刪除，這一段不自動切回（換段或重新載入卡片才會再套用）";
                    Debug.Log("[CutScene] " + Status);
                }
                return;
            }
            if (userOverride) return;
            bool rootOn = false;
            try { rootOn = map.mapRoot != null && map.mapRoot.activeSelf; } catch { }
            if (rootOn != on)
            {
                SetRootActive(on);
                hiddenByUs = !on;
                Status = on ? "地圖：#" + m.map : "地圖：記號資料夾或上層沒打勾（已隱藏）";
                if (on) Restore(m, st, map);
                return;
            }
            if (!on) return;

            Vector3 fp = Pos(m), fr = Rot(m);
            try
            {
                var ca = st.sceneInfo.caMap;
                bool folderMoved = (fp - m.lastPos).sqrMagnitude > 1e-10f || (fr - m.lastRot).sqrMagnitude > 1e-10f;
                bool mapMoved = (ca.pos - m.lastPos).sqrMagnitude > 1e-10f || (ca.rot - m.lastRot).sqrMagnitude > 1e-10f;
                if (folderMoved)
                {
                    if ((ca.pos - fp).sqrMagnitude > 1e-10f) ca.pos = fp;
                    if ((ca.rot - fr).sqrMagnitude > 1e-10f) ca.rot = fr;
                }
                else if (mapMoved)
                {
                    var fca = m.folder.objectInfo.changeAmount;
                    fca.pos = ca.pos;
                    fca.rot = ca.rot;
                    fp = ca.pos; fr = ca.rot;
                }
                m.lastPos = fp; m.lastRot = fr;
            }
            catch { }
        }

        /// <summary>套回那一段的地圖位置/旋轉、太陽、地圖選項。</summary>
        static void Restore(Marker m, Studio.Studio st, Studio.Map map)
        {
            Vector3 fp = Pos(m), fr = Rot(m);
            try
            {
                var ca = st.sceneInfo.caMap;
                if ((ca.pos - fp).sqrMagnitude > 1e-8f) ca.pos = fp;
                if ((ca.rot - fr).sqrMagnitude > 1e-8f) ca.rot = fr;
            }
            catch { }
            m.lastPos = fp; m.lastRot = fr;
            if (m.sun >= 0)
            {
                try
                {
                    st.sceneInfo.sunLightType = m.sun;
                    map.sunType = (SunLightInfo.Info.Type)m.sun;
                }
                catch { }
            }
            if (m.opt >= 0)
            {
                try
                {
                    st.sceneInfo.mapOption = m.opt == 1;
                    map.visibleOption = m.opt == 1;
                }
                catch { }
            }
        }

        static void SetRootActive(bool on)
        {
            try
            {
                var map = Singleton<Studio.Map>.Instance;
                if (map != null && map.mapRoot != null && map.mapRoot.activeSelf != on)
                    map.mapRoot.SetActive(on);
            }
            catch { }
        }
    }
    /// <summary>
    /// 問 Sideloader 的 UniversalAutoResolver：模組的「編號 + GUID」在本機是幾號。
    /// 跟 UAR 自己載入場景時的換算一模一樣：
    ///   地圖 / 調色（ACE） GetStudioResolveInfos(guid, slot, true).First().LocalSlot
    ///   Ramp               TryGetResolutionInfo(slot, "Ramp", guid).LocalSlot
    /// 用反射呼叫，沒裝 Sideloader 也不會出錯（回傳 -1）。
    /// </summary>
    internal static class Uar
    {
        static bool inited;
        static MethodInfo mStudio, mRamp;

        public static bool Available { get { Init(); return mStudio != null; } }

        static void Init()
        {
            if (inited) return;
            inited = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("Sideloader.AutoResolver.UniversalAutoResolver", false); } catch { }
                if (t == null) continue;
                var F = BindingFlags.Public | BindingFlags.Static;
                try { mStudio = t.GetMethod("GetStudioResolveInfos", F, null, new[] { typeof(string), typeof(int), typeof(bool) }, null); } catch { }
                try { mRamp = t.GetMethod("TryGetResolutionInfo", F, null, new[] { typeof(int), typeof(string), typeof(string) }, null); } catch { }
                break;
            }
        }

        static int Local(object info)
        {
            if (info == null) return -1;
            try
            {
                var p = info.GetType().GetProperty("LocalSlot");
                return p == null ? -1 : (int)p.GetValue(info, null);
            }
            catch { return -1; }
        }

        /// <summary>地圖、調色（ACE）這類 Studio 清單項目。</summary>
        public static int StudioLocal(string guid, int slot)
        {
            Init();
            if (mStudio == null || string.IsNullOrEmpty(guid)) return -1;
            try
            {
                var e = mStudio.Invoke(null, new object[] { guid, slot, true }) as IEnumerable;
                if (e != null) foreach (var x in e) return Local(x);
            }
            catch (Exception ex) { Debug.LogWarning("[CutScene] UAR 換算失敗：" + ex.Message); }
            return -1;
        }

        public static int RampLocal(string guid, int slot)
        {
            Init();
            if (mRamp == null || string.IsNullOrEmpty(guid)) return -1;
            try { return Local(mRamp.Invoke(null, new object[] { slot, "Ramp", guid })); }
            catch (Exception ex) { Debug.LogWarning("[CutScene] UAR 換算失敗：" + ex.Message); }
            return -1;
        }

        public static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s); } catch { return s; }
        }
    }
}
