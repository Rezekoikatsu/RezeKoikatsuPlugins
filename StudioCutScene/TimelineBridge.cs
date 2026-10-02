using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 用反射存取 Timeline 外掛，不硬寫型別名稱。
    /// 作法跟 VNSoundTools 抓 VNSound Manager 一樣：先掃候選型別，找到就快取成員。
    /// </summary>
    public static class TimelineBridge
    {
        const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static;
        const BindingFlags ST = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static readonly string[] TIME_NAMES = { "playbackTime", "_playbackTime", "currentTime", "time" };
        static readonly string[] PLAYING_NAMES = { "isPlaying", "_isPlaying", "playing" };
        static readonly string[] DURATION_NAMES = { "duration", "_duration", "length" };

        public static Type Type;
        public static object Instance;          // null = 靜態成員
        public static string Report = "尚未掃描。";
        public static string PauseMode = "-";

        static MemberInfo timeM, playingM, durationM;
        static MethodInfo playMi, pauseMi, stopMi;
        static MethodInfo seekMi;          // 吃一個數值參數的跳轉方法

        // 沒有 Pause 可用時的退路：每幀把時間寫回去
        static bool freezing;
        static float freezeAt;
        // 過場期間的「釘住」。跟上面那個退路分開兩個旗標：Pause()/Resume() 只動
        // freezing，釘住只動 holding，誰也不會把對方關掉。
        static bool holding;
        static float holdAt;
        static float holdDrift;         // 釘住期間被推走最多多少秒（診斷用）

        public static bool Ready { get { return timeM != null; } }
        public static bool Freezing { get { return freezing; } }
        /// <summary>有沒有掃到 isPlaying 之類的旗標。沒有的話 GetIsPlaying() 恆為 false，不可拿來判斷。</summary>
        public static bool HasPlayingFlag { get { return playingM != null; } }

        // ------------------------------------------------------------------ 掃描

        public static bool Scan()
        {
            Type = null; Instance = null;
            timeM = playingM = durationM = null;
            playMi = pauseMi = stopMi = null;
            freezing = false;

            var sb = new StringBuilder();
            var cands = new List<Type>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.FullName == null) continue;
                    if (t.FullName.IndexOf("Timeline", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (FindMember(t, TIME_NAMES) == null) continue;
                    cands.Add(t);
                    sb.AppendLine("候選: " + t.FullName + "   [" + asm.GetName().Name + "]");
                }
            }

            if (cands.Count == 0)
            {
                Report = "找不到 Timeline 的型別。\n請確認 Timeline 外掛已載入（在 Studio 裡開過一次 Timeline 視窗再試）。";
                // 這一行一定要留下痕跡，但掃描是每秒重試的，不能每次都寫 ——
                // 只在「說法變了」的時候寫一次，log 才不會被洗版。
                LogOnce("[CutScene] Timeline 掃描：一個候選型別都沒有。"
                        + " 已載入的組件 " + AppDomain.CurrentDomain.GetAssemblies().Length + " 個。"
                        + " Timeline 外掛可能還沒載完，或它的型別名稱變了。");
                return false;
            }

            // 計分挑選，**不能**用「第一個有 Play() 的就選」。
            //
            // 血淚：現在同時載入的含 "Timeline" 字樣的組件有三個 ——
            // Timeline 1.5.6（真正要的那個，Timeline.Timeline）、Timeline VMD Sync、
            // 以及 Timeline Flow Control Logic。它們都可能有某個型別同時具備
            // 「叫 time 的成員」和「Play() 方法」。舊的挑法拿到誰完全取決於
            // AppDomain.GetAssemblies() 這一次的順序 —— 所以會發生
            // 「昨天好好的，今天開起來 F7 的播放鈕全部沒反應」，
            // log 裡就是那句「時間軸無法跳轉，請自己把播放頭拉回 0 再按一次」。
            //
            // 現在改成每個候選算一個分數，取最高的；同分才照原順序。
            Type pick = null;
            int bestScore = int.MinValue;
            foreach (var t in cands)
            {
                int s = Score(t);
                sb.AppendLine("  分數 " + s + "　" + t.FullName);
                if (s > bestScore) { bestScore = s; pick = t; }
            }
            if (pick == null) pick = cands[0];

            Type = pick;
            timeM = FindMember(pick, TIME_NAMES);
            playingM = FindMember(pick, PLAYING_NAMES);
            durationM = FindMember(pick, DURATION_NAMES);
            playMi = FindMethod(pick, "Play");
            pauseMi = FindMethod(pick, "Pause");
            if (pauseMi == null) pauseMi = FindMethod(pick, "PausePlayback");
            stopMi = FindMethod(pick, "Stop");
            seekMi = FindSeekMethod(pick);

            if (!IsStatic(timeM))
            {
                Instance = FindInstance(pick);
                if (Instance == null)
                {
                    sb.AppendLine();
                    sb.AppendLine("!! 找到型別但拿不到實例。請先在 Studio 裡開一次 Timeline 視窗再掃描。");
                }
            }

            if (pauseMi != null) PauseMode = "Pause() 方法";
            else if (playingM != null && CanWrite(playingM)) PauseMode = "寫 isPlaying = false";
            else PauseMode = "凍結模式（每幀寫回時間）";

            sb.AppendLine();
            sb.AppendLine("選用型別 : " + pick.FullName);
            sb.AppendLine("實例     : " + (Instance == null ? "(靜態)" : Instance.ToString()));
            sb.AppendLine("時間成員 : " + Describe(timeM));
            sb.AppendLine("播放旗標 : " + Describe(playingM));
            sb.AppendLine("總長成員 : " + Describe(durationM));
            sb.AppendLine("Play()   : " + (playMi != null ? "有" : "無"));
            sb.AppendLine("Pause()  : " + (pauseMi != null ? "有" : "無"));
            sb.AppendLine("Stop()   : " + (stopMi != null ? "有" : "無"));
            sb.AppendLine("跳轉     : " + (seekMi != null ? seekMi.Name + "(數值)"
                                        : (CanWrite(timeM) ? "直接寫 " + timeM.Name : "**做不到**")));
            sb.AppendLine("暫停方式 : " + PauseMode);

            Report = sb.ToString();

            // 一定要記進 log。挑錯型別的症狀是「播放鈕全部沒反應」，
            // 而那個症狀本身看不出是挑錯了 —— 有這一行就一眼看得出來。
            LogOnce("[CutScene] Timeline 掃描：候選 " + cands.Count + " 個，選用 "
                    + pick.FullName + "（" + SafeAsm(pick) + "，分數 " + bestScore + "）"
                    + "　時間成員=" + Describe(timeM)
                    + "　跳轉=" + (seekMi != null ? seekMi.Name + "()"
                                 : (CanWrite(timeM) ? "寫 " + timeM.Name : "**做不到**"))
                    + "　暫停=" + PauseMode
                    + "　實例=" + (Instance == null ? "(靜態)" : "有"));
            return timeM != null;
        }

        static string lastLogged = "";

        /// <summary>同一句話只寫一次 log。掃描是每秒重試的，不擋會把 log 洗掉。</summary>
        static void LogOnce(string s)
        {
            if (s == lastLogged) return;
            lastLogged = s;
            Debug.Log(s);
        }

        static string SafeAsm(Type t)
        {
            try { return t.Assembly.GetName().Name; }
            catch { return "?"; }
        }

        /// <summary>
        /// 候選型別的分數。分數高的才是「真正的那個 Timeline」。
        ///
        /// 權重的道理：
        ///   能不能跳轉是**功能性的**（跳不了整排播放鈕就是死的），所以給最高分；
        ///   名字和組件只是線索，給中等分，避免哪天型別改名就整個挑錯；
        ///   Play / Pause / duration 是加分項，用來在幾個都能跳轉時分高下。
        /// </summary>
        static int Score(Type t)
        {
            int s = 0;
            try
            {
                // --- 名字與出處（線索） ---
                if (t.FullName == "Timeline.Timeline") s += 100;
                else if (t.Namespace == "Timeline") s += 40;
                try { if (t.Assembly.GetName().Name == "Timeline") s += 40; } catch { }

                // --- 功能（決定性） ---
                MemberInfo tm = FindMember(t, TIME_NAMES);
                if (tm == null) return int.MinValue;          // 不該發生，候選都是有的
                // playbackTime 是 Timeline 真正的播放頭；time / currentTime 太泛，
                // 別的外掛拿來放自己的計時器也叫這個名字。
                if (tm.Name.IndexOf("playbackTime", StringComparison.OrdinalIgnoreCase) >= 0) s += 60;

                bool canSeek = FindSeekMethod(t) != null || CanWrite(tm);
                if (canSeek) s += 120;                        // 跳不動的型別一律不要

                if (FindMethod(t, "Play") != null) s += 20;
                if (FindMethod(t, "Pause") != null || FindMethod(t, "PausePlayback") != null) s += 20;
                if (FindMember(t, DURATION_NAMES) != null) s += 20;
                if (FindMember(t, PLAYING_NAMES) != null) s += 10;

                // 拿不到實例的話前面加的都是空的
                if (!IsStatic(tm) && FindInstance(t) == null) s -= 200;
            }
            catch { return int.MinValue; }
            return s;
        }

        /// <summary>完整倒出選中型別的所有成員，貼到剪貼簿用。</summary>
        public static string DumpMembers()
        {
            if (Type == null) return "尚未掃描到型別。";
            var sb = new StringBuilder();
            sb.AppendLine("=== " + Type.FullName + " ===");
            sb.AppendLine("-- Fields --");
            foreach (var f in Type.GetFields(ALL))
                sb.AppendLine(string.Concat(f.IsStatic ? "static " : "", f.FieldType.Name, " ", f.Name));
            sb.AppendLine("-- Properties --");
            foreach (var p in Type.GetProperties(ALL))
                sb.AppendLine(string.Concat(p.PropertyType.Name, " ", p.Name,
                    p.CanRead ? " get" : "", p.CanWrite ? " set" : ""));
            sb.AppendLine("-- Methods --");
            foreach (var m in Type.GetMethods(ALL))
            {
                if (m.IsSpecialName) continue;
                var ps = m.GetParameters();
                var a = new string[ps.Length];
                for (int i = 0; i < ps.Length; i++) a[i] = ps[i].ParameterType.Name;
                sb.AppendLine(string.Concat(m.IsStatic ? "static " : "", m.ReturnType.Name, " ",
                    m.Name, "(", string.Join(", ", a), ")"));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ 存取

        public static float GetTime()
        {
            if (timeM == null) return -1f;
            object v = ReadMember(timeM);
            if (v == null) return -1f;
            try { return Convert.ToSingle(v); }
            catch { return -1f; }
        }

        /// <summary>時間軸能不能跳轉。做不到的話「從頭播放」只能靠使用者自己拉回 0。</summary>
        public static bool CanSeek
        {
            get { return seekMi != null || (timeM != null && CanWrite(timeM)); }
        }

        /// <summary>
        /// 跳轉。失敗的話自動重掃一次再試 —— 只重試一次，不會無限重掃。
        ///
        /// 為什麼要有這層：掃描是在插件啟動時做的，那時候有些外掛還沒載完，
        /// 候選清單跟實際跑起來不一樣。與其讓整排播放鈕默默失效，
        /// 不如在真的要用的時候重認一次。
        /// </summary>
        /// <summary>
        /// 跳不動就先重掃一次。**要在 Pause() 之前叫** ——
        /// 挑錯型別的時候 Pause() 也是打在錯的東西上，等到 SeekRetry 才重掃就晚了。
        /// </summary>
        public static void EnsureSeekable()
        {
            if (Ready && CanSeek) return;
            Scan();
        }

        public static bool SeekRetry(float t)
        {
            if (SetTime(t)) return true;
            Debug.LogWarning("[CutScene] 跳轉失敗，重新掃描 Timeline 再試一次");
            Scan();
            return SetTime(t);
        }

        public static bool SetTime(float t)
        {
            // 先試方法（有些版本的 playbackTime 是唯讀屬性，直接寫會靜默失敗）
            if (seekMi != null)
            {
                try
                {
                    var ps = seekMi.GetParameters();
                    object arg = ps[0].ParameterType == typeof(double) ? (object)(double)t
                               : ps[0].ParameterType == typeof(int) ? (object)(int)t
                               : (object)t;
                    seekMi.Invoke(seekMi.IsStatic ? null : Instance, new object[] { arg });
                    return true;
                }
                catch (Exception e) { Debug.LogWarning("[CutScene] " + seekMi.Name + " 失敗: " + e.Message); }
            }
            if (timeM != null && CanWrite(timeM))
            {
                try { WriteMember(timeM, ConvertTo(timeM, t)); return true; } catch { }
            }
            return false;
        }

        static MethodInfo FindSeekMethod(Type t)
        {
            string[] names = { "Seek", "SetTime", "SetPlaybackTime", "JumpTo", "Goto", "GoTo" };
            foreach (var n in names)
            {
                foreach (var m in t.GetMethods(ALL))
                {
                    if (m.Name != n) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    var pt = ps[0].ParameterType;
                    if (pt == typeof(float) || pt == typeof(double) || pt == typeof(int))
                        return m;
                }
            }
            return null;
        }

        public static float GetDuration()
        {
            if (durationM == null) return -1f;
            object v = ReadMember(durationM);
            if (v == null) return -1f;
            try { return Convert.ToSingle(v); }
            catch { return -1f; }
        }

        public static bool GetIsPlaying()
        {
            if (playingM == null) return false;
            object v = ReadMember(playingM);
            return v is bool && (bool)v;
        }

        // ------------------------------------------------------------------ 暫停 / 恢復

        public static void Pause()
        {
            freezing = false;
            if (pauseMi != null)
            {
                try { pauseMi.Invoke(pauseMi.IsStatic ? null : Instance, null); return; }
                catch (Exception e) { Debug.LogWarning("[CutScene] Pause() 失敗: " + e.Message); }
            }
            if (playingM != null && CanWrite(playingM))
            {
                try { WriteMember(playingM, false); return; }
                catch (Exception e) { Debug.LogWarning("[CutScene] 寫 isPlaying 失敗: " + e.Message); }
            }
            // 最後退路：凍結
            freezeAt = GetTime();
            freezing = true;
        }

        public static void Resume()
        {
            if (freezing)
            {
                freezing = false;
                SetTime(freezeAt);
            }
            if (playMi != null)
            {
                try { playMi.Invoke(playMi.IsStatic ? null : Instance, null); return; }
                catch (Exception e) { Debug.LogWarning("[CutScene] Play() 失敗: " + e.Message); }
            }
            if (playingM != null && CanWrite(playingM))
            {
                try { WriteMember(playingM, true); } catch { }
            }
        }

        /// <summary>
        /// 把播放頭釘死在 at，直到 Release()。
        ///
        /// 為什麼 Pause() 不夠：Pause() 只是「叫 Timeline 停一次」。停完之後
        /// 誰都可以再把它放開 —— Timeline 自己的 alt+T、Timeline 視窗上的播放鈕、
        /// 別的插件、我們自己的播放快捷鍵。放開之後沒有人會再把它按回去，
        /// 於是「過場播到一半，底下的時間軸自己跑掉了」。
        ///
        /// 釘住是每一幀都寫回去，所以不管是誰放開的，下一幀就被拉回來。
        /// 過場期間時間軸本來就該完全不動，這個條件是硬的，值得用硬的手段。
        /// </summary>
        public static void Hold()
        {
            float t = GetTime();
            holdAt = t < 0f ? 0f : t;
            holdDrift = 0f;
            holding = true;
        }

        /// <summary>解除釘住。**不會**順便播放 —— 要播放請另外叫 Resume()。</summary>
        public static void Release()
        {
            holding = false;
        }

        public static bool Holding { get { return holding; } }

        /// <summary>釘住期間被推走最多幾秒。0 = 全程都沒人動它。</summary>
        public static float HoldDrift { get { return holdDrift; } }

        /// <summary>凍結模式時要每幀呼叫（放在 LateUpdate）。</summary>
        public static void TickFreeze()
        {
            if (holding)
            {
                float now = GetTime();
                if (now >= 0f)
                {
                    float d = now - holdAt;
                    if (d < 0f) d = -d;
                    if (d > holdDrift) holdDrift = d;
                }
                SetTime(holdAt);
                return;
            }
            if (freezing) SetTime(freezeAt);
        }

        // ------------------------------------------------------------------ 反射小工具

        static MemberInfo FindMember(Type t, string[] names)
        {
            foreach (var n in names)
            {
                var p = t.GetProperty(n, ALL);
                if (p != null && p.CanRead) return p;
                var f = t.GetField(n, ALL);
                if (f != null) return f;
            }
            return null;
        }

        static MethodInfo FindMethod(Type t, string name)
        {
            var m = t.GetMethod(name, ALL, null, System.Type.EmptyTypes, null);
            return m;
        }

        static object FindInstance(Type t)
        {
            foreach (var f in t.GetFields(ST))
            {
                if (!t.IsAssignableFrom(f.FieldType)) continue;
                try { var v = f.GetValue(null); if (v != null) return v; } catch { }
            }
            foreach (var p in t.GetProperties(ST))
            {
                if (!t.IsAssignableFrom(p.PropertyType) || !p.CanRead) continue;
                try { var v = p.GetValue(null, null); if (v != null) return v; } catch { }
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                var o = UnityEngine.Object.FindObjectOfType(t);
                if (o != null) return o;
            }
            // 再掃一次：任何型別上的靜態成員，型別剛好是 t
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var ot in types)
                {
                    if (ot == null) continue;
                    FieldInfo[] fs;
                    try { fs = ot.GetFields(ST); } catch { continue; }
                    foreach (var f in fs)
                    {
                        if (!t.IsAssignableFrom(f.FieldType)) continue;
                        try { var v = f.GetValue(null); if (v != null) return v; } catch { }
                    }
                }
            }
            return null;
        }

        static bool IsStatic(MemberInfo m)
        {
            var f = m as FieldInfo;
            if (f != null) return f.IsStatic;
            var p = m as PropertyInfo;
            if (p != null)
            {
                var g = p.GetGetMethod(true);
                return g != null && g.IsStatic;
            }
            return false;
        }

        static bool CanWrite(MemberInfo m)
        {
            var f = m as FieldInfo;
            if (f != null) return !f.IsInitOnly && !f.IsLiteral;
            var p = m as PropertyInfo;
            return p != null && p.CanWrite;
        }

        static object ReadMember(MemberInfo m)
        {
            try
            {
                var f = m as FieldInfo;
                if (f != null) return f.GetValue(f.IsStatic ? null : Instance);
                var p = m as PropertyInfo;
                if (p != null) return p.GetValue(IsStatic(m) ? null : Instance, null);
            }
            catch { }
            return null;
        }

        static void WriteMember(MemberInfo m, object val)
        {
            var f = m as FieldInfo;
            if (f != null) { f.SetValue(f.IsStatic ? null : Instance, val); return; }
            var p = m as PropertyInfo;
            if (p != null) p.SetValue(IsStatic(m) ? null : Instance, val, null);
        }

        static object ConvertTo(MemberInfo m, float v)
        {
            Type mt = null;
            var f = m as FieldInfo;
            if (f != null) mt = f.FieldType;
            var p = m as PropertyInfo;
            if (p != null) mt = p.PropertyType;
            if (mt == typeof(double)) return (double)v;
            if (mt == typeof(int)) return (int)v;
            return v;
        }

        static string Describe(MemberInfo m)
        {
            if (m == null) return "(無)";
            var f = m as FieldInfo;
            if (f != null) return "field " + f.FieldType.Name + " " + f.Name + (f.IsStatic ? " [static]" : "");
            var p = m as PropertyInfo;
            if (p != null) return "prop " + p.PropertyType.Name + " " + p.Name
                                 + (p.CanWrite ? " [rw]" : " [ro]");
            return m.Name;
        }
    }
}
