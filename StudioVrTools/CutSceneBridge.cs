using System;
using System.Reflection;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// 從 Studio CutScene（F7）拿「這一幀該在頭顯裡畫什麼」。
    ///
    /// 為什麼是反射而不是加參照
    /// ------------------------
    /// 這兩支是分開的插件，要能各自單獨存在：
    ///   只裝 CutScene    → 過場照樣在桌面播，只是頭顯裡沒有畫面
    ///   只裝 VR Tools    → 回到相機視角、手柄那些照樣能用，只是沒有過場可畫
    /// 加組件參照的話，缺一邊就 TypeLoadException，兩支一起死。
    ///
    /// CutScene 那邊的接口是刻意設計成唯讀的四個屬性加一個旗標，
    /// 名字不會亂改；真的對不上也只是 Available 回 false，不會丟例外。
    /// </summary>
    public static class CutSceneBridge
    {
        public static string LastReport = "（還沒找過）";

        static Type overlayType;
        static FieldInfo instanceField;      // public static CutOverlay Instance
        static PropertyInfo wantsProp, texProp, alphaProp, aspectProp;
        static FieldInfo suppressField;
        static float nextTry;
        static bool bound;

        static bool Bind()
        {
            if (bound) return true;
            if (Time.realtimeSinceStartup < nextTry) return false;
            nextTry = Time.realtimeSinceStartup + 2f;

            try
            {
                // 走 ReflectUtil：部分型別載不起來的組件要濾掉壞的那些，不是整個跳過
                if (overlayType == null)
                    overlayType = ReflectUtil.Find("StudioCutScene.CutOverlay");
                if (overlayType == null) { LastReport = "沒有 Studio CutScene（沒裝，或還沒載入）"; return false; }

                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                instanceField = overlayType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                wantsProp = overlayType.GetProperty("VrWantsDraw", pub);
                texProp = overlayType.GetProperty("VrTexture", pub);
                alphaProp = overlayType.GetProperty("VrAlpha", pub);
                aspectProp = overlayType.GetProperty("VrAspect", pub);
                suppressField = overlayType.GetField("SuppressDesktop", pub);

                if (instanceField == null || wantsProp == null || texProp == null)
                {
                    LastReport = "CutScene 的版本太舊，找不到 VR 接口（需要 1.12.0 以上）";
                    return false;
                }
                bound = true;
                LastReport = "已連上 Studio CutScene";
                return true;
            }
            catch (Exception e)
            {
                LastReport = Lang.T("連接 CutScene 失敗：") + e.Message;
                return false;
            }
        }

        public static bool Available { get { return Bind(); } }

        static object Overlay()
        {
            if (!Bind()) return null;
            try
            {
                object o = instanceField.GetValue(null);
                // MonoBehaviour 被銷毀之後 C# 參考還在，但 Unity 的 == null 會是 true
                var uo = o as UnityEngine.Object;
                return uo != null ? o : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 讀出這一幀的狀態。回傳 false 代表沒有東西要畫
        /// （沒裝 CutScene、沒在播、或讀取失敗）。
        /// </summary>
        public static bool Read(out Texture tex, out float alpha, out float aspect)
        {
            tex = null; alpha = 0f; aspect = 16f / 9f;
            object ov = Overlay();
            if (ov == null) return false;
            try
            {
                object w = wantsProp.GetValue(ov, null);
                if (!(w is bool) || !(bool)w) return false;

                tex = texProp.GetValue(ov, null) as Texture;
                if (alphaProp != null)
                {
                    object a = alphaProp.GetValue(ov, null);
                    if (a is float) alpha = (float)a;
                }
                if (aspectProp != null)
                {
                    object s = aspectProp.GetValue(ov, null);
                    if (s is float && (float)s > 0.01f) aspect = (float)s;
                }
                return tex != null;
            }
            catch (Exception e)
            {
                LastReport = Lang.T("讀 CutScene 狀態失敗：") + e.Message;
                return false;
            }
        }

        /// <summary>告訴 CutScene 桌面那一份要不要省略。</summary>
        public static void SetSuppressDesktop(bool on)
        {
            object ov = Overlay();
            if (ov == null || suppressField == null) return;
            try { suppressField.SetValue(ov, on); }
            catch { }
        }
    }
}
