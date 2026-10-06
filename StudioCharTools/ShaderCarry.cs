using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StudioCharTools
{
    /// <summary>
    /// 換人時把「場景原角色」的著色器帶到新角色身上（MaterialEditor）。
    ///
    /// 場景卡的燈光、後製通常是配合原角色的著色器調的（例如全套 xukmi），
    /// 換上用別套著色器的人物卡（原版、KKUTS…）顏色、陰影就會跟場景不搭。
    ///
    /// 做法：換人前記下原角色本體每個部位（身體、臉、眼白、眼睛、眉毛、牙齒、舌頭…）
    /// 第 0 個材質的著色器；換人後依「部位」（renderer 名稱）把新角色對應材質的著色器換成一樣的。
    /// 只換著色器，參數、顏色、貼圖、反射都不動（用新角色自己的）。
    /// 用部位對應而不是材質名稱，所以頭模不同（cf_m_face_00 ↔ cf_m_face_imas_001）也對得上。
    /// 只用反射呼叫 MaterialEditor，沒裝 MaterialEditor 時整個功能安靜地不做事。
    /// </summary>
    public static class ShaderCarry
    {
        public static string LastReport = "";

        static readonly string[] Parts =
        {
            "o_body_a", "cf_O_face", "cf_Ohitomi_L", "cf_Ohitomi_R", "cf_Ohitomi_L02", "cf_Ohitomi_R02",
            "cf_O_mayuge", "cf_O_tooth", "cf_O_canine", "o_tang", "cf_O_eyeline", "cf_O_eyeline_low",
            "cf_O_noseline", "cf_O_namida_L", "cf_O_namida_M", "cf_O_namida_S", "o_dankon", "o_dan_f",
        };

        public class Entry
        {
            public string Part;
            public string MaterialName;
            public string Shader;
        }

        public class Snapshot
        {
            public List<Entry> Entries = new List<Entry>();
            public string Summary()
            {
                var sb = new StringBuilder();
                foreach (var e in Entries) sb.Append(e.Part).Append('=').Append(e.Shader).Append("; ");
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ MaterialEditor 反射
        static Type _ctrlType;
        static Type CtrlType
        {
            get
            {
                if (_ctrlType == null) _ctrlType = HarmonyLib.AccessTools.TypeByName("KK_Plugins.MaterialEditor.MaterialEditorCharaController");
                return _ctrlType;
            }
        }
        public static bool Available { get { return CtrlType != null; } }

        static Component Ctrl(ChaControl cha)
        {
            if (cha == null || CtrlType == null) return null;
            return cha.GetComponent(CtrlType);
        }

        static MethodInfo M(string name, int nParams)
        {
            foreach (var m in CtrlType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == nParams) return m;
            return null;
        }

        static object CharacterEnum(MethodInfo m)
        {
            Type et = m.GetParameters()[1].ParameterType;
            return Enum.Parse(et, "Character");
        }

        static object F(object o, string name)
        {
            if (o == null) return null;
            var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null ? p.GetValue(o, null) : null;
        }

        static IEnumerable<object> List(Component ctrl, string field)
        {
            var l = F(ctrl, field) as IEnumerable;
            if (l == null) yield break;
            foreach (var x in l) yield return x;
        }

        static bool IsCharacter(object item)
        {
            var t = F(item, "ObjectType");
            return t != null && t.ToString() == "Character";
        }

        // ------------------------------------------------------------ 部位 → 材質
        static Renderer FindPart(ChaControl cha, string part)
        {
            foreach (var root in new[] { cha.objHead, cha.objBody })
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.name == part) return r;
            }
            return null;
        }

        static Material PartMaterial(ChaControl cha, string part)
        {
            var r = FindPart(cha, part);
            if (r == null) return null;
            var mats = r.sharedMaterials;
            return mats != null && mats.Length > 0 ? mats[0] : null;
        }

        static string MatName(Material m)
        {
            // 跟 MaterialEditor 的 NameFormatted 一樣：去掉 " (Instance)"
            return m == null ? "" : m.name.Replace("(Instance)", "").Trim();
        }

        // ------------------------------------------------------------ 換人前：記下原角色
        public static Snapshot Capture(ChaControl cha)
        {
            var snap = new Snapshot();
            var sb = new StringBuilder();
            try
            {
                if (!Available) { LastReport = "沒有 MaterialEditor"; return snap; }
                foreach (string part in Parts)
                {
                    Material mat = PartMaterial(cha, part);
                    if (mat == null || mat.shader == null) continue;
                    var e = new Entry { Part = part, MaterialName = MatName(mat), Shader = mat.shader.name };
                    snap.Entries.Add(e);
                    sb.AppendLine(string.Format("  {0} [{1}] {2}", part, e.MaterialName, e.Shader));
                }
            }
            catch (Exception ex) { sb.AppendLine("記錄失敗: " + ex.Message); }
            LastReport = sb.ToString();
            return snap;
        }

        /// <summary>MaterialEditor 認不認得這個著色器。問不到（版本不同）就當作認得，維持原本的行為。</summary>
        static bool ShaderKnown(string name)
        {
            try
            {
                Type pb = HarmonyLib.AccessTools.TypeByName("MaterialEditorAPI.MaterialEditorPluginBase");
                FieldInfo f = pb == null ? null : pb.GetField("LoadedShaders",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var d = f == null ? null : f.GetValue(null) as System.Collections.IDictionary;
                return d == null || d.Contains(name);
            }
            catch { return true; }
        }

        // ------------------------------------------------------------ 換人後：套到新角色
        public static int Apply(ChaControl cha, Snapshot snap)
        {
            var sb = new StringBuilder();
            int changed = 0;
            try
            {
                if (snap == null || snap.Entries.Count == 0 || !Available) { LastReport = "沒有可套用的著色器"; return 0; }
                var ctrl = Ctrl(cha);
                if (ctrl == null) { LastReport = "新角色沒有 MaterialEditor 控制器"; return 0; }
                var mSetShader = M("SetMaterialShader", 6);
                var mGetOrig = M("GetMaterialShaderOriginal", 4);
                var mRemoveShader = M("RemoveMaterialShader", 5);
                if (mSetShader == null) { LastReport = "MaterialEditor 版本不同，找不到 SetMaterialShader"; return 0; }
                object charType = CharacterEnum(mSetShader);
                GameObject go = cha.gameObject;

                foreach (var e in snap.Entries)
                {
                    Material mat = PartMaterial(cha, e.Part);
                    if (mat == null) continue;
                    string before = mat.shader != null ? mat.shader.name : "";
                    if (before != e.Shader)
                    {
                        string orig = null;
                        try { if (mGetOrig != null) orig = mGetOrig.Invoke(ctrl, new object[] { 0, charType, mat, go }) as string; } catch { }
                        if (orig == e.Shader && mRemoveShader != null)
                            mRemoveShader.Invoke(ctrl, new object[] { 0, charType, mat, go, true });
                        else
                        {
                            // MaterialEditor 只換得了它自己表裡有的著色器。表裡沒有的它會默默失敗，
                            // 但還是在角色身上記一筆無效的覆寫 —— 那一筆會跟著存進卡片。
                            // 兩款遊戲內建的表不一樣，原角色用的著色器新角色這邊不一定有，先查再換。
                            if (!ShaderKnown(e.Shader))
                            {
                                sb.AppendLine(string.Format("  {0} [{1}] 略過（MaterialEditor 沒有 {2}）", e.Part, MatName(mat), e.Shader));
                                continue;
                            }
                            mSetShader.Invoke(ctrl, new object[] { 0, charType, mat, e.Shader, go, true });
                        }
                        mat = PartMaterial(cha, e.Part) ?? mat;
                        changed++;
                        sb.AppendLine(string.Format("  {0} [{1}] {2} → {3}", e.Part, MatName(mat), before, e.Shader));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("套用失敗: " + (ex.InnerException ?? ex).Message); }
            LastReport = sb.ToString();
            return changed;
        }
    }
}
