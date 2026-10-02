using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 依 Timeline 時間播放外部音檔，取代 VNSound。
    ///
    /// 跟 VNSound 的根本差別：VNSound 的觸發是「物件從停用變啟用」的一次性事件，
    /// 沒有時間概念，所以把時間軸拉到中段，音訊只會從頭播。
    /// 這裡每幀都拿 Timeline 的 playbackTime 去算音檔該在哪個位置，
    /// 拉到哪裡音訊就跳到哪裡。
    ///
    ///     音檔位置 = MapToAudio(track, timeline 時間)
    ///
    /// 所以音檔不必切割 —— 同一支原始 wav 配不同對應關係就是不同段。
    ///
    /// 對應關係有兩種寫法：
    ///   offset  —— 假設 timeline 時間 = 真實時間，最簡單
    ///   anchors —— 一串 [timeline秒, 音檔秒] 對應點，折線內插。
    ///              場景有「時間流速」(timeScale) 軌道時必須用這個。
    /// </summary>
    public class TrackPlayer : MonoBehaviour
    {
        public static TrackPlayer Instance;

        AudioSource src;
        AudioClip clip;
        string loadedPath = "";
        bool loading;

        float lastT = -1f;
        float fadeGain = 1f;
        int curTrack = -1;

        public string Status = "未啟用";
        public float WantPos, ActualPos;

        // 診斷：hardSeek 每觸發一次，聽起來就是「那一小段重播了」。
        // 對應關係正確時整段播完應該是 0 次。
        public int SeekCount;
        public string LastSeek = "-";

        CutConfig cfg;

        const float SEEK_FADE = 0.045f;   // seek 後的淡入，避免爆音

        // 過場期間的「自由播」：不受 timeline 驅動，就讓音訊照真實時間往下走。
        // 過場的聲音本來就是原始音檔那一段，所以播完剛好接上下一個場景。
        bool freeRun;
        public bool HasClip { get { return clip != null; } }

        public void BeginFreeRun(float audioStart)
        {
            freeRun = true;
            if (clip == null) return;
            if (audioStart >= 0f) SeekTo(audioStart);
            if (!src.isPlaying) { src.UnPause(); if (!src.isPlaying) src.Play(); }
            src.pitch = 1f;
        }

        /// <summary>
        /// 同上，但參數是**主配音**的秒數（＝影片的秒數）。
        /// 換算留在這裡做，外面就不必知道現在放的是哪一版配音。
        /// </summary>
        public void BeginFreeRunMain(float mainSec)
        {
            BeginFreeRun(mainSec < 0f ? -1f : VariantSec(ActiveVariant, mainSec));
        }

        /// <summary>過場期間音軌正在自由播（＝它才是這段過場的主時鐘）。</summary>
        public bool FreeRunning { get { return freeRun && clip != null && src != null; } }

        string ActiveVariant { get { return cfg == null ? "" : cfg.activeVariant; } }

        /// <summary>自由播時音訊現在播到哪，換算成**主配音**（＝影片）的秒數。</summary>
        public float FreeRunMainSec()
        {
            if (!FreeRunning) return -1f;
            return MainSec(ActiveVariant, src.time);
        }

        /// <summary>自由播時把音訊跳到主配音的某一秒。快轉／倒轉用。</summary>
        public bool FreeRunSeekMain(float mainSec)
        {
            if (!FreeRunning) return false;
            float want = VariantSec(ActiveVariant, mainSec);
            if (want < 0f) want = 0f;
            if (want > clip.length - 0.05f) want = Mathf.Max(0f, clip.length - 0.05f);
            SeekTo(want);
            if (!src.isPlaying) { src.UnPause(); if (!src.isPlaying) src.Play(); }
            return true;
        }

        public void EndFreeRun()
        {
            freeRun = false;
            // 不強制 seek —— 位置按設計就該是對的，
            // 真的有偏差交給 Update 裡的 slew / hardSeek 處理。
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("StudioCutSceneTracks");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<TrackPlayer>();
            Instance.Build();
        }

        void Build()
        {
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.bypassEffects = true;
            src.bypassListenerEffects = true;
            // 注意：這裡「不」設 ignoreListenerPause。
            // 過場播放時 AudioListener.pause = true 會順便把它一起暫停，這正是我們要的。
        }

        public void SetConfig(CutConfig c) { cfg = c; }

        public void StopAll()
        {
            if (src != null) src.Stop();
            curTrack = -1;
            Status = "已停止";
        }

        /// <summary>換配音版本 / 換設定檔之後呼叫，強迫下次重新載入。</summary>
        public void ResetDiag() { SeekCount = 0; LastSeek = "-"; }

        public void Invalidate()
        {
            loadedPath = "";
            curTrack = -1;
            ResetDiag();
            if (src != null) src.Stop();
        }

        /// <summary>
        /// 換場景：把音檔整個丟掉，不只是標記「下次重載」。
        ///
        /// Invalidate() 只清 loadedPath，clip 還掛在 AudioSource 上；
        /// 新場景如果沒有設定檔，cfg.tracks 是空的、走不到 LoadRoutine，
        /// 那顆上一張卡的 clip 就會一直留著 —— 一按播放就放出上一個場景的配音。
        /// 這裡直接 Stop + 解除 + Destroy，確定不會有殘留。
        /// </summary>
        public void Unload()
        {
            StopAllCoroutines();               // 可能有 LoadRoutine 正在跑，先停掉
            loading = false;
            loadedPath = "";
            curTrack = -1;
            lastT = -1f;
            fadeGain = 1f;
            ResetDiag();
            if (src != null) { src.Stop(); src.clip = null; }
            if (clip != null) { UnityEngine.Object.Destroy(clip); clip = null; }
            Status = "未載入";
        }

        // ------------------------------------------------------------------

        void Update()
        {
            if (cfg == null || !cfg.tracksEnabled) { Status = "未啟用"; return; }

            // 過場播放中：AudioListener.pause 已經把這條音軌一起暫停了，不要動它
            var ov = CutOverlay.Instance;
            if (ov != null && ov.IsBusy)
            {
                if (freeRun && clip != null)
                {
                    if (!src.isPlaying) { src.UnPause(); if (!src.isPlaying) src.Play(); }
                    src.pitch = 1f;
                    if (fadeGain < 1f)
                    {
                        fadeGain += Time.unscaledDeltaTime / SEEK_FADE;
                        if (fadeGain > 1f) fadeGain = 1f;
                    }
                    src.volume = fadeGain * Mathf.Clamp01(cfg.trackVolume);
                    ActualPos = src.time;
                    Status = string.Format(Lang.T("過場中（音訊續播 @ {0}s）"), src.time.ToString("F2"));
                }
                else Status = "過場中（已隨全域暫停）";
                return;
            }

            if (!TimelineBridge.Ready) { Status = "Timeline 未就緒"; return; }

            float t = TimelineBridge.GetTime();
            if (t < 0f) return;

            int idx = FindTrack(t);
            if (idx < 0)
            {
                if (src.isPlaying) src.Stop();
                curTrack = -1;
                Status = string.Format(Lang.T("此時間點沒有音軌 ({0}s)"), t.ToString("F2"));
                lastT = t;
                return;
            }

            AudioTrack tr = cfg.tracks[idx];
            string path = ResolveAudio(tr);
            if (string.IsNullOrEmpty(path))
            {
                Status = "音軌路徑無效";
                lastT = t;
                return;
            }

            if (path != loadedPath)
            {
                if (!loading) StartCoroutine(LoadRoutine(path));
                Status = "載入中…";
                lastT = t;
                return;
            }
            if (clip == null) { Status = "音檔未載入"; lastT = t; return; }

            // anchors 給的是**主配音**的秒數；目前在放的若是別版，再換算一次。
            // 沒有對照表時 VariantSec 原樣回傳，跟以前一模一樣。
            float want = VariantSec(VariantOf(tr), MapToAudio(tr, t));
            WantPos = want;

            if (want < 0f || want >= clip.length)
            {
                if (src.isPlaying) src.Stop();
                Status = string.Format(Lang.T("超出音檔範圍 ({0}s / {1}s)"), want.ToString("F2"), clip.length.ToString("F2"));
                lastT = t;
                return;
            }

            bool advancing = lastT < 0f || Mathf.Abs(t - lastT) > 1e-4f;
            // 一幀之內時間軸跳了這麼多 = 使用者拉了進度條（正常播放一幀只走 0.017 秒）
            bool jumped = lastT >= 0f && Mathf.Abs(t - lastT) > cfg.jumpThreshold;
            bool trackChanged = (idx != curTrack);
            curTrack = idx;

            if (src.clip != clip) src.clip = clip;

            if (!advancing)
            {
                // Timeline 停著 → 暫停但保留位置
                if (src.isPlaying) src.Pause();
                src.pitch = 1f;
                Status = string.Format(Lang.T("暫停 @ {0}s"), src.time.ToString("F2"));
                ActualPos = src.time;
                lastT = t;
                return;
            }

            float drift = 0f;
            if (!src.isPlaying)
            {
                SeekTo(want);
                src.UnPause();
                if (!src.isPlaying) src.Play();
                src.pitch = 1f;
            }
            else
            {
                drift = src.time - want;          // 正 = 音訊跑太前面
                float ad = Mathf.Abs(drift);

                // 只有「時間軸真的跳了」才重新定位。
                // 用漂移量當條件是錯的 —— 連續播放時往回跳只會聽到那一小段重播，
                // 慢慢用 pitch 拉回去才對。
                bool needSeek = jumped
                                || (trackChanged && ad > cfg.slewDeadzone * 2f)
                                || ad > cfg.panicSeek;

                if (needSeek)
                {
                    SeekCount++;
                    LastSeek = "timeline " + t.ToString("F2") + "s："
                             + (jumped ? "時間軸跳轉" : trackChanged ? "換段" : "漂移過大")
                             + "  音訊 " + src.time.ToString("F2") + " → " + want.ToString("F2")
                             + "  (差 " + (drift * 1000f).ToString("F0") + " ms)";
                    if (cfg.logSeeks)
                        Debug.LogWarning("[CutScene] seek #" + SeekCount + "  " + LastSeek);
                    SeekTo(want);
                    src.pitch = 1f;
                }
                else if (ad > cfg.slewDeadzone)
                {
                    // 小幅偏差用 pitch 慢慢拉回來。±2% 聽不出來，而且沒有 seek 的爆音，
                    // 這是影片播放器對付音訊時鐘漂移的標準做法。
                    float corr = Mathf.Clamp(-drift * cfg.slewGain, -cfg.slewMax, cfg.slewMax);
                    src.pitch = 1f + corr;
                }
                else
                {
                    // 對得夠準就完全不要動它 —— 正常播放時兩邊都是真實時間，本來就不會漂
                    src.pitch = 1f;
                }
            }

            // seek 後的淡入
            if (fadeGain < 1f)
            {
                fadeGain += Time.unscaledDeltaTime / SEEK_FADE;
                if (fadeGain > 1f) fadeGain = 1f;
            }
            src.volume = Mathf.Clamp01(tr.volume) * fadeGain * Mathf.Clamp01(cfg.trackVolume);

            ActualPos = src.time;
            int nA = tr.anchorT == null ? 0 : tr.anchorT.Length;
            Status = string.Format(Lang.T("播放中  音檔 {0}s / 目標 {1}s  (差 {2} ms)"),
                                   src.time.ToString("F2"), want.ToString("F2"),
                                   ((src.time - want) * 1000f).ToString("F0"))
                     + "  pitch " + src.pitch.ToString("F3")
                     + "   track#" + idx + (nA >= 2 ? "  anchors " + nA : Lang.T("  無 anchors"))
                     + MapNote(VariantOf(tr));
            lastT = t;
        }

        void SeekTo(float sec)
        {
            if (clip == null) return;
            int samples = (int)(sec * clip.frequency);
            if (samples < 0) samples = 0;
            if (samples >= clip.samples) samples = clip.samples - 1;
            try { src.timeSamples = samples; }
            catch { try { src.time = sec; } catch { } }
            fadeGain = 0f;              // 淡入，蓋掉 seek 的 click
            src.volume = 0f;
            src.pitch = 1f;
        }

        /// <summary>
        /// timeline 時間 → 音檔位置。
        ///
        /// 沒有 anchors 時退回 offset + (t - from)，這假設 timeline 時間 = 真實時間。
        /// 場景有「時間流速」(timeScale) 軌道時那個假設不成立：
        /// timeScale 0.82 時真實過 1 秒、timeline 只走 0.82 秒，
        /// 而作者的音訊是疊在成品影片上、對應真實時間 —— 誤差會一路累積。
        /// anchors 就是用折線去逼近 ∫dt/timeScale，點給得越密越準。
        /// </summary>
        public static float MapToAudio(AudioTrack tr, float t)
        {
            var T = tr.anchorT; var A = tr.anchorA;
            int n = (T == null || A == null) ? 0 : Mathf.Min(T.Length, A.Length);
            if (n == 0) return tr.offset + (t - tr.from);
            if (n == 1) return A[0] + (t - T[0]);

            if (t <= T[0])
            {
                float sl0 = Slope(T[0], A[0], T[1], A[1]);
                return A[0] + (t - T[0]) * sl0;
            }
            for (int i = 0; i < n - 1; i++)
            {
                if (t <= T[i + 1])
                {
                    float sl = Slope(T[i], A[i], T[i + 1], A[i + 1]);
                    return A[i] + (t - T[i]) * sl;
                }
            }
            float slN = Slope(T[n - 2], A[n - 2], T[n - 1], A[n - 1]);
            return A[n - 1] + (t - T[n - 1]) * slN;
        }

        /// <summary>
        /// <summary>診斷列要顯示的配音對照狀態。沒有對照表就什麼都不顯示。</summary>
        string MapNote(string name)
        {
            if (cfg == null || cfg.variantMaps == null || string.IsNullOrEmpty(name)) return "";
            for (int i = 0; i < cfg.variantMaps.Length; i++)
            {
                var m = cfg.variantMaps[i];
                if (m != null && m.name == name && m.refT != null && m.refT.Length > 0)
                    return string.Format(Lang.T("  配音對照 {0} 點"), m.refT.Length);
            }
            return "";
        }

        /// <summary>
        /// 這條音軌實際在放哪一個配音版本的名稱。
        /// "@" = 目前選的那個；"@名稱" = 指定的那個；直接寫路徑的話沒有版本可言。
        /// </summary>
        public string VariantOf(AudioTrack tr)
        {
            if (cfg == null || tr == null) return "";
            string p = tr.audio;
            if (string.IsNullOrEmpty(p) || p[0] != '@') return "";
            return p.Length > 1 ? p.Substring(1) : cfg.activeVariant;
        }

        /// <summary>
        /// 主配音的秒數 → 指定配音版本的秒數。
        ///
        /// 找不到對照表（或那一版就是主配音）時原樣回傳 —— 也就是「兩版同步」，
        /// 這正是所有舊 cutscene.json 的行為，所以加了這一層不會動到任何既有的卡。
        ///
        /// 兩端之外用最靠近的那一段斜率外推，跟 MapToAudio 同一套規矩：
        /// 對照點通常只量在中間，頭尾一定落在區間外，硬夾住的話開頭結尾就歪了。
        /// </summary>
        public float VariantSec(string name, float refSec)
        {
            if (cfg == null || cfg.variantMaps == null || string.IsNullOrEmpty(name))
                return refSec;
            for (int i = 0; i < cfg.variantMaps.Length; i++)
            {
                var m = cfg.variantMaps[i];
                if (m == null || m.name != name) continue;
                var R = m.refT; var O = m.outT;
                int n = (R == null || O == null) ? 0 : Mathf.Min(R.Length, O.Length);
                if (n == 0) return refSec;
                if (n == 1) return O[0] + (refSec - R[0]);
                if (refSec <= R[0])
                    return O[0] + (refSec - R[0]) * Slope(R[0], O[0], R[1], O[1]);
                for (int k = 0; k < n - 1; k++)
                    if (refSec <= R[k + 1])
                        return O[k] + (refSec - R[k]) * Slope(R[k], O[k], R[k + 1], O[k + 1]);
                return O[n - 1] + (refSec - R[n - 1])
                       * Slope(R[n - 2], O[n - 2], R[n - 1], O[n - 1]);
            }
            return refSec;
        }

        /// <summary>
        /// VariantSec 的反函數：指定配音版本的秒數 → 主配音的秒數。
        ///
        /// 過場快轉時需要它 —— 影片的秒數是主配音那一套，音訊播到哪卻是這一版的，
        /// 兩邊要對齊就得能雙向換算。沒有對照表時原樣回傳，跟 VariantSec 一致。
        /// </summary>
        public float MainSec(string name, float outSec)
        {
            if (cfg == null || cfg.variantMaps == null || string.IsNullOrEmpty(name))
                return outSec;
            for (int i = 0; i < cfg.variantMaps.Length; i++)
            {
                var m = cfg.variantMaps[i];
                if (m == null || m.name != name) continue;
                var R = m.refT; var O = m.outT;
                int n = (R == null || O == null) ? 0 : Mathf.Min(R.Length, O.Length);
                if (n == 0) return outSec;
                if (n == 1) return R[0] + (outSec - O[0]);
                if (outSec <= O[0])
                    return R[0] + (outSec - O[0]) * Slope(O[0], R[0], O[1], R[1]);
                for (int k = 0; k < n - 1; k++)
                    if (outSec <= O[k + 1])
                        return R[k] + (outSec - O[k]) * Slope(O[k], R[k], O[k + 1], R[k + 1]);
                return R[n - 1] + (outSec - O[n - 1])
                       * Slope(O[n - 2], R[n - 2], O[n - 1], R[n - 1]);
            }
            return outSec;
        }

        static float Slope(float t0, float a0, float t1, float a1)
        {
            float dt = t1 - t0;
            if (Mathf.Abs(dt) < 1e-6f) return 1f;
            return (a1 - a0) / dt;
        }

        int FindTrack(float t)
        {
            if (cfg == null || cfg.tracks == null) return -1;
            for (int i = 0; i < cfg.tracks.Length; i++)
            {
                var tr = cfg.tracks[i];
                if (tr == null || tr.mute) continue;
                if (t >= tr.from && (tr.to <= tr.from || t < tr.to)) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ 路徑

        public static string GameRoot()
        {
            try { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
            catch { return ""; }
        }

        public string ResolveAudio(AudioTrack tr)
        {
            string p = tr.audio;
            if (string.IsNullOrEmpty(p)) return "";
            if (p == "@" || p.StartsWith("@"))
            {
                string want = p.Length > 1 ? p.Substring(1) : cfg.activeVariant;
                p = VariantFile(want);
                if (string.IsNullOrEmpty(p)) return "";
            }
            return ResolvePath(p, cfg.audioRoot);
        }

        public string ResolvePath(string p, string root)
        {
            if (string.IsNullOrEmpty(p)) return "";
            if (cfg != null && cfg.pathMode == "rel")
            {
                string g = GameRoot();
                string r = string.IsNullOrEmpty(root) ? "" : root;
                return Path.GetFullPath(Path.Combine(Path.Combine(g, r), p));
            }
            return p;
        }

        public string VariantFile(string name)
        {
            if (cfg == null || cfg.variantNames == null || cfg.variantFiles == null) return "";
            for (int i = 0; i < cfg.variantNames.Length && i < cfg.variantFiles.Length; i++)
                if (cfg.variantNames[i] == name) return cfg.variantFiles[i];
            // 找不到就用第一個
            if (cfg.variantFiles.Length > 0) return cfg.variantFiles[0];
            return "";
        }

        // ------------------------------------------------------------------ 載入

        IEnumerator LoadRoutine(string path)
        {
            loading = true;
            if (clip != null) { src.Stop(); src.clip = null; UnityEngine.Object.Destroy(clip); clip = null; }
            loadedPath = "";

            if (!File.Exists(path))
            {
                Status = Lang.T("找不到音檔: ") + path;
                loading = false;
                yield break;
            }

            string ext = Path.GetExtension(path).ToLower();
            AudioType at;
            bool stream;
            if (ext == ".wav") { at = AudioType.WAV; stream = false; }
            else if (ext == ".ogg") { at = AudioType.OGGVORBIS; stream = true; }
            else
            {
                Status = Lang.T("音檔格式不支援（只吃 .wav / .ogg）: ") + ext;
                loading = false;
                yield break;
            }

            var www = new WWW("file:///" + path.Replace('\\', '/'));
            while (!www.isDone) yield return null;
            if (!string.IsNullOrEmpty(www.error))
            {
                Status = Lang.T("載入失敗: ") + www.error;
                loading = false;
                yield break;
            }

            clip = www.GetAudioClip(false, stream, at);
            float t0 = Time.realtimeSinceStartup;
            while (clip != null && clip.loadState == AudioDataLoadState.Loading)
            {
                if (Time.realtimeSinceStartup - t0 > 60f) break;
                yield return null;
            }

            if (clip == null || clip.loadState == AudioDataLoadState.Failed)
            {
                Status = Lang.T("解碼失敗: ") + Path.GetFileName(path);
                clip = null;
                loading = false;
                yield break;
            }

            src.clip = clip;
            loadedPath = path;
            curTrack = -1;              // 強迫下一幀重新對位
            Status = Lang.T("已載入 ") + Path.GetFileName(path)
                     + "  " + clip.length.ToString("F1") + "s / " + clip.frequency + "Hz";
            loading = false;
        }

        public string LoadedName()
        {
            return string.IsNullOrEmpty(loadedPath) ? Lang.T("(無)") : Path.GetFileName(loadedPath);
        }
    }
}
