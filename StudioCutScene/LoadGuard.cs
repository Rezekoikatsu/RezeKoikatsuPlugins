using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Studio;

namespace StudioCutScene
{
    /// <summary>
    /// 場景載入保護：某個物件載入時丟例外，就跳過它、讓後面的物件繼續載。
    ///
    /// 為什麼要有這個：Studio 載入場景是 AddObjectAssist.LoadChild 一個節點接一個節點建。
    /// 中間任何一個物件丟例外（模組版本不同、KKS 的道具拿到 KK 讀、某個外掛的 patch 出錯……），
    /// 例外會一路往上丟到 LoadSceneCoroutine，**整個載入就停在那裡**，排在後面的物件全部沒建。
    /// 合併過的場景卡最明顯：出錯的道具夾在中間，後面幾段整個消失，
    /// 接著物件順序錯位 → Timeline 的 Light()/Item() InvalidCastException、接管相機也接不起來。
    ///
    /// 做法：在 LoadChild(ObjectInfo, …) 掛 Harmony finalizer。出錯時：
    ///   1. 物件已經建好（是建完之後才出錯）→ 留著，把還沒載的子節點補載
    ///   2. 道具沒建起來 → 用遊戲本體的道具頂替（隱藏、名字標「載入失敗」），
    ///      這樣物件順序不變，Timeline / NodesConstraints 的編號還是對的；
    ///      卡片原本存的道具編號會放回去，存檔不會被改掉
    ///   3. 其他種類沒建起來 → 只能跳過（會少一個，後面的順序會差一格）
    /// 每一個都寫進 log：[載入保護] dicKey=… 。
    /// </summary>
    internal static class LoadGuard
    {
        internal static bool Enabled = true;
        private static ManualLogSource _log;
        private static readonly List<string> _hits = new List<string>();

        public static void Install(Harmony harmony)
        {
            _log = Logger.CreateLogSource("載入保護");
            try
            {
                MethodInfo target = AccessTools.Method(typeof(AddObjectAssist), "LoadChild",
                    new[] { typeof(ObjectInfo), typeof(ObjectCtrlInfo), typeof(TreeNodeObject) });
                if (target == null) { _log.LogWarning("找不到 AddObjectAssist.LoadChild，載入保護沒有啟用"); return; }
                harmony.Patch(target, finalizer: new HarmonyMethod(typeof(LoadGuard), "Finalizer"));
                MethodInfo scene = AccessTools.Method(typeof(Studio.Studio), "LoadScene", new[] { typeof(string) });
                if (scene != null)
                    harmony.Patch(scene, prefix: new HarmonyMethod(typeof(LoadGuard), "BeforeScene"));
            }
            catch (Exception e)
            {
                _log.LogWarning("載入保護掛不上（不影響其他功能）：" + e.Message);
            }
        }

        private static void BeforeScene() { _hits.Clear(); }

        /// <summary>這次載入跳過／頂替了哪些物件（給面板或之後的工具用）。</summary>
        public static IList<string> LastHits { get { return _hits; } }

        // __0 = ObjectInfo _child, __1 = ObjectCtrlInfo _parent, __2 = TreeNodeObject _parentNode
        private static Exception Finalizer(Exception __exception, ObjectInfo __0, ObjectCtrlInfo __1, TreeNodeObject __2)
        {
            if (__exception == null || !Enabled) return __exception;
            try
            {
                Handle(__exception, __0, __1, __2);
            }
            catch (Exception e2)
            {
                if (_log != null) _log.LogWarning("處理載入失敗的物件時又出錯：" + e2.Message);
            }
            return null;        // 吞掉，讓 Studio 繼續載下一個
        }

        private static void Handle(Exception ex, ObjectInfo info, ObjectCtrlInfo parent, TreeNodeObject parentNode)
        {
            if (info == null) { Report("（空的物件資料）", ex, "跳過"); return; }
            string desc = Describe(info);
            ObjectCtrlInfo oci = null;
            Studio.Studio st = Studio.Studio.Instance;
            if (st != null && st.dicObjectCtrl != null) st.dicObjectCtrl.TryGetValue(info.dicKey, out oci);

            if (oci != null)
            {
                int n = LoadMissingChildren(info, oci);
                Report(desc, ex, "物件已建好，留著" + (n > 0 ? "，補載 " + n + " 個子節點" : ""));
                return;
            }

            OIItemInfo item = info as OIItemInfo;
            if (item != null && Substitute(item, parent, parentNode))
            {
                Report(desc, ex, "用本體道具頂替（已隱藏，物件順序不變）");
                return;
            }
            Report(desc, ex, "跳過（後面物件的順序會差一格）");
        }

        /// <summary>道具換成遊戲本體一定有的道具再建一次，建完把卡片原本的編號放回去。</summary>
        private static bool Substitute(OIItemInfo item, ObjectCtrlInfo parent, TreeNodeObject parentNode)
        {
            int g = item.group, c = item.category, no = item.no;
            Traverse t = Traverse.Create(item);
            try
            {
                t.Property("group").SetValue(0);
                t.Property("category").SetValue(0);
                t.Property("no").SetValue(0);
                OCIItem oci = AddObjectItem.Load(item, parent, parentNode);
                if (oci == null) return false;
                try
                {
                    if (oci.treeNodeObject != null)
                    {
                        oci.treeNodeObject.SetVisible(false);
                        oci.treeNodeObject.textName = "[載入失敗] " + g + "/" + c + "/" + no;
                    }
                }
                catch (Exception) { }
                return true;
            }
            catch (Exception e)
            {
                if (_log != null) _log.LogWarning("  頂替也失敗：" + e.Message);
                return false;
            }
            finally
            {
                t.Property("group").SetValue(g);
                t.Property("category").SetValue(c);
                t.Property("no").SetValue(no);
            }
        }

        /// <summary>物件建好了但子節點沒載完：把還沒出現在 dicObjectCtrl 的子節點一個一個補載。</summary>
        private static int LoadMissingChildren(ObjectInfo info, ObjectCtrlInfo oci)
        {
            List<ObjectInfo> children = null;
            if (info is OIItemInfo) children = ((OIItemInfo)info).child;
            else if (info is OIFolderInfo) children = ((OIFolderInfo)info).child;
            if (children == null || children.Count == 0) return 0;
            Dictionary<int, ObjectCtrlInfo> dic = Studio.Studio.Instance.dicObjectCtrl;
            int n = 0;
            foreach (ObjectInfo ch in children)
            {
                if (ch == null || dic.ContainsKey(ch.dicKey)) continue;
                AddObjectAssist.LoadChild(ch, oci, null);       // 這一層一樣有保護
                n++;
            }
            return n;
        }

        private static string Describe(ObjectInfo info)
        {
            string kind;
            switch (info.kind)
            {
                case 0: kind = "角色"; break;
                case 1: kind = "道具"; break;
                case 2: kind = "燈光"; break;
                case 3: kind = "資料夾"; break;
                case 4: kind = "路徑"; break;
                case 5: kind = "相機"; break;
                default: kind = "種類 " + info.kind; break;
            }
            string s = "dicKey=" + info.dicKey + " " + kind;
            OIItemInfo it = info as OIItemInfo;
            if (it != null) s += " " + it.group + "/" + it.category + "/" + it.no;
            OIFolderInfo f = info as OIFolderInfo;
            if (f != null) s += " 「" + f.name + "」";
            return s;
        }

        private static void Report(string desc, Exception ex, string action)
        {
            string line = desc + " —— " + action;
            _hits.Add(line);
            if (_log != null)
                _log.LogWarning("[載入保護] " + line + "（" + ex.GetType().Name + ": " + ex.Message + "）");
        }
    }
}
