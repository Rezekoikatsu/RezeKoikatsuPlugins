using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Studio;

namespace StudioCharTools
{
    /// <summary>
    /// 飾品欄管理。
    ///
    /// 注意：基礎的 nowCoordinate.accessory.parts 只有 20 格，
    /// 超過的部分由 MoreAccessories 另外保管，各版本存放方式不同。
    /// 所以這裡一律「以實際偵測到的長度為準」，並提供 Dump() 把真實結構印出來。
    /// </summary>
    public static class AccessoryTools
    {
        public static string LastMessage = "";
        public static string LastReport = "";

        public const int EmptyType = 120;      // KK 用 120 代表這一格沒有飾品

        public class AccInfo
        {
            public int slot;              // 0 起算
            public string name = "";      // 飾品名稱（來自 infoAccessory）
            public string parentKey = ""; // 掛點骨骼
            public bool visible = true;
            public bool IsEmpty;
            public int type = EmptyType;
            public int id;
            /// <summary>KK 的「主要／次要」分類（PartsInfo.hideCategory）：0 = 主要、1 = 次要。</summary>
            public int hideCategory;
            public string Category { get { return ParentToCategory(parentKey); } }
        }

        /// <summary>
        /// 基礎的 nowCoordinate.accessory.parts。有 MoreAccessories 時可能取不到，
        /// 所以只當作補充資料來源（parentKey 等），主要清單不依賴它。
        /// </summary>
        public static Array GetPartsArray(ChaControl cha)
        {
            try
            {
                if (cha == null) return null;
                var coord = cha.nowCoordinate;
                object acc = GetMemberValue(coord, "accessory");
                if (acc == null) return null;
                return GetMemberValue(acc, "parts") as Array;
            }
            catch { return null; }
        }


        /// <summary>欄位或屬性都試，找不到回 null。</summary>
        static object GetMemberValue(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                try
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(o);

                    var pr = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (pr != null && pr.CanRead) return pr.GetValue(o, null);
                }
                catch { }
            }
            return null;
        }



        // ---------------------------------------------------------------
        // 讀取：走 infoAccessory / objAccessory
        // nowCoordinate.accessory.parts 在有 MoreAccessories 時取不到，
        // 而這兩個陣列會被一起放大，所以以它們為準。
        // ---------------------------------------------------------------
        public static List<AccInfo> List(ChaControl cha, bool includeEmpty)
        {
            var result = new List<AccInfo>();
            if (cha == null) return result;

            ListInfoBase[] infos = null;
            GameObject[] objs = null;
            try { infos = cha.infoAccessory; } catch { }
            try { objs = cha.objAccessory; } catch { }

            int n = Math.Max(infos == null ? 0 : infos.Length, objs == null ? 0 : objs.Length);
            if (n == 0) { LastMessage = "偵測不到任何飾品欄位"; return result; }

            for (int i = 0; i < n; i++)
            {
                object info = (infos != null && i < infos.Length) ? infos[i] : null;
                GameObject obj = (objs != null && i < objs.Length) ? objs[i] : null;

                var a = new AccInfo { slot = i };
                a.IsEmpty = (info == null && obj == null);

                if (info != null) a.name = GetInfoName(info);

                var parts = GetPartsArray(cha);
                if (parts != null && i < parts.Length)
                {
                    var pi = parts.GetValue(i);
                    a.type = GetInt(pi, "type", EmptyType);
                    a.id = GetInt(pi, "id", 0);
                    a.hideCategory = GetInt(pi, "hideCategory", 0);
                }
                if (obj != null)
                {
                    if (obj.transform.parent != null) a.parentKey = obj.transform.parent.name;
                    a.visible = obj.activeSelf;
                }
                if (a.parentKey.Length == 0) a.parentKey = GetParentFromParts(cha, i);

                if (a.IsEmpty && !includeEmpty) continue;
                result.Add(a);
            }

            LastMessage = "偵測到 " + n + " 個欄位，其中 " + result.Count + " 個列出";
            return result;
        }

        static string GetInfoName(object info)
        {
            foreach (var n in new[] { "Name", "name" })
            {
                try
                {
                    var pr = info.GetType().GetProperty(n,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pr != null)
                    {
                        var v = pr.GetValue(info, null) as string;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                    var f = info.GetType().GetField(n,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                    {
                        var v = f.GetValue(info) as string;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
            }
            return "";
        }

        static string GetParentFromParts(ChaControl cha, int slot)
        {
            try
            {
                var arr = GetPartsArray(cha);
                if (arr == null || slot >= arr.Length) return "";
                return GetStr(arr.GetValue(slot), "parentKey");
            }
            catch { return ""; }
        }

        static bool GetVisible(ChaControl cha, int slot)
        {
            try
            {
                var objs = cha.objAccessory;
                if (objs != null && slot < objs.Length && objs[slot] != null)
                    return objs[slot].activeSelf;
            }
            catch { }
            return true;
        }

        // ---------------------------------------------------------------
        // 操作
        // ---------------------------------------------------------------
        public static bool SetVisible(ChaControl cha, int slot, bool on)
        {
            try
            {
                var m = cha.GetType().GetMethod("SetAccessoryState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int), typeof(bool) }, null);
                if (m != null) { m.Invoke(cha, new object[] { slot, on }); return true; }

                var objs = cha.objAccessory;
                if (objs != null && slot < objs.Length && objs[slot] != null)
                {
                    objs[slot].SetActive(on);
                    return true;
                }
            }
            catch (Exception e) { LastReport = e.GetBaseException().ToString(); }
            return false;
        }

        /// <summary>把某一格清空（type 設回 120 並重新套用）。</summary>
        /// <summary>
        /// 清空某一格飾品。
        ///
        /// 關鍵在於「有兩份 parts 陣列」：
        ///   nowCoordinate.accessory.parts      MoreAccessories 換過的擴充版（21 格）
        ///   chaFile.coordinate[n].accessory.parts   原始那份，存卡寫出去的是這個
        /// 之前只清前者，所以驗證一直顯示成功，存檔卻完全沒受影響。
        /// 現在兩邊都清，而且兩邊都驗證。
        /// </summary>
        public static bool Remove(ChaControl cha, int slot)
        {
            var sb = new StringBuilder();
            try
            {
                int coordType = 0;
                try { coordType = Convert.ToInt32(GetMemberValue(cha.fileStatus, "coordinateType")); }
                catch { }

                Func<Array> fileParts = () =>
                {
                    try
                    {
                        var coords = cha.chaFile.coordinate;
                        if (coords == null || coordType < 0 || coordType >= coords.Length) return null;
                        object acc = GetMemberValue(coords[coordType], "accessory");
                        return acc == null ? null : GetMemberValue(acc, "parts") as Array;
                    }
                    catch { return null; }
                };

                Func<Array, string> peek = (arr) =>
                {
                    if (arr == null || slot >= arr.Length) return "?";
                    return GetInt(arr.GetValue(slot), "type", -1).ToString();
                };

                sb.AppendLine("移除第 " + (slot + 1) + " 格（服裝槽 " + coordType + "）");
                sb.AppendLine("  起始  now=" + peek(GetPartsArray(cha)) + "  file=" + peek(fileParts()));

                // 1) 先叫遊戲卸下這格飾品
                var m = cha.GetType().GetMethod("ChangeAccessory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int), typeof(int), typeof(int), typeof(string), typeof(bool) }, null);
                if (m != null)
                {
                    m.Invoke(cha, new object[] { slot, EmptyType, 0, "", true });
                    sb.AppendLine("  卸下後 now=" + peek(GetPartsArray(cha)) + "  file=" + peek(fileParts()));
                }
                else
                {
                    SetVisible(cha, slot, false);
                    sb.AppendLine("  找不到 ChangeAccessory，只隱藏");
                }

                // 2) 兩份陣列都清
                int cleared = 0;
                foreach (var arr in new[] { GetPartsArray(cha), fileParts() })
                {
                    if (arr == null || slot >= arr.Length) continue;
                    var pi = arr.GetValue(slot);
                    if (pi == null) continue;
                    SetInt(pi, "type", EmptyType);
                    SetInt(pi, "id", 0);
                    cleared++;
                }
                sb.AppendLine("  已清 " + cleared + " 份陣列");
                sb.AppendLine("  清除後 now=" + peek(GetPartsArray(cha)) + "  file=" + peek(fileParts()));

                // 3) 通知 MoreAccessories，但不用會反向重建的 TrimAndSync
                SyncAfterEdit(cha, false);
                sb.AppendLine("  同步後 now=" + peek(GetPartsArray(cha)) + "  file=" + peek(fileParts()));

                // 存卡看的是 file 那一份，所以以它為準
                string finalFile = peek(fileParts());
                bool ok = finalFile == EmptyType.ToString();

                LastMessage = ok
                    ? "已移除第 " + (slot + 1) + " 格"
                    : "第 " + (slot + 1) + " 格清除失敗，存檔用的陣列仍是 " + finalFile;
                LastReport = sb.ToString();
                Debug.Log("[AccessoryTools] " + LastMessage + "\n" + sb);
                return ok;
            }
            catch (Exception e)
            {
                LastMessage = "移除失敗: " + e.GetBaseException().Message;
                LastReport = sb + "\n" + e.GetBaseException();
                Debug.LogWarning("[AccessoryTools] " + LastReport);
                return false;
            }
        }

        /// <summary>目前穿的是第幾套換裝。</summary>
        public static int NowCoordinateType(ChaControl cha)
        {
            try { return Convert.ToInt32(GetMemberValue(cha.fileStatus, "coordinateType")); }
            catch { return 0; }
        }

        /// <summary>這張卡有幾套換裝。</summary>
        public static int CoordinateCount(ChaControl cha)
        {
            try
            {
                var coords = cha.chaFile.coordinate;
                return coords == null ? 0 : coords.Length;
            }
            catch { return 0; }
        }

        /// <summary>取得指定換裝的 accessory.parts（存檔用的那一份）。</summary>
        public static Array CoordinateParts(ChaControl cha, int coordType)
        {
            try
            {
                var coords = cha.chaFile.coordinate;
                if (coords == null || coordType < 0 || coordType >= coords.Length) return null;
                object acc = GetMemberValue(coords[coordType], "accessory");
                return acc == null ? null : GetMemberValue(acc, "parts") as Array;
            }
            catch { return null; }
        }

        /// <summary>
        /// 清掉「非目前這一套」換裝的某一格。
        ///
        /// 不能用 ChangeAccessory——那個只作用在目前穿的那套。這裡直接改存檔
        /// 用的陣列。回傳 false 代表那一套的陣列沒有這麼長（通常是
        /// MoreAccessories 的擴充欄位在非當前換裝時拿不到），會寫進 LastReport。
        /// </summary>
        public static bool RemoveInCoordinate(ChaControl cha, int coordType, int slot)
        {
            try
            {
                Array arr = CoordinateParts(cha, coordType);
                if (arr == null)
                {
                    LastReport += "\n  服裝槽 " + coordType + " 取不到 parts 陣列";
                    return false;
                }
                if (slot >= arr.Length)
                {
                    LastReport += "\n  服裝槽 " + coordType + " 只有 " + arr.Length
                                  + " 格，碰不到第 " + (slot + 1) + " 格";
                    return false;
                }
                object pi = arr.GetValue(slot);
                if (pi == null) return false;
                SetInt(pi, "type", EmptyType);
                SetInt(pi, "id", 0);
                LastReport += "\n  服裝槽 " + coordType + " 第 " + (slot + 1) + " 格已清除";
                return true;
            }
            catch (Exception e)
            {
                LastReport += "\n  服裝槽 " + coordType + " 清除失敗: " + e.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>
        /// 把清除結果推給 MoreAccessories。
        /// 注意 NowCoordinateTrimAndSync 有可能反過來從它的內部資料重建 parts，
        /// 所以預設不呼叫，只在明確需要時才用。
        /// </summary>
        public static void SyncAfterEdit(ChaControl cha, bool includeTrim)
        {
            try
            {
                Type ma = FindType("MoreAccessoriesKOI.MoreAccessories") ?? FindType("MoreAccessories");
                if (ma == null) return;

                object inst = null;
                foreach (var fn in new[] { "_self", "self", "Instance", "instance" })
                {
                    var f = ma.GetField(fn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) inst = f.GetValue(null);
                    if (inst != null) break;
                }
                if (inst == null) inst = UnityEngine.Object.FindObjectOfType(ma);

                var names = includeTrim
                    ? new[] { "ArraySync", "NowCoordinateTrimAndSync" }
                    : new[] { "ArraySync" };

                foreach (var name in names)
                {
                    var m = ma.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static, null,
                        new[] { typeof(ChaControl) }, null);
                    if (m == null) continue;
                    try { m.Invoke(m.IsStatic ? null : inst, new object[] { cha }); }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[AccessoryTools] " + name + " 失敗: "
                                         + e.GetBaseException().Message);
                    }
                }
            }
            catch { }
        }


        // ---------------------------------------------------------------
        // 分類：由 parentKey（掛點骨骼）推出部位
        // ---------------------------------------------------------------
        static readonly Dictionary<string, string> ParentNames = new Dictionary<string, string>
        {
            { "a_n_head",       "頭部" },
            { "a_n_headtop",    "頭頂" },
            { "a_n_headside",   "頭側" },
            { "a_n_hair_pony",  "馬尾" },
            { "a_n_hair_twin_L","雙馬尾 L" },
            { "a_n_hair_twin_R","雙馬尾 R" },
            { "a_n_hair_pin",   "髮夾" },
            { "a_n_hair_pin_R", "髮夾 R" },
            { "a_n_earrings_L", "耳環 L" },
            { "a_n_earrings_R", "耳環 R" },
            { "a_n_nose",       "鼻" },
            { "a_n_mouth",      "口" },
            { "a_n_neck",       "頸" },
            { "a_n_bust",       "胸" },
            { "a_n_bust_f",     "胸前" },
            { "a_n_waist",      "腰" },
            { "a_n_waist_f",    "腰前" },
            { "a_n_waist_b",    "腰後" },
            { "a_n_waist_L",    "腰 L" },
            { "a_n_waist_R",    "腰 R" },
            { "a_n_back",       "背" },
            { "a_n_back_L",     "背 L" },
            { "a_n_back_R",     "背 R" },
            { "a_n_shoulder_L", "肩 L" },
            { "a_n_shoulder_R", "肩 R" },
            { "a_n_arm_L",      "手臂 L" },
            { "a_n_arm_R",      "手臂 R" },
            { "a_n_wrist_L",    "手腕 L" },
            { "a_n_wrist_R",    "手腕 R" },
            { "a_n_hand_L",     "手 L" },
            { "a_n_hand_R",     "手 R" },
            { "a_n_leg_L",      "腿 L" },
            { "a_n_leg_R",      "腿 R" },
            { "a_n_ankle_L",    "腳踝 L" },
            { "a_n_ankle_R",    "腳踝 R" },
            { "a_n_heel_L",     "鞋 L" },
            { "a_n_heel_R",     "鞋 R" },
        };

        public static string ParentToCategory(string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return Lang.T("未指定");
            string v;
            if (ParentNames.TryGetValue(parentKey, out v)) return Lang.T(v);

            // 沒對到的用前綴粗分，避免全部擠在「其他」
            if (parentKey.Contains("head") || parentKey.Contains("hair")) return Lang.T("頭部（其他）");
            if (parentKey.Contains("hand") || parentKey.Contains("arm")) return Lang.T("手部（其他）");
            if (parentKey.Contains("leg") || parentKey.Contains("ankle")) return Lang.T("腿部（其他）");
            return parentKey;
        }


        /// <summary>
        /// 純資料物件的深複製。逐欄位複製並展開陣列，
        /// Unity 型別與字串維持參考。
        /// </summary>
        static object DeepClone(object src, int depth)
        {
            if (src == null || depth > 8) return src;

            var t = src.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return src;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return src;
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) return src;

            var arr = src as Array;
            if (arr != null)
            {
                if (arr.Rank > 1) return arr.Clone();

                var et = t.GetElementType();
                var copy = Array.CreateInstance(et, arr.Length);
                for (int i = 0; i < arr.Length; i++)
                    copy.SetValue(DeepClone(arr.GetValue(i), depth + 1), i);
                return copy;
            }

            object dst;
            try { dst = Activator.CreateInstance(t); }
            catch { return src; }

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance))
            {
                try { f.SetValue(dst, DeepClone(f.GetValue(src), depth + 1)); } catch { }
            }
            return dst;
        }












        static void InvokeIfExists(ChaControl cha, string name, int arg)
        {
            try
            {
                var m = cha.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                if (m != null) m.Invoke(cha, new object[] { arg });
            }
            catch { }
        }

        static void Reload(ChaControl cha)
        {
            foreach (var name in new[] { "ChangeAccessory", "ChangeCoordinateTypeAndReload" })
            {
                var m = cha.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (m == null) continue;
                try { m.Invoke(cha, new object[] { true }); return; } catch { }
            }

            var m2 = cha.GetType().GetMethod("Reload", BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m2 != null) { try { m2.Invoke(cha, null); } catch { } }
        }

        // ---------------------------------------------------------------
        // 診斷
        // ---------------------------------------------------------------
        static string WhyNoParts(ChaControl cha)
        {
            try
            {
                var coord = cha.nowCoordinate;
                if (coord == null) return "nowCoordinate 是 null";
                var accField = coord.GetType().GetField("accessory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (accField == null) return "ChaFileCoordinate 沒有 accessory 欄位";
                var acc = accField.GetValue(coord);
                if (acc == null) return "accessory 是 null";
                var pf = acc.GetType().GetField("parts",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pf == null) return acc.GetType().Name + " 沒有 parts 欄位";
                return pf.GetValue(acc) == null ? "parts 是 null" : "其實取得到";
            }
            catch (Exception e) { return "例外: " + e.GetBaseException().Message; }
        }

        /// <summary>
        /// 針對某一格做移除前後的比對。
        /// 移除後重讀又復活，代表還有別的地方保留著這一格的資料，
        /// 這裡把所有可能的來源一次列出來。
        /// </summary>
        public static void DumpSlot(ChaControl cha, int slot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== 第 " + (slot + 1) + " 格 資料來源 =====");

            try
            {
                var parts = GetPartsArray(cha);
                sb.AppendLine("nowCoordinate.parts 長度 " + (parts == null ? -1 : parts.Length));
                if (parts != null && slot < parts.Length)
                {
                    var pi = parts.GetValue(slot);
                    sb.AppendLine("  type=" + GetInt(pi, "type", -1) + " id=" + GetInt(pi, "id", -1)
                                  + " parentKey=" + GetStr(pi, "parentKey"));
                }

                // 每個服裝槽都看，確認不是只清了目前這一槽
                var coords = cha.chaFile.coordinate;
                for (int c = 0; c < coords.Length; c++)
                {
                    object acc = GetMemberValue(coords[c], "accessory");
                    var pa = acc == null ? null : GetMemberValue(acc, "parts") as Array;
                    if (pa == null || slot >= pa.Length) continue;
                    sb.AppendLine("  coordinate[" + c + "].parts[" + slot + "] type="
                                  + GetInt(pa.GetValue(slot), "type", -1));
                }

                sb.AppendLine("objAccessory[" + slot + "] = "
                              + (cha.objAccessory != null && slot < cha.objAccessory.Length
                                 && cha.objAccessory[slot] != null ? "有物件" : "null"));
                sb.AppendLine("infoAccessory[" + slot + "] = "
                              + (cha.infoAccessory != null && slot < cha.infoAccessory.Length
                                 && cha.infoAccessory[slot] != null ? "有資料" : "null"));

                // MoreAccessories 自己保管的那一份
                Type ma = FindType("MoreAccessoriesKOI.MoreAccessories") ?? FindType("MoreAccessories");
                sb.AppendLine("--- MoreAccessories ---");
                if (ma == null) sb.AppendLine("  找不到型別");
                else
                {
                    object inst = null;
                    foreach (var fn in new[] { "_self", "self", "Instance", "instance" })
                    {
                        var f = ma.GetField(fn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (f != null) inst = f.GetValue(null);
                        if (inst != null) break;
                    }
                    if (inst == null) inst = UnityEngine.Object.FindObjectOfType(ma);

                    foreach (var f in ma.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Instance | BindingFlags.Static))
                    {
                        object v = null;
                        try { v = f.GetValue(f.IsStatic ? null : inst); } catch { }

                        string extra = "";
                        var arr = v as Array;
                        var dict = v as System.Collections.IDictionary;
                        var list = v as System.Collections.ICollection;
                        if (arr != null) extra = " 長度 " + arr.Length;
                        else if (dict != null) extra = " 字典 " + dict.Count + " 筆";
                        else if (list != null) extra = " 集合 " + list.Count + " 筆";
                        else if (v != null && v.GetType().IsClass && !(v is string)) extra = " (物件)";

                        sb.AppendLine("  [欄位] " + f.FieldType.Name + " " + f.Name + extra);
                    }
                }

                // MoreAccessories 在工作室是把資料放在 StudioMode 這個物件裡，
                // 存卡是由它的 OnActualCharaSave 從那裡寫進卡片，
                // 所以要追進去看這一格在不在。
                if (ma != null)
                {
                    sb.AppendLine("--- MoreAccessories.StudioMode ---");
                    object studioMode = null;
                    foreach (var fn in new[] { "StudioMode", "<StudioMode>k__BackingField", "_studioMode" })
                    {
                        var f = ma.GetField(fn, BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static);
                        if (f == null) continue;
                        try { studioMode = f.GetValue(f.IsStatic ? null : UnityEngine.Object.FindObjectOfType(ma)); }
                        catch { }
                        if (studioMode != null) break;
                    }

                    if (studioMode == null) sb.AppendLine("  取不到 StudioMode");
                    else
                    {
                        sb.AppendLine("  型別 " + studioMode.GetType().FullName);
                        foreach (var f in studioMode.GetType().GetFields(
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            object v = null;
                            try { v = f.GetValue(studioMode); } catch { }

                            var dict = v as System.Collections.IDictionary;
                            var col = v as System.Collections.ICollection;
                            string extra = dict != null ? " 字典 " + dict.Count + " 筆"
                                         : (col != null ? " 集合 " + col.Count + " 筆" : "");
                            sb.AppendLine("    [欄位] " + f.FieldType.Name + " " + f.Name + extra);

                            // 字典裡如果有這個角色，把它的資料攤開
                            if (dict == null) continue;
                            foreach (System.Collections.DictionaryEntry kv in dict)
                            {
                                bool mine = ReferenceEquals(kv.Key, cha)
                                            || ReferenceEquals(kv.Key, cha.chaFile);
                                if (!mine) continue;

                                sb.AppendLine("      ↳ 命中這個角色，值型別 "
                                              + (kv.Value == null ? "null" : kv.Value.GetType().Name));
                                if (kv.Value == null) continue;

                                foreach (var vf in kv.Value.GetType().GetFields(
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    object vv = null;
                                    try { vv = vf.GetValue(kv.Value); } catch { }
                                    var vcol = vv as System.Collections.ICollection;
                                    sb.AppendLine("        " + vf.FieldType.Name + " " + vf.Name
                                                  + (vcol != null ? " 共 " + vcol.Count + " 筆" : ""));
                                }
                            }
                        }
                    }
                }

                // ExtensibleSaveFormat 存在 coordinate 上的資料
                sb.AppendLine("--- coordinate 的 extended data ---");
                var es = FindType("ExtensibleSaveFormat.ExtendedSave");
                if (es == null) sb.AppendLine("  找不到 ExtendedSave");
                else
                {
                    var get = es.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "GetAllExtendedData"
                                             && m.GetParameters().Length == 1
                                             && m.GetParameters()[0].ParameterType
                                                .IsInstanceOfType(cha.nowCoordinate));
                    if (get == null) sb.AppendLine("  找不到 GetAllExtendedData");
                    else
                    {
                        var raw = get.Invoke(null, new object[] { cha.nowCoordinate })
                                  as System.Collections.IDictionary;
                        if (raw == null || raw.Count == 0) sb.AppendLine("  沒有資料");
                        else foreach (System.Collections.DictionaryEntry kv in raw)
                            sb.AppendLine("  " + kv.Key);
                    }

                    sb.AppendLine("--- chaFile 的 extended data（存卡就是存這裡）---");
                    var getFile = es.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "GetAllExtendedData"
                                             && m.GetParameters().Length == 1
                                             && m.GetParameters()[0].ParameterType
                                                .IsInstanceOfType(cha.chaFile));
                    if (getFile == null) sb.AppendLine("  找不到對應多載");
                    else
                    {
                        var raw2 = getFile.Invoke(null, new object[] { cha.chaFile })
                                   as System.Collections.IDictionary;
                        if (raw2 == null || raw2.Count == 0) sb.AppendLine("  沒有資料");
                        else foreach (System.Collections.DictionaryEntry kv in raw2)
                            sb.AppendLine("  " + kv.Key);
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("診斷失敗: " + e.GetBaseException()); }

            sb.AppendLine("===== 結束 =====");
            Debug.Log(sb.ToString());
            LastMessage = "第 " + (slot + 1) + " 格診斷已輸出到主控台";
        }

        /// <summary>
        /// 直接讀取存出來的人物卡檔案，看裡面那一格到底是什麼。
        ///
        /// 這一步能把問題切成兩半：
        ///   檔案裡是 120 → 存檔正常，是「讀取時有東西把它還原回去」
        ///   檔案裡是 123 → 存檔就沒寫進去，是「存檔時有東西把它蓋回去」
        /// 兩者的修法完全不同，先分清楚才不會繼續亂試。
        /// </summary>
        public static void InspectSavedCard(string cardPath, int slot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== 檢查存出的卡 =====");
            sb.AppendLine("檔案: " + cardPath);

            try
            {
                if (string.IsNullOrEmpty(cardPath) || !File.Exists(cardPath))
                {
                    sb.AppendLine("檔案不存在");
                }
                else
                {
                    var chaFileType = FindType("ChaFileControl");
                    if (chaFileType == null) sb.AppendLine("找不到 ChaFileControl");
                    else
                    {
                        object cf = Activator.CreateInstance(chaFileType);

                        MethodInfo load = null;
                        foreach (var m in chaFileType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                                 | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                        {
                            if (m.Name != "LoadCharaFile" && m.Name != "LoadFile") continue;
                            var ps = m.GetParameters();
                            if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                            if (load == null || ps.Length < load.GetParameters().Length) load = m;
                        }

                        if (load == null) sb.AppendLine("找不到 LoadCharaFile");
                        else
                        {
                            var lp = load.GetParameters();
                            var args = new object[lp.Length];
                            args[0] = cardPath;
                            for (int i = 1; i < lp.Length; i++)
                            {
                                var pt = lp[i].ParameterType;
                                if (pt == typeof(bool)) args[i] = true;
                                else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                            }

                            sb.AppendLine("使用 " + load.Name + "(" + lp.Length + " 參數)");
                            object r = load.Invoke(cf, args);
                            sb.AppendLine("讀取回傳: " + (r == null ? "void" : r.ToString()));

                            var coords = GetMemberValue(cf, "coordinate") as Array;
                            if (coords == null) sb.AppendLine("取不到 coordinate 陣列");
                            else
                            {
                                sb.Append("檔案內各服裝槽第 " + (slot + 1) + " 格: ");
                                for (int c = 0; c < coords.Length; c++)
                                {
                                    object acc = GetMemberValue(coords.GetValue(c), "accessory");
                                    var pa = acc == null ? null : GetMemberValue(acc, "parts") as Array;
                                    sb.Append("[" + c + "]=" + (pa == null || slot >= pa.Length
                                        ? "?" : GetInt(pa.GetValue(slot), "type", -1).ToString()) + " ");
                                }
                                sb.AppendLine();
                            }
                        }
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("檢查失敗: " + e.GetBaseException()); }

            sb.AppendLine("===== 結束 =====");
            Debug.Log(sb.ToString());
            LastMessage = "已檢查存出的卡，見主控台";
        }

        public static void Dump(ChaControl cha)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== AccessoryTools 診斷 =====");

            var arr = GetPartsArray(cha);
            sb.AppendLine("parts 陣列長度: " + (arr == null ? "取不到" : arr.Length.ToString()));
            sb.AppendLine("parts 取不到的原因: " + WhyNoParts(cha));
            try { sb.AppendLine("infoAccessory 長度: " + (cha.infoAccessory == null ? "null" : cha.infoAccessory.Length.ToString())); }
            catch { sb.AppendLine("infoAccessory: 讀取失敗"); }
            try { sb.AppendLine("objAccessory 長度: " + (cha.objAccessory == null ? "null" : cha.objAccessory.Length.ToString())); }
            catch { sb.AppendLine("objAccessory: 讀取失敗"); }

            sb.AppendLine("MoreAccessories 是否載入: " + (FindType("MoreAccessoriesKOI.MoreAccessories") != null
                                                       || FindType("MoreAccessories") != null));

            if (arr != null && arr.Length > 0)
            {
                var pi = arr.GetValue(0);
                if (pi != null)
                {
                    sb.AppendLine("--- PartsInfo 欄位 ---");
                    foreach (var f in pi.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                             | BindingFlags.Instance))
                    {
                        object v = null;
                        try { v = f.GetValue(pi); } catch { }
                        sb.AppendLine("  " + f.FieldType.Name + " " + f.Name + " = " + (v ?? "null"));
                    }
                }
            }

            try
            {
                var acc = GetMemberValue(cha.nowCoordinate, "accessory");
                if (acc != null)
                {
                    sb.AppendLine("--- " + acc.GetType().FullName + " 的成員 ---");
                    foreach (var f in acc.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                              | BindingFlags.Instance))
                        sb.AppendLine("  [欄位] " + f.FieldType.Name + " " + f.Name);
                    foreach (var pr in acc.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                                                   | BindingFlags.Instance))
                        sb.AppendLine("  [屬性] " + pr.PropertyType.Name + " " + pr.Name);
                }
            }
            catch (Exception e) { sb.AppendLine("列舉 ChaFileAccessory 失敗: " + e.Message); }

            sb.AppendLine("--- ChaControl 中含 Accessory 的方法 ---");
            foreach (var m in cha.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance))
            {
                if (m.Name.IndexOf("Accessor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var args = new List<string>();
                foreach (var p in m.GetParameters()) args.Add(p.ParameterType.Name);
                sb.AppendLine("  " + m.Name + "(" + string.Join(", ", args.ToArray()) + ")");
            }

            sb.AppendLine("===== 診斷結束 =====");
            Debug.Log(sb.ToString());
            LastMessage = "診斷已輸出到主控台";
            LastReport = "parts 長度 " + (arr == null ? "?" : arr.Length.ToString());
        }

        // ---------------------------------------------------------------
        static object ReadMember(object o, string name)
        {
            if (o == null) return null;
            var v = GetMemberValue(o, name);
            if (v != null) return v;

            // 自動屬性的實際欄位叫 <name>k__BackingField
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField("<" + name + ">k__BackingField",
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) { try { return f.GetValue(o); } catch { } }
            }
            return null;
        }

        static bool WriteMember(object o, string name, object value)
        {
            if (o == null) return false;

            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                try
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) { f.SetValue(o, Convert.ChangeType(value, f.FieldType)); return true; }

                    var bf = t.GetField("<" + name + ">k__BackingField",
                        BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (bf != null) { bf.SetValue(o, Convert.ChangeType(value, bf.FieldType)); return true; }

                    var pr = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (pr != null && pr.CanWrite)
                    {
                        pr.SetValue(o, Convert.ChangeType(value, pr.PropertyType), null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }


        // ---------------------------------------------------------------
        // 整組縮放／移動（例如用很多飾品拼成的頭髮）— 滑桿即時版
        //
        // 每個飾品有兩層調整：N_move（調整1）、N_move2（調整2）。只改「調整1」的位置與縮放，
        // 調整2 在調整1 的空間裡會自動跟著，所以一整組看起來就像整體被縮放／移動。
        //   頭部骨頭：以參考骨頭（預設 cf_j_head）的原點與軸向為準（X 左右、Y 上下、Z 前後）
        //   各自掛點：以每個飾品自己的掛點為準
        // 調整1 的位置存成 addMove[0,0]，畫面上 localPosition = 值 × 0.01；縮放 addMove[0,2]。
        //
        // 做法：開始拖滑桿時先拍一份「基準」，之後每次滑桿變動都從基準重新算、寫絕對值
        // （不會越拖越歪）。按「確定」把目前結果當成新基準，「取消」回到基準。
        // ---------------------------------------------------------------
        public class ScaleSession
        {
            internal class Item
            {
                public Vector3[,] am;
                public Vector3 p0, r0, s0;
                public Transform frame;   // 算位置用的座標系（N_move 的父節點）
                public Transform mv;      // 目前這套的 N_move，其他服裝槽為 null
                public Vector3 mvPos0, mvScl0;
                public bool hadMv;
            }
            internal readonly List<Item> Items = new List<Item>();
            internal Transform RefBone;
            public ChaControl Cha;
            public int Coord;
            public string Key = "";
            public int SlotCount, ExtraCount, OnlyScreen;
            /// <summary>勾選同步、但同一格不是同一個飾品（或沒有那一格）的服裝槽。</summary>
            public readonly HashSet<int> Unsynced = new HashSet<int>();

            /// <summary>從基準套用：s = 縮放倍率，off = 位移（addMove 單位，頭部骨頭模式時是參考骨頭的軸向）。</summary>
            public void Apply(Vector3 s, Vector3 off)
            {
                foreach (var it in Items)
                {
                    Vector3 np, nc;
                    ComputeScaled(it.p0, it.r0, it.s0, s, off, it.frame, RefBone, out np, out nc);
                    it.am[0, 0] = np; it.am[0, 2] = nc;
                    if (it.hadMv && it.mv != null) { it.mv.localPosition = np * 0.01f; it.mv.localScale = nc; }
                }
            }

            public void Revert()
            {
                foreach (var it in Items)
                {
                    it.am[0, 0] = it.p0; it.am[0, 2] = it.s0;
                    if (it.hadMv && it.mv != null) { it.mv.localPosition = it.mvPos0; it.mv.localScale = it.mvScl0; }
                }
            }

            /// <summary>角色、換裝或飾品物件換掉了就不能再用。</summary>
            public bool Valid(ChaControl cha)
            {
                if (cha == null || cha != Cha || NowCoordinateType(cha) != Coord) return false;
                foreach (var it in Items) if (it.hadMv && it.mv == null) return false;   // 飾品物件被重建（Unity 的 == null）
                return true;
            }
        }

        static Vector3[,] GetAddMove(object partsInfo)
        {
            return partsInfo == null ? null : ReadMember(partsInfo, "addMove") as Vector3[,];
        }

        /// <summary>第 slot 格的 N_move（調整1）Transform。原本 20 格用 objAcsMove，擴充格往飾品裡找。</summary>
        public static Transform GetMove01(ChaControl cha, int slot)
        {
            try
            {
                var mv = cha.objAcsMove;
                if (mv != null && slot < mv.GetLength(0) && mv[slot, 0] != null) return mv[slot, 0].transform;
            }
            catch { }
            try
            {
                var objs = cha.objAccessory;
                if (objs != null && slot < objs.Length && objs[slot] != null)
                    return FindDeep(objs[slot].transform, "N_move");
            }
            catch { }
            return null;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        public static Transform FindBone(ChaControl cha, string name)
        {
            if (cha == null || string.IsNullOrEmpty(name)) return null;
            Transform r = null;
            try { if (cha.objBodyBone != null) r = FindDeep(cha.objBodyBone.transform, name); } catch { }
            if (r == null) r = FindDeep(cha.transform, name);
            return r;
        }

        /// <summary>算出縮放＋位移後的 調整1 位置（addMove 單位）與縮放。frame = N_move 的父節點；refBone = null 代表各自掛點。</summary>
        static void ComputeScaled(Vector3 pos, Vector3 rot, Vector3 scl, Vector3 s, Vector3 off, Transform frame, Transform refBone,
                                  out Vector3 newPos, out Vector3 newScl)
        {
            Quaternion q = Quaternion.Euler(rot);
            Vector3 f = Vector3.zero;
            if (frame != null && refBone != null)
            {
                Vector3 P = frame.TransformPoint(pos * 0.01f);
                Vector3 L = refBone.InverseTransformPoint(P);
                Vector3 P2 = refBone.TransformPoint(Vector3.Scale(s, L) + off * 0.01f);
                newPos = frame.InverseTransformPoint(P2) * 100f;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 e = k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
                    Vector3 d = refBone.InverseTransformDirection(frame.TransformDirection(q * e)).normalized;
                    f[k] = Vector3.Scale(s, d).magnitude;
                }
            }
            else
            {
                newPos = Vector3.Scale(pos, s) + off;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 e = k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
                    f[k] = Vector3.Scale(s, q * e).magnitude;
                }
            }
            newScl = Vector3.Scale(scl, f);
        }

        /// <summary>
        /// 拍基準。slots = 要一起動的飾品；headCenter = true 以 refBoneName 為準；
        /// otherCoords 裡的服裝槽，同一格若是同一個飾品（種類、ID、掛點都一樣）也一起動。
        /// </summary>
        public static ScaleSession BeginScale(ChaControl cha, IList<int> slots, bool headCenter, string refBoneName,
                                              IEnumerable<int> otherCoords)
        {
            var ss = new ScaleSession { Cha = cha, Coord = NowCoordinateType(cha) };
            var seen = new HashSet<Vector3[,]>();
            ss.RefBone = headCenter ? FindBone(cha, string.IsNullOrEmpty(refBoneName) ? "cf_j_head" : refBoneName) : null;
            Array nowParts = GetPartsArray(cha);
            Array fileParts = CoordinateParts(cha, ss.Coord);
            Action<Vector3[,], Transform, Transform> add = (am, frame, mv) =>
            {
                if (am == null || seen.Contains(am)) return;
                seen.Add(am);
                var it = new ScaleSession.Item { am = am, p0 = am[0, 0], r0 = am[0, 1], s0 = am[0, 2], frame = frame, mv = mv };
                if (mv != null) { it.hadMv = true; it.mvPos0 = mv.localPosition; it.mvScl0 = mv.localScale; }
                ss.Items.Add(it);
            };
            foreach (int slot in slots)
            {
                object pi = (nowParts != null && slot < nowParts.Length) ? nowParts.GetValue(slot) : null;
                object pf = (fileParts != null && slot < fileParts.Length) ? fileParts.GetValue(slot) : null;
                Transform mv = GetMove01(cha, slot);
                Transform frame = mv != null ? mv.parent : null;
                int before = ss.Items.Count;
                add(GetAddMove(pi), frame, mv);
                add(GetAddMove(pf), frame, ss.Items.Count > before ? null : mv);
                if (ss.Items.Count == before) continue;
                ss.SlotCount++;
                if (pf == null) ss.OnlyScreen++;

                if (otherCoords == null) continue;
                SlotId me = GetSlotId(cha, slot);
                foreach (int c in otherCoords)
                {
                    if (c == ss.Coord) continue;
                    object pc;
                    if (me == null || !MatchInCoordinate(cha, c, slot, me, out pc)) { ss.Unsynced.Add(c); continue; }
                    int b2 = ss.Items.Count;
                    add(GetAddMove(pc), frame, null);
                    if (ss.Items.Count > b2) ss.ExtraCount++;
                }
            }
            return ss;
        }


        // ---------------------------------------------------------------
        // 同步服裝槽：只有「同一格、同一個飾品」（種類、ID、掛點都一樣）才會一起操作
        // ---------------------------------------------------------------
        public class SlotId { public int type, id; public string parent = ""; }

        /// <summary>目前這套第 slot 格是什麼飾品；空格或讀不到回 null。</summary>
        public static SlotId GetSlotId(ChaControl cha, int slot)
        {
            Array parts = GetPartsArray(cha);
            object pi = (parts != null && slot < parts.Length) ? parts.GetValue(slot) : null;
            if (pi == null) return null;
            var r = new SlotId { type = GetInt(pi, "type", EmptyType), id = GetInt(pi, "id", 0), parent = GetStr(pi, "parentKey") };
            return r.type == EmptyType ? null : r;
        }

        /// <summary>服裝槽 coordType 的第 slot 格是不是同一個飾品。</summary>
        public static bool MatchInCoordinate(ChaControl cha, int coordType, int slot, SlotId me, out object partsInfo)
        {
            partsInfo = null;
            if (me == null) return false;
            Array cp = CoordinateParts(cha, coordType);
            if (cp == null || slot >= cp.Length) return false;
            object pc = cp.GetValue(slot);
            if (pc == null || GetInt(pc, "type", -1) != me.type || GetInt(pc, "id", -1) != me.id
                || GetStr(pc, "parentKey") != me.parent) return false;
            partsInfo = pc;
            return true;
        }

        static int GetInt(object o, string name, int fallback)
        {
            try
            {
                var v = ReadMember(o, name);
                return v == null ? fallback : Convert.ToInt32(v);
            }
            catch { return fallback; }
        }

        static void SetInt(object o, string name, int value) { WriteMember(o, name, value); }

        static string GetStr(object o, string name)
        {
            try { return ReadMember(o, name) as string ?? ""; }
            catch { return ""; }
        }

        static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(name); if (t != null) return t; } catch { }
            }
            return null;
        }
    }
}
