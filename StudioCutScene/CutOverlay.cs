using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;

namespace StudioCutScene
{
    /// <summary>
    /// 全螢幕過場播放器。用 IMGUI 畫（GUI.depth 壓到最上層），
    /// 不依賴 UnityEngine.UI，也不用 RenderTexture（VideoPlayer 走 APIOnly）。
    ///
    /// 注意：Koikatsu 是 Unity 5.6，VideoPlayer 的成員比新版少很多。
    /// 這個檔案只直接用 5.6 確定有的成員；其餘（例如 loopPointReached）
    /// 一律走反射，缺了就自動退回偵測法，不會編不過。
    /// </summary>
    public class CutOverlay : MonoBehaviour
    {
        public static CutOverlay Instance;

        VideoPlayer vp;
        AudioSource src;
        AudioClip clip;

        // 色彩模式：0=原樣  1=RT/Linear  2=RT/sRGB  3=sRGBWrite(繪製時補回 linear→sRGB)
        RenderTexture rt;
        PropertyInfo srgbWriteProp;
        int colorMode;
        int rtW = 1920, rtH = 1080;

        public bool Visible;
        public float Alpha;
        public string Status = "";
        public bool SkipRequested;
        public bool IsBusy;

        /// <summary>
        /// 這一段影片能拉到哪個區間（播放中由播放協程填）。
        /// hi &lt;= lo 代表沒有上界（整支片、長度不明）。
        /// </summary>
        float clipLo, clipHi;

        /// <summary>
        /// 過場**播放中**的快轉／倒轉：只動影片和它的音軌，不碰 timeline。
        ///
        /// 為什麼要分開：動畫還在播的時候去 seek timeline，畫面上看到的還是
        /// 影片、但底下的場景已經被拉到後面去了，動畫播完就接在錯的地方。
        /// 播放中要動的是「這支影片播到哪」，不是「場景演到哪」。
        /// </summary>
        public bool Nudge(float delta)
        {
            if (!IsBusy || vp == null) return false;
            try
            {
                double before = vp.time;
                double t = before + delta;
                if (t < clipLo) t = clipLo;
                if (clipHi > clipLo && t > clipHi) t = clipHi;
                vp.time = t;
                float moved = (float)(t - before);

                // 有獨立音軌時它是主時鐘，不跟著挪的話下一次同步會把影片拉回去
                if (src != null && src.clip != null && src.isPlaying)
                {
                    float st = (float)(t - clipLo);
                    if (st < 0f) st = 0f;
                    if (st > src.clip.length - 0.05f) st = Mathf.Max(0f, src.clip.length - 0.05f);
                    src.time = st;
                }
                else
                {
                    // 過場的聲音沿用場景音軌時，主時鐘是 TrackPlayer 那條，不是這裡的 src。
                    // 以前只挪了影片、沒挪它 —— 症狀就是「過場中快轉，影音就對不上」，
                    // 而且沒有任何東西會把它們拉回來（自由播期間兩邊都是各跑各的）。
                    var tp = TrackPlayer.Instance;
                    if (tp != null && tp.FreeRunning && TrackBaseMain >= 0f)
                        tp.FreeRunSeekMain(TrackBaseMain + (float)(t - clipLo));
                }

                // 快轉就是要早點看完。durationSec 是用「真實經過秒數」算的，
                // 不扣掉跳過的量的話，影片已經往前一秒、過場卻還是演滿原本的長度，
                // 結果是最後那一秒的畫面被砍掉、音訊還多跑一秒到下一段去。
                nudgeAccum += moved;

                // 影音對齊靠播放迴圈那段「每秒最多校一次、差 0.2 秒以上才校」的邏輯。
                // 跳完之後不能等它 —— VideoPlayer 的 seek 是非同步而且不精準，
                // 兩邊落地的位置本來就會差一點，等一秒就是聽得出來的不同步。
                // 舉旗子叫迴圈這一幀就以音訊為準把影片拉齊。
                nudgeResync = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CutScene] Nudge: " + e.Message);
                return false;
            }
        }

        /// <summary>剛跳過，播放迴圈要立刻重新對齊影音（見 Nudge）。</summary>
        bool nudgeResync;

        /// <summary>這一段過場總共被快轉／倒轉了幾秒（正 = 往前）。</summary>
        float nudgeAccum;

        /// <summary>
        /// 過場的聲音沿用場景音軌時，音訊是從**主配音**的第幾秒開始放的。
        /// 影片的秒數跟主配音是同一套，所以
        /// 　　音訊現在的主配音秒數 − TrackBaseMain ＝ 影片從開播到現在走了幾秒
        /// 這個等式就是過場期間影音對齊的依據。負數 = 這一段不是用音軌配音。
        /// </summary>
        public float TrackBaseMain = -1f;

        /// <summary>IsBusy 期間每幀更新。外面用它判斷協程是不是死掉了。</summary>
        public float Heartbeat;

        // --- 給外部 VR 插件用的唯讀接口 ---
        //
        // IMGUI 畫的東西進不了頭顯的眼睛貼圖，所以 VR 要另外用世界空間的板子來畫。
        // 那件事**不在這支插件裡** —— 它在獨立的 Studio VR Tools（F9）。
        // 這裡只負責把「現在該畫什麼」攤開來給它拿，不反過來依賴它：
        // 沒裝 VR 插件的話，下面這幾個就只是沒人讀而已，桌面播放完全不受影響。
        //
        // 之所以是屬性而不是直接把欄位開公開：DisplayTexture() 會隨色彩模式
        // 在 RenderTexture 和 VideoPlayer.texture 之間切換，外面不該知道這件事。

        /// <summary>這一幀有沒有東西要畫。</summary>
        public bool VrWantsDraw { get { return Visible && Alpha > 0.001f; } }

        /// <summary>目前要畫的貼圖（已經處理過色彩模式）。</summary>
        public Texture VrTexture { get { return DisplayTexture(); } }

        /// <summary>目前的淡入淡出值。</summary>
        public float VrAlpha { get { return Alpha; } }

        /// <summary>影片的長寬比。拿不到就回 16:9。</summary>
        public float VrAspect
        {
            get
            {
                // 尺寸從**貼圖**拿，不從 VideoPlayer 拿。
                // Unity 5.6 的 VideoPlayer 還沒有 width / height 這兩個屬性
                // （那是 2017 之後才加的），寫 vp.width 直接編譯不過。
                // Texture 從第一版 Unity 就有 width / height，不會有版本問題。
                try
                {
                    // 優先用影片自己的輸出貼圖 —— 那是原始影片尺寸。
                    Texture src = vp != null ? vp.texture : null;
                    if (src != null && src.width > 0 && src.height > 0)
                        return (float)src.width / (float)src.height;

                    // 退而求其次用實際顯示的那張。色彩模式 1/2 走 RenderTexture，
                    // 那張是固定尺寸的，長寬比不一定等於原片，所以只當備援。
                    Texture shown = DisplayTexture();
                    if (shown != null && shown.width > 0 && shown.height > 0)
                        return (float)shown.width / (float)shown.height;
                }
                catch { }
                return 16f / 9f;
            }
        }

        /// <summary>
        /// VR 插件畫了之後，可以把這個設起來讓桌面這一份不要重複畫。
        /// 預設 false = 桌面照樣畫（方便旁邊有人看、也方便除錯）。
        /// 由 VR 插件每幀推，沒裝就永遠是 false。
        /// </summary>
        public bool SuppressDesktop;

        CutEntry preparedFor;

        // 預載的「世代」。每開一次 PreloadRoutine 就 +1，舊的那個每次 yield 回來
        // 看到世代變了就自己退場 —— 兩個預載同時對同一台 VideoPlayer 下
        // url / Prepare / seek 的話，後面那個會把前面做好的整個重來一次。
        int preloadGen;
        // 正在預載哪一段、從什麼時候開始。同一段已經有人在載，就等它，不要重來。
        CutEntry loadingFor;
        float loadingSince;

        // 最近一次預載各花多久（秒），給「出畫面前等了多久」那一行 log 用
        float lastPrepareSec, lastSeekSec;

        // 共用影片：整支來源片只 Prepare 一次，之後每段過場只是 seek。
        // 這樣不必把影片切成一堆小檔，過場之間也不用重新載入。
        string preparedUrl = "";
        bool sharedMode;

        // loopPointReached / seekCompleted 的反射掛載
        EventInfo endEvent;
        Delegate endHandler;
        bool endFired;
        EventInfo seekEvent;
        Delegate seekHandler;
        bool seekDone;

        /// <summary>整支共用的來源影片（cfg.videoFile），由 plugin 設定。</summary>
        public string SharedFile = "";

        /// <summary>
        /// 影片「真的開始播的那一刻」才呼叫。音軌的自由播要掛在這裡，
        /// 不能在 RunCut 一開頭就啟動 —— 中間還隔著 Prepare / seek，
        /// 那段時間音訊會先跑掉，開場尤其明顯（開場永遠不會被預載）。
        /// </summary>
        public Action OnPlaybackStart;

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("StudioCutSceneOverlay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<CutOverlay>();
            Instance.Build();
        }

        void Build()
        {
            vp = gameObject.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.source = VideoSource.Url;
            vp.renderMode = VideoRenderMode.APIOnly;   // 預設；SetColorMode 會改
            vp.waitForFirstFrame = true;
            vp.isLooping = false;

            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.ignoreListenerPause = true;   // 關鍵：不受 AudioListener.pause 影響
            src.bypassEffects = true;
            src.bypassListenerEffects = true;

            // 影片播完事件：5.6 有沒有都不影響編譯
            try
            {
                endEvent = typeof(VideoPlayer).GetEvent("loopPointReached");
                if (endEvent != null)
                    endHandler = Delegate.CreateDelegate(endEvent.EventHandlerType, this, "OnVideoEnd");
            }
            catch { endEvent = null; endHandler = null; }

            try
            {
                seekEvent = typeof(VideoPlayer).GetEvent("seekCompleted");
                if (seekEvent != null)
                    seekHandler = Delegate.CreateDelegate(seekEvent.EventHandlerType, this, "OnSeekDone");
            }
            catch { seekEvent = null; seekHandler = null; }

            // GL.sRGBWrite：Unity 5.6 起才有，用反射避免編不過
            try
            {
                srgbWriteProp = typeof(GL).GetProperty("sRGBWrite",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch { srgbWriteProp = null; }
        }

        // 反射用，簽章要跟 VideoPlayer.EventHandler 一致
        void OnVideoEnd(VideoPlayer source) { endFired = true; }
        void OnSeekDone(VideoPlayer source) { seekDone = true; }

        void HookEnd(bool on)
        {
            if (endEvent == null || endHandler == null) return;
            try
            {
                if (on) endEvent.AddEventHandler(vp, endHandler);
                else endEvent.RemoveEventHandler(vp, endHandler);
            }
            catch { }
        }

        /// <summary>
        /// Koikatsu 跑 Linear color space，影片貼圖的 sRGB 旗標若標錯，
        /// gamma 會多做或少做一次 → 畫面偏暗偏紅（或整個發白）。
        /// 這裡沒有「正確答案」可以從程式判斷，所以做成三段讓使用者眼睛決定。
        /// </summary>
        public void SetColorMode(int mode, int w, int h)
        {
            if (w > 0) rtW = w;
            if (h > 0) rtH = h;
            colorMode = mode;

            if (mode == 0 || mode == 3)
            {
                vp.renderMode = VideoRenderMode.APIOnly;
                ReleaseRT();
                return;
            }

            bool wantSrgb = (mode == 2);
            if (rt == null || rt.width != rtW || rt.height != rtH || rtSrgb != wantSrgb)
            {
                ReleaseRT();
                rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32,
                        wantSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                rt.Create();
                rtSrgb = wantSrgb;
            }
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
        }

        bool rtSrgb;

        void ReleaseRT()
        {
            if (rt == null) return;
            try { vp.targetTexture = null; } catch { }
            try { rt.Release(); } catch { }
            try { UnityEngine.Object.Destroy(rt); } catch { }
            rt = null;
        }

        Texture DisplayTexture()
        {
            if (colorMode == 1 || colorMode == 2) return rt;
            return vp != null ? vp.texture : null;
        }

        public string ColorModeName()
        {
            if (colorMode == 1) return "Linear";
            if (colorMode == 2) return "sRGB";
            if (colorMode == 3) return srgbWriteProp != null ? "sRGBWrite" : "sRGBWrite(不支援)";
            return "原樣";
        }

        public bool SrgbWriteAvailable { get { return srgbWriteProp != null; } }

        void Update()
        {
            if (IsBusy) Heartbeat = Time.realtimeSinceStartup;
        }

        /// <summary>硬停。外面的看門狗或「強制復原」按鈕會呼叫。</summary>
        public void HardReset()
        {
            StopAllCoroutines();
            HookEnd(false);
            try { if (vp != null) vp.Stop(); } catch { }
            try { if (src != null) src.Stop(); } catch { }
            Visible = false;
            Alpha = 0f;
            IsBusy = false;
            SkipRequested = false;
            preparedFor = null;
            preparedUrl = "";
            loadingFor = null;
            preloadGen++;
            Status = "已強制復原";
        }

        // ------------------------------------------------------------------ 預載

        /// <summary>在到達切點前先把影片 Prepare、音訊讀好，避免播放瞬間卡住。</summary>
        public IEnumerator PreloadRoutine(CutEntry e)
        {
            if (e == null || preparedFor == e) yield break;

            // 同一段已經在預載（例如時間軸提前 3 秒開始載，載到一半就到了切點）
            // → 等它載完就好。以前這裡會再開一個，於是 Prepare / seek 從頭再來一次，
            // 那段等待就是播放前的卡頓。
            if (loadingFor == e && Time.realtimeSinceStartup - loadingSince < 15f)
            {
                while (loadingFor == e && preparedFor != e
                       && Time.realtimeSinceStartup - loadingSince < 15f)
                    yield return null;
                if (preparedFor == e) yield break;
            }

            int my = ++preloadGen;
            loadingFor = e;
            loadingSince = Time.realtimeSinceStartup;
            lastPrepareSec = 0f;
            lastSeekSec = 0f;
            preparedFor = null;

            // 每段自己的影片檔，或整支共用來源片（videoStart/videoEnd 指定區間）
            string file = !string.IsNullOrEmpty(e.video) ? e.video : SharedFile;
            sharedMode = string.IsNullOrEmpty(e.video) && e.videoStart >= 0f;

            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                Status = "找不到影片: " + file;
                yield break;
            }

            bool sep = !string.IsNullOrEmpty(e.audio);
            if (sep || e.useTrackAudio)
            {
                // useTrackAudio：聲音由 TrackPlayer 續播，影片本身不出聲
                vp.audioOutputMode = VideoAudioOutputMode.None;
            }
            else
            {
                vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
                vp.controlledAudioTrackCount = 1;
                vp.SetTargetAudioSource(0, src);
            }

            string url = ToUrl(file);
            if (url != preparedUrl || !vp.isPrepared)
            {
                Status = "預載影片…";
                vp.url = url;
                vp.Prepare();
                float t0 = Time.realtimeSinceStartup;
                while (!vp.isPrepared)
                {
                    if (Time.realtimeSinceStartup - t0 > 30f)
                    {
                        Status = "影片 Prepare 逾時";
                        if (my == preloadGen) loadingFor = null;
                        yield break;
                    }
                    yield return null;
                    if (my != preloadGen) yield break;       // 被新的預載取代
                }
                preparedUrl = url;
                lastPrepareSec = Time.realtimeSinceStartup - t0;

                if (!sep && !e.useTrackAudio)
                {
                    try { vp.EnableAudioTrack(0, true); }
                    catch (Exception ex) { Debug.LogWarning("[CutScene] EnableAudioTrack: " + ex.Message); }
                }
            }

            // 共用來源片就 seek 到這一段的起點；整支等待 seek 完成才算預載好
            // 已經停在這一段的起點（剛 Prepare 完的 0 秒、或上一次就定位好了）
            // 就不必再 seek —— seek 要等 seekCompleted，最多會等到 5 秒。
            if (sharedMode && vp.isPrepared && !vp.isPlaying
                && Mathf.Abs((float)vp.time - e.videoStart) < 0.02f)
            {
                // 不用動
            }
            else if (sharedMode)
            {
                Status = "定位到 " + e.videoStart.ToString("F2") + "s…";
                seekDone = false;
                bool hooked = false;
                if (seekEvent != null && seekHandler != null)
                {
                    try { seekEvent.AddEventHandler(vp, seekHandler); hooked = true; } catch { }
                }
                vp.time = e.videoStart;
                float t1 = Time.realtimeSinceStartup;
                // 有 seekCompleted 就等事件，沒有就固定等 0.4 秒
                while (Time.realtimeSinceStartup - t1 < (hooked ? 5f : 0.4f))
                {
                    if (hooked && seekDone) break;
                    yield return null;
                    if (my != preloadGen)
                    {
                        if (hooked) { try { seekEvent.RemoveEventHandler(vp, seekHandler); } catch { } }
                        yield break;
                    }
                }
                if (hooked) { try { seekEvent.RemoveEventHandler(vp, seekHandler); } catch { } }
                lastSeekSec = Time.realtimeSinceStartup - t1;
            }

            if (sep)
            {
                Status = "預載音訊…";
                yield return StartCoroutine(LoadClip(e.audio));
                if (my != preloadGen) yield break;
                if (clip == null) { loadingFor = null; yield break; }
                src.clip = clip;
            }

            if (my != preloadGen) yield break;
            preparedFor = e;
            loadingFor = null;
            Texture pt = DisplayTexture();
            Status = "已預載 " + (pt != null ? pt.width + "x" + pt.height : "(尚無影格)")
                     + "  色彩:" + ColorModeName()
                     + (sharedMode ? "  共用來源片" : "");
        }

        IEnumerator LoadClip(string path)
        {
            if (clip != null) { UnityEngine.Object.Destroy(clip); clip = null; }
            if (!File.Exists(path)) { Status = "找不到音訊: " + path; yield break; }

            string ext = Path.GetExtension(path).ToLower();
            AudioType at;
            bool stream;
            if (ext == ".ogg") { at = AudioType.OGGVORBIS; stream = true; }
            else if (ext == ".wav") { at = AudioType.WAV; stream = false; }
            else { Status = "音訊格式不支援（只吃 .ogg / .wav）: " + ext; yield break; }

            var www = new WWW(ToUrl(path));
            while (!www.isDone) yield return null;
            if (!string.IsNullOrEmpty(www.error)) { Status = "音訊載入失敗: " + www.error; yield break; }

            clip = www.GetAudioClip(false, stream, at);
            float t0 = Time.realtimeSinceStartup;
            while (clip != null && clip.loadState == AudioDataLoadState.Loading)
            {
                if (Time.realtimeSinceStartup - t0 > 30f) break;
                yield return null;
            }
        }

        // ------------------------------------------------------------------ 播放

        public IEnumerator PlayRoutine(CutEntry e, float fadeIn, float fadeOut)
        {
            IsBusy = true;
            Heartbeat = Time.realtimeSinceStartup;
            SkipRequested = false;
            Alpha = 0f;
            endFired = false;

            float tEnter = Time.realtimeSinceStartup;
            bool wasReady = preparedFor == e;
            float tReady = tEnter, tPlay = tEnter;
            bool firstLogged = false;

            try
            {
                if (preparedFor != e)
                {
                    var pre = PreloadRoutine(e);
                    while (pre.MoveNext()) yield return pre.Current;
                }
                if (preparedFor != e) yield break;   // 預載失敗，Status 會說原因
                tReady = Time.realtimeSinceStartup;

                bool sep = !string.IsNullOrEmpty(e.audio);

                HookEnd(true);
                Visible = true;
                {
                    var f = FadeTo(1f, fadeIn);
                    while (f.MoveNext()) yield return f.Current;
                }

                if (!sharedMode) vp.time = 0d;      // 共用來源片的位置在預載時就 seek 好了
                // 快轉／倒轉能拉的範圍。共用來源片只能在自己那一段裡動，
                // 整支片的情況拿不到長度（Unity 5.6 的 VideoPlayer 沒有 length），
                // 所以只擋下界，往後拉過頭就是自然播完。
                clipLo = sharedMode ? e.videoStart : 0f;
                clipHi = (sharedMode && e.videoEnd > e.videoStart) ? e.videoEnd : -1f;
                vp.Play();
                tPlay = Time.realtimeSinceStartup;
                if (OnPlaybackStart != null)
                {
                    try { OnPlaybackStart(); } catch (Exception ex)
                    { Debug.LogWarning("[CutScene] OnPlaybackStart: " + ex.Message); }
                }
                if (sep) { src.time = 0f; src.Play(); }

                float started = Time.realtimeSinceStartup;
                float notPlaying = 0f;
                float lastSync = 0f;
                nudgeAccum = 0f;
                // 音軌自由播的那條主時鐘。沒有就是 null，底下的對齊整段跳過。
                TrackPlayer clock = (!sep && TrackBaseMain >= 0f
                                     && TrackPlayer.Instance != null
                                     && TrackPlayer.Instance.FreeRunning)
                                    ? TrackPlayer.Instance : null;

                while (true)
                {
                    if (SkipRequested) { Status = "已跳過"; break; }
                    if (endFired) { Status = "播放結束"; break; }

                    float el = Time.realtimeSinceStartup - started;
                    float dt = Time.unscaledDeltaTime;

                    // 共用來源片：播到區間終點就收
                    if (sharedMode && e.videoEnd > e.videoStart
                        && (float)vp.time >= e.videoEnd - 0.02f) { Status = "播放結束"; break; }

                    // json 給了長度就以它為準，最準也最省事。
                    // 快轉／倒轉過就要扣掉跳過的量，不然快轉等於白跳。
                    if (e.durationSec > 0f && el >= e.durationSec - nudgeAccum) { Status = "播放結束"; break; }

                    if (sep)
                    {
                        if (el > 0.3f && !src.isPlaying) break;
                        // 音訊是主時鐘。seek 很傷，門檻放寬 + 每秒最多校一次。
                        // 但剛被 Nudge 跳過的話要立刻校 —— 不然那一秒聽起來就是不同步。
                        bool forced = nudgeResync;
                        if (forced) nudgeResync = false;
                        if (vp.isPrepared
                            && (forced
                                || (el - lastSync > 1f
                                    && Mathf.Abs((float)vp.time - src.time) > 0.20f)))
                        {
                            vp.time = clipLo + src.time;
                            lastSync = el;
                        }
                    }
                    else
                    {
                        // 過場的聲音沿用場景音軌 → 那條音軌是主時鐘，影片跟著它走。
                        // 自由播期間兩邊本來各跑各的：正常播不會差，但只要快轉過一次，
                        // VideoPlayer 的 seek 又是非同步、落地位置不精準，
                        // 差出來的那一點就再也沒人收得回來。這裡讓影片每秒對一次音訊。
                        bool forced = nudgeResync;
                        if (forced) nudgeResync = false;
                        if (clock != null && clock.FreeRunning)
                        {
                            float want = clipLo + (clock.FreeRunMainSec() - TrackBaseMain);
                            if (clipHi > clipLo && want > clipHi) want = clipHi;
                            if (want < clipLo) want = clipLo;
                            if (vp.isPrepared
                                && (forced
                                    || (el - lastSync > 1f
                                        && Mathf.Abs((float)vp.time - want) > 0.20f)))
                            {
                                vp.time = want;
                                lastSync = el;
                            }
                        }

                        // 解碼卡住時 isPlaying 會瞬間變 false，
                        // 所以要連續一段時間都沒在播才認定結束
                        if (vp.isPlaying) notPlaying = 0f;
                        else if (el > 0.5f) notPlaying += dt;
                        if (notPlaying > 1.0f) { Status = "影片停了 1 秒，判定結束"; break; }
                    }

                    if (e.maxSeconds > 0f && el > e.maxSeconds) { Status = "到達 maxSeconds"; break; }

                    // 畫面真的開始動的那一刻，記一行「從觸發到出畫面等了多久」。
                    // 只有明顯久（> 0.5 秒）才記，平常不吵。
                    if (!firstLogged && vp.isPlaying && (float)vp.time > clipLo + 0.03f)
                    {
                        firstLogged = true;
                        float now = Time.realtimeSinceStartup;
                        float total = now - tEnter;
                        if (total > 0.5f)
                            Debug.LogWarning("[CutScene] 過場 " + e.kind + " @" + e.t.ToString("F2")
                                + "：觸發到畫面開始動花了 " + total.ToString("F2") + " 秒"
                                + "（" + (wasReady ? "已預載好" :
                                    "當場預載 " + (tReady - tEnter).ToString("F2") + " 秒："
                                    + "Prepare " + lastPrepareSec.ToString("F2")
                                    + "、定位 " + lastSeekSec.ToString("F2"))
                                + "；淡入 " + (tPlay - tReady).ToString("F2")
                                + "；Play 到第一格 " + (now - tPlay).ToString("F2") + "）");
                    }

                    Status = "播放中 " + ((float)vp.time).ToString("F1") + " s"
                             + (vp.isPlaying ? "" : "  [stall]");
                    yield return null;
                }

                {
                    var f = FadeTo(0f, fadeOut);
                    while (f.MoveNext()) yield return f.Current;
                }
            }
            finally
            {
                OnPlaybackStart = null;
                HookEnd(false);
                // 共用來源片用 Pause，保住 prepared 狀態，下一段過場只要 seek
                try { if (sharedMode) vp.Pause(); else { vp.Stop(); preparedUrl = ""; } } catch { }
                try { src.Stop(); } catch { }
                Visible = false;
                Alpha = 0f;
                preparedFor = null;
                IsBusy = false;
            }
        }

        IEnumerator FadeTo(float target, float dur)
        {
            if (dur <= 0f) { Alpha = target; yield break; }
            float from = Alpha;
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;       // timeScale = 0 時照樣走
                Alpha = Mathf.Lerp(from, target, Mathf.Clamp01(t / dur));
                yield return null;
            }
            Alpha = target;
        }

        public void Stop()
        {
            SkipRequested = true;
        }

        // ------------------------------------------------------------------ 繪製

        void OnGUI()
        {
            if (!Visible || Alpha <= 0.001f) return;
            if (Event.current.type != EventType.Repaint) return;
            // VR 插件如果已經在頭顯裡畫了，可以要求桌面這一份不要重複畫 ——
            // 少畫一次全螢幕貼圖，多少省一點。預設是照樣畫。
            if (SuppressDesktop) return;

            GUI.depth = -5000;
            Color prev = GUI.color;
            Rect full = new Rect(0f, 0f, Screen.width, Screen.height);

            GUI.color = new Color(0f, 0f, 0f, Alpha);
            GUI.DrawTexture(full, Texture2D.whiteTexture);

            Texture tex = DisplayTexture();
            if (tex != null)
            {
                // colorMode 3：引擎把影片貼圖做了一次 sRGB→Linear 卻沒轉回去，
                // 這裡在寫入畫面時補上 Linear→sRGB，一來一回抵銷。
                bool restore = false;
                object prevSrgb = null;
                if (colorMode == 3 && srgbWriteProp != null)
                {
                    try
                    {
                        prevSrgb = srgbWriteProp.GetValue(null, null);
                        srgbWriteProp.SetValue(null, true, null);
                        restore = true;
                    }
                    catch { restore = false; }
                }

                GUI.color = new Color(1f, 1f, 1f, Alpha);
                GUI.DrawTexture(full, tex, ScaleMode.ScaleToFit, false);

                if (restore)
                {
                    try { srgbWriteProp.SetValue(null, prevSrgb, null); } catch { }
                }
            }

            GUI.color = prev;
        }

        static string ToUrl(string path)
        {
            return "file:///" + path.Replace('\\', '/');
        }
    }
}
