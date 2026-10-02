using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 在 VR 裡自動把指定的工作室物件「取消勾選」（就是工作區樹狀清單左邊那個眼睛）。
    ///
    /// 為什麼需要這個
    /// --------------
    /// xukmi FX Shaders 那一組（景深、Two Tone Fog…）是**全螢幕後處理**。
    /// 它們掛在單眼相機上算一次，VRGIN 把畫面轉成雙眼之後再算一次，
    /// 於是在頭顯裡變成整片糊掉、偏色、泛光 —— 桌面上卻完全正常。
    /// 但同一張場景卡在桌面錄影又需要那些效果，所以不能直接從場景裡刪掉：
    /// 唯一合理的做法是「進 VR 就把它關掉，卡片本身不動」。
    ///
    /// 為什麼是改 TreeNodeObject 而不是關 GameObject
    /// --------------------------------------------
    /// 直接 SetActive(false) 的話，工作區清單上那個勾還是亮的 —— 看起來開著、
    /// 實際上關著，而且存檔時 visible 還是 true。反過來也一樣糟：
    /// 使用者手動勾回來，我們這邊的狀態就跟畫面對不上。
    ///
    /// TreeNodeObject.SetVisible(bool) 正好就是那個勾勾按下去會走的路
    /// （驗過 IL：寫 m_Visible → 觸發 onVisible → 換 sprite → 傳給子節點），
    /// 所以呼叫它等於使用者自己去點了一下，畫面、勾勾、存檔三者永遠一致。
    ///
    /// 為什麼「一個物件只處理一次」
    /// --------------------------
    /// 如果每幀都硬把它壓成關閉，使用者在 VR 裡想暫時打開某個效果看一眼就辦不到 ——
    /// 勾了馬上又被關掉，而且完全看不出是誰在關。所以只在物件**第一次出現**
    /// （載入場景／手動新增）時動一次，之後就不再碰它。
    ///
    /// 為什麼要等「安定」再動手
    /// ----------------------
    /// 場景是分批載入的，物件先進 dicObjectCtrl、visible 才從卡片資料寫回去。
    /// 太早關的話會被載入程序自己覆蓋回 true。所以看 dicObjectCtrl 的數量，
    /// 停止變動一段時間之後才算「載完」。
    /// </summary>
    public static class VrFxMute
    {
        /// <summary>要不要自動關。</summary>
        public static bool Enabled = true;

        /// <summary>只在 VR 裡關。桌面上要留著原本的效果。</summary>
        public static bool OnlyInVr = true;

        /// <summary>
        /// 要關掉的清單，逗號分隔。每一項可以是兩種寫法之一：
        ///
        ///   **名稱**（預設用這種）：不分大小寫的「包含」比對，
        ///   例如 <c>Depth of Field</c> 一條就涵蓋九種景深。
        ///   比對的對象有兩個，任一個中就算：
        ///     1. 物品在工作室清單資料庫裡的原始名稱（Info.dicItemLoadInfo 的 name）
        ///     2. 工作區樹狀清單上顯示的文字（你自己改過名也還是抓得到）
        ///
        ///   **編號**：<c>群組:分類:編號</c>，編號可寫範圍（<c>7-15</c>），
        ///   三欄都可以寫 <c>*</c>。
        ///
        /// 為什麼預設改用名稱
        /// ----------------
        /// 第一版預設是 <c>7364:0:2, 7364:0:7-15</c> —— 那是直接從場景卡裡讀出來的
        /// 群組／編號，結果掃了 996 個物件一個都沒中。
        /// 原因幾乎可以確定是 **Sideloader 會重新編號**：mod 的 csv 寫的是
        /// 7364 / 0 / 2，但為了避免不同 mod 撞號，載入時會配一組執行期專用的編號，
        /// 存檔時再換回原本的。所以「卡片裡的編號」和「執行時物件身上的編號」
        /// 本來就不是同一組，拿卡片的號碼去比對永遠不會中。
        ///
        /// 名稱不會被重新編號，而且就是你在工作區清單上看到的那個字，
        /// 要加要減都直觀。（真正的執行期編號可以按面板上的
        /// 「把場上物件列進 log」查，想用編號寫規則就用那邊列出來的。）
        /// </summary>
        public static string ListText = DefaultList;

        public const string DefaultList = "Two Tone Fog, Depth of Field";

        /// <summary>(FX) 資料夾的名字。獨立開關用的，不走清單。</summary>
        public const string FxFolder = "(FX)";

        /// <summary>等 dicObjectCtrl 不再變動幾秒才算載入完成。</summary>
        public static float SettleSeconds = 1.0f;

        public static string LastReport = "尚未執行";

        /// <summary>這一輪（自上次載入以來）總共關掉幾個。</summary>
        public static int MutedThisScene;

        /// <summary>這一輪看到幾個家具／特效類的物件（OCIItem）。</summary>
        public static int ItemsThisScene;

        // ------------------------------------------------------------ 清單解析

        struct Rule
        {
            public string name;           // 非 null = 用名稱比對（已轉小寫）
            public int group, category;   // -1 = *
            public int noFrom, noTo;      // -1 = *
        }

        static readonly List<Rule> rules = new List<Rule>();
        static string parsedFrom = null;
        static int badEntries;

        static void EnsureRules()
        {
            if (parsedFrom == ListText) return;
            parsedFrom = ListText;
            rules.Clear();
            badEntries = 0;

            string src = ListText ?? "";
            foreach (string rawPart in src.Split(','))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;

                Rule r;

                // 先收在區域變數再組 struct。
                // 直接對未指派的 struct 欄位用 out 在舊版 C# 編譯器上不一定過，
                // 這裡沒有理由去賭那個。
                int g, c, from, to;
                string[] f = part.Split(':');

                // 三段、而且三段都是數字或 * —— 才當成編號規則。
                // 其他一律當名稱：名稱裡本來就不會有冒號，而「看起來像編號但解不開」
                // 如果默默當成名稱去比對，會變成一條永遠不中的規則且毫無提示。
                if (f.Length == 3 && Field(f[0], out g) && Field(f[1], out c)
                    && ParseNo(f[2], out from, out to))
                {
                    r.name = null;
                    r.group = g; r.category = c; r.noFrom = from; r.noTo = to;
                }
                else if (part.IndexOf(':') >= 0)
                {
                    badEntries++; continue;
                }
                else
                {
                    r.name = part.ToLowerInvariant();
                    r.group = -1; r.category = -1; r.noFrom = -1; r.noTo = -1;
                }
                rules.Add(r);
            }
        }

        static bool Field(string s, out int v)
        {
            s = s.Trim();
            if (s == "*") { v = -1; return true; }
            return Int(s, out v);
        }

        // .NET 3.5 的 int.TryParse 在這個 runtime 上可用，但 culture 會影響負號／空白，
        // 這裡的輸入只可能是十進位正整數，自己掃比較不會出意外。
        static bool Int(string s, out int v)
        {
            v = 0;
            s = s.Trim();
            if (s.Length == 0) return false;
            int acc = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                acc = acc * 10 + (c - '0');
                if (acc > 99999999) return false;
            }
            v = acc;
            return true;
        }

        /// <summary>
        /// 加一條「編號」欄位的解析：單一數字、範圍 a-b，或 *。
        /// </summary>
        static bool ParseNo(string s, out int from, out int to)
        {
            from = -1; to = -1;
            s = s.Trim();
            if (s == "*") return true;

            int dash = s.IndexOf('-');
            if (dash > 0)
            {
                if (!Int(s.Substring(0, dash), out from)) return false;
                if (!Int(s.Substring(dash + 1), out to)) { from = -1; return false; }
                if (to < from) { int t = from; from = to; to = t; }
                return true;
            }
            if (!Int(s, out from)) { from = -1; return false; }
            to = from;
            return true;
        }

        /// <summary>
        /// 只用「名稱」規則比對。給不是家具的節點用：資料夾、光源、角色……
        ///
        /// 為什麼要跟 Matches 分開：編號規則（含 <c>*:*:*</c>）不應該套到這些節點上。
        /// 合成一個假的編號丟進 Matches 的話，使用者寫一條 <c>*:*:*</c> 就會把
        /// 整個場景連角色一起關掉 —— 那不是他要的，而且完全看不出是誰關的。
        /// </summary>
        static bool MatchesName(string treeName)
        {
            if (treeName == null) return false;
            EnsureRules();
            for (int i = 0; i < rules.Count; i++)
            {
                Rule r = rules[i];
                if (r.name == null) continue;
                if (treeName.IndexOf(r.name, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <param name="catName">物品資料庫裡的原始名稱，已轉小寫；沒有就傳 null</param>
        /// <param name="treeName">工作區清單上顯示的文字，已轉小寫；沒有就傳 null</param>
        static bool Matches(int group, int category, int no, string catName, string treeName)
        {
            EnsureRules();
            for (int i = 0; i < rules.Count; i++)
            {
                Rule r = rules[i];
                if (r.name != null)
                {
                    if (catName != null && catName.IndexOf(r.name, StringComparison.Ordinal) >= 0) return true;
                    if (treeName != null && treeName.IndexOf(r.name, StringComparison.Ordinal) >= 0) return true;
                    continue;
                }
                if (r.group >= 0 && r.group != group) continue;
                if (r.category >= 0 && r.category != category) continue;
                if (r.noFrom >= 0 && (no < r.noFrom || no > r.noTo)) continue;
                return true;
            }
            return false;
        }

        /// <summary>清單合法的項目數，給面板顯示用。</summary>
        public static int RuleCount { get { EnsureRules(); return rules.Count; } }

        /// <summary>清單裡看不懂的項目數。不是 0 就代表使用者打錯了。</summary>
        public static int BadCount { get { EnsureRules(); return badEntries; } }

        // ------------------------------------------------------------ 反射

        static Type tStudio, tOciItem, tTreeNode, tInfoDb;
        static PropertyInfo pInstance, pItemInfo, pGroup, pCategory, pNo, pNodeVisible;
        static PropertyInfo pTreeText, pInfoInstance;
        static FieldInfo fDic, fTreeNode, fDicItem, fItemName;
        static MethodInfo mSetVisible;
        static bool probed;
        static string probeError;

        /// <summary>
        /// 找齊需要的型別／成員。
        ///
        /// 失敗**不**快取。ReflectUtil.Find 已經為了同樣的理由不快取 miss ——
        /// 太早問一次就把失敗記一輩子，後來型別載進來也永遠找不到，
        /// 而且這種失敗完全沒有徵兆。Tick 那邊本來就有 0.25 秒節流，重試不貴。
        /// </summary>
        static bool Probe()
        {
            if (probed) return true;
            probeError = null;
            try
            {
                tStudio = ReflectUtil.Find("Studio.Studio");
                if (tStudio == null) { probeError = "找不到 Studio.Studio"; return false; }

                // Singleton<Studio>.Instance 是泛型基底的靜態成員。
                // FlattenHierarchy 抓得到，但不同版本不保證，所以待會還有 FindObjectOfType 備援。
                pInstance = tStudio.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                fDic = tStudio.GetField("dicObjectCtrl",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fDic == null) { probeError = "找不到 Studio.dicObjectCtrl"; return false; }

                tOciItem = ReflectUtil.Find("Studio.OCIItem");
                if (tOciItem == null) { probeError = "找不到 Studio.OCIItem"; return false; }

                pItemInfo = tOciItem.GetProperty("itemInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pItemInfo == null) { probeError = "找不到 OCIItem.itemInfo"; return false; }

                Type tInfo = ReflectUtil.Find("Studio.OIItemInfo");
                if (tInfo == null) { probeError = "找不到 Studio.OIItemInfo"; return false; }
                pGroup = tInfo.GetProperty("group", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                pCategory = tInfo.GetProperty("category", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                pNo = tInfo.GetProperty("no", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pGroup == null || pCategory == null || pNo == null)
                { probeError = "OIItemInfo 少了 group/category/no"; return false; }

                Type tCtrl = ReflectUtil.Find("Studio.ObjectCtrlInfo");
                if (tCtrl == null) { probeError = "找不到 Studio.ObjectCtrlInfo"; return false; }
                fTreeNode = tCtrl.GetField("treeNodeObject",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fTreeNode == null) { probeError = "找不到 ObjectCtrlInfo.treeNodeObject"; return false; }

                tTreeNode = ReflectUtil.Find("Studio.TreeNodeObject");
                if (tTreeNode == null) { probeError = "找不到 Studio.TreeNodeObject"; return false; }

                // 一定要挑「吃一個 bool」的那個多載。
                // 這個型別上同名的東西不只一個（SetVisibleChild(TreeNodeObject, bool)），
                // 只用名字找會拿到錯的 —— 之前 GetPress 就是這樣整組按鍵靜悄悄失效的。
                mSetVisible = tTreeNode.GetMethod("SetVisible",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
                pNodeVisible = tTreeNode.GetProperty("visible",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mSetVisible == null && pNodeVisible == null)
                { probeError = "TreeNodeObject 沒有 SetVisible(bool) 也沒有 visible"; return false; }

                // 以下是「有的話更好」的部分，找不到也不擋 —— 名稱比對會退化成
                // 只看工作區清單上的文字，編號比對照常。
                pTreeText = tTreeNode.GetProperty("textName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                tInfoDb = ReflectUtil.Find("Studio.Info");
                if (tInfoDb != null)
                {
                    pInfoInstance = tInfoDb.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    fDicItem = tInfoDb.GetField("dicItemLoadInfo",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                probed = true;
                return true;
            }
            catch (Exception e)
            {
                probeError = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------ 物品名稱

        // 物品資料庫：群組 → 分類 → 編號 → ItemLoadInfo，name 在基底 LoadCommonInfo 上。
        static IDictionary itemDb;
        static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>();

        static IDictionary ItemDb()
        {
            if (itemDb != null) return itemDb;
            try
            {
                if (fDicItem == null || tInfoDb == null) return null;
                object inst = null;
                try { if (pInfoInstance != null) inst = pInfoInstance.GetValue(null, null); }
                catch { }
                if (inst == null) inst = UnityEngine.Object.FindObjectOfType(tInfoDb);
                if (inst == null) return null;
                itemDb = fDicItem.GetValue(inst) as IDictionary;
            }
            catch { }
            return itemDb;
        }

        /// <summary>
        /// 這個群組／分類／編號在工作室物品清單裡叫什麼。查不到回 null。
        ///
        /// 查得到的才有意義：Sideloader 會把 mod 的編號重新配過，
        /// 但名稱是照著 csv 走的，所以名稱才是穩定的那一個。
        /// </summary>
        static string CatalogueName(int g, int c, int n)
        {
            string key = g + "/" + c + "/" + n;
            string hit;
            if (nameCache.TryGetValue(key, out hit)) return hit;

            string result = null;
            try
            {
                IDictionary d1 = ItemDb();
                IDictionary d2 = (d1 != null && d1.Contains(g)) ? d1[g] as IDictionary : null;
                IDictionary d3 = (d2 != null && d2.Contains(c)) ? d2[c] as IDictionary : null;
                object info = (d3 != null && d3.Contains(n)) ? d3[n] : null;
                if (info != null)
                {
                    if (fItemName == null)
                        fItemName = info.GetType().GetField("name",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fItemName != null) result = fItemName.GetValue(info) as string;
                }
            }
            catch { }

            nameCache[key] = result;
            return result;
        }

        /// <summary>工作區樹狀清單上顯示的那行字。使用者改過名也是抓這個。</summary>
        static string TreeText(object oci)
        {
            try
            {
                if (pTreeText == null) return null;
                object node = fTreeNode.GetValue(oci);
                if (node == null) return null;
                return pTreeText.GetValue(node, null) as string;
            }
            catch { return null; }
        }

        static string Lower(string s)
        {
            return s == null ? null : s.ToLowerInvariant();
        }

        static object StudioInstance()
        {
            try
            {
                if (pInstance != null)
                {
                    object v = pInstance.GetValue(null, null);
                    if (v != null) return v;
                }
            }
            catch { }

            // Instance 拿不到就自己在場上找。Studio 是 MonoBehaviour，一定只有一個。
            try
            {
                UnityEngine.Object o = UnityEngine.Object.FindObjectOfType(tStudio);
                if (o != null) return o;
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------ 主迴圈

        static readonly HashSet<int> handled = new HashSet<int>();

        /// <summary>被我們關掉的物件的 dicKey。只還原這些。</summary>
        static readonly List<int> muted = new List<int>();

        // ---------------------------------------------------------- (FX) 開關

        /// <summary>(FX) 資料夾要不要顯示。勾 = 顯示。</summary>
        public static bool FxFolderOn = true;

        // 上一次真的套用出去的值。null 代表還沒套過。
        // 有這個才不會每幀都去蓋 —— 只有開關被改動、或場景重載時才動手，
        // 中間你自己在工作區點勾勾我們不會跟你搶。
        static int fxApplied = -1;

        public static string FxReport = "";

        /// <summary>把 (FX) 開關的狀態套到場上所有名字含 (FX) 的節點。</summary>
        static void ApplyFxFolder()
        {
            // 自己節流：值沒變就什麼都不做，所以每幀叫也不貴。
            int want = FxFolderOn ? 1 : 0;
            if (fxApplied == want) return;
            if (!Probe()) return;

            IDictionary dic = Dic();
            if (dic == null) return;             // 還沒進工作室，下次再說

            int hit = 0, changed = 0;
            string needle = FxFolder.ToLowerInvariant();
            try
            {
                IDictionaryEnumerator it = dic.GetEnumerator();
                while (it.MoveNext())
                {
                    object oci = it.Value;
                    if (oci == null) continue;
                    string tree = Lower(TreeText(oci));
                    if (tree == null || tree.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                    hit++;
                    if (SetNodeVisible(oci, FxFolderOn)) changed++;
                }
            }
            catch (Exception e)
            {
                FxReport = "(FX) 套用出錯：" + e.Message;
                return;
            }

            fxApplied = want;
            FxReport = "(FX) " + (FxFolderOn ? "顯示" : "關閉")
                       + "　場上 " + hit + " 個，這次改了 " + changed + " 個";
            if (changed > 0) Debug.Log("[VrFxMute] " + FxReport);
        }

        /// <summary>場景換了要重套一次，不然新場景的 (FX) 不會跟著開關走。</summary>
        static void InvalidateFxFolder() { fxApplied = -1; }

        // ---------------------------------------------------------- 加入清單

        static PropertyInfo pTreeCtrl, pSelectOci;

        /// <summary>
        /// 上一次「加入清單」實際加了幾條。「移除最後加入的」就是砍掉這麼多條。
        ///
        /// 記數量而不是記名字：一次選好幾個物件是常態，
        /// 使用者心裡的「最後一個加入」是那一次動作，不是那一串裡的最後一條。
        /// </summary>
        static int lastAdded;

        /// <summary>還有沒有東西可以移除，給按鈕變灰用。</summary>
        public static bool CanRemoveLast { get { return lastAdded > 0; } }

        /// <summary>
        /// 把工作區目前選取的物件／資料夾的名字加進清單。
        ///
        /// 比「列進 log 再自己打」實用得多：名字是你看到的那一個，
        /// 也不會打錯字。名字裡的逗號會被換掉 —— 清單本身是逗號分隔的。
        /// </summary>
        public static string AddSelectedToList(string current, out string report)
        {
            report = "";
            if (!Probe()) { report = "無法讀取選取：" + probeError; return current; }

            try
            {
                if (pTreeCtrl == null)
                    pTreeCtrl = tStudio.GetProperty("treeNodeCtrl",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pTreeCtrl == null) { report = "找不到 Studio.treeNodeCtrl"; return current; }

                object studio = StudioInstance();
                if (studio == null) { report = "還沒進工作室"; return current; }

                object ctrl = pTreeCtrl.GetValue(studio, null);
                if (ctrl == null) { report = "treeNodeCtrl 是 null"; return current; }

                if (pSelectOci == null)
                    pSelectOci = ctrl.GetType().GetProperty("selectObjectCtrl",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pSelectOci == null) { report = "找不到 selectObjectCtrl"; return current; }

                var sel = pSelectOci.GetValue(ctrl, null) as IEnumerable;
                if (sel == null) { report = "選取清單不是可列舉的"; return current; }

                string list = (current ?? "").Trim();
                int added = 0, skipped = 0;
                foreach (object oci in sel)
                {
                    if (oci == null) continue;
                    string name = TreeText(oci);
                    if (string.IsNullOrEmpty(name)) continue;

                    // 清單是逗號分隔的，名字裡有逗號會把一條規則切成兩條
                    name = name.Replace(",", " ").Replace("，", " ").Trim();
                    if (name.Length == 0) continue;

                    if (list.ToLowerInvariant().IndexOf(name.ToLowerInvariant(),
                                                        StringComparison.Ordinal) >= 0)
                    { skipped++; continue; }

                    list = list.Length == 0 ? name : list + ", " + name;
                    added++;
                }

                if (added == 0 && skipped == 0)
                { report = "工作區沒有選取任何東西"; return current; }

                lastAdded = added;
                report = "加了 " + added + " 個" + (skipped > 0 ? "，" + skipped + " 個已經在清單裡" : "");
                return list;
            }
            catch (Exception e)
            {
                report = "加入清單失敗：" + e.GetType().Name + " " + e.Message;
                return current;
            }
        }

        /// <summary>
        /// 把上一次加進去的那幾條從清單尾端拿掉。
        /// 只認「上一次那個動作」加了幾條 —— 連按兩次不會一路往回刪，
        /// 那樣很容易把原本就在清單裡的規則誤砍掉。
        /// </summary>
        public static string RemoveLastAdded(string current, out string report)
        {
            if (lastAdded <= 0)
            {
                report = "沒有可以移除的（要先按過「把選取的加入清單」）";
                return current;
            }

            var parts = new List<string>();
            foreach (string raw in (current ?? "").Split(','))
            {
                string t = raw.Trim();
                if (t.Length > 0) parts.Add(t);
            }

            int take = Mathf.Min(lastAdded, parts.Count);
            var removed = new List<string>();
            for (int i = 0; i < take; i++)
            {
                removed.Add(parts[parts.Count - 1]);
                parts.RemoveAt(parts.Count - 1);
            }
            lastAdded = 0;

            report = "移除了 " + take + " 個：" + string.Join("、", removed.ToArray());
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>把我們關掉的物件勾回去。關功能、離開 VR 時自動叫。</summary>
        public static int Restore()
        {
            int back = 0;
            try
            {
                IDictionary dic = Dic();
                if (dic != null)
                {
                    for (int i = 0; i < muted.Count; i++)
                    {
                        int key = muted[i];
                        if (!dic.Contains(key)) continue;      // 物件已經被刪了
                        object oci = dic[key];
                        if (oci != null && SetNodeVisible(oci, true)) back++;
                    }
                }
            }
            catch { }

            muted.Clear();
            MutedThisScene = 0;
            // 還原之後要讓它們重新被考慮，否則再打開功能時會被當成「處理過了」
            handled.Clear();
            swept = false;
            if (back > 0) Debug.Log("[VrFxMute] 已把 " + back + " 個物件勾回去");
            return back;
        }
        static readonly List<int> scratch = new List<int>();
        static int lastCount = -1;
        static float settleAt;
        static float nextScan;
        static bool swept;

        /// <summary>
        /// 每幀叫。真正動作的頻率由內部節流控制。
        /// </summary>
        /// <param name="vrRunning">現在是不是 VR 模式</param>
        public static void Tick(bool vrRunning)
        {
            bool active = Enabled && (!OnlyInVr || vrRunning);

            // 功能關掉（或離開 VR）就把**我們自己關過的**勾回去。
            // 只還原我們動過的那些 —— 本來就沒勾的不要碰，不然一關這個功能
            // 就會把使用者自己關掉的東西通通打開，那比沒有還原還糟。
            // (FX) 開關跟「啟用」無關，兩種模式下都要跟著走
            ApplyFxFolder();

            if (!active)
            {
                if (muted.Count > 0) Restore();
                LastReport = !Enabled ? "已關閉" : "桌面模式，不動作";
                return;
            }

            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 0.25f;

            if (!Probe()) { LastReport = "無法運作：" + probeError; return; }

            IDictionary dic = Dic();
            if (dic == null) { LastReport = "還沒進工作室"; return; }

            int count = dic.Count;
            if (count != lastCount)
            {
                // 還在載／還在增減，等它停下來
                lastCount = count;
                swept = false;
                settleAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, SettleSeconds);
                Prune(dic);
                LastReport = "場景變動中（" + count + " 個物件）…";
                return;
            }
            if (swept) return;                      // 這批已經看完了，不必每 0.25 秒再翻一次
            if (Time.realtimeSinceStartup < settleAt) return;

            // 掃到一半出錯就不要標記完成 —— 下一輪還要再試一次，
            // 不然一次偶發例外會讓這個功能整場都不動，而且完全沒有徵兆。
            swept = Sweep(dic);
        }

        static IDictionary Dic()
        {
            try
            {
                object studio = StudioInstance();
                if (studio == null) return null;
                return fDic.GetValue(studio) as IDictionary;
            }
            catch { return null; }
        }

        /// <summary>
        /// 場景重載之後 dicKey 會重複使用。已經不在字典裡的 key 要從 handled 拿掉，
        /// 不然下一張場景的同號物件會被當成「處理過了」直接放過去。
        /// </summary>
        static void Prune(IDictionary dic)
        {
            if (handled.Count == 0) return;
            scratch.Clear();
            foreach (int k in handled)
                if (!dic.Contains(k)) scratch.Add(k);
            for (int i = 0; i < scratch.Count; i++) handled.Remove(scratch[i]);
            if (scratch.Count > 0)
            {
                MutedThisScene = 0; ItemsThisScene = 0;
                // 場景換了，舊的 dicKey 會被重新配給別的物件 ——
                // 留著的話「還原」會去勾一個完全不相干的東西。
                muted.Clear();
                InvalidateFxFolder();
            }
        }

        static bool Sweep(IDictionary dic)
        {
            int looked = 0, mutedNow = 0;
            try
            {
                Prune(dic);
                IDictionaryEnumerator it = dic.GetEnumerator();
                while (it.MoveNext())
                {
                    if (!(it.Key is int)) continue;
                    int key = (int)it.Key;
                    if (handled.Contains(key)) continue;
                    handled.Add(key);          // 不管有沒有關，這個物件都只看一次
                    looked++;

                    object oci = it.Value;
                    if (oci == null) continue;

                    string tree = Lower(TreeText(oci));

                    // 家具／特效：編號和名稱都能比對
                    if (tOciItem.IsInstanceOfType(oci))
                    {
                        object info = pItemInfo.GetValue(oci, null);
                        if (info != null)
                        {
                            ItemsThisScene++;
                            int g = (int)pGroup.GetValue(info, null);
                            int c = (int)pCategory.GetValue(info, null);
                            int n = (int)pNo.GetValue(info, null);
                            if (Matches(g, c, n, Lower(CatalogueName(g, c, n)), tree)
                                && SetNodeVisible(oci, false)) { mutedNow++; muted.Add(key); }
                            continue;
                        }
                    }

                    // 其他節點（資料夾、光源、角色…）只能用名稱。
                    // 資料夾很重要：合併場景之後那個 (FX) 資料夾一定會出現，
                    // 關掉資料夾，底下所有東西一起收 —— TreeNodeObject.SetVisible
                    // 本來就會往下傳給子節點，所以一條規則就夠，不必列出裡面每一個。
                    if (MatchesName(tree) && SetNodeVisible(oci, false))
                    { mutedNow++; muted.Add(key); }
                }
            }
            catch (Exception e)
            {
                LastReport = "掃描出錯：" + e.GetType().Name + " " + e.Message;
                return false;
            }

            if (mutedNow > 0)
            {
                MutedThisScene += mutedNow;
                Debug.Log("[VrFxMute] VR 模式：自動取消勾選 " + mutedNow + " 個物件"
                          + "（本場景累計 " + MutedThisScene + "）");
            }
            LastReport = "已看過 " + handled.Count + " 個物件（其中家具／特效 " + ItemsThisScene
                         + " 個），關掉 " + MutedThisScene + " 個"
                         + (looked > 0 ? "，這一輪新增 " + looked : "");
            return true;
        }

        static bool SetNodeVisible(object oci, bool visible)
        {
            try
            {
                object node = fTreeNode.GetValue(oci);
                if (node == null) return false;

                // 已經是想要的狀態就不要動。重點不是省效能，是不要在 log 裡
                // 灌一堆「關掉了」卻其實什麼都沒發生的假訊息。
                if (pNodeVisible != null)
                {
                    object cur = pNodeVisible.GetValue(node, null);
                    if (cur is bool && (bool)cur == visible) return false;
                }

                if (mSetVisible != null) mSetVisible.Invoke(node, new object[] { visible });
                else pNodeVisible.SetValue(node, visible, null);
                return true;
            }
            catch { return false; }
        }

        // 這裡原本有一個 DumpToLog()：把場上物件的執行期編號和名稱全部寫進 log，
        // 用來查「清單該填什麼」。現在面板上是「把選取的加入清單」——
        // 在工作區點一下就加進去，不必去 log 裡翻、也不會打錯字。
        // 那顆按鈕拿掉之後這個方法就沒有任何呼叫者了，留著只是死碼。

        /// <summary>
        /// 面板上的「現在就套用一次」。忘掉處理紀錄，下一次 Tick 整個重掃。
        /// </summary>
        public static void ForceNow()
        {
            handled.Clear();
            muted.Clear();
            InvalidateFxFolder();
            MutedThisScene = 0;
            ItemsThisScene = 0;
            lastCount = -1;
            settleAt = 0f;
            nextScan = 0f;
            swept = false;
            parsedFrom = null;      // 清單可能剛被改過
        }
    }
}
