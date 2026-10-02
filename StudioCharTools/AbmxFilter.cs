using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// KKABMX（Advanced Bone Sliders）的逐根篩選。
    ///
    /// 為什麼需要
    /// ----------
    /// 換人時 ABMX 原本是全有全無：「保留舊卡身材」整包帶回去，其餘模式一根都不留。
    /// 但這兩個極端都不理想 ——
    ///   整包帶回去：新卡連臉都不是自己的（鼻子嘴巴那兩根還會壞，所以本來就有例外處理）
    ///   一根都不留：手腳長度、脖子長度這些「KK 體型滑桿根本沒有」的比例全部跟著新卡跑，
    ///               場景裡辛苦擺的姿勢就錯位了
    /// 能逐根挑的話，就可以只留「會移動關節位置」的那些，臉和胸部讓新卡做自己。
    ///
    /// 為什麼不去動 blob
    /// -----------------
    /// `KKABMPlugin.ABMData` 是 LZ4 + MessagePack。解出來改再壓回去，會踩到
    /// 交接文件裡那兩個坑：float32 被升成 float64、int32 被壓成 fixint，
    /// 用 object 接資料的外掛就 InvalidCastException。
    /// 所以這裡走另一條路：**整包還原之後，再用 KKABMX 自己的 API 把不要的移除**。
    /// 完全不碰序列化，而且那個做法在這支插件裡已經被實卡驗證過
    /// （修鼻子嘴巴那段就是這樣做的，能撐過 KKABMX 的非同步重建）。
    ///
    /// 全部走反射：沒裝 KKABMX 的人插件照樣能載入，只是這些功能靜靜地不作用。
    /// </summary>
    public static class AbmxFilter
    {
        public static string LastReport = "";

        /// <summary>找出掛在這個角色身上的 KKABMX BoneController。</summary>
        static object FindController(Studio.OCIChar oci)
        {
            try
            {
                if (oci == null || oci.charInfo == null) return null;
                MonoBehaviour[] all = oci.charInfo.gameObject.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour c in all)
                    if (c != null && c.GetType().Name == "BoneController") return c;
            }
            catch (Exception e)
            {
                LastReport = "找 BoneController 失敗：" + e.Message;
            }
            return null;
        }

        static IEnumerable<object> Modifiers(object ctrl)
        {
            MethodInfo mi = ctrl.GetType().GetMethod("GetAllModifiers", Type.EmptyTypes);
            if (mi == null) return null;
            return Cast(mi.Invoke(ctrl, null) as System.Collections.IEnumerable);
        }

        static IEnumerable<object> Cast(System.Collections.IEnumerable src)
        {
            var list = new List<object>();
            if (src != null) foreach (object o in src) list.Add(o);
            return list;
        }

        static string NameOf(object modifier)
        {
            try
            {
                PropertyInfo p = modifier.GetType().GetProperty("BoneName");
                if (p == null) return null;
                return p.GetValue(modifier, null) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// 這個角色身上目前有哪幾根骨頭被 ABMX 改過。
        ///
        /// **一定要在 ChangeChara 之前叫** —— 換完人之後拿到的是新卡的名單，
        /// 拿它去過濾舊卡的資料就整個反了。跟姿勢要先存是同一個道理。
        /// </summary>
        public static List<string> CaptureNames(Studio.OCIChar oci)
        {
            var names = new List<string>();
            object ctrl = FindController(oci);
            if (ctrl == null) { LastReport = "沒有 KKABMX（或這個角色沒有 BoneController）"; return names; }
            try
            {
                IEnumerable<object> mods = Modifiers(ctrl);
                if (mods == null) { LastReport = "KKABMX 沒有 GetAllModifiers"; return names; }
                foreach (object m in mods)
                {
                    string n = NameOf(m);
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                LastReport = "讀到 " + names.Count + " 根被改過的骨頭";
            }
            catch (Exception e)
            {
                LastReport = "讀取 modifier 失敗：" + e.Message;
            }
            return names;
        }

        /// <summary>把名單排版成可以直接貼進設定的字串（也用於傾印到 log）。</summary>
        public static string Dump(List<string> names)
        {
            if (names == null || names.Count == 0) return "（沒有任何被 ABMX 改過的骨頭）";
            var sb = new StringBuilder();
            sb.Append("ABMX 骨頭一覽（共 ").Append(names.Count).Append(" 根）：\n");
            foreach (string n in names) sb.Append("    ").Append(n).Append('\n');
            sb.Append("  可直接貼進設定的格式：\n    ").Append(string.Join(",", names.ToArray()));
            return sb.ToString();
        }

        /// <summary>
        /// 這一組規則有沒有留下這根骨頭。
        ///
        /// 規則寫法：
        ///     cf_j_*        前綴比對
        ///     cf_s_head     完整名稱
        ///     -cf_j_ana     前面加減號 = 排除（優先於所有保留規則）
        ///
        /// **大小寫敏感，而且非如此不可。** KK 的骨架是 `cf_j_`（小寫 j），
        /// 臉部細節是 `cf_J_`（大寫 J）—— 只差一個字母，意義完全相反。
        /// 用 IgnoreCase 比對的話，一條 `cf_j_*` 會把整張臉（FaceBase、Eye、
        /// NoseBase、Mouth…）全部當成骨架留下來，換出來的人臉是舊卡的，
        /// 而且完全看不出是規則的問題。
        /// </summary>
        public static bool Keeps(string bone, ICollection<string> rules)
        {
            if (string.IsNullOrEmpty(bone) || rules == null) return false;
            bool keep = false;
            foreach (string raw in rules)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                bool exclude = raw[0] == '-';
                string r = exclude ? raw.Substring(1) : raw;
                if (r.Length == 0) continue;

                bool hit;
                if (r[r.Length - 1] == '*')
                    hit = bone.StartsWith(r.Substring(0, r.Length - 1), StringComparison.Ordinal);
                else
                    hit = string.Equals(bone, r, StringComparison.Ordinal);

                if (!hit) continue;
                if (exclude) return false;         // 排除一律優先，不再看其他規則
                keep = true;
            }
            return keep;
        }

        /// <summary>移除「規則沒留下」的 modifier。回傳移除幾根。</summary>
        public static int KeepOnly(Studio.OCIChar oci, ICollection<string> rules)
        {
            return Prune(oci, delegate(string n) { return !Keeps(n, rules); });
        }

        /// <summary>
        /// 「維持新卡身材」的預設規則。
        ///
        /// 留下來的三類：
        ///   1. 骨架關節 cf_j_*（決定關節位置，換掉姿勢就錯位）
        ///   2. **cf_n_height —— 整隻的高度縮放。** 這根一定要留。
        ///      體型滑桿的 Height 是另一套，補不了它：舊卡靠 cf_n_height 放大過的話，
        ///      砍掉人就縮回去，腳離地浮空。第一版漏了這根，實測就是浮空。
        ///   3. 腰部以下（waist / siri / thigh / leg / foot）與手臂手掌
        ///      （arm / forearm / elbo / shoulder / hand / wrist）的縮放骨。
        ///      這些是四肢的長度與粗細 —— KK 的體型滑桿根本沒有長度項，
        ///      只能靠 ABMX 留，不留就跟場景裡擺好的姿勢對不上。
        ///
        /// 刻意不留、讓新卡做自己的：
        ///   臉 cf_J_*（注意大寫 J）、胸與乳頭 cf_s_bust* / cf_d_bust* / *bnip*、
        ///   軀幹縮放 cf_s_spine*、裙子變形骨 cf_d_sk_*。
        ///   cf_j_ana 也排掉：它在 cf_j_ 底下，但跟比例無關。
        ///
        /// 在真實的 94 根名單上驗過：留 50、移 44，臉與胸一根都沒漏留。
        /// </summary>
        public const string DefaultKeepRules =
            "cf_j_*,-cf_j_ana,cf_n_height,cf_s_head,cf_s_neck,"
            + "cf_s_waist*,cf_s_siri*,cf_d_siri*,cf_s_thigh*,cf_s_leg*,cf_s_foot*,"
            + "cf_s_arm*,cf_s_forearm*,cf_s_elbo*,cf_s_shoulder*,cf_s_hand*,cf_s_wrist*";

        // -----------------------------------------------------------------
        // 維持新卡身材：舊卡 + 新卡合併
        // -----------------------------------------------------------------

        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        static string LocOf(object modifier)
        {
            try
            {
                PropertyInfo p = modifier.GetType().GetProperty("BoneLocation");
                object v = p != null ? p.GetValue(modifier, null) : null;
                return v != null ? v.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 「維持新卡身材」真正要的那一包 ABMX：
        ///   舊卡裡**規則有留**的骨頭（關節長度、身高…，姿勢才對得上）
        ///   ＋ 新卡裡**規則沒留**的骨頭（臉 cf_J_*、胸…，新卡自己調過的樣子）。
        ///
        /// 為什麼要這樣：原本的做法是把舊卡整包寫回去、再把規則外的移掉 ——
        /// 移掉之後剩下的是「沒有 modifier」，不是「新卡的 modifier」。
        /// 新卡作者用 ABMX 修過的臉（眼距、下巴、嘴型…）就這樣全部消失，
        /// 所以只有「場景原角色有 ABMX」的那些地圖，換出來的臉特別怪。
        ///
        /// 這裡用 KKABMX 自己的 ReadModifiers / SaveModifiers 讀寫，
        /// 格式由它負責，不會踩到自己解 MessagePack 的型別坑。
        /// ok = false 代表 KKABMX 的 API 找不到或出錯，呼叫端要退回舊做法。
        /// ok = true 但回傳 null 代表合併完是空的（一根都沒有）。
        /// </summary>
        public static ExtensibleSaveFormat.PluginData Merge(ExtensibleSaveFormat.PluginData oldData,
            ExtensibleSaveFormat.PluginData newData, ICollection<string> rules, out bool ok)
        {
            ok = false;
            try
            {
                Type ctrlT = FindType("KKABMX.Core.BoneController");
                Type modT = FindType("KKABMX.Core.BoneModifier");
                if (ctrlT == null || modT == null) { LastReport = "沒有 KKABMX，不合併"; return null; }

                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo read = ctrlT.GetMethod("ReadModifiers", SF, null,
                    new[] { typeof(ExtensibleSaveFormat.PluginData) }, null);
                MethodInfo save = null;
                foreach (MethodInfo m in ctrlT.GetMethods(SF))
                {
                    if (m.Name != "SaveModifiers") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length >= 1 && ps.Length <= 2) { save = m; break; }
                }
                if (read == null || save == null) { LastReport = "這版 KKABMX 沒有 ReadModifiers / SaveModifiers，不合併"; return null; }

                var merged = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modT));
                var seen = new HashSet<string>();
                var fromOld = new List<string>();
                var fromNew = new List<string>();

                foreach (object m in ReadAll(read, oldData))
                {
                    string n = NameOf(m);
                    if (string.IsNullOrEmpty(n) || !Keeps(n, rules)) continue;
                    if (seen.Add(n + "|" + LocOf(m))) { merged.Add(m); fromOld.Add(n); }
                }
                foreach (object m in ReadAll(read, newData))
                {
                    string n = NameOf(m);
                    if (string.IsNullOrEmpty(n) || Keeps(n, rules)) continue;
                    if (seen.Add(n + "|" + LocOf(m))) { merged.Add(m); fromNew.Add(n); }
                }

                ok = true;
                LastReport = "舊卡留 " + fromOld.Count + " 根、新卡留 " + fromNew.Count + " 根"
                             + (fromNew.Count > 0 ? "（新卡：" + string.Join(", ", fromNew.ToArray()) + "）" : "");
                if (merged.Count == 0) return null;

                ParameterInfo[] sp = save.GetParameters();
                object[] args = sp.Length == 2 ? new object[] { merged, false } : new object[] { merged };
                return save.Invoke(null, args) as ExtensibleSaveFormat.PluginData;
            }
            catch (Exception e)
            {
                ok = false;
                LastReport = "合併失敗：" + (e.InnerException ?? e).Message;
                return null;
            }
        }

        static List<object> ReadAll(MethodInfo read, ExtensibleSaveFormat.PluginData data)
        {
            var list = new List<object>();
            if (data == null) return list;
            var src = read.Invoke(null, new object[] { data }) as System.Collections.IEnumerable;
            if (src != null) foreach (object o in src) if (o != null) list.Add(o);
            return list;
        }

        /// <summary>移除「在 drop 裡」的 modifier（原本修鼻子嘴巴那段的行為）。</summary>
        public static int DropThese(Studio.OCIChar oci, ICollection<string> drop)
        {
            return Prune(oci, delegate(string n)
            {
                foreach (string d in drop)
                    if (string.Equals(d, n, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            });
        }

        static int Prune(Studio.OCIChar oci, Predicate<string> shouldRemove)
        {
            object ctrl = FindController(oci);
            if (ctrl == null) { LastReport = "沒有 KKABMX，略過篩選"; return 0; }
            int n = 0;
            var removed = new List<string>();
            try
            {
                MethodInfo remove = ctrl.GetType().GetMethod("RemoveModifier");
                if (remove == null) { LastReport = "KKABMX 沒有 RemoveModifier"; return 0; }

                // 先整份收集再移除 —— 邊列舉邊改集合會丟 InvalidOperationException
                var doomed = new List<object>();
                IEnumerable<object> mods = Modifiers(ctrl);
                if (mods == null) { LastReport = "KKABMX 沒有 GetAllModifiers"; return 0; }
                foreach (object m in mods)
                {
                    string name = NameOf(m);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (shouldRemove(name)) { doomed.Add(m); removed.Add(name); }
                }
                foreach (object m in doomed)
                {
                    try { remove.Invoke(ctrl, new[] { m }); n++; }
                    catch (Exception e) { Debug.LogWarning("[AbmxFilter] RemoveModifier 失敗: " + e.Message); }
                }
                LastReport = "移除 " + n + " 根"
                             + (removed.Count > 0 ? "：" + string.Join(", ", removed.ToArray()) : "");
            }
            catch (Exception e)
            {
                LastReport = "篩選失敗：" + e.Message;
            }
            return n;
        }

        /// <summary>把設定字串拆成名單（逗號 / 分號 / 換行都可以當分隔）。</summary>
        public static List<string> Parse(string csv)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(csv)) return outList;
            foreach (string part in csv.Split(',', ';', '\n', '\r'))
            {
                string s = part.Trim();
                if (s.Length > 0 && !outList.Contains(s)) outList.Add(s);
            }
            return outList;
        }
    }
}
