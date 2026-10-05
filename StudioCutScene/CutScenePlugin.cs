using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace StudioCutScene
{
    [Serializable]
    public class CutEntry
    {
        public float t;             // 觸發時間（Timeline 秒數）
        public string video = "";   // 影片絕對路徑
        public string audio = "";   // 選填：獨立音訊 (.ogg / .wav)。留空就用影片自帶音軌
        public bool skip;           // 單獨關掉這一段
        public float maxSeconds;    // 選填：保險用的硬上限，0 = 不限
        public float durationSec;   // 選填：影片長度（秒）。填了結束判定最準
        public string kind = "";    // "opening" | "transition"（預設） | "ending"

        // 過場的聲音就是原始音檔在那一段的內容，所以預設「不暫停音軌、讓它自由播完」，
        // 不需要另外切過場音訊檔。影片本身用 -an 不帶音軌即可。
        public bool useTrackAudio = true;
        public float audioStart = -1f;    // >=0 才 seek（開場要跳回 0）；-1 = 從目前位置接著播
        public float fadeInOv = -1f;      // 每段自己的淡入淡出，-1 = 用全域值
        public float fadeOutOv = -1f;

        // 共用來源片的區間（秒）。video 留空時才用，videoStart >= 0 才生效。
        public float videoStart = -1f;
        public float videoEnd = -1f;

        // 多張卡接成一張時用（各卡的影片、音檔不合併）：
        //   source —— 這一段的「共用來源片」。空的就用設定檔最上層的 videoFile。
        //             跟 video 不一樣：video 是整支播完，source 只播 videoStart～videoEnd。
        //   track  —— 過場期間沿用哪一條音軌的音檔（tracks 的索引）。
        //             -1 = 目前載入的那一個（單一音檔的設定檔一直是這樣）。
        public string source = "";
        public int track = -1;

        [NonSerialized] public bool done;
    }

    [Serializable]
    public class AudioTrack
    {
        public float from;          // Timeline 起（秒）
        public float to;            // Timeline 迄（秒）；<= from 表示到最後
        public string audio = "@";  // "@" = 用目前配音版本；"@名稱" = 指定版本；其餘 = 直接當路徑
        public float offset;        // 音檔中對應 from 的位置（秒）。沒有 anchors 時用這個
        public float volume = 1f;
        public bool mute;

        // 對應點：anchorT[i] 這個 timeline 時間，對應到音檔的 anchorA[i]。
        // 兩點以上就用折線內插，等於用折線逼近 ∫dt/timeScale。
        // 場景有「時間流速」軌道時，非用不可 —— 否則誤差會一路累積。
        public float[] anchorT = new float[0];
        public float[] anchorA = new float[0];

        // 這一段自己的配音檔。多張卡接成一張、各卡的音檔不合併時用：
        // 每一段是原本那張卡的音檔，播到哪一段就換哪個檔。
        //   fileNames[i] 這個配音版本 → filePaths[i] 這個檔
        // 有填的話 "@" / "@名稱" 就在這裡找（找不到那個名稱就用第一個），
        // 配音對照表也改用這一段自己的 maps —— 最上層的 variantFiles / variantMaps
        // 是另一個音檔的秒數，套過來一定錯。
        public string[] fileNames = new string[0];
        public string[] filePaths = new string[0];
        public VariantMap[] maps = new VariantMap[0];

        public bool HasFiles
        {
            get { return fileNames != null && filePaths != null
                         && fileNames.Length > 0 && filePaths.Length >= fileNames.Length; }
        }

        /// <summary>這一段實際會用哪個配音名稱：有那個名稱就用它，沒有就用第一個。</summary>
        public string PickName(string want)
        {
            if (!HasFiles) return want;
            for (int i = 0; i < fileNames.Length; i++)
                if (fileNames[i] == want) return want;
            return fileNames[0];
        }

        public string FileFor(string want)
        {
            if (!HasFiles) return "";
            for (int i = 0; i < fileNames.Length; i++)
                if (fileNames[i] == want) return filePaths[i];
            return filePaths[0];
        }

        public bool HasName(string want)
        {
            if (!HasFiles) return false;
            for (int i = 0; i < fileNames.Length; i++)
                if (fileNames[i] == want) return true;
            return false;
        }
    }

    /// <summary>
    /// 一個配音版本相對「主配音」的時間對照。
    ///
    /// 為什麼需要：anchors 是 timeline → **主配音**的秒數。作者放出的各個配音
    /// 版本如果是同一套剪輯重新上音，那每一版的秒數都一樣，一份 anchors 通吃。
    /// 但偶爾其中一版沒跟著做最後的剪輯（少了開場、中間多剪掉一塊…），
    /// 那一版的秒數就跟主配音對不上，而且**不是固定偏移** ——
    /// 剪掉的地方不同，差距會一路變。
    ///
    /// 所以這裡再疊一層折線：主配音秒數 → 這一版的秒數。
    ///     refT[i] 秒（主配音） 對應到 outT[i] 秒（這一版）
    /// 兩點以上折線內插，一點就是純平移，沒有就是完全同步（＝舊卡的行為）。
    ///
    /// 刻意不做成「每個配音各一整套 anchors」：慢動作那種形狀已經在 anchors 裡了，
    /// 各版共用才不會改一個地方要改三份；而且這樣每一版只要量一兩個點。
    /// </summary>
    [Serializable]
    public class VariantMap
    {
        public string name = "";
        public float[] refT = new float[0];
        public float[] outT = new float[0];
    }

    [Serializable]
    public class CutConfig
    {
        public bool enabled = true;
        public bool useOpening = true;      // 開場動畫
        public bool useTransitions = true;  // 場景之間的過場
        public bool useEnding = true;       // 片尾
        public float fadeIn = 0.25f;
        public float fadeOut = 0.35f;
        public float preloadLead = 3f;   // 提前幾秒開始預載
        public float window = 0.5f;      // 超過切點多久就不補播（防止 seek 誤觸）
        public bool freezeTimeScale = true;

        // 時間軸播到底、自己繞回 0 的時候要不要再播一次。
        // 預設 false：Timeline 本身會循環，不擋的話整部會無限重播，
        // 而且開場動畫每輪都跟著重來一次。要連續看就自己開。
        public bool autoReplay = false;

        // 0 = 只有插件啟動的播放才會觸發過場（預設）
        // 1 = 時間軸一往前走就觸發（舊行為）
        //
        // 為什麼預設是 0：在 Timeline 面板裡調東西、拉進度條、按播放試看動作，
        // 是編輯時最常做的事，每次都被過場影片蓋住畫面根本沒辦法工作。
        // 過場是「放給人看」的時候才要，那個時機由插件自己決定最準。
        public int triggerMode = 0;
        public int colorMode = 3;        // 0=原樣 1=RT/Linear 2=RT/sRGB 3=sRGBWrite(正解)
        public int videoWidth = 1920;    // colorMode 1/2 用的 RenderTexture 尺寸
        public int videoHeight = 1080;

        // --- 路徑 ---
        public string pathMode = "abs";              // "abs" | "rel"（rel 以遊戲根目錄為基準）
        public string audioRoot = "UserData\\audio";
        public string videoRoot = "UserData\\cutscene";
        public string videoFile = "";   // 整支來源影片；各段用 videoStart/videoEnd 指定區間

        // --- 配音版本（用兩個平行陣列，比 Dictionary 好手改）---
        public string[] variantNames = new string[0];
        public string[] variantFiles = new string[0];
        public string activeVariant = "";

        // anchors 和 cuts 的秒數是以「哪一個配音版本」為準的。空的話就是第一個。
        // 只有這一版的秒數可以直接拿去播；其他版要先經過 variantMaps 換算。
        public string refVariant = "";
        public VariantMap[] variantMaps = new VariantMap[0];

        // --- 音軌（取代 VNSound）---
        public bool tracksEnabled = true;
        public float jumpThreshold = 0.35f; // 一幀內時間軸跳超過這麼多秒 → 判定使用者拉了進度條
        public float panicSeek = 1.5f;      // 漂到這個地步只好硬跳（正常不該發生）
        public float hardSeek = 0.35f;      // 舊名字，留著相容
        public float slewDeadzone = 0.03f;  // 小於這個就不管
        public float slewMax = 0.02f;       // pitch 微調上限（±2%，聽不出來）
        public float slewGain = 0.15f;
        public float trackVolume = 1f;
        public bool logSeeks = false;       // 把重新定位寫進 BepInEx 主控台（預設關，很吵）

        // Unity 把 Time.deltaTime 卡在 Time.maximumDeltaTime（預設 0.333 秒）。
        // 遊戲卡頓 1.5 秒時 Timeline 只認 0.333 秒 —— 憑空丟掉 1.17 秒，
        // 但音訊走音效卡時鐘不會丟，於是每次卡頓都累積一次偏差。
        // 調大之後卡多久就認多久，場景往前跳一下補回來，音訊完全不用動。
        // 0 = 不要動這個設定（預設）。
        //
        // 注意：開大是有代價的。停頓 1.5 秒的那一幀 deltaTime 真的變成 1.5 秒之後，
        // Unity 會在同一幀內補跑 75 次 FixedUpdate（1.5 / 0.02），物理與動畫一次追完，
        // 本身又造成一次更長的停頓。重卡片上實測反而更不同步，所以預設不動。
        public float maxDeltaTime = 0f;
        public AudioTrack[] tracks = new AudioTrack[0];

        public CutEntry[] cuts = new CutEntry[0];
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class CutScenePlugin : BaseUnityPlugin
    {
        public const string GUID = "reze.studio.cutscene";
        public const string NAME = "Studio CutScene";
        public const string VERSION = "1.14.0";

        // 對應折線只擋「倒退」，不擋「陡」。
        // 斜率 = 該處的 1/timeScale：卡片把時間流速調到 0.03（近乎定格）時
        // 斜率就是 33，那是真的。把陡的點當成錯誤砍掉，等於把那段影片時間
        // 憑空抹掉，後面全部提早，聽起來就是「語音落後畫面好幾秒」。
        public const float ANCHOR_SLOPE_MIN = 0f;
        public const float ANCHOR_SLOPE_MAX = 50f;

        public static CutScenePlugin Self;

        // --- 設定 ---
        CutConfig cfg = new CutConfig();
        string lastPathFile = "";
        string pathInput = "";
        // 真正載入成功的那一份。
        //
        // 血淚：自動載入原本用「best != pathInput」判斷要不要載入。
        // 但 pathInput 只是輸入框的內容 —— 開遊戲時會先從 last.txt 填回去卻**不載入**
        // （1.8.1 刻意這樣做）。於是第一次開的卡如果剛好就是上次那份，
        // best 等於 pathInput，判斷成「已經載入了」直接跳過，實際上什麼都沒載。
        // 開第二張卡因為檔名不同才會正常 —— 症狀完全吻合。
        string loadedPath = "";
        bool[] variantOk;           // 每個配音檔在不在（載入時查一次）
        float[] variantLen;         // 每個配音檔的秒數（WAV 讀檔頭；讀不到 = -1）
        bool videoOk = true;
        string message = "尚未載入設定檔。";

        // --- 狀態 ---
        bool show;
        bool running;
        bool armed;      // 跟播中：只有這時候才會觸發過場（triggerMode 0）

        /// <summary>
        /// 跳過所有動畫。只擋**自動觸發**那一條路 ——
        /// 段落清單上的 test 是「我現在就要看這一支」，那是明確的動作，不該被總開關吃掉。
        /// 刻意不寫進 cutscene.json：這是「這一次要不要看」的臨時開關，不是卡片的設定。
        /// </summary>
        bool skipAllCuts;

        float nextTint, toolbarRetryUntil, toolbarNextTry;
        bool toolbarShown = true;

        /// <summary>
        /// 顯示／隱藏工具列按鈕，並把「按下去變綠色」改成黃色。
        ///
        /// 綠色在頭顯裡通常代表 Passthrough，撞在一起會誤會。
        /// KKAPI 只在狀態改變時上色，所以每 0.5 秒補塗一次就夠，不必每幀。
        ///
        /// 開頭三十秒要重複套用顯示狀態：KKAPI 的工具列是等工作室載完才排版的，
        /// 我們在 Awake 建按鈕、設定又是關的時候，第一次 SetVisible 很可能
        /// 打在還不存在的控制項上，然後按鈕就這樣冒出來了。
        /// </summary>
        void TickToolbar()
        {
            bool want = cfgToolbarButton == null || cfgToolbarButton.Value;
            bool changed = toolbarShown != want;
            bool retry = !want && Time.realtimeSinceStartup < toolbarRetryUntil
                         && Time.realtimeSinceStartup >= toolbarNextTry;
            if (changed || retry)
            {
                toolbarShown = want;
                toolbarNextTry = Time.realtimeSinceStartup + 0.5f;
                ToolbarButton.SetVisible(want);
            }

            if (Time.realtimeSinceStartup >= nextTint)
            {
                nextTint = Time.realtimeSinceStartup + 0.5f;
                ToolbarButton.TintToggled(new Color(1f, 0.82f, 0.2f, 1f));
            }
        }

        // ------------------------------------------------------------ 來自 F9 的指令

        /// <summary>
        /// 聽 F9（VR Tools）用手柄送過來的東西。
        ///
        /// 為什麼由這邊決定「暫停還是跳過」：F9 只知道你按了那組鍵，
        /// 不知道現在是不是正在播過場。那個狀態在這裡最準，
        /// 所以手柄只送一個 "pause-or-skip"，怎麼解讀由我們自己判斷。
        /// </summary>
        void TickVrLink()
        {
            string cmd = VrLink.TakeCommand();
            if (!string.IsNullOrEmpty(cmd)) RunCommand(cmd);

            if (VrLink.TakeSaveRequest()) SaveViewpointHere();
            TickViewFollow();
        }

        // ------------------------------------------------------------ VR 視角

        int viewScene = -1;         // 目前算出來在第幾段
        int viewApplied = -1;       // 已經套用過哪一段的視角
        float viewHoldUntil;        // 這個時間點之前不自動套視角（剛換卡的緩衝）

        /// <summary>
        /// 現在播到第幾段場景 —— 也就是面板上「場景 N 開始」的 N−1。
        ///
        /// 合併場景卡的 (1) (2) 這些段落，在時間軸上就是一段一段的音軌，
        /// 所以音軌索引正好可以當「第幾個場景」用，不必另外去猜資料夾結構。
        /// 沒有音軌的卡片一律算第 0 段 —— 整張卡就一個視角。
        /// </summary>
        int CurrentSceneIndex()
        {
            // ---- 第一優先：音軌 ----
            // 有音軌的卡片，一段音軌就是一段場景，面板上那幾列「場景 N 開始」用的也是它。
            if (cfg != null && cfg.tracks != null && cfg.tracks.Length > 0)
            {
                float t = TimelineBridge.Ready ? TimelineBridge.GetTime() : -1f;
                if (t < 0f) return 0;
                for (int i = 0; i < cfg.tracks.Length; i++)
                {
                    AudioTrack tr = cfg.tracks[i];
                    float to = tr.to > tr.from ? tr.to : float.MaxValue;
                    if (t >= tr.from - 0.01f && t < to) return i;
                }
                return cfg.tracks.Length - 1;      // 超出最後一段就算最後一段
            }

            // ---- 退路：用過場切點分段 ----
            // 沒有音軌但有過場的卡片（合併了好幾張卡、只是沒配音）。
            // transition 本來就長在兩段場景的接縫上，拿它當分界線剛好。
            // 第 0 段是「第一個 transition 之前」，所以段數 = transition 數 + 1。
            float[] marks = TransitionMarks();
            if (marks.Length > 0)
            {
                float t2 = TimelineBridge.Ready ? TimelineBridge.GetTime() : -1f;
                if (t2 < 0f) return 0;
                int idx = 0;
                for (int i = 0; i < marks.Length; i++)
                    if (t2 >= marks[i] - 0.01f) idx = i + 1;
                return idx;
            }

            // ---- 都沒有：整張卡就一段 ----
            // 單一場景、或本身沒音軌也沒過場的卡片。存的就是唯一的那一個視角，
            // 載入這張卡時直接套用。
            return 0;
        }

        /// <summary>過場（transition）的時間點，由小到大。沒有就回空陣列。</summary>
        float[] TransitionMarks()
        {
            if (cfg == null || cfg.cuts == null || cfg.cuts.Length == 0) return EmptyF;
            var list = new System.Collections.Generic.List<float>();
            foreach (var c in cfg.cuts)
                if (c != null && c.kind == "transition" && !c.skip) list.Add(c.t);
            if (list.Count == 0) return EmptyF;
            list.Sort();
            return list.ToArray();
        }

        static readonly float[] EmptyF = new float[0];

        /// <summary>這張卡總共分成幾段。1 就是「整張卡只有一個視角」。</summary>
        int SceneCount()
        {
            if (cfg != null && cfg.tracks != null && cfg.tracks.Length > 0) return cfg.tracks.Length;
            int m = TransitionMarks().Length;
            return m > 0 ? m + 1 : 1;
        }

        /// <summary>
        /// 跨進新的一段時，把那一段存過的視角套回去。
        ///
        /// 只在「段數真的變了」那一下動作，不是每幀 —— 不然你在同一段裡
        /// 自己移動視角，下一幀就被拉回去，變成完全不能動。
        /// </summary>
        void TickViewFollow()
        {
            if (cfgAutoView == null || !cfgAutoView.Value) return;

            // 剛換卡的頭兩秒先不要動視角。
            // 那段時間 VRGIN 還在建相機、CameraSync 也還在做初次對齊 ——
            // 這時候把 origin 搬過去，下一刻就會被對齊流程蓋掉，
            // 表現出來就是「自動套用偶爾有效偶爾沒效」，而且完全看不出規律。
            if (Time.realtimeSinceStartup < viewHoldUntil) return;

            int now = CurrentSceneIndex();
            if (now == viewScene) return;
            viewScene = now;
            if (now == viewApplied) return;

            float[] pose;
            if (!ViewStore.TryGet(now, out pose)) return;
            viewApplied = now;
            VrLink.RequestGoto(pose);
            Logger.LogInfo("[CutScene] " + (SceneCount() <= 1 ? "這張卡" : "切到場景 " + (now + 1))
                           + "，已送出存好的視角");
        }

        /// <summary>把 F9 目前的視角存進這張卡的視角檔，記在「現在這一段」底下。</summary>
        void SaveViewpointHere()
        {
            float[] pose = VrLink.CurrentPose;
            if (pose == null)
            {
                message = Lang.T("存視角：拿不到 VR 的視角（沒裝 Studio VR Tools，或現在不在 VR 裡）");
                Logger.LogWarning("[CutScene] " + message);
                return;
            }

            int scene = CurrentSceneIndex();
            if (ViewStore.Set(scene, pose))
            {
                viewApplied = scene;          // 剛存的就是現在的位置，不用再套一次
                message = (SceneCount() <= 1 ? Lang.T("已把視角存進這張卡")
                           : string.Format(Lang.T("已把視角存進場景 {0}"), scene + 1))
                          + "：" + ViewStore.LastReport;
                Logger.LogInfo("[CutScene] " + message);
            }
            else
            {
                message = Lang.T("存視角失敗：") + ViewStore.LastReport;
                Logger.LogWarning("[CutScene] " + message);
            }
        }

        /// <summary>換卡時把視角檔換成新卡的那一份。</summary>
        void BindViewStore()
        {
            viewScene = -1;
            viewApplied = -1;
            viewHoldUntil = Time.realtimeSinceStartup + 2f;

            string card = ScenePathProbe.Detect();
            string dir = FirstSearchDir();
            ViewStore.Bind(card, dir);
            Logger.LogInfo("[CutScene] 視角檔：" + ViewStore.LastReport);
        }

        /// <summary>視角檔要放哪裡 —— 用設定檔搜尋資料夾的第一個，跟 cutscene.json 同一個地方。</summary>
        string FirstSearchDir()
        {
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
                foreach (string part in (cfgSearchDirs.Value ?? "").Split(';'))
                {
                    string d = part.Trim();
                    if (d.Length == 0) continue;
                    return Path.IsPathRooted(d) ? d
                           : Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 快轉／倒轉的連發節奏，按鈕和鍵盤共用。
        ///
        /// 節奏照抄 F9 手柄那一套：**按下去先跳一格**（手感才跟得上），
        /// 按住超過 Seek Repeat Delay 才開始連發，之後每 Seek Repeat Interval 跳一格。
        /// 一直按著就是一直跳，但每一格都是實打實的 seek，不會像「加速播放」
        /// 那樣把沿路的過場 cue 一個個觸發出來。
        /// </summary>
        void TickSeek(int dir)
        {
            float now = Time.realtimeSinceStartup;
            if (dir != seekDirHeld)
            {
                seekDirHeld = dir;
                if (dir != 0)
                {
                    SeekBy(dir * cfgSeekStep.Value);
                    seekNextAt = now + Mathf.Max(0.05f, cfgSeekDelay.Value);
                }
                return;
            }
            if (dir == 0 || now < seekNextAt) return;
            seekNextAt = now + Mathf.Max(0.02f, cfgSeekRepeat.Value);
            SeekBy(dir * cfgSeekStep.Value);
        }

        void SeekBy(float delta)
        {
            // 過場正在播 → 推影片，不要推 timeline。
            // 推 timeline 的話畫面上還是影片、底下的場景卻已經跑掉，
            // 動畫播完就接在錯的位置（你看到的那個症狀）。
            // 這裡刻意不寫 log —— 連發時一秒會進來七、八次，寫了就把 log 洗版，
            // 真正要看的警告會被沖掉。
            var ov = CutOverlay.Instance;
            if (ov != null && ov.IsBusy && ov.Nudge(delta)) return;
            RunCommand("seek:" + delta.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// <summary>面板上的快轉／倒轉鈕 + 桌面快捷鍵。</summary>
        void TickTransport()
        {
            int dir = 0;
            if (uiRewHeld) dir = -1;
            else if (uiFwdHeld) dir = +1;
            uiRewHeld = uiFwdHeld = false;      // OnGUI 每一幀重新設

            if (cfgHotkeysOn != null && cfgHotkeysOn.Value && !TypingInField())
            {
                // 一次按下就好的用 IsDown()；快轉倒轉要能按住，所以用 IsPressed()。
                // KeyboardShortcut 自己比對修飾鍵，而且要求「剛好是這幾個」——
                // 設成 Ctrl+R 的話按 Shift+Ctrl+R 不會觸發，反過來也一樣。
                if (cfgKeyPlay.Value.IsDown()) RunCommand("play");
                if (cfgKeyPause.Value.IsDown()) RunCommand("pause");
                if (cfgKeyStop.Value.IsDown()) RunCommand("stop");
                if (cfgKeyReplay.Value.IsDown()) RunCommand("replay");
                if (cfgKeyPrev.Value.IsDown()) RunCommand("prev-scene");
                if (cfgKeyNext.Value.IsDown()) RunCommand("next-scene");
                if (cfgKeySkip.Value.IsDown()) RunCommand("skip-cut");
                if (dir == 0 && cfgKeyRew.Value.IsPressed()) dir = -1;
                if (dir == 0 && cfgKeyFwd.Value.IsPressed()) dir = +1;
            }

            TickSeek(dir);
        }

        /// <summary>
        /// 正在文字欄位裡打字。不擋的話，在「影片路徑」那種欄位裡打 D
        /// 就會一邊輸入一邊快轉。
        /// </summary>
        static bool TypingInField()
        {
            return GUIUtility.keyboardControl != 0;
        }

        /// <summary>
        /// 從現在的時間找上一個 / 下一個場景起點。
        ///
        /// 「上一個」留了 1.5 秒的回頭餘裕：剛跳進一段就按上一個，直覺是要回到
        /// 再前面那一段，而不是把同一段重播一次（跟音樂播放器的上一首一樣）。
        /// </summary>
        bool FindSceneStart(bool forward, out float to)
        {
            to = 0f;
            if (cfg == null || cfg.tracks == null || cfg.tracks.Length == 0) return false;
            float now = TimelineBridge.GetTime();
            if (now < 0f) now = 0f;
            bool got = false;
            for (int i = 0; i < cfg.tracks.Length; i++)
            {
                float f = cfg.tracks[i].from;
                if (forward)
                {
                    if (f > now + 0.05f && (!got || f < to)) { to = f; got = true; }
                }
                else
                {
                    if (f < now - 1.5f && (!got || f > to)) { to = f; got = true; }
                }
            }
            return got;
        }

        void RunCommand(string cmd)
        {
            try
            {
                var ov = CutOverlay.Instance;
                bool cutBusy = ov != null && ov.IsBusy;

                if (cmd == "pause-or-skip")
                {
                    // 過場播到一半 → 跳過這一段。跳完時間軸會繼續往前跑，
                    // 而過場的位置本來就在兩段場景的接縫上，所以「跳過」＝「進下一段」。
                    if (cutBusy) { ov.Stop(); Logger.LogInfo("[CutScene] 手柄：跳過這一段過場"); return; }
                    cmd = "toggle-pause";
                }

                if (cmd == "skip-cut")
                {
                    if (cutBusy) ov.Stop();
                    return;
                }

                // 快轉／倒轉。秒數跟在冒號後面，正的往前、負的往後。
                if (cmd.StartsWith("seek:", StringComparison.Ordinal))
                {
                    float d;
                    if (float.TryParse(cmd.Substring(5), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out d))
                        NudgeTime(d);
                    return;
                }

                if (cmd == "replay")
                {
                    if (cutBusy) ov.Stop();
                    Restart(PlayFromStart());
                    Logger.LogInfo("[CutScene] 手柄：重播");
                    return;
                }

                if (cmd == "stop")
                {
                    if (cutBusy) ov.Stop();
                    StopAll();
                    vrThinksPlaying = false;
                    Logger.LogInfo("[CutScene] 手柄：停止");
                    return;
                }

                // 上一個 / 下一個場景。場景的起點就是音軌的 from ——
                // 「段落」清單裡那幾列「場景 N 開始」用的是同一份資料，
                // 所以鍵盤跳過去的位置跟點那一列完全一樣。
                if (cmd == "prev-scene" || cmd == "next-scene")
                {
                    if (cutBusy) ov.Stop();
                    float to;
                    if (!FindSceneStart(cmd == "next-scene", out to))
                    {
                        Logger.LogInfo("[CutScene] " + cmd + "：沒有更多場景了");
                        return;
                    }
                    armed = true;
                    Restart(StartFrom(to, true, true));
                    Logger.LogInfo("[CutScene] 跳到場景起點 " + to.ToString("F2") + "s");
                    return;
                }

                if (cmd == "play")
                {
                    // 「跟播中」那顆切換鈕已經移除，但它開啟時做的「清掉 done 旗標」
                    // 不能跟著消失 —— 少了它，播過一輪再按播放就一個過場都不會觸發。
                    if (!armed) foreach (var c in cfg.cuts) c.done = false;
                    armed = true;
                    vrThinksPlaying = true;
                    // 過場播到一半按播放，意思是「等一下接著播」，不是「現在就把
                    // 底下的時間軸放掉」—— 放掉的話影片還在演、場景已經先跑了。
                    if (cutBusy) { cutResumeWanted = true; Logger.LogInfo("[CutScene] 過場中按播放：等過場結束再播"); }
                    else TimelineBridge.Resume();
                    return;
                }

                if (cmd == "pause")
                {
                    vrThinksPlaying = false;
                    if (cutBusy) cutResumeWanted = false;
                    else TimelineBridge.Pause();
                    return;
                }

                if (cmd == "toggle-pause")
                {
                    TimelineBridge.EnsureSeekable();
                    // isPlaying 旗標不是每個版本都掃得到，掃不到就用我們自己記的狀態，
                    // 不然「沒有旗標」會讓這顆鍵變成永遠只做同一件事。
                    bool playing = TimelineBridge.HasPlayingFlag
                                   ? TimelineBridge.GetIsPlaying() : vrThinksPlaying;
                    if (playing)
                    {
                        vrThinksPlaying = false;
                        if (cutBusy) cutResumeWanted = false;
                        else TimelineBridge.Pause();
                        Logger.LogInfo("[CutScene] 手柄：暫停");
                    }
                    else
                    {
                        if (!armed) foreach (var c in cfg.cuts) c.done = false;
                        armed = true;
                        vrThinksPlaying = true;
                        if (cutBusy) cutResumeWanted = true;
                        else TimelineBridge.Resume();
                        Logger.LogInfo("[CutScene] 手柄：播放");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CutScene] 執行手柄指令 " + cmd + " 失敗：" + e.Message);
            }
        }

        bool vrThinksPlaying;
        /// <summary>過場中按了播放 —— 等過場結束再真的讓時間軸跑起來。</summary>
        bool cutResumeWanted;
        float nextSeekWarn;

        /// <summary>
        /// 把播放頭往前／往後挪一段。
        ///
        /// 幾個刻意的選擇：
        ///
        /// 　用 SetTime 不用 SeekRetry —— 連發時一秒會進來七、八次，
        /// 　SeekRetry 失敗會重掃整個 AppDomain 找 Timeline，連發時重掃會直接卡死。
        /// 　真的跳不動就節流著警告，五秒一次。
        ///
        /// 　跳完把 lastTime 設成新位置 —— 不設的話主迴圈下一幀會看到時間突然倒退，
        /// 　判定成「使用者拉了進度條」而把所有過場的 done 旗標清掉，
        /// 　於是倒轉經過的每一段過場都會重播一次。掃來掃去找位置時那很煩。
        /// </summary>
        void NudgeTime(float delta)
        {
            TimelineBridge.EnsureSeekable();
            float t = TimelineBridge.GetTime();
            if (t < 0f) return;

            float want = t + delta;
            if (want < 0f) want = 0f;
            float dur = TimelineBridge.GetDuration();
            if (dur > 0.02f && want > dur) want = dur;

            if (!TimelineBridge.SetTime(want))
            {
                if (Time.realtimeSinceStartup >= nextSeekWarn)
                {
                    nextSeekWarn = Time.realtimeSinceStartup + 5f;
                    Logger.LogWarning("[CutScene] 手柄快轉：時間軸跳不動（"
                                      + TimelineBridge.PauseMode + "）");
                }
                return;
            }
            lastTime = want;
        }

        float lastTime = -1f;
        Coroutine preloading;
        CutEntry preloadTarget;

        // 復原用
        bool audioPausedByUs;
        bool scaleSetByUs;
        float savedScale = 1f;
        bool manualAudioPause;      // 使用者自己按的音訊暫停，看門狗不要去動它
        float cutStartRealtime;

        // --- UI ---
        // x 從 60 移到 360：預設位置會壓到工作室左邊那排工具按鈕，
        // VR 裡拖視窗又特別麻煩，一開始就擋住等於不能用。
        Rect win = new Rect(360f, 60f, 660f, 0f);
        Vector2 scroll;
        bool showSettings;
        string[] ambiguous;   // 總長度分不出來時的候選清單
        string quickVideo = "";
        string quickAudio = "";
        readonly CutEntry quick = new CutEntry();

        // 熱鍵和自動載入放 BepInEx 設定，可以用 ConfigurationManager 改，
        // 也不會跟「每張卡一份」的 cutscene.json 混在一起 —— 那是卡片的設定，這是工具的設定。
        ConfigEntry<KeyCode> cfgHotkey;
        ConfigEntry<bool> cfgAutoLoad;
        ConfigEntry<string> cfgSearchDirs;
        ConfigEntry<bool> cfgShowSettings;
        ConfigEntry<bool> cfgToolbarButton;
        ConfigEntry<bool> cfgAutoView;
        // 快轉／倒轉與桌面快捷鍵。數值刻意跟 F9（Studio VR Tools）那邊同名同預設 ——
        // 兩邊最後都走 RunCommand()，手感一致才不會「VR 跟桌面不一樣」。
        ConfigEntry<float> cfgSeekStep, cfgSeekDelay, cfgSeekRepeat;
        ConfigEntry<bool> cfgHotkeysOn;
        // KeyboardShortcut 而不是 KeyCode：修飾鍵自己帶著走，
        // ConfigurationManager 也會直接給「Press any key」那個擷取介面。
        ConfigEntry<KeyboardShortcut> cfgKeyPlay, cfgKeyPause, cfgKeyStop,
                                      cfgKeyReplay, cfgKeyFwd, cfgKeyRew,
                                      cfgKeyPrev, cfgKeyNext, cfgKeySkip;
        KeyCode HOTKEY { get { return cfgHotkey == null ? KeyCode.F7 : cfgHotkey.Value; } }

        /// <summary>
        /// 預設值：Alt＋這個鍵。
        ///
        /// 為什麼是 Alt 不是 Shift+Ctrl：Ctrl 開頭的組合被其他插件占滿了
        /// （videoexport 的 Ctrl+R 之類），而 Unity 的 Input 是輪詢式的、
        /// 沒有「我吃掉了」這回事 —— 別人照樣收得到。Alt 開頭的用的人少很多，
        /// 撞不到就不必去攔截全域輸入。
        /// </summary>
        static KeyboardShortcut SC(KeyCode k)
        {
            return new KeyboardShortcut(k, KeyCode.LeftAlt);
        }

        // 快轉／倒轉的連發狀態。按鈕和鍵盤共用同一組，所以同時按住也不會變兩倍速。
        bool showKeys;              // 快捷鍵設定視窗
        Rect keysRect = new Rect(80, 80, 330, 340);
        int keyCapture = -1;        // 正在等使用者按下哪一項的新鍵；-1 = 沒有

        int seekDirHeld;            // -1 倒轉 / 0 沒有 / +1 快轉
        float seekNextAt;
        bool uiRewHeld, uiFwdHeld;  // OnGUI 設、Update 讀
        const float WATCHDOG_SEC = 2f;
        static readonly float[] MAXDT = { 0f, 0.3333f, 1f, 2f, 5f };

        void Awake()
        {
            Self = this;
            CutOverlay.Ensure();
            TrackPlayer.Ensure();

            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                string dir = string.IsNullOrEmpty(loc)
                    ? Path.Combine(Application.dataPath, "..")
                    : Path.GetDirectoryName(loc);
                lastPathFile = Path.Combine(dir, "StudioCutScene.last.txt");
            }
            catch { lastPathFile = ""; }

            try
            {
                // 只把上次的路徑填回輸入框，**不自動載入**。
                // 開卡片時該載入哪一份由「比對時間軸總長度」決定；
                // 沿用上一張卡的設定檔只會在新卡上播錯段落，比沒載入還糟。
                if (!string.IsNullOrEmpty(lastPathFile) && File.Exists(lastPathFile))
                    pathInput = File.ReadAllText(lastPathFile).Trim();
            }
            catch { }

            // VR 介面外觀的設定已經搬到 F9（Studio VR Tools）統一管理。
            // 這裡原本各自綁一份，三支插件三個地方要改，而且很容易改到不同步。
            // 現在 F9 用 AppDomain 的共用資料槽公布設定，這邊 VrSkin.Follow() 跟著走；
            // F9 沒安裝的話 Follow() 會退回本地預設值，單獨安裝照常運作。
            cfgHotkey = Config.Bind("General", "Panel Hotkey", KeyCode.F7, "開關面板");
            cfgShowSettings = Config.Bind("General", "Show Advanced Section", false,
                "面板下半部那塊「設定（音軌 / 色彩 / 診斷）」要不要出現。\n"
                + "裡面是卡頓補償、色彩空間、重新掃描 Timeline 這些查錯用的東西，"
                + "設好之後幾乎不會再碰，卻占掉面板一大半 —— 所以預設整區連展開鈕都不畫。"
                + "要調的時候把這個打開");
            cfgAutoLoad = Config.Bind("General", "Auto Load Config", true,
                "載入場景卡之後，自動找一份對得上的 .cutscene.json");
            cfgSearchDirs = Config.Bind("General", "Search Folders",
                "UserData/cutscene",
                "用分號隔開，相對於遊戲根目錄；會往下遞迴找 *.cutscene.json。\n"
                + "另外一定會看場景卡旁邊的 <卡片檔名>.cutscene.json（有的話優先用它）");
            cfgAutoView = Config.Bind("General", "Auto Apply Saved View", true,
                "播到下一段場景時，如果那一段存過視角，就自動把頭顯的視角移過去。\n"
                + "視角是用 Studio VR Tools（F9）的「扳機＋X 記住視角」存的，"
                + "存成卡片旁邊的 <卡片檔名>.view.json，每一段場景各一個。\n"
                + "關掉的話視角照樣會存，只是不會自動套用");
            cfgSeekStep = Config.Bind("Transport", "Seek Step Seconds", 1f,
                "快轉／倒轉一次跳幾秒。跟 F9（Studio VR Tools）的同名設定各自獨立，"
                + "但預設值一樣，改成一致就會是同一個手感");
            cfgSeekDelay = Config.Bind("Transport", "Seek Repeat Delay", 0.4f,
                "按住之後隔多久才開始連發。按一下只跳一格，不會連發");
            cfgSeekRepeat = Config.Bind("Transport", "Seek Repeat Interval", 0.12f,
                "連發起來之後每隔多久跳一格");
            cfgHotkeysOn = Config.Bind("Transport", "Desktop Hotkeys", true,
                "桌面鍵盤快捷鍵總開關。一律是 Shift+Ctrl+<鍵>，"
                + "在文字欄位打字的時候會自動停用");
            cfgKeyPlay = Config.Bind("Transport", "Key Play", SC(KeyCode.W), "播放");
            cfgKeyPause = Config.Bind("Transport", "Key Pause", SC(KeyCode.S), "暫停");
            cfgKeyStop = Config.Bind("Transport", "Key Stop", SC(KeyCode.F), "停止");
            cfgKeyReplay = Config.Bind("Transport", "Key Replay", SC(KeyCode.R), "重播");
            cfgKeyPrev = Config.Bind("Transport", "Key Prev Scene", SC(KeyCode.Z), "上一個場景");
            cfgKeyNext = Config.Bind("Transport", "Key Next Scene", SC(KeyCode.C), "下一個場景");
            cfgKeyFwd = Config.Bind("Transport", "Key Forward", SC(KeyCode.D), "快轉（按住連發）");
            cfgKeyRew = Config.Bind("Transport", "Key Rewind", SC(KeyCode.A), "倒轉（按住連發）");
            cfgKeySkip = Config.Bind("Transport", "Key Skip Cut", SC(KeyCode.X),
                "跳過正在播的動畫");

            cfgLang = Config.Bind("General", "Language", 0,
                "0 = 繁體中文（預設）　1 = English　2 = 日本語。\n"
                + "面板最下方的 Language 按鈕也可以切，三支插件會一起換");
            Lang.Set(cfgLang.Value);
            cfgToolbarButton = Config.Bind("General", "Show Toolbar Button", true,
                "工作室左邊那排工具列上那顆膠卷圖示（右下角有個 R）。"
                + "關掉就只剩 F7 熱鍵。改完立刻生效，不用重開遊戲");

            // 聽「載入場景卡」這件事。掛得上的話自動載入就不用輪詢了；
            // 掛不上也只是退回原本每秒掃一次的做法，不會壞。
            SceneLoadWatch.Apply();
            Logger.LogInfo("[SceneLoadWatch] " + SceneLoadWatch.LastReport);

            // Studio 左邊工具列的按鈕（跟 Timeline 那顆同一排）。
            // 沒裝 KKAPI 也不會怎樣，只是少一顆按鈕。
            ToolbarButton.Create(delegate(bool on) { show = on; }, show);
            toolbarRetryUntil = Time.realtimeSinceStartup + 30f;

            ApplyColorMode();
            ApplyTracks();
            ApplyMaxDelta();
            Logger.LogInfo(NAME + " " + VERSION + " 已載入，按 " + HOTKEY + " 開啟面板。"
                           + "（Timeline 自動掃描，設定檔自動比對時間軸總長度載入）");
        }

        // ------------------------------------------------------------------ 主迴圈

        // ---- 卡頓計量 -------------------------------------------------------
        // Time.unscaledDeltaTime 會被 Time.maximumDeltaTime 夾住，realtimeSinceStartup 不會。
        // 兩者的差，就是「時間軸相對真實時間憑空少掉」的秒數。
        // 音訊走的是真實時間（音效執行緒不會被主執行緒卡住），所以
        //     累計少掉的秒數 ≒ 音訊領先時間軸的秒數。
        // 這個數字對得上 TrackPlayer 的修正量，就證明問題是主執行緒停頓，不是對應點算錯。
        float lastRealtime = -1f;
        public int StallCount;
        public float StallWall;          // 量測期間的真實秒數
        public float StallTick;          // 同一期間 Unity 記到的秒數
        public float StallWorst;         // 單幀最久的停頓
        float lastStallAt = -1f;
        public float StallGap;           // 最近兩次停頓的間隔

        /// <summary>
        /// 時間軸相對真實時間少掉的秒數。
        ///
        /// 只能用「累積差」，不能把每幀的正差加起來：
        /// 停頓通常發生在別的外掛的 Update 裡，而它排在我們前面，
        /// 所以那一幀 Unity 記到的 deltaTime 還是正常值，停頓要到「下一幀」才算進去。
        /// 逐幀取正差會把同一次停頓算兩次，負差又被丟掉，數字會嚴重灌水
        /// （實測開場就顯示已經少掉 12 秒，那其實是載入卡片的時間）。
        /// 兩個總和相減則會自然抵消，不管停頓落在哪一幀都準。
        /// </summary>
        public float StallLost { get { return StallWall - StallTick; } }

        public void ResetStallMeter()
        {
            StallCount = 0; StallWall = 0f; StallTick = 0f; StallWorst = 0f;
            lastStallAt = -1f; StallGap = 0f; lastRealtime = -1f;
        }

        void MeasureStall()
        {
            float rt = Time.realtimeSinceStartup;
            if (lastRealtime > 0f)
            {
                float wall = rt - lastRealtime;
                StallWall += wall;
                StallTick += Time.unscaledDeltaTime;
                if (wall > 0.1f)                       // 這一幀明顯比正常久
                {
                    StallCount++;
                    if (wall > StallWorst) StallWorst = wall;
                    if (lastStallAt > 0f) StallGap = rt - lastStallAt;
                    lastStallAt = rt;
                    if (cfg.logSeeks)
                        Logger.LogWarning("[CutScene] 這幀花了 " + (wall * 1000f).ToString("F0")
                                          + " ms　累計：真實 " + StallWall.ToString("F1")
                                          + "s / 時間軸 " + StallTick.ToString("F1")
                                          + "s　少掉 " + StallLost.ToString("F2") + " s");
                }
            }
            lastRealtime = rt;
        }

        float nextScan, nextRetry, lastDuration = -1f;
        float nextTlScan;                 // Timeline 掃描的重試節流，跟 nextScan 分開，理由見 AutoTick
        bool autoBusy;
        // **初始值必須是 0，不能是 −1。**
        // SceneLoadWatch.Stamp 一開始也是 0；用 −1 的話插件一啟動就被當成
        // 「剛載入了一張卡」，於是在 Timeline 還是空的（總長 10.00 s 的預設值）時
        // 就跑了一次比對，當然什麼都對不上 —— 而且之後沒有真的載入事件的話
        // 就再也不會重跑，畫面上那句「沒有總長 10.00 s 對得上的」會一直留著。
        int seenStamp;             // 已經處理過的 SceneLoadWatch.Stamp
        float loadSettle = -1f;    // 下一次檢查「總長度穩了沒」的時間點
        float prevDuration = -1f;  // 換場景那一刻讀到的總長度（用來認出「時間軸還沒重建」）
        float nameProbeAt = -1f;   // 什麼時候做那一次「比對卡片檔名」
        float settleDur = -1f;     // 上一次讀到的總長度
        int settleHits;            // 連續讀到同一個值幾次
        float settleDeadline;      // 等到這個時間還沒穩就放棄
        bool wasLoading;           // 上一幀遊戲是不是正在載入場景

        /// <summary>
        /// 換場景了 —— 把上一張卡的設定檔整個卸掉。
        ///
        /// 為什麼要有這個：以前只有「找到新的就換掉」，沒有「找不到就清空」。
        /// 於是先開一張有 cutscene.json 的卡、再開一張沒有的，舊的那份會原封不動留著 ——
        /// 段落清單、音軌、過場全是上一張卡的，而且時間點當然對不上，
        /// 播到一半突然黑畫面放上一張卡的影片。比沒載入還糟。
        ///
        /// 所以改成「每次換場景先歸零」：清乾淨之後再去找新的，
        /// 找不到就維持空白，面板上直接看得出來這張卡沒有設定檔。
        ///
        /// 只在自動載入開著的時候清。關掉自動載入的人是手動指定設定檔的，
        /// 替他清掉等於每換一張卡就要重貼一次路徑。
        /// </summary>
        void ClearLoaded()
        {
            if (cfgAutoLoad == null || !cfgAutoLoad.Value) return;
            if (string.IsNullOrEmpty(loadedPath)
                && (cfg == null || ((cfg.cuts == null || cfg.cuts.Length == 0)
                                    && (cfg.tracks == null || cfg.tracks.Length == 0))))
                return;                                   // 本來就是空的，不用做事

            try
            {
                if (CutOverlay.Instance != null && CutOverlay.Instance.IsBusy)
                    CutOverlay.Instance.Stop();
            }
            catch { }

            armed = false;
            lastTime = -1f;
            if (preloading != null) { try { StopCoroutine(preloading); } catch { } preloading = null; }
            preloadTarget = null;
            ambiguous = null;
            cfg = new CutConfig();
            loadedPath = "";
            pathInput = "";
            lastDuration = -1f;
            message = Lang.T("換場景，已清掉上一張卡的設定檔。");

            // 音軌要跟著換掉，不然上一張卡的配音會繼續掛在播放器上 ——
            // 這正是「換到沒有設定檔的場景，一按播放還是放出上一個場景的音頻」。
            // Invalidate() 不夠，clip 還在，所以直接 Unload()。
            if (TrackPlayer.Instance != null) TrackPlayer.Instance.Unload();
            ApplyTracks();
            ApplyColorMode();
            ApplyMaxDelta();
        }

        /// <summary>排一次「等總長度穩定再比對」。重複叫只是把計時重設，不會排兩次。</summary>
        void ArmSettle()
        {
            // 先把「換場景那一刻的總長度」記下來，**要在 ClearLoaded 之前**，
            // 這樣下面才分得出「時間軸重建好了」和「讀到的還是上一張卡的值」。
            prevDuration = TimelineBridge.Ready ? TimelineBridge.GetDuration() : -1f;
            ClearLoaded();
            MapSwitch.Reset();                // 內建地圖的記號換成新卡的
            EnvSwitch.Reset();                // 畫面效果 / 角色燈光的記號也是
            BindViewStore();                  // 視角檔也跟著換成新卡的那一份
            nameProbeAt = Time.realtimeSinceStartup + 0.3f;
            loadSettle = Time.realtimeSinceStartup + 1.0f;
            settleDur = -1f;
            settleHits = 0;
            settleDeadline = Time.realtimeSinceStartup + 25f;
        }

        /// <summary>
        /// 決定「什麼時候該去找對得上的設定檔」。
        ///
        /// 有兩條路，優先用第一條：
        ///
        ///   1. **載入事件**（SceneLoadWatch）。載入哪張卡直接知道，連帶
        ///      「同名優先」那條規則才真的有東西可比。沒有載入就完全不做事 ——
        ///      不再每秒輪詢，也不再每 5 秒重掃資料夾。
        ///
        ///   2. **比對時間軸總長度**（Harmony 掛不上時的退路）。cutscene.json 裡
        ///      每段音軌的 from/to 是照卡片段落算的，最後一段的 to 必然等於總長，
        ///      拿這個比對不碰任何內部 API。缺點就是兩張一樣長的卡分不出來。
        ///
        /// 載入事件來的當下場景還沒建好，Timeline 的總長也還是舊的，
        /// 所以不立刻做 —— 排一個 settle 時間，等它安定再處理。
        /// </summary>
        /// <summary>
        /// 載入事件監聽現在是什麼狀態，濃縮成一句話。
        /// 放進面板訊息裡，這樣「為什麼沒自動載入」不用去翻 log 就看得出來。
        /// </summary>
        static string WatchNote()
        {
            if (!SceneLoadWatch.Active)
                return "載入事件沒掛上 → " + SceneLoadWatch.LastReport;
            if (string.IsNullOrEmpty(SceneLoadWatch.LastPath))
                return "載入事件掛上了，但一次路徑都沒收到";
            return "載入事件收到的是 " + Path.GetFileName(SceneLoadWatch.LastPath);
        }

        void AutoTick()
        {
            // ---- Timeline 掃描：獨立於底下所有的 early return ----
            //
            // 血淚：這件事原本只掛在最底下那條「輪詢退路」上，而那條路的前面有
            //     if (SceneLoadWatch.Active || SceneLoadWatch.LoadFlagAvailable) return;
            // 載入事件掛得上的時候（正常情況都掛得上）那一行就直接 return 了，
            // 於是 TimelineBridge.Scan() 一次都不會被叫到。
            // 另一個呼叫點在 settle 分支裡，但「檔名比對成功」會在它前面就 return。
            // 結果就是面板上寫著「時間軸未就緒（每秒自動重試）」，其實**沒有在重試** ——
            // 設定檔正常自動載入、段落清單正常顯示，只有時間軸永遠是紅字，
            // 非得去設定裡按「重新掃描 Timeline」才會好。
            //
            // 時間軸能不能用跟「場景有沒有在載入」「要不要找設定檔」是三件事，
            // 所以現在它有自己的節流、自己的位置，誰也擋不到它。
            if (!TimelineBridge.Ready && Time.realtimeSinceStartup >= nextTlScan)
            {
                nextTlScan = Time.realtimeSinceStartup + 1f;
                TimelineBridge.Scan();
            }

            // ---- 載入中一律不動作 ----
            // Manager.Scene.IsNowLoading / IsNowLoadingFade 是**遊戲自己的旗標**，
            // 不是我們猜的時機。KK_VR_CameraSync 判斷「可以對齊相機了沒」用的也是這兩個。
            if (SceneLoadWatch.IsLoading)
            {
                // 開始換卡的那一刻：藏起來的內建地圖先打開（MapSwitch 的說明）
                if (!wasLoading) { MapSwitch.Reset(); EnvSwitch.Reset(); }
                wasLoading = true;
                return;
            }

            // ---- 載入剛結束 ----
            // true → false 的那一刻就是該去比對設定檔的時機。
            // 有了這個就不必為了「等場景載完」而定時掃資料夾 —— 那正是你看到的
            // 「剛開遊戲什麼都沒載入也一直在掃」的來源。
            if (wasLoading)
            {
                wasLoading = false;
                ArmSettle();
            }

            // ---- 載入事件（Harmony）也會排一次 ----
            // 兩個訊號互為備援：載入旗標涵蓋所有場景切換，Harmony 掛鉤則連
            // 「用別的方式載入」也抓得到，而且它才知道**是哪一張卡**。
            if (SceneLoadWatch.Stamp != seenStamp)
            {
                seenStamp = SceneLoadWatch.Stamp;
                if (SceneLoadWatch.Stamp > 0) ArmSettle();
            }

            // ---- 先用「卡片檔名」比對，這一步不必等時間軸 ----
            //
            // 載入事件當下就知道是哪一張卡了，而同名的設定檔是最可信的答案 ——
            // 那根本不需要時間軸總長度。以前這一步被綁在 settle 後面一起做，
            // 等於白等，而且一起吃到下面那個「總長還是舊的」的坑。
            if (nameProbeAt >= 0f && Time.realtimeSinceStartup >= nameProbeAt)
            {
                nameProbeAt = -1f;
                if (cfgAutoLoad != null && cfgAutoLoad.Value && !autoBusy)
                {
                    StartCoroutine(AutoLoadRoutine(-1f, true));
                    return;
                }
            }

            // ---- 有排程就等總長度穩定 ----
            if (loadSettle >= 0f)
            {
                if (Time.realtimeSinceStartup < loadSettle) return;
                if (autoBusy) return;                     // 檔名那一輪還在跑，等它講完
                if (!string.IsNullOrEmpty(loadedPath))    // 檔名已經對上了，不必再比總長
                { loadSettle = -1f; return; }
                loadSettle = Time.realtimeSinceStartup + 0.25f;   // 下一次檢查

                if (!TimelineBridge.Ready) TimelineBridge.Scan();

                // 等總長度「穩定」，不是等固定秒數。
                // 第一版等 1.5 秒就動手，結果讀到 10.00 s —— 那是 Timeline 還沒把
                // 新卡的軌道建起來時的預設值，拿它去比對當然一個都對不上。
                // 場景大小差很多（這張 509 秒、幾百條軌），固定秒數不可能猜得準。
                float d = TimelineBridge.Ready ? TimelineBridge.GetDuration() : -1f;
                bool timeout = Time.realtimeSinceStartup >= settleDeadline;

                // 「穩定」還不夠 —— 上一張卡的總長度也很穩定。
                //
                // 這就是「換到沒有設定檔的第二張卡，卻還是載入第一張的 json」的真凶：
                // 換場景之後 Timeline 要過一會兒才會把新卡的軌道建起來，在那之前
                // GetDuration() 回的是**上一張卡的 509.01 s**，而它兩次讀起來當然一樣，
                // 於是 settleHits 立刻成立，拿 509.01 去比對，正好命中上一張卡的設定檔。
                // （log 裡就是這一行：「自動載入 Scenecard.cutscene.json
                // （時間軸總長 509.01 s 對得上，誤差 0 ms）」，而面板上顯示的是 75.00 s。）
                //
                // 所以除了「穩定」還要求「跟換場景那一刻讀到的值不一樣」。
                // prevDuration 是在 ArmSettle 當下抓的快照。
                //
                // 但也不能無限等：新卡的總長度剛好跟上一張一樣是有可能的，
                // 而且 ArmSettle 跑的時候時間軸偶爾已經重建好了（那時快照到的就是新值）。
                // 所以同一個值連續讀滿 5 秒（0.25 s × 20）就當它是真的。
                bool longStable = settleHits >= 20;
                bool fresh = prevDuration < 0f || Mathf.Abs(d - prevDuration) > 0.01f || longStable;

                if (d > 0.02f && Mathf.Abs(d - settleDur) < 0.005f) settleHits++;
                else { settleDur = d; settleHits = 0; }

                if ((settleHits < 1 || !fresh) && !timeout) return;

                loadSettle = -1f;
                if (d <= 0.02f) return;                  // 逾時了還是沒東西，放棄這一次

                // 逾時了而總長度還是舊值 —— 這個數字不能拿來比對。
                // 認得出卡片的話就直接維持空白；那比載到上一張卡的設定檔好太多。
                if (!fresh)
                {
                    string c2 = ScenePathProbe.Detect();
                    if (!string.IsNullOrEmpty(c2) && ScenePathProbe.Source == "載入事件")
                    {
                        message = string.Format(Lang.T("自動載入：卡片是 {0}，沒有同名的設定檔；時間軸總長度還停在上一張卡的 {1} s，不拿來比對，維持空白。"),
                                                Path.GetFileName(c2), d.ToString("F2"));
                        Logger.LogInfo("[CutScene] " + message);
                        return;
                    }
                }

                lastDuration = d;
                if (cfgAutoLoad == null || !cfgAutoLoad.Value || autoBusy) return;
                StartCoroutine(AutoLoadRoutine(d, false));
                return;
            }

            // ---- 只要有可靠訊號，就完全不輪詢 ----
            // 這一行就是「剛開遊戲一直在掃」的解藥：兩個訊號任一個可用，
            // 閒著的時候一次資料夾都不會掃。
            if (SceneLoadWatch.Active || SceneLoadWatch.LoadFlagAvailable) return;

            // ---- 退路：兩個訊號都拿不到才輪詢（遊戲改版之類） ----
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 1f;

            if (!TimelineBridge.Ready) { TimelineBridge.Scan(); return; }

            float dur = TimelineBridge.GetDuration();
            if (dur <= 0.01f) return;

            bool changed = Mathf.Abs(dur - lastDuration) > 0.01f;
            // 目前什麼都沒載入的話，每 5 秒再試一次。
            // 兩種情況需要它：換的那張卡總長度剛好一樣（偵測不到「變了」），
            // 以及第一次偵測跑得比場景載完還早（那時讀到的總長還是舊的）。
            bool empty = (cfg.cuts == null || cfg.cuts.Length == 0)
                         && (cfg.tracks == null || cfg.tracks.Length == 0);
            bool retry = empty && Time.realtimeSinceStartup >= nextRetry;
            if (!changed && !retry) return;
            if (changed) lastDuration = dur;
            if (empty) nextRetry = Time.realtimeSinceStartup + 5f;

            if (cfgAutoLoad == null || !cfgAutoLoad.Value || autoBusy) return;
            StartCoroutine(AutoLoadRoutine(dur, false));
        }

        /// <summary>
        /// 「立刻重找」：現在就去資料夾裡找一次設定檔。
        ///
        /// 以前這顆按鈕只是把輪詢退路的計時歸零（lastDuration / nextScan），
        /// 但載入事件掛得上的時候（正常都掛得上）那條退路根本不會跑 ——
        /// 所以按了完全沒反應。症狀：載入卡片時設定檔還沒做好，做好之後按「立刻重找」
        /// 不會掛上，要重新載入一次卡片才會。
        ///
        /// 現在直接跑跟「剛載入卡片」一樣的那一輪：先比對檔名（卡片同名的設定檔），
        /// 認不出是哪張卡的時候才退回比對時間軸總長度。
        /// 這是手動按的，所以不管「自動載入」有沒有勾都照做。
        /// </summary>
        void RescanNow()
        {
            MapSwitch.Rescan();               // 地圖記號也順便重認一次
            EnvSwitch.Rescan();
            if (autoBusy) { message = Lang.T("正在找設定檔，稍等一下…"); return; }
            message = Lang.T("重新搜尋設定檔…");
            StartCoroutine(RescanRoutine());
        }

        IEnumerator RescanRoutine()
        {
            string before = loadedPath;
            yield return StartCoroutine(AutoLoadRoutine(-1f, true));
            // 檔名那一輪已經有結論（載入了、或確定這張卡沒有同名設定檔）就到此為止
            if (loadedPath != before || !string.IsNullOrEmpty(loadedPath)) yield break;
            string card = ScenePathProbe.Detect();
            if (!string.IsNullOrEmpty(card) && ScenePathProbe.Source == "載入事件") yield break;
            // 認不出卡片 → 用時間軸總長度
            if (!TimelineBridge.Ready) TimelineBridge.Scan();
            float d = TimelineBridge.Ready ? TimelineBridge.GetDuration() : -1f;
            if (d > 0.02f) yield return StartCoroutine(AutoLoadRoutine(d, false));
        }

        /// <summary>
        /// 遞迴列出資料夾下所有 *.cutscene.json。
        ///
        /// 不能用 Directory.GetFiles(..., AllDirectories)：那個只要中途踩到一個
        /// 沒權限或有問題的子資料夾就整個拋例外，一個檔案都拿不到。
        /// 場景資料夾底下什麼都有，踩到的機率不低 —— 實測就是這樣變成「看了 0 個」。
        /// 自己走、每層各自 try，壞掉的那層跳過就好。
        /// </summary>
        static void Collect(string dir, System.Collections.Generic.List<string> outList,
                            System.Collections.Generic.List<string> errs, int depth)
        {
            if (depth > 12) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                    if (f.EndsWith(".cutscene.json", StringComparison.OrdinalIgnoreCase))
                        outList.Add(f);
            }
            catch (Exception e) { errs.Add(dir + " → " + e.GetType().Name); }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception e) { errs.Add(dir + " → " + e.GetType().Name); return; }
            foreach (string d in subs) Collect(d, outList, errs, depth + 1);
        }

        /// <summary>
        /// 找一份對得上的設定檔並載入。
        ///
        /// nameOnly = true 是「換場景之後立刻跑的那一輪」：只做**檔名比對**。
        /// 那一步只需要卡片路徑，載入事件當下就知道了，完全不必等時間軸；
        /// 對上就結束，對不上就什麼都不改，留給後面總長度那條路。
        ///
        /// nameOnly = false 是總長度那一輪：檔名還是先試一次（成本只有一次字串比對），
        /// 不成再拿 dur 去比對每一份設定檔的最後一段音軌。
        /// </summary>
        IEnumerator AutoLoadRoutine(float dur, bool nameOnly)
        {
            autoBusy = true;
            ambiguous = null;
            var hits = new System.Collections.Generic.List<string>();
            var errs = new System.Collections.Generic.List<string>();
            var notes = new System.Collections.Generic.List<string>();

            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            var dirs = new System.Collections.Generic.List<string>();
            foreach (string part in (cfgSearchDirs.Value ?? "").Split(';'))
            {
                string d = part.Trim();
                if (d.Length == 0) continue;
                dirs.Add(Path.IsPathRooted(d) ? d
                         : Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
            }
            foreach (string full in dirs)
            {
                if (!Directory.Exists(full)) { notes.Add("找不到資料夾 " + full); continue; }
                int before = hits.Count;
                Collect(full, hits, errs, 0);
                notes.Add(full + " → " + (hits.Count - before) + " 個");
                yield return null;
            }

            // 卡片旁邊的 <卡片檔名>.cutscene.json 也算，而且排第一個（最明確：就是跟這張卡放一起的）。
            // 只看卡片所在的那一層、只看同名那一個檔，不往下掃 —— 場景資料夾很大，遞迴太慢。
            string cardNow = ScenePathProbe.Detect();
            if (!string.IsNullOrEmpty(cardNow))
            {
                try
                {
                    string side = Path.Combine(Path.GetDirectoryName(cardNow) ?? "",
                                               Path.GetFileNameWithoutExtension(cardNow) + ".cutscene.json");
                    if (File.Exists(side))
                    {
                        hits.RemoveAll(h => string.Equals(Path.GetFullPath(h), Path.GetFullPath(side),
                                                          StringComparison.OrdinalIgnoreCase));
                        hits.Insert(0, side);
                        notes.Add("卡片旁邊 → 1 個");
                    }
                    else notes.Add("卡片旁邊 → 0 個");
                }
                catch (Exception e) { errs.Add("卡片旁邊 → " + e.GetType().Name); }
            }

            Logger.LogInfo("[CutScene] 自動載入掃描：遊戲根目錄 " + root
                           + "　" + string.Join("；", notes.ToArray()));
            if (errs.Count > 0)
                Logger.LogWarning("[CutScene] 有 " + errs.Count + " 個資料夾讀不了（已跳過）："
                                  + string.Join("；", errs.GetRange(0, Mathf.Min(3, errs.Count)).ToArray()));

            // ---- 先比對檔名 ----
            // 問得到卡片路徑的話，同名的那一份最可信 —— 總長度可能撞，檔名不會。
            // 問不到就往下走比對總長度那條路。
            string card = ScenePathProbe.Detect();
            if (!string.IsNullOrEmpty(card))
            {
                string want = Path.GetFileNameWithoutExtension(card) + ".cutscene.json";
                foreach (string f in hits)
                    if (string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
                    {
                        // **比 loadedPath，不是 pathInput。**
                        // pathInput 是「文字框裡打的字」，loadedPath 才是「真的載進去了的」。
                        // 原本比 pathInput 的話，只要文字框裡剛好已經是這個路徑
                        // （上次手動貼過、或上次載入留下的），就會判定「已經是它了」而跳過
                        // LoadConfig —— 結果什麼都沒載入，面板還停在「尚未載入設定檔」，
                        // 看起來就像自動載入整個沒作用。
                        // 而且兩者的路徑分隔符號還可能不一致（一個是 / 一個是 \），
                        // 比起來更不可靠。
                        if (!string.Equals(f, loadedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.LogInfo("[CutScene] 自動載入 " + want
                                           + "（檔名跟卡片對上；卡片路徑來自 "
                                           + ScenePathProbe.Source + "；位置 " + f + "）");
                            pathInput = f;
                            LoadConfig(f);          // 這行自己會把 message 設成「已載入 N 段音軌 / M 段過場」
                            // 補一句來源就好，不要整個蓋掉 —— 段數才是你真正要看的資訊
                            message += Lang.T("　｜ 自動：檔名跟卡片對上");
                        }
                        else
                        {
                            // 已經載入同一份了。還是要寫訊息，不然上一次失敗的紅字會一直留著，
                            // 明明是對的卻看起來像壞掉。
                            message = string.Format(Lang.T("自動載入：{0}（檔名跟卡片對上，內容已經是它了）"), want);
                        }
                        autoBusy = false;
                        yield break;
                    }
                Logger.LogInfo("[CutScene] 卡片是 " + Path.GetFileName(card)
                               + "，但沒有同名的設定檔，改用總長度比對");
            }

            // ---- 檔名那一輪到此為止 ----
            //
            // **認得出卡片、卻沒有同名的設定檔 → 就到此為止，維持空白。**
            //
            // 以前這裡會掉到「比對總長度」那條路，而那正是
            // 「換到沒有設定檔的第二張卡，卻載入了第一張的 json」的來源：
            // 換場景之後 Timeline 要過一會兒才重建，在那之前 GetDuration()
            // 回的還是上一張卡的 509.01 s，拿去比對正好命中上一張卡的設定檔。
            // （log：「自動載入 Scenecard.cutscene.json（時間軸總長 509.01 s
            // 對得上，誤差 0 ms）」，而面板上的時間軸是 75.00 s。）
            //
            // 總長度本來就只是「問不到是哪張卡」時的退路。現在載入事件掛得上、
            // 卡片路徑每次都問得到，就沒有理由再讓那條退路有機會猜錯 ——
            // 猜錯的代價是整張卡播錯段落、還會放出上一張卡的配音。
            //
            // 設定檔想被自動載入，就跟卡片同名：<卡片檔名>.cutscene.json。
            // 訊息裡直接把該取的名字寫出來，不用去翻說明。
            if (nameOnly)
            {
                // 只有「路徑來自載入事件」才夠可信到敢下這個結論。
                // 退路那條（讀別的外掛寫的 *.data）拿到的可能是歷史紀錄裡的舊卡，
                // 拿它來判定「這張卡沒有設定檔」會錯得很難查 —— 那種情況照舊走總長度。
                if (!string.IsNullOrEmpty(card) && ScenePathProbe.Source == "載入事件")
                {
                    loadSettle = -1f;                    // 連帶取消總長度那一輪
                    message = string.Format(Lang.T("自動載入：卡片是 {0}，沒有同名的設定檔 → 維持空白（想自動載入就把設定檔改名成 {1}.cutscene.json）"),
                                            Path.GetFileName(card), Path.GetFileNameWithoutExtension(card));
                    Logger.LogInfo("[CutScene] " + message);
                }
                else
                {
                    // 認不出卡片才留給總長度那條路。
                    message = string.Format(Lang.T("自動載入：認不出現在是哪張卡（{0}），等時間軸總長度再比對…"), WatchNote());
                }
                autoBusy = false;
                yield break;
            }

            // ---- 比對時間軸總長度 ----
            var cand = new System.Collections.Generic.List<string>();   // 對得上的
            var candErr = new System.Collections.Generic.List<float>();
            var near = new System.Collections.Generic.List<string>();    // 對不上的，印出來參考
            int seen = 0;
            foreach (string f in hits)
            {
                float to = TrackEndOf(f);
                if (to > 0f)
                {
                    float err = Mathf.Abs(to - dur);
                    if (err <= 0.5f) { cand.Add(f); candErr.Add(err); }
                    else if (near.Count < 5)
                        near.Add(Path.GetFileName(f) + " 總長 " + to.ToString("F2"));
                }
                else if (near.Count < 5)
                    near.Add(Path.GetFileName(f) + " 讀不到音軌");
                if ((++seen & 7) == 0) yield return null;
            }

            // 依誤差排序
            for (int i = 1; i < cand.Count; i++)
                for (int j = i; j > 0 && candErr[j] < candErr[j - 1]; j--)
                {
                    float fe = candErr[j]; candErr[j] = candErr[j - 1]; candErr[j - 1] = fe;
                    string fs = cand[j]; cand[j] = cand[j - 1]; cand[j - 1] = fs;
                }

            // 兩份的總長度差不到 0.05 秒 → 分不出來。
            // 猜錯的代價是整張卡播錯段落而且不容易發現，所以寧可不載入，列出來讓人點。
            ambiguous = null;

            string best = null;
            float bestErr = 0f;
            if (cand.Count == 1 || (cand.Count > 1 && candErr[1] - candErr[0] > 0.05f))
            {
                best = cand[0];
                bestErr = candErr[0];
            }
            else if (cand.Count > 1)
            {
                ambiguous = cand.ToArray();
                message = string.Format(Lang.T("自動載入：有 {0} 份設定檔的總長度都對得上（差不到 0.05 秒），分不出來，請自己點一個"), cand.Count);
                Logger.LogWarning("[CutScene] " + message);
                autoBusy = false;
                yield break;
            }

            if (best != null)
            {
                // 已經是同一份的話也要寫訊息，否則上一次失敗的字會一直留著；
                // 真的重新載入的話讓 LoadConfig 自己寫段數，這裡只補一句來源。
                if (best == loadedPath)
                    message = string.Format(Lang.T("自動載入：{0}（總長對得上，內容已經是它了）"), Path.GetFileName(best));
                if (best != loadedPath)
                {
                    Logger.LogInfo("[CutScene] 自動載入 " + Path.GetFileName(best)
                                   + "（時間軸總長 " + dur.ToString("F2") + " s 對得上，誤差 "
                                   + (bestErr * 1000f).ToString("F0") + " ms"
                                   + (cand.Count > 1 ? "；另有 " + (cand.Count - 1) + " 個也在範圍內，但這個明顯最近" : "")
                                   + "）");
                    pathInput = best;
                    LoadConfig(best);
                    message += Lang.T("　｜ 自動：總長對得上");
                }
            }
            else
            {
                // 訊息裡一定要說「卡片認出來了沒」。
                // 之前只印「沒有總長 X 對得上的」，看不出真正的原因是
                // 「卡片路徑問不到 → 同名優先那條規則根本沒執行」還是真的沒有同名檔。
                message = string.Format(Lang.T("自動載入：看了 {0} 個設定檔，沒有總長 {1} s 對得上的"),
                                        hits.Count, dur.ToString("F2"));
                message += string.IsNullOrEmpty(card)
                    ? string.Format(Lang.T("；而且認不出現在是哪張卡（{0}），所以連同名比對都沒能用"), WatchNote())
                    : string.Format(Lang.T("；卡片是 {0}，但沒有同名的設定檔"), Path.GetFileName(card));
                if (hits.Count == 0)
                    message += Lang.T("（一個都沒找到，詳情看主控台）");
                else
                    Logger.LogInfo("[CutScene] 最接近的幾個：" + string.Join("；", near.ToArray()));
            }
            autoBusy = false;
        }

        /// <summary>
        /// 音軌沒有對應點時，從設定檔最上層的 pairs 撈這一段的頭尾補上。
        ///
        /// pairs 的格式是 [[timeline秒, 音檔秒, "說明"], ...]，工具每一段會寫頭尾兩筆。
        /// 我們只取落在這一段 [from, to] 範圍內的，兩點以上就拉一條折線。
        ///
        /// 補不到的話**一定要講**。以前這種情況是靜悄悄地從 0 秒開始播，
        /// 而使用者只會看到「第二段聲音不對」，完全不會想到是設定檔缺了東西。
        /// </summary>
        void PatchTracksFromPairs(CutConfig c, JNode root)
        {
            if (c == null || c.tracks == null || c.tracks.Length == 0) return;

            JNode pr = root == null ? null : root.Get("pairs");
            int np = pr == null ? 0 : pr.Count;

            var warned = new System.Collections.Generic.List<string>();

            for (int i = 0; i < c.tracks.Length; i++)
            {
                AudioTrack tr = c.tracks[i];
                if (tr == null) continue;
                if (tr.anchorT != null && tr.anchorT.Length >= 2) continue;   // 本來就有，不碰

                float from = tr.from;
                float to = tr.to > tr.from ? tr.to : float.MaxValue;

                var ft = new System.Collections.Generic.List<float>();
                var fa = new System.Collections.Generic.List<float>();
                for (int k = 0; k < np; k++)
                {
                    JNode e = pr.At(k);
                    if (e == null || e.Kind != JNode.ARR || e.Count < 2) continue;
                    JNode a0 = e.At(0), a1 = e.At(1);
                    if (a0 == null || a1 == null || a0.Kind != JNode.NUM || a1.Kind != JNode.NUM) continue;
                    float t = (float)a0.Num, a = (float)a1.Num;
                    // 容差 0.05 秒：工具寫的段落起點是 108.01，pairs 也是 108.01，
                    // 但四捨五入到小數三位之後不保證完全相等。
                    if (t < from - 0.05f || t > to + 0.05f) continue;
                    ft.Add(t); fa.Add(a);
                }

                // 依時間排序並去掉重複／倒退的點
                for (int x = 1; x < ft.Count; x++)
                    for (int y = x; y > 0 && ft[y] < ft[y - 1]; y--)
                    {
                        float f1 = ft[y]; ft[y] = ft[y - 1]; ft[y - 1] = f1;
                        float f2 = fa[y]; fa[y] = fa[y - 1]; fa[y - 1] = f2;
                    }
                var kt = new System.Collections.Generic.List<float>();
                var ka = new System.Collections.Generic.List<float>();
                for (int x = 0; x < ft.Count; x++)
                {
                    if (kt.Count > 0 && ft[x] - kt[kt.Count - 1] <= 1e-6f) continue;
                    kt.Add(ft[x]); ka.Add(fa[x]);
                }

                if (kt.Count >= 2)
                {
                    tr.anchorT = kt.ToArray();
                    tr.anchorA = ka.ToArray();
                    Logger.LogWarning("[CutScene] 音軌 " + i + "（" + from.ToString("F2") + "–"
                                      + tr.to.ToString("F2") + " s）設定檔裡沒有對應點，"
                                      + "已從 pairs 補上 " + kt.Count + " 點："
                                      + kt[0].ToString("F2") + "s→" + ka[0].ToString("F2") + "s ～ "
                                      + kt[kt.Count - 1].ToString("F2") + "s→"
                                      + ka[ka.Count - 1].ToString("F2") + "s。"
                                      + "這一段是用直線內插的，不如密集取樣準 —— "
                                      + "建議重跑產生工具拿到完整的 anchors。");
                    warned.Add(string.Format(Lang.T("音軌 {0}（已用 pairs 補）"), i + 1));
                }
                else if (tr.offset == 0f)
                {
                    // 補不到，而且 offset 也是 0 → 這一段會從音檔 0 秒開始播。
                    Logger.LogError("[CutScene] 音軌 " + i + "（" + from.ToString("F2") + "–"
                                    + tr.to.ToString("F2") + " s）既沒有對應點也沒有 offset，"
                                    + "而 pairs 裡也找不到這一段的點 —— 這一段會從音檔 0 秒開始播，"
                                    + "聲音一定對不上。請重跑產生設定檔的工具。");
                    warned.Add(string.Format(Lang.T("音軌 {0}（**沒有對應點，會從 0 秒播**）"), i + 1));
                }
            }

            trackWarning = warned.Count == 0 ? ""
                : "⚠ " + string.Join("、", warned.ToArray());
        }

        /// <summary>音軌對應點有問題時顯示在面板上的那一行。沒問題就是空字串。</summary>
        string trackWarning = "";

        /// <summary>只讀「最後一段音軌的 to」，用來跟時間軸總長比對。</summary>
        static float TrackEndOf(string file)
        {
            try
            {
                JNode root = MiniJson.Parse(File.ReadAllText(file));
                JNode tr = root == null ? null : root.Get("tracks");
                int n = tr == null ? 0 : tr.Count;
                float to = 0f;
                for (int i = 0; i < n; i++)
                {
                    JNode e = tr.At(i);
                    if (e != null) to = Mathf.Max(to, e.F("to", 0f));
                }
                return to;
            }
            catch { return 0f; }
        }

        void Update()
        {
            // 別的插件面板上換了語言就跟著換
            int langNow;
            Lang.Follow(cfgLang.Value, out langNow);
            if (langNow != cfgLang.Value) cfgLang.Value = langNow;

            if (Input.GetKeyDown(HOTKEY)) { show = !show; ToolbarButton.Sync(show); }

            TickToolbar();
            TickVrLink();
            TickTransport();

            AutoTick();
            // 合併卡的內建地圖切換（卡裡沒有 [MAPINFO] 記號就什麼都不做）
            // 畫面效果 / 角色燈光也一樣（[ENV] 記號）
            if (!SceneLoadWatch.IsLoading) { MapSwitch.Tick(this); EnvSwitch.Tick(); }
            MeasureStall();
            Watchdog();

            var ov = CutOverlay.Instance;
            if (ov != null && ov.IsBusy)
            {
                // 只吃 ESC 和空白鍵。滑鼠左鍵拿掉了 ——
                // 過場播到一半手滑點一下就整段跳過，太容易誤觸。
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Space))
                    ov.Stop();
                return;
            }

            if (running || !cfg.enabled || !TimelineBridge.Ready) return;

            float t = TimelineBridge.GetTime();
            if (t < 0f) return;

            // 往回 seek → 重置已觸發旗標，這樣可以重看
            if (lastTime >= 0f && t < lastTime - 0.1f)
            {
                // 要分清楚是「時間軸播到底自己繞回 0」還是「使用者手動往回拉」。
                // 繞回去的特徵是：從很後面一口氣掉到接近 0。
                // Timeline 本身是循環播放的，不擋的話整部會一直重播，
                // 而且每一輪都會把開場動畫再放一次。
                bool wrapped = t <= 0.5f && lastTime > 1f;

                // 播到底繞回 0 —— 排在「最後那一小段」的 cue 從來沒被觸發過。
                // 觸發條件是 t >= c.t，但 Timeline 繞回去之前 t 永遠到不了總時長，
                // 所以片尾（kkcutscene 就是把它排在總時長那個點）等於永遠不會播。
                // 在清掉 done 旗標、暫停之前先把它補播掉。
                if (wrapped) RunMissedEndCut(lastTime);

                if (wrapped && !cfg.autoReplay)
                {
                    armed = false;
                    TimelineBridge.Pause();
                    lastTime = -1f;
                    foreach (var c in cfg.cuts) c.done = false;
                    return;
                }
                foreach (var c in cfg.cuts) c.done = false;
            }
            bool advancing = lastTime >= 0f && t > lastTime + 1e-4f;
            lastTime = t;

            // 沒在「跟播」就只更新時間、不觸發任何過場。
            // 這樣在 Timeline 面板裡按播放、拉進度條都不會被影片打斷。
            if (cfg.triggerMode == 0 && !armed) return;

            for (int i = 0; i < cfg.cuts.Length; i++)
            {
                var c = cfg.cuts[i];
                if (c.done || c.skip || !KindEnabled(c.kind)) continue;

                // 總開關：標成已播再跳過，不是單純 continue ——
                // 不標的話每一幀都會重新考慮它，時間軸走過去之後還會留在「待播」，
                // 中途把開關關掉就會突然補播一支。
                if (skipAllCuts) { c.done = true; continue; }

                // 開場動畫在 timeline 0 秒，但載入卡片時 t 本來就是 0 ——
                // 要等使用者真的按下播放（時間軸開始前進）才觸發，否則一開卡就會播。
                if (c.t <= 0.05f && !advancing) continue;

                // c.t <= 0.05 的開場，[c.t - preloadLead, c.t) 這個區間是負的，永遠不成立，
                // 所以另外給它一條路：只要還沒播就先預載好待命。
                bool inWindow = (t >= c.t - cfg.preloadLead && t < c.t) || c.t <= 0.05f;
                if (preloadTarget != c && inWindow)
                {
                    preloadTarget = c;
                    if (preloading != null) StopCoroutine(preloading);
                    preloading = StartCoroutine(CutOverlay.Instance.PreloadRoutine(c));
                }

                if (t < c.t) continue;
                if (t > c.t + cfg.window) { c.done = true; continue; }   // 跳太遠就不補播

                c.done = true;
                StartCoroutine(RunCut(c));
                break;
            }
        }

        /// <summary>
        /// 時間軸播到底繞回 0 的那一刻，把「排在結尾、因此從來沒被 t >= c.t
        /// 掃到」的 cue 補播掉。片尾就是這一種：它的 t 正好等於總時長。
        ///
        /// 只補**最後那一顆**。排在結尾附近的如果不只一顆，連續播好幾段
        /// 過場是錯的；其餘的一律標成已播，免得下一輪又被考慮一次。
        /// </summary>
        void RunMissedEndCut(float endedAt)
        {
            if (cfg == null || cfg.cuts == null) return;
            // 跟主迴圈同一道門檻：沒在跟播就不該觸發任何過場
            //（在 Timeline 面板裡自己拉進度條、播完繞回去都不算）。
            if (cfg.triggerMode == 0 && !armed) return;
            if (skipAllCuts) return;

            float dur = TimelineBridge.GetDuration();
            CutEntry last = null;
            for (int i = 0; i < cfg.cuts.Length; i++)
            {
                var c = cfg.cuts[i];
                if (c == null || c.done || c.skip || !KindEnabled(c.kind)) continue;
                // 沒被掃到的條件：它排在我們最後看到的時間點之後
                if (c.t < endedAt - 0.001f) continue;
                // 而且還在時間軸的範圍內（總長讀不到就不做這層過濾）
                if (dur > 0f && c.t > dur + 0.05f) continue;
                c.done = true;
                if (last == null || c.t > last.t) last = c;
            }
            if (last != null) Restart(RunCut(last));
        }

        void LateUpdate()
        {
            // 過場一收工就立刻鬆手，不等 RunCut 的 finally ——
            // 按「跳過」「停止」「下一個場景」時，下一幀就要能把時間軸拉到新位置，
            // 還釘著的話會被寫回原位，看起來像按了沒反應。
            var ov = CutOverlay.Instance;
            if (TimelineBridge.Holding && (ov == null || !ov.IsBusy)) TimelineBridge.Release();
            TimelineBridge.TickFreeze();
        }

        /// <summary>
        /// 看門狗：協程若因例外中途死掉，IsBusy 會卡在 true 而 Heartbeat 停止更新。
        /// 這時強制把 AudioListener.pause / timeScale 還原，
        /// 免得整個遊戲靜音（VNSound 看起來就像壞掉）。
        /// </summary>
        void Watchdog()
        {
            var ov = CutOverlay.Instance;
            float now = Time.realtimeSinceStartup;

            // 協程還宣稱在播，但心跳停了 → 死在半路
            bool stale = ov != null && ov.IsBusy && now - ov.Heartbeat > WATCHDOG_SEC;
            // overlay 已經收工了，我們卻還把音訊 / timeScale 押著 → RunCut 的 finally 沒跑到
            bool zombie = (audioPausedByUs || scaleSetByUs)
                          && (ov == null || !ov.IsBusy)
                          && now - cutStartRealtime > 1f;

            if (!stale && !zombie) return;

            Logger.LogWarning("[CutScene] 看門狗觸發，強制復原（stale=" + stale + " zombie=" + zombie + "）");
            ForceRestore();
        }

        bool KindEnabled(string k)
        {
            if (k == "opening") return cfg.useOpening;
            if (k == "ending") return cfg.useEnding;
            return cfg.useTransitions;
        }

        /// <summary>從頭播：先播開場，再讓時間軸從 0 開始跑。</summary>
        IEnumerator PlayFromStart()
        {
            armed = true;
            CutEntry open = null;
            for (int i = 0; i < cfg.cuts.Length; i++)
                if (cfg.cuts[i].kind == "opening") { open = cfg.cuts[i]; break; }

            TimelineBridge.EnsureSeekable();
            TimelineBridge.Pause();
            if (!TimelineBridge.SeekRetry(0f))
                Logger.LogWarning("[CutScene] 時間軸無法跳轉，請自己把播放頭拉回 0 再按一次");
            foreach (var c in cfg.cuts) c.done = false;
            lastTime = -1f;
            yield return null;

            if (open != null && cfg.useOpening && !open.skip && !skipAllCuts)
            {
                // 先把影片準備好再開播，否則音訊會先跑、畫面慢好幾秒才出來
                // （載卡時 WarmupVideo 通常已經做完，這裡就是瞬間通過）
                float tPre = Time.realtimeSinceStartup;
                var pre = CutOverlay.Instance.PreloadRoutine(open);
                while (pre.MoveNext()) yield return pre.Current;
                float waited = Time.realtimeSinceStartup - tPre;
                if (waited > 0.5f)
                    Logger.LogWarning("[CutScene] 從頭播放：開場影片還沒預載好，等了 "
                                      + waited.ToString("F2") + " 秒");

                open.done = true;
                var it = RunCut(open);
                while (it.MoveNext()) yield return it.Current;
            }
            TimelineBridge.Resume();
        }

        /// <summary>
        /// 從指定時間開始播。
        /// 之前的過場一律標成「已播」，不然時間軸一動就會把前面的全部補播一遍。
        /// </summary>
        /// <param name="skipOpening">從場景段落起點開始時，開場動畫也不要再放。</param>
        /// <param name="skipAtT">
        /// 連**剛好落在 t 這個時間點**的過場也一起跳過。
        ///
        /// 為什麼需要：場景 2 的起點和它前面那支 transition 是同一個時間（235.01s）。
        /// 原本的條件是 `c.t &lt; t - 0.01`，235.01 不小於 235.00，所以那支不算「之前的」，
        /// 一按「場景 2 開始」就又播一次。按場景起點的意思是「我要直接看這一段」，
        /// 那個銜接動畫已經不需要了。
        ///
        /// 但按**過場自己那一列**的 ▶ 時要的正好相反 —— 那就是要看那一支，
        /// 所以這個參數只有場景列會傳 true。後面時間點的過場都不受影響，照常播。
        /// </param>
        IEnumerator StartFrom(float t, bool skipOpening, bool skipAtT)
        {
            armed = true;
            TimelineBridge.EnsureSeekable();
            TimelineBridge.Pause();
            if (!TimelineBridge.SeekRetry(t))
                Logger.LogWarning("[CutScene] 時間軸無法跳轉，請自己把播放頭拉到 "
                                  + t.ToString("F2") + " 秒再按一次");

            // 0.05 秒的容差：json 裡的時間是算出來的，場景起點和過場時間常常
            // 差在小數第二位（235.00 vs 235.01），用嚴格相等會抓不到。
            float cut = skipAtT ? t + 0.05f : t - 0.01f;
            foreach (var c in cfg.cuts)
                c.done = c.t < cut || (skipOpening && c.kind == "opening");

            lastTime = -1f;
            yield return null;
            TimelineBridge.Resume();
        }

        /// <summary>停止：收掉影片、時間軸暫停並回到 0，退出跟播。</summary>
        void StopAll()
        {
            if (CutOverlay.Instance != null && CutOverlay.Instance.IsBusy)
                CutOverlay.Instance.Stop();
            TimelineBridge.Pause();
            TimelineBridge.SetTime(0f);
            armed = false;
            lastTime = -1f;
            foreach (var c in cfg.cuts) c.done = false;
        }

        float origMaxDelta = -1f;

        void ApplyMaxDelta()
        {
            if (origMaxDelta < 0f) origMaxDelta = Time.maximumDeltaTime;
            float want = cfg.maxDeltaTime > 0f ? cfg.maxDeltaTime : origMaxDelta;
            if (Mathf.Abs(Time.maximumDeltaTime - want) > 0.001f)
            {
                Time.maximumDeltaTime = want;
                Logger.LogInfo("[CutScene] Time.maximumDeltaTime = " + want.ToString("F2")
                               + "（原本 " + origMaxDelta.ToString("F3") + "）");
            }
        }

        void ApplyTracks()
        {
            if (TrackPlayer.Instance == null) return;
            TrackPlayer.Instance.SetConfig(cfg);
            TrackPlayer.Instance.Invalidate();
        }

        /// <summary>
        /// 設定檔一載入就先把影片 Prepare 好、定位到第一段過場的起點。
        ///
        /// 原本要等「跟播」開始、時間軸走到切點前 3 秒才預載 —— 開場在 0 秒，
        /// 根本沒有「之前 3 秒」，所以每次按從頭播放都要當場 Prepare 一整支影片，
        /// 大檔（尤其 4K）要好幾秒，畫面就停在那裡等。
        /// 這裡先做掉，而且共用來源片 Prepare 一次之後會一直保持，後面的過場只剩 seek。
        /// </summary>
        void WarmupVideo()
        {
            var ov = CutOverlay.Instance;
            if (ov == null || cfg == null || !cfg.enabled || cfg.cuts == null) return;
            CutEntry first = null;
            foreach (var c in cfg.cuts)
            {
                if (c == null || c.skip || !KindEnabled(c.kind)) continue;
                if (first == null || c.t < first.t) first = c;
            }
            if (first == null) return;
            if (preloading != null) { try { StopCoroutine(preloading); } catch { } }
            preloadTarget = first;
            preloading = StartCoroutine(ov.PreloadRoutine(first));
        }

        void ApplyColorMode()
        {
            if (CutOverlay.Instance == null) return;
            CutOverlay.Instance.SetColorMode(cfg.colorMode, cfg.videoWidth, cfg.videoHeight);
            CutOverlay.Instance.SharedFile = cfg.videoFile ?? "";
        }

        public void ForceRestore()
        {
            if (CutOverlay.Instance != null) CutOverlay.Instance.HardReset();
            if (!manualAudioPause) AudioListener.pause = false;
            if (scaleSetByUs) Time.timeScale = savedScale <= 0f ? 1f : savedScale;
            audioPausedByUs = false;
            scaleSetByUs = false;
            TimelineBridge.Release();       // 釘住的話一定要鬆開，不然時間軸整個卡死
            cutResumeWanted = false;
            preloadTarget = null;
            running = false;
        }

        /// <summary>
        /// 先把正在播的過場收掉，再開始新的。
        ///
        /// 為什麼需要這一層：面板上的播放按鈕以前是靠 GUI.enabled = !running 變灰來
        /// 避免兩段過場疊在一起，但那樣連「停止」都按不了。改成按鈕一直能按之後，
        /// 就得由這裡負責「舊的先結束、下一幀再開始新的」——
        /// 中間那一幀是留給 RunCut 的 finally 跑完（解除音訊暫停、還原 timeScale），
        /// 少了它，新的過場會被舊的收尾動作反過來蓋掉，看起來像按了沒反應。
        /// </summary>
        void Restart(IEnumerator co)
        {
            StartCoroutine(RestartRoutine(co));
        }

        IEnumerator RestartRoutine(IEnumerator co)
        {
            if (CutOverlay.Instance != null && CutOverlay.Instance.IsBusy)
            {
                CutOverlay.Instance.Stop();
                yield return null;
            }
            yield return StartCoroutine(co);
        }

        IEnumerator RunCut(CutEntry e)
        {
            running = true;
            savedScale = Time.timeScale;
            cutStartRealtime = Time.realtimeSinceStartup;

            // 原本沒在播就不要幫他播起來（手動試播時很重要）。
            // 掃不到 isPlaying 旗標時退回舊行為：一律 Resume。
            bool wasPlaying = !TimelineBridge.HasPlayingFlag || TimelineBridge.GetIsPlaying();

            // 過場的聲音沿用音軌（原始音檔那一段）時，就不能把全域音訊暫停掉，
            // 改成讓音軌自由播 —— 過場長度本來就等於那段音訊的長度，接回去剛好吻合。
            var tp = TrackPlayer.Instance;
            // 多張卡接起來的設定檔：這一段過場的聲音在「它那張卡的音檔」裡，
            // 不一定是現在載入的那個（開場排在卡片交界，這時載著的還是上一張卡的）。
            AudioTrack cutTrack = (e.track >= 0 && cfg.tracks != null && e.track < cfg.tracks.Length)
                                  ? cfg.tracks[e.track] : null;
            bool useTrack = e.useTrackAudio && string.IsNullOrEmpty(e.audio)
                            && tp != null && cfg.tracksEnabled
                            && (tp.HasClip || cutTrack != null);
            bool pinned = false;

            try
            {
                TimelineBridge.Pause();
                // Pause() 只停一次，誰都可以再放開（Timeline 自己的 alt+T、
                // Timeline 視窗的播放鈕、我們自己的播放快捷鍵、手柄）。放開之後
                // 沒人會把它按回去 —— 症狀就是「過場播到一半，底下時間軸自己跑掉」，
                // 過場結束接回場景時位置已經差了整段過場的長度。
                // 釘住是每幀寫回去，不管誰放開下一幀就被拉回來。
                TimelineBridge.Hold();
                cutResumeWanted = false;
                if (cfg.freezeTimeScale) { Time.timeScale = 0f; scaleSetByUs = true; }

                // 指定了音軌 → 先確定那個音檔已經載入。通常快到交界時就預載好了，
                // 這裡只是保險；真的要等的話時間軸已經釘住，不會跑掉。
                if (useTrack && cutTrack != null)
                {
                    tp.Pin(cutTrack);
                    pinned = true;
                    float w0 = Time.realtimeSinceStartup;
                    while (!tp.PinReady && !tp.PinFailed
                           && Time.realtimeSinceStartup - w0 < 20f)
                        yield return null;
                    if (!tp.PinReady)
                    {
                        Logger.LogWarning("[CutScene] 過場 @" + e.t.ToString("F2")
                                          + " 的音檔載不進來，這一段過場沒有聲音：" + tp.Status);
                        tp.EndFreeRun();
                        pinned = false;
                        useTrack = false;
                    }
                }

                // 音軌的自由播掛在「影片真的開始播」那一刻，不是現在 ——
                // 中間還有 Prepare / seek，先啟動的話音訊會領先那段時間。
                if (useTrack)
                {
                    // 音訊的秒數跟影片的秒數是同一套（音檔就是從這支影片拆出來的，
                    // 配音版本也是照著同一條時間軸配的），所以過場一開始就把音訊
                    // 直接 seek 到 videoStart —— 影片從第幾秒開始，音訊就從第幾秒開始。
                    //
                    // 原本 audioStart = -1 的意思是「從音訊目前的位置接著播」。
                    // 那只有在時間軸自然走到過場、音訊剛好也在對的位置時才成立；
                    // 「試播」按鈕和「從頭播放」時音訊在別的地方，就會播到完全不相干的段落。
                    // 改成以 videoStart 為準之後，每一次過場都會重新校準，
                    // 順便把過場之前累積的漂移一次歸零。
                    float st = e.audioStart >= 0f ? e.audioStart
                             : (e.videoStart  >= 0f ? e.videoStart : -1f);
                    // videoStart / audioStart 都是**主配音**（＝影片）的秒數。
                    // 換算成「目前這一版配音的秒數」留給 TrackPlayer 做 ——
                    // 這裡保持主配音那一套，因為過場期間影片就是用這套秒數在走，
                    // 快轉時兩邊要對齊，靠的就是它們同一套。
                    CutOverlay.Instance.TrackBaseMain = st;
                    CutOverlay.Instance.OnPlaybackStart = delegate { tp.BeginFreeRunMain(st); };
                }
                else
                {
                    CutOverlay.Instance.TrackBaseMain = -1f;
                    AudioListener.pause = true; audioPausedByUs = true;
                }

                float fi = e.fadeInOv >= 0f ? e.fadeInOv : cfg.fadeIn;
                float fo = e.fadeOutOv >= 0f ? e.fadeOutOv : cfg.fadeOut;
                var it = CutOverlay.Instance.PlayRoutine(e, fi, fo);
                while (it.MoveNext()) yield return it.Current;
            }
            finally
            {
                if ((useTrack || pinned) && tp != null) tp.EndFreeRun();
                if (audioPausedByUs && !manualAudioPause) AudioListener.pause = false;
                if (scaleSetByUs) Time.timeScale = savedScale <= 0f ? 1f : savedScale;
                audioPausedByUs = false;
                scaleSetByUs = false;

                // 釘住期間真的有人推過時間軸的話講一聲 —— 這樣下次再出現
                // 「過場完接在錯的位置」就知道是誰的問題，不必再猜。
                float drift = TimelineBridge.HoldDrift;
                TimelineBridge.Release();
                if (drift > 0.05f)
                    Logger.LogWarning("[CutScene] 過場期間有東西想把時間軸推走（最多 "
                                      + drift.ToString("F2") + " 秒），已擋下並釘回原位。");

                // 片尾播完就是整部結束 —— 沒開「自動重播」的話停在那裡，不要接著播。
                //
                // 片尾排在總時長那個點，時間軸走不到，實際上都是「播到底繞回 0」
                // 那一刻由 RunMissedEndCut 補播的。那時時間軸還在播（wasPlaying = true），
                // 以前這裡照樣 Resume，結果時間軸從 0 又跑起來 —— 看起來就像自動重播。
                // 主迴圈那邊雖然也下了 Pause，但它比這裡的 finally 早執行，會被蓋掉。
                bool endStop = e.kind == "ending" && !cfg.autoReplay;
                if (endStop)
                {
                    armed = false;
                    if (cutResumeWanted) TimelineBridge.Resume();   // 過場中自己按了播放就照做
                    else TimelineBridge.Pause();
                }
                // 過場中按了播放 → 過場結束才真的開始播。
                else if (wasPlaying || cutResumeWanted) TimelineBridge.Resume();
                cutResumeWanted = false;
                preloadTarget = null;
                running = false;
            }
        }

        // ------------------------------------------------------------------ 設定檔

        /// <summary>
        /// 把路徑洗乾淨：拿掉雙向控制字元和包在外面的引號。
        ///
        /// Windows 檔案總管的「複製檔案位址」會在字串前面塞一個 U+202A
        /// （LEFT-TO-RIGHT EMBEDDING）。那個字元看不見，但 Mono 的
        /// Path.IsPathRooted 會因此判定「這不是絕對路徑」，於是路徑被接到
        /// 目前工作目錄後面 —— 症狀是「明明檔案就在那裡，卻說找不到」，
        /// 而且訊息裡印出來的路徑看起來完全正常。
        /// </summary>
        static string CleanPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            var sb = new System.Text.StringBuilder(p.Length);
            foreach (char ch in p)
            {
                if (ch == '‎' || ch == '‏' || ch == '﻿') continue;
                if (ch >= '‪' && ch <= '‮') continue;
                if (ch >= '⁦' && ch <= '⁩') continue;
                sb.Append(ch);
            }
            string s = sb.ToString().Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"')
                               || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        /// <summary>
        /// 讀一份配音對照表：{ "配音名稱": [[主配音秒, 這一版的秒], ...] }。
        /// 最上層的 variantMaps 和每條音軌自己的 maps 是同一個格式，共用這裡。
        /// </summary>
        VariantMap[] ParseMaps(JNode vm, string where)
        {
            var maps = new System.Collections.Generic.List<VariantMap>();
            if (vm != null && vm.Kind == JNode.OBJ && vm.Obj != null)
            {
                foreach (var kv in vm.Obj)
                {
                    JNode arr = kv.Value;
                    if (arr == null || arr.Kind != JNode.ARR) continue;
                    var rt = new System.Collections.Generic.List<float>();
                    var ot = new System.Collections.Generic.List<float>();
                    for (int k = 0; k < arr.Count; k++)
                    {
                        JNode pr = arr.At(k);
                        if (pr == null || pr.Kind != JNode.ARR || pr.Count < 2) continue;
                        JNode a0 = pr.At(0), a1 = pr.At(1);
                        if (a0 == null || a1 == null
                            || a0.Kind != JNode.NUM || a1.Kind != JNode.NUM) continue;
                        rt.Add((float)a0.Num); ot.Add((float)a1.Num);
                    }
                    // 依主配音秒數排序（手改時順序寫錯也不會壞）
                    for (int x = 1; x < rt.Count; x++)
                        for (int y = x; y > 0 && rt[y] < rt[y - 1]; y--)
                        {
                            float f0 = rt[y]; rt[y] = rt[y - 1]; rt[y - 1] = f0;
                            float f1 = ot[y]; ot[y] = ot[y - 1]; ot[y - 1] = f1;
                        }
                    // 兩邊都必須嚴格遞增：時間只會往前走。
                    // 倒退的點一定是量錯或抄錯，留著會讓那一帶的音訊來回跳。
                    var kr = new System.Collections.Generic.List<float>();
                    var ko = new System.Collections.Generic.List<float>();
                    int bad = 0;
                    for (int x = 0; x < rt.Count; x++)
                    {
                        if (kr.Count > 0
                            && (rt[x] - kr[kr.Count - 1] <= 1e-6f
                                || ot[x] - ko[ko.Count - 1] <= -1e-6f))
                        { bad++; continue; }
                        kr.Add(rt[x]); ko.Add(ot[x]);
                    }
                    if (bad > 0)
                        Logger.LogWarning("[CutScene] " + where + "配音對照「" + kv.Key + "」丟掉 "
                                          + bad + " 個倒退的點，剩 " + kr.Count + " 點。");
                    if (kr.Count == 0) continue;
                    var m = new VariantMap();
                    m.name  = kv.Key;
                    m.refT  = kr.ToArray();
                    m.outT  = ko.ToArray();
                    maps.Add(m);
                }
            }
            return maps.ToArray();
        }

        void LoadConfig(string path)
        {
            path = CleanPath(path);
            // 換設定檔＝換卡片，跟播狀態不應該沿用 —— 不然新卡一載入就開始亂播過場。
            armed = false;
            loadedPath = "";
            try
            {
                string json = File.ReadAllText(path);
                JNode root = MiniJson.Parse(json);          // 失敗會丟明確訊息
                if (root == null || root.Kind != JNode.OBJ)
                {
                    message = Lang.T("最外層必須是一個 { } 物件");
                    return;
                }

                var c = new CutConfig();
                c.enabled         = root.B("enabled", true);
                c.useOpening      = root.B("useOpening", true);
                c.useTransitions  = root.B("useTransitions", true);
                c.useEnding       = root.B("useEnding", true);
                c.autoReplay      = root.B("autoReplay", false);
                c.fadeIn          = root.F("fadeIn", 0.25f);
                c.fadeOut         = root.F("fadeOut", 0.35f);
                c.preloadLead     = root.F("preloadLead", 3f);
                c.window          = root.F("window", 0.5f);
                c.freezeTimeScale = root.B("freezeTimeScale", true);

                c.colorMode       = root.I("colorMode", 3);
                c.videoWidth      = root.I("videoWidth", 1920);
                c.videoHeight     = root.I("videoHeight", 1080);

                c.pathMode      = root.S("pathMode", "abs");
                c.audioRoot     = root.S("audioRoot", "UserData\\audio");
                c.videoRoot     = root.S("videoRoot", "UserData\\cutscene");
                c.videoFile     = root.S("videoFile", "");
                c.variantNames  = root.SArr("variantNames");
                c.variantFiles  = root.SArr("variantFiles");
                c.activeVariant = root.S("activeVariant",
                                         c.variantNames.Length > 0 ? c.variantNames[0] : "");
                c.refVariant    = root.S("refVariant",
                                         c.variantNames.Length > 0 ? c.variantNames[0] : "");

                // variantMaps: { "配音名稱": [[主配音秒, 這一版的秒], ...] }
                // 整個欄位不在、或某個配音沒列進去，就是「跟主配音同步」——
                // 舊的 cutscene.json 因此完全不受影響。
                c.variantMaps = ParseMaps(root.Get("variantMaps"), "");

                c.tracksEnabled = root.B("tracksEnabled", true);
                c.trackVolume   = root.F("trackVolume", 1f);
                c.logSeeks      = root.B("logSeeks", false);
                c.maxDeltaTime  = root.F("maxDeltaTime", 0f);
                // seekTolerance 是舊名字，還讀得進來
                c.hardSeek      = root.F("hardSeek", root.F("seekTolerance", 0.35f));
                c.jumpThreshold = root.F("jumpThreshold", 0.35f);
                c.panicSeek     = root.F("panicSeek", 1.5f);
                c.slewDeadzone  = root.F("slewDeadzone", 0.03f);
                c.slewMax       = root.F("slewMax", 0.02f);
                c.slewGain      = root.F("slewGain", 0.15f);

                JNode tn = root.Get("tracks");
                int ntr = tn != null ? tn.Count : 0;
                c.tracks = new AudioTrack[ntr];
                for (int i = 0; i < ntr; i++)
                {
                    JNode e = tn.At(i);
                    var tr = new AudioTrack();
                    if (e != null && e.Kind == JNode.OBJ)
                    {
                        tr.from   = e.F("from", 0f);
                        tr.to     = e.F("to", 0f);
                        tr.audio  = e.S("audio", "@");
                        tr.offset = e.F("offset", 0f);
                        tr.volume = e.F("volume", 1f);
                        tr.mute   = e.B("mute", false);

                        // files: { "配音名稱": "這一段的音檔", ... } —— 這一段自己的配音檔
                        // maps : 同 variantMaps 的格式，秒數是這一段自己那個音檔的
                        {
                            JNode fn = e.Get("files");
                            if (fn != null && fn.Kind == JNode.OBJ && fn.Obj != null)
                            {
                                var nm = new System.Collections.Generic.List<string>();
                                var fp = new System.Collections.Generic.List<string>();
                                foreach (var kv in fn.Obj)
                                {
                                    if (kv.Value == null || kv.Value.Kind != JNode.STR) continue;
                                    string fpath = CleanPath(kv.Value.Str);
                                    if (fpath.Length == 0) continue;
                                    nm.Add(kv.Key); fp.Add(fpath);
                                }
                                tr.fileNames = nm.ToArray();
                                tr.filePaths = fp.ToArray();
                            }
                            tr.maps = ParseMaps(e.Get("maps"), "音軌 " + i + " 的");
                        }

                        // anchors: [[timeline秒, 音檔秒], ...]
                        JNode an = e.Get("anchors");
                        int na = an != null ? an.Count : 0;
                        var at = new System.Collections.Generic.List<float>();
                        var aa = new System.Collections.Generic.List<float>();
                        for (int k = 0; k < na; k++)
                        {
                            JNode pr = an.At(k);
                            if (pr == null) continue;
                            if (pr.Kind == JNode.ARR && pr.Count >= 2)
                            {
                                JNode a0 = pr.At(0), a1 = pr.At(1);
                                if (a0 != null && a1 != null && a0.Kind == JNode.NUM && a1.Kind == JNode.NUM)
                                { at.Add((float)a0.Num); aa.Add((float)a1.Num); }
                            }
                            else if (pr.Kind == JNode.OBJ)
                            { at.Add(pr.F("t", 0f)); aa.Add(pr.F("a", 0f)); }
                        }
                        // 依 timeline 時間排序，順序寫錯也不會壞
                        for (int x = 1; x < at.Count; x++)
                            for (int y = x; y > 0 && at[y] < at[y - 1]; y--)
                            {
                                float ft = at[y]; at[y] = at[y - 1]; at[y - 1] = ft;
                                float fa = aa[y]; aa[y] = aa[y - 1]; aa[y - 1] = fa;
                            }
                        // 折線一定要嚴格遞增、斜率合理。
                        // 「timeline 往前走、音訊卻要往回」在物理上不可能，
                        // 出現就一定是產生 anchors 的那一端算錯了。
                        // 這種點若留著，TrackPlayer 會在那裡來回硬 seek（實測 ±1.5 秒）。
                        // 這裡直接丟掉不可信的點，讓兩側的好點連成一條直線。
                        int dropped = 0;
                        var kt = new System.Collections.Generic.List<float>();
                        var ka = new System.Collections.Generic.List<float>();
                        for (int x = 0; x < at.Count; x++)
                        {
                            if (kt.Count == 0) { kt.Add(at[x]); ka.Add(aa[x]); continue; }
                            float dt = at[x] - kt[kt.Count - 1];
                            if (dt <= 1e-6f) { dropped++; continue; }
                            float sl = (aa[x] - ka[ka.Count - 1]) / dt;
                            if (sl < ANCHOR_SLOPE_MIN || sl > ANCHOR_SLOPE_MAX) { dropped++; continue; }
                            kt.Add(at[x]); ka.Add(aa[x]);
                        }
                        if (dropped > 0)
                            Logger.LogWarning("[CutScene] 音軌 " + i + "：丟掉 " + dropped + " 個不合理的對應點"
                                              + "（倒退或斜率超出 " + ANCHOR_SLOPE_MIN + "~" + ANCHOR_SLOPE_MAX
                                              + "），剩 " + kt.Count + " 點。"
                                              + "請重跑 kkcutscene plan 產生乾淨的檔案。");
                        tr.anchorT = kt.ToArray();
                        tr.anchorA = ka.ToArray();
                    }
                    c.tracks[i] = tr;
                }

                // ---- 沒有對應點的音軌，從 pairs 補回來 ----
                //
                // 為什麼要有這一層：產生設定檔的工具在積分算不出曲線時
                // （例如那一段中間有 timeScale = 0 的凍結），會寫出 "anchors": []
                // 然後就結束了。而沒有 anchors、又沒有 offset 的音軌，
                // MapToAudio 會退回 offset = 0 —— **整段音檔從 0 秒開始播**。
                //
                // 這是最糟的失敗方式：不報錯、不跳出，只是聲音完全對不上，
                // 而且因為前後兩段都正常，看起來像「只有第二段壞掉」，很難聯想到設定檔。
                //
                // 好消息是同一份檔案裡的 pairs 就有每一段的頭尾對應點
                // （工具算不出中間的曲線，但頭尾是量出來的）。兩點拉一條直線
                // 雖然不如密集取樣準，但比從 0 開始好太多，而且多數段落本來就接近 1:1。
                PatchTracksFromPairs(c, root);

                JNode cn = root.Get("cuts");
                int ncu = cn != null ? cn.Count : 0;
                c.cuts = new CutEntry[ncu];
                for (int i = 0; i < ncu; i++)
                {
                    JNode e = cn.At(i);
                    var ce = new CutEntry();
                    if (e != null && e.Kind == JNode.OBJ)
                    {
                        ce.t           = e.F("t", 0f);
                        ce.video       = e.S("video", "");
                        ce.audio       = e.S("audio", "");
                        ce.skip        = e.B("skip", false);
                        ce.maxSeconds  = e.F("maxSeconds", 0f);
                        ce.durationSec = e.F("durationSec", 0f);
                        ce.kind          = e.S("kind", "");
                        ce.useTrackAudio = e.B("useTrackAudio", true);
                        ce.audioStart    = e.F("audioStart", -1f);
                        ce.fadeInOv      = e.F("fadeIn", -1f);
                        ce.fadeOutOv     = e.F("fadeOut", -1f);
                        ce.videoStart    = e.F("videoStart", -1f);
                        ce.videoEnd      = e.F("videoEnd", -1f);
                        ce.source        = CleanPath(e.S("source", ""));
                        ce.track         = e.I("track", -1);
                        if (ce.videoStart >= 0f && ce.videoEnd > ce.videoStart && ce.durationSec <= 0f)
                            ce.durationSec = ce.videoEnd - ce.videoStart;
                    }
                    c.cuts[i] = ce;
                }

                cfg = c;
                foreach (var e2 in cfg.cuts) e2.done = false;
                CheckFiles();

                ApplyColorMode();
                ApplyTracks();
                ApplyMaxDelta();
                WarmupVideo();
                message = string.Format(Lang.T("已載入 {0} 段音軌 / {1} 段過場："), cfg.tracks.Length, cfg.cuts.Length)
                          + SafeName(path);
                pathInput = path;
                loadedPath = path;          // 真正載入成功才算數
                try { if (!string.IsNullOrEmpty(lastPathFile)) File.WriteAllText(lastPathFile, path); }
                catch { }
            }
            catch (Exception ex)
            {
                message = Lang.T("載入失敗: ") + ex.Message;
                Logger.LogError("[CutScene] 載入 " + path + " 失敗: " + ex);
            }
        }

        /// <summary>
        /// 載入設定檔時順手確認影片和每個配音檔真的存在。
        ///
        /// 為什麼要做：路徑壞掉（搬過資料夾、或設定檔被其他工具改過）時，
        /// 以前的行為是「什麼都不說，就是沒有聲音」，要一路挖到 json 才看得出問題。
        /// 只在載入時做一次，OnGUI 每幀去敲硬碟太浪費。
        /// </summary>
        void CheckFiles()
        {
            videoOk = string.IsNullOrEmpty(cfg.videoFile) || Exists(cfg.videoFile);
            // 過場自己帶來源片的（多張卡接起來的設定檔），每一支都要在
            if (cfg.cuts != null)
                foreach (var ce in cfg.cuts)
                    if (ce != null && !ce.skip && !string.IsNullOrEmpty(ce.source) && !Exists(ce.source))
                        videoOk = false;

            // 有任何一條音軌自己帶音檔 → 這份設定檔是「每一段各自的音檔」
            multiFile = false;
            if (cfg.tracks != null)
                foreach (var tr in cfg.tracks)
                    if (tr != null && tr.HasFiles) { multiFile = true; break; }

            int n = cfg.variantNames == null ? 0 : cfg.variantNames.Length;
            if (!multiFile) n = cfg.variantFiles == null ? 0 : cfg.variantFiles.Length;
            variantOk = new bool[n];
            variantLen = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!multiFile)
                {
                    variantOk[i] = Exists(cfg.variantFiles[i]);
                    variantLen[i] = variantOk[i] ? WavSeconds(cfg.variantFiles[i]) : -1f;
                    continue;
                }
                // 每一段各自的音檔：這個配音版本在每一段要用的檔都在才算好
                string name = cfg.variantNames[i];
                bool ok = true;
                foreach (var tr in cfg.tracks)
                {
                    if (tr == null || tr.mute) continue;
                    string f;
                    if (tr.HasFiles) f = tr.FileFor(name);
                    else if (!string.IsNullOrEmpty(tr.audio) && tr.audio[0] != '@') continue;   // 寫死路徑的不歸這裡管
                    else f = (cfg.variantFiles != null && i < cfg.variantFiles.Length) ? cfg.variantFiles[i] : "";
                    if (!Exists(f)) { ok = false; break; }
                }
                variantOk[i] = ok;
                variantLen[i] = -1f;      // 每一段長度都不同，沒有「這一版的長度」可言
            }
        }

        /// <summary>這份設定檔是不是「每一段音軌各自帶音檔」（多張卡接起來、音檔不合併）。</summary>
        bool multiFile;

        /// <summary>
        /// WAV 的長度（秒）：只讀檔頭（fmt 的 byteRate、data 的大小），不載入音訊。
        /// 不是 WAV 或讀不懂就回傳 -1。
        /// </summary>
        static float WavSeconds(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return -1f;
                    if (new string(br.ReadChars(4)) != "RIFF") return -1f;
                    br.ReadUInt32();
                    if (new string(br.ReadChars(4)) != "WAVE") return -1f;
                    uint byteRate = 0;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = new string(br.ReadChars(4));
                        uint size = br.ReadUInt32();
                        long next = fs.Position + size + (size & 1);
                        if (id == "fmt " && size >= 12)
                        {
                            br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32();
                            byteRate = br.ReadUInt32();
                        }
                        else if (id == "data")
                        {
                            if (byteRate == 0) return -1f;
                            long data = size;
                            // 超大檔 / 串流寫入的 WAV 有時 data 大小是 0 或 0xFFFFFFFF：用檔案剩下的長度
                            if (data == 0 || data == 0xFFFFFFFFL || fs.Position + data > fs.Length)
                                data = fs.Length - fs.Position;
                            return (float)((double)data / byteRate);
                        }
                        if (next <= fs.Position || next > fs.Length) break;
                        fs.Position = next;
                    }
                }
            }
            catch { }
            return -1f;
        }

        /// <summary>
        /// 這一版跟主配音秒數一樣（差 0.05 秒以內）—— 本來就同步，沒有對照表也不用提醒。
        /// 任一邊讀不到長度就當成「不知道」（回傳 false，照舊提醒）。
        /// </summary>
        bool SameLengthAsRef(int v)
        {
            if (multiFile) return true;     // 對照表跟著每一段走，這裡沒辦法也不需要比
            if (variantLen == null || cfg.variantNames == null || v < 0 || v >= variantLen.Length) return false;
            string rv = string.IsNullOrEmpty(cfg.refVariant)
                        ? (cfg.variantNames.Length > 0 ? cfg.variantNames[0] : "")
                        : cfg.refVariant;
            int r = Array.IndexOf(cfg.variantNames, rv);
            if (r < 0 || r >= variantLen.Length) return false;
            float a = variantLen[v], b = variantLen[r];
            return a > 0f && b > 0f && Mathf.Abs(a - b) <= 0.05f;
        }

        static bool Exists(string p)
        {
            try { return !string.IsNullOrEmpty(p) && File.Exists(p); }
            catch { return false; }   // 路徑含非法字元時 File.Exists 會丟例外
        }

        /// <summary>
        /// 這個配音版本跟主配音的關係：""＝它就是主配音、"⇄"＝有對照表、"≈"＝沒有（當成同步）。
        /// </summary>
        string MapMark(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (multiFile)
            {
                // 每一段各自的對照表：只要有一段在換算就標 ⇄，其餘不標
                foreach (var tr in cfg.tracks)
                {
                    if (tr == null || tr.maps == null || !tr.HasName(name)) continue;
                    for (int i = 0; i < tr.maps.Length; i++)
                        if (tr.maps[i] != null && tr.maps[i].name == name
                            && tr.maps[i].refT != null && tr.maps[i].refT.Length > 0)
                            return "⇄";
                }
                return "";
            }
            string rv = string.IsNullOrEmpty(cfg.refVariant)
                        ? (cfg.variantNames != null && cfg.variantNames.Length > 0
                           ? cfg.variantNames[0] : "")
                        : cfg.refVariant;
            if (name == rv) return "";
            if (cfg.variantMaps != null)
                for (int i = 0; i < cfg.variantMaps.Length; i++)
                    if (cfg.variantMaps[i] != null && cfg.variantMaps[i].name == name
                        && cfg.variantMaps[i].refT != null && cfg.variantMaps[i].refT.Length > 0)
                        return "⇄";
            return "≈";
        }

        bool VariantOk(int i)
        {
            return variantOk != null && i < variantOk.Length && variantOk[i];
        }

        // ------------------------------------------------------------------ 面板


        void OnGUI()
        {
            if (!show) return;
            // 過場影片的 overlay 是 GUI.depth = -5000。面板要比它更前面才點得到，
            // 不然播影片時整個面板被蓋住、也吃不到滑鼠。
            GUI.depth = -6000;

            VrSkin.Follow();   // 設定統一由 F9 管，見 VrSkin.Follow 的註解
            GUISkin savedSkin = VrSkin.Begin();
            win = GUILayout.Window(GUID.GetHashCode(), win, DrawWindow,
                NAME + "  " + VERSION + "   (" + HOTKEY + Lang.T(" 開關)"));
            if (showKeys)
                keysRect = GUILayout.Window(GUID.GetHashCode() + 1, keysRect, DrawKeysWindow,
                                            Lang.T("快捷鍵設定"));
            VrSkin.End(savedSkin);
        }

        /// <summary>
        /// 快捷鍵設定視窗。每一列點「設定」之後進入擷取模式，按下的下一個鍵
        /// 就是新的鍵；Esc 取消。修飾鍵固定是 Shift+Ctrl，只挑最後那一個鍵。
        /// </summary>
        void DrawKeysWindow(int id)
        {
            var rows = KeyRows();

            GUILayout.Label(
                Lang.T("點「設置..」之後按下你要的組合（Shift / Ctrl / Alt 可任意搭配）。"),
                Rich());
            cfgHotkeysOn.Value = GUILayout.Toggle(cfgHotkeysOn.Value,
                Lang.T(" 啟用桌面快捷鍵"));
            GUILayout.Space(4f);

            for (int i = 0; i < rows.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T(rows[i].Label), GUILayout.Width(100));
                if (keyCapture == i)
                {
                    GUILayout.Label(Lang.T("<color=#f1c40f>Press any key</color>"),
                                    Rich(), GUILayout.Width(120));
                    if (GUILayout.Button(Lang.T("取消"), GUILayout.Width(50)))
                        keyCapture = -1;
                }
                else
                {
                    GUILayout.Label(rows[i].Entry.Value.ToString(), GUILayout.Width(120));
                    if (GUILayout.Button(Lang.T("設置.."), GUILayout.Width(50)))
                        keyCapture = i;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("回復預設")))
            {
                for (int i = 0; i < rows.Length; i++)
                    rows[i].Entry.Value = (KeyboardShortcut)rows[i].Entry.DefaultValue;
                keyCapture = -1;
            }
            if (GUILayout.Button(Lang.T("關閉")))
            {
                showKeys = false;
                keyCapture = -1;
            }
            GUILayout.EndHorizontal();

            // 擷取。讀這一幀的事件而不是 Input —— 事件才知道「按的是哪一個鍵」，
            // 而且 ev.shift / ev.control / ev.alt 同一時間就帶著修飾鍵，
            // 不用自己去輪詢六個修飾鍵的狀態。
            //
            // 修飾鍵本身不算數：按著 Shift 找下一個鍵的時候不該當成已經設定完。
            if (keyCapture >= 0 && keyCapture < rows.Length)
            {
                Event ev = Event.current;
                if (ev != null && ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None
                    && !IsModifierKey(ev.keyCode))
                {
                    if (ev.keyCode != KeyCode.Escape)
                    {
                        int n = (ev.shift ? 1 : 0) + (ev.control ? 1 : 0) + (ev.alt ? 1 : 0);
                        var mods = new KeyCode[n];
                        int mi = 0;
                        if (ev.shift) mods[mi++] = KeyCode.LeftShift;
                        if (ev.control) mods[mi++] = KeyCode.LeftControl;
                        if (ev.alt) mods[mi++] = KeyCode.LeftAlt;
                        rows[keyCapture].Entry.Value =
                            new KeyboardShortcut(ev.keyCode, mods);
                    }
                    keyCapture = -1;
                    ev.Use();
                }
            }

            GUI.DragWindow();
        }

        static bool IsModifierKey(KeyCode k)
        {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift
                || k == KeyCode.LeftControl || k == KeyCode.RightControl
                || k == KeyCode.LeftAlt || k == KeyCode.RightAlt
                || k == KeyCode.LeftCommand || k == KeyCode.RightCommand;
        }

        struct KeyRow
        {
            public string Label;
            public ConfigEntry<KeyboardShortcut> Entry;
            public KeyRow(string l, ConfigEntry<KeyboardShortcut> e) { Label = l; Entry = e; }
        }

        KeyRow[] KeyRows()
        {
            return new[]
            {
                new KeyRow("播放", cfgKeyPlay),
                new KeyRow("暫停", cfgKeyPause),
                new KeyRow("停止", cfgKeyStop),
                new KeyRow("重播", cfgKeyReplay),
                new KeyRow("上一個場景", cfgKeyPrev),
                new KeyRow("下一個場景", cfgKeyNext),
                new KeyRow("快進", cfgKeyFwd),
                new KeyRow("倒轉", cfgKeyRew),
                new KeyRow("跳過動畫", cfgKeySkip),
            };
        }

        string guiError = "";

        void DrawWindow(int id)
        {
            // 任何一行畫面程式丟例外，Unity 就不會再呼叫這個視窗 —— 面板等於憑空消失，
            // 而且沒有任何提示。包起來，至少把錯誤印在面板上。
            try { DrawWindowBody(); }
            catch (Exception e)
            {
                guiError = e.GetType().Name + ": " + e.Message;
                GUILayout.Label(Lang.T("<color=#e05b5b>面板繪製出錯：") + guiError + "</color>", Rich());
                if (GUILayout.Button(Lang.T("清掉錯誤"))) guiError = "";
            }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        void DrawWindowBody()
        {
            GUILayout.BeginVertical();

            // 原本的「Timeline」和「音訊（VNSound）」兩大區塊拿掉了：
            // Timeline 現在每秒自動掃描、設定檔自動比對總長度載入，平常沒有東西要按；
            // VNSound 的手動暫停也早就被音軌取代。診斷用的按鈕收進下面的「設定」。
            // 這一排左邊原本是「時間軸 x / y s」，已經拿掉 ——
            // 下面的進度條同樣顯示現在幾秒／總長，而且還能拖。
            // 它兼的「時間軸未就緒」紅字也沒有斷：進度條那一列自己會畫（見 DrawSeekBar）。
            // 右邊的音軌狀態留著 —— 那個進度條沒有，而且「暫停 @ 幾秒」
            // 是判斷聲音有沒有跟上時間軸唯一的即時線索。
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (TrackPlayer.Instance != null && TrackPlayer.Instance.HasClip)
                GUILayout.Label(Lang.T(TrackPlayer.Instance.Status), Small());
            GUILayout.EndHorizontal();

            // --- 設定檔 ---
            GUILayout.Label(Lang.T("<b>過場設定檔</b>"), Rich());
            GUILayout.BeginHorizontal();
            pathInput = GUILayout.TextField(pathInput ?? "");
            if (GUILayout.Button(Lang.T("載入 / 重載"), GUILayout.Width(90)))
            {
                // 文字框是空的就當成「幫我找」—— 以前會直接丟給 LoadConfig，
                // 得到一句看不懂的「載入失敗: Empty path not allowed」。
                if (string.IsNullOrEmpty(CleanPath(pathInput))) RescanNow();
                else LoadConfig(pathInput);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T(message));
            GUILayout.BeginHorizontal();
            if (cfgAutoLoad != null)
                cfgAutoLoad.Value = GUILayout.Toggle(cfgAutoLoad.Value,
                    Lang.T(" 自動載入（比對時間軸總長度）"), GUILayout.Width(210));
            if (GUILayout.Button(Lang.T("立刻重找"), GUILayout.Width(80)))
                RescanNow();
            GUILayout.Label(cfgSearchDirs == null ? "" : Lang.T("搜尋：") + Lang.T("卡片旁邊") + "；" + cfgSearchDirs.Value, Small());
            GUILayout.EndHorizontal();
            if (ambiguous != null)
            {
                GUILayout.Label(Lang.T("<color=#f1c40f>總長度一樣，分不出來 —— 點一個：</color>"), Rich());
                foreach (string f in ambiguous)
                    if (GUILayout.Button(SafeName(f)))
                    {
                        pathInput = f;
                        LoadConfig(f);
                        ambiguous = null;
                        break;
                    }
            }
            if (!string.IsNullOrEmpty(cfg.videoFile))
                GUILayout.Label(Lang.T("共用來源片: ") + SafeName(cfg.videoFile), Small());

            GUILayout.BeginHorizontal();
            cfg.enabled = GUILayout.Toggle(cfg.enabled, Lang.T(" 總開關"), GUILayout.Width(70));
            cfg.useOpening = GUILayout.Toggle(cfg.useOpening, Lang.T(" 開場"), GUILayout.Width(60));
            cfg.useTransitions = GUILayout.Toggle(cfg.useTransitions, Lang.T(" 過場"), GUILayout.Width(60));
            cfg.useEnding = GUILayout.Toggle(cfg.useEnding, Lang.T(" 片尾"), GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            cfg.freezeTimeScale = GUILayout.Toggle(cfg.freezeTimeScale, Lang.T(" 凍結 timeScale"));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            // --- 音軌（取代 VNSound）---
            GUILayout.Label(Lang.T("<b>音軌（跟著時間軸走，可任意 seek）</b>"), Rich());
            GUILayout.BeginHorizontal();
            cfg.tracksEnabled = GUILayout.Toggle(cfg.tracksEnabled, Lang.T(" 啟用"), GUILayout.Width(60));
            GUILayout.Label(Lang.T("共 ") + (cfg.tracks == null ? 0 : cfg.tracks.Length) + Lang.T(" 段"), GUILayout.Width(60));
            if (GUILayout.Button(Lang.T("重載音檔"), GUILayout.Width(80)) && TrackPlayer.Instance != null)
                TrackPlayer.Instance.Invalidate();
            GUILayout.Label(Lang.T("音量"), GUILayout.Width(30));
            cfg.trackVolume = GUILayout.HorizontalSlider(cfg.trackVolume, 0f, 1f, GUILayout.Width(90));
            GUILayout.Label(Mathf.RoundToInt(cfg.trackVolume * 100f) + "%", GUILayout.Width(40));
            GUILayout.EndHorizontal();

            // 對應點有問題的話一定要看得見。這種錯誤的症狀是「某一段聲音對不上」，
            // 從畫面上完全看不出原因 —— 不寫在這裡就只能去翻 log。
            if (trackWarning.Length > 0)
            {
                Color sv = GUI.color;
                GUI.color = new Color(1f, 0.75f, 0.2f);
                GUILayout.Label(trackWarning, Small());
                GUI.color = sv;
            }

            if (cfg.variantNames != null && cfg.variantNames.Length > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("配音"), GUILayout.Width(34));
                bool anyMissing = false;
                bool noMap = false;
                for (int v = 0; v < cfg.variantNames.Length; v++)
                {
                    bool on = cfg.activeVariant == cfg.variantNames[v];
                    // 檔案不在就標紅字 —— 不然選了也只是靜音，完全看不出原因
                    bool ok = VariantOk(v);
                    if (!ok) anyMissing = true;
                    // 剪輯跟主配音不一樣的版本，要嘛有對照表（⇄），要嘛是被當成同步（≈）。
                    // 沒標記的那個就是主配音本身。這件事一定要看得見 ——
                    // 「換了配音之後整段對不上」從畫面上完全看不出原因。
                    string mk = MapMark(cfg.variantNames[v]);
                    // 只有**現在選的這一版**沒有對照表才提醒。
                    // 以前是「任何一版沒有就提醒」，於是主配音也好、已經量好對照表的
                    // 那一版也好，只要清單裡還有另一版沒量，那行黃字就一直掛著 ——
                    // 對現在聽到的聲音完全沒有意義，久了就變成看不見的背景。
                    // 主配音本身 MapMark 回空字串，所以自然不會觸發。
                    // 秒數本來就跟主配音一樣的，沒有對照表也是對的，不提醒
                    if (on && mk == "≈" && !SameLengthAsRef(v)) noMap = true;
                    string label = ok ? " " + cfg.variantNames[v] + (mk.Length > 0 ? " " + mk : "")
                                      : " <color=#e74c3c>" + cfg.variantNames[v] + " ✕</color>";
                    if (GUILayout.Toggle(on, label, RichBtn(), GUILayout.Width(96)) && !on)
                    {
                        cfg.activeVariant = cfg.variantNames[v];
                        if (TrackPlayer.Instance != null) TrackPlayer.Instance.Invalidate();
                    }
                }
                GUILayout.EndHorizontal();
                if (anyMissing || !videoOk)
                    GUILayout.Label(Lang.T("<color=#e74c3c>紅色 ✕ ＝ 這個檔案不在了")
                        + (videoOk ? "" : Lang.T("（共用來源影片也找不到）"))
                        + Lang.T("，請用工具重新產生 json，或直接改 json 裡的路徑。</color>"), Rich());
                if (noMap)
                    GUILayout.Label(
                        Lang.T("<color=#f1c40f>現在這一版沒有配音對照表（≈），當成跟主配音同秒數。</color>"),
                        Rich());
            }

            // --- 段落 ---
            // 過場（開場 / 過場 / 片尾）和「場景段落的起點」混在同一張清單裡，
            // 按時間排序。場景起點來自音軌的 from —— 那本來就是每一段場景的開頭，
            // 想從第幾場開始看，直接點那一列的 start。
            GUILayout.Label(Lang.T("<b>段落</b>"), Rich());
            int nCuts = cfg.cuts == null ? 0 : cfg.cuts.Length;
            int nTrk = cfg.tracks == null ? 0 : cfg.tracks.Length;
            if (nCuts == 0 && nTrk == 0)
            {
                GUILayout.Label(Lang.T("（尚未載入任何設定檔。下面的「快速試播」不需要設定檔。）"), Small());
            }
            else
            {
                int rows = nCuts + nTrk;
                float h = Mathf.Clamp(rows * 30f + 10f, 130f, 340f);
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(h));

                // 依時間合併兩份清單（數量很少，直接選最小值即可）
                var usedCut = new bool[nCuts];
                var usedTrk = new bool[nTrk];
                for (int n = 0; n < rows; n++)
                {
                    int ci = -1, ti = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < nCuts; i++)
                        if (!usedCut[i] && cfg.cuts[i].t < best) { best = cfg.cuts[i].t; ci = i; ti = -1; }
                    for (int i = 0; i < nTrk; i++)
                        if (!usedTrk[i] && cfg.tracks[i].from < best)
                        { best = cfg.tracks[i].from; ti = i; ci = -1; }

                    GUILayout.BeginHorizontal("box");
                    if (ci >= 0)
                    {
                        usedCut[ci] = true;
                        var c = cfg.cuts[ci];
                        if (GUILayout.Button(Icons.Start, IconBtn(), GUILayout.Width(28),
                                             GUILayout.Height(24)))
                        {
                            if (c.kind == "opening") Restart(PlayFromStart());
                            else Restart(StartFrom(c.t, false, false));
                        }
                        GUILayout.Label(c.t.ToString("F2") + "s", GUILayout.Width(62));
                        GUILayout.Label(string.IsNullOrEmpty(c.kind) ? "transition" : c.kind,
                                        GUILayout.Width(78));
                        GUILayout.Label(c.done ? Lang.T("已播") : "", Small(), GUILayout.Width(34));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("test", GUILayout.Width(46))) Restart(RunCut(c));
                        // skip 打開時用紅字，一眼看得出這段被關掉了
                        c.skip = GUILayout.Toggle(c.skip,
                            c.skip ? "<color=#e74c3c><b>skip</b></color>" : "skip",
                            RichBtn(), GUILayout.Width(46));
                        if (GUILayout.Button(Icons.Reset, IconBtn(), GUILayout.Width(28),
                                             GUILayout.Height(24)))
                            c.done = false;
                    }
                    else if (ti >= 0)
                    {
                        usedTrk[ti] = true;
                        var tr = cfg.tracks[ti];
                        if (GUILayout.Button(Icons.Start, IconBtn(), GUILayout.Width(28),
                                             GUILayout.Height(24)))
                            Restart(StartFrom(tr.from, true, true));
                        GUILayout.Label(tr.from.ToString("F2") + "s", GUILayout.Width(62));
                        GUILayout.Label(Lang.T("場景 ") + (ti + 1) + Lang.T(" 開始"), GUILayout.Width(78));
                        GUILayout.FlexibleSpace();
                        GUILayout.Label("→ " + tr.to.ToString("F2") + "s", Small(), GUILayout.Width(80));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            // --- 進度條 ---
            DrawSeekBar();

            // --- 播放控制 ---
            // 這排按鈕**不**會在播放中變灰。之前用 GUI.enabled = !running 擋住，
            // 結果過場一播出去整排就按不動 —— 最需要按「停止」的時候偏偏按不了。
            // 現在改成：任何「開始播」的動作都先把正在播的收掉再開始（見 Restart）。
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Icons.Play, IconBtn(), GUILayout.Width(34), GUILayout.Height(28)))
                RunCommand("play");
            // 倒轉 ◀◀ ——「按著不放就一直倒」，節奏由 TickSeek 決定。
            // RepeatButton 只負責回報「現在有沒有被按著」，實際跳幾格不在這裡算：
            // OnGUI 一幀可能跑好幾次（Layout / Repaint），在這裡直接 seek 會跳兩三倍。
            if (GUILayout.RepeatButton("<<", GUILayout.Width(34), GUILayout.Height(28)))
                uiRewHeld = true;
            if (GUILayout.Button(Icons.Pause, IconBtn(), GUILayout.Width(34), GUILayout.Height(28)))
                RunCommand("pause");
            // 快轉 ▶▶
            if (GUILayout.RepeatButton(">>", GUILayout.Width(34), GUILayout.Height(28)))
                uiFwdHeld = true;
            if (GUILayout.Button(Icons.Stop, IconBtn(), GUILayout.Width(34), GUILayout.Height(28)))
            {
                if (CutOverlay.Instance != null && CutOverlay.Instance.IsBusy)
                    CutOverlay.Instance.Stop();
                StopAll();
            }
            // ↺ ＝ 從頭播（含開場）。原本旁邊那顆黃色「從頭播放（含開場）」已經刪掉，
            // 兩顆做同一件事沒有意義。
            if (GUILayout.Button(Icons.Replay, IconBtn(), GUILayout.Width(34), GUILayout.Height(28)))
                Restart(PlayFromStart());

            GUILayout.Space(8f);
            cfg.autoReplay = GUILayout.Toggle(cfg.autoReplay, Lang.T(" 自動重播"), GUILayout.Width(84));
            skipAllCuts = GUILayout.Toggle(skipAllCuts,
                skipAllCuts ? Lang.T("<color=#e74c3c><b> 跳過所有動畫</b></color>") : Lang.T(" 跳過所有動畫"),
                RichBtn(), GUILayout.Width(120));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("快捷鍵"), GUILayout.Width(60), GUILayout.Height(28)))
                showKeys = !showKeys;

            GUILayout.EndHorizontal();

            // --- 快速試播 ---
            GUILayout.Label(Lang.T("<b>快速試播</b>"), Rich());
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("影片"), GUILayout.Width(34));
            quickVideo = GUILayout.TextField(quickVideo ?? "");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("音訊"), GUILayout.Width(34));
            quickAudio = GUILayout.TextField(quickAudio ?? "");
            GUILayout.Label(Lang.T("(留空=影片自帶)"), Small(), GUILayout.Width(110));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            // 只有「沒填影片」才變灰；播放中照樣能按（會先把正在播的收掉）
            GUI.enabled = !string.IsNullOrEmpty(quickVideo);
            if (GUILayout.Button(Lang.T("試播這支")))
            {
                quick.video = quickVideo.Trim();
                quick.audio = (quickAudio ?? "").Trim();
                quick.done = false; quick.skip = false; quick.maxSeconds = 0f; quick.durationSec = 0f;
                Restart(RunCut(quick));
            }
            GUI.enabled = true;
            if (GUILayout.Button(Lang.T("停止"), GUILayout.Width(70)))
            { if (CutOverlay.Instance != null) CutOverlay.Instance.Stop(); }
            if (GUILayout.Button(Lang.T("強制復原"), GUILayout.Width(100)))
            { manualAudioPause = false; ForceRestore(); }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            // --- 設定（音軌 / 色彩 / 診斷）---
            //
            // 整區預設不畫，連那顆展開鈕都不畫。這裡面全是查錯用的東西
            // （卡頓補償、色彩空間、重新掃描 Timeline…），設好之後就不會再碰，
            // 卻占掉面板一大半。要用的時候去 .cfg 把「顯示設定區」打開。
            GUILayout.Space(6f);
            if (cfgShowSettings != null && cfgShowSettings.Value)
            {
                showSettings = GUILayout.Toggle(showSettings,
                    showSettings ? Lang.T("  ▼  設定") : Lang.T("  ▶  設定（音軌 / 色彩 / 診斷）"), "Button");
            }
            else showSettings = false;
            if (showSettings)
            {
                GUILayout.BeginVertical("box");
            if (TrackPlayer.Instance != null)
            {
                var tpi = TrackPlayer.Instance;
                GUILayout.Label("  " + tpi.LoadedName() + "   " + Lang.T(tpi.Status), Small());
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("  重新定位 ") + tpi.SeekCount + Lang.T(" 次")
                                + (tpi.SeekCount > 0 ? Lang.T("（對應關係若正確應為 0）") : ""),
                                Small(), GUILayout.Width(230));
                if (GUILayout.Button(Lang.T("歸零"), GUILayout.Width(50))) tpi.ResetDiag();
                cfg.logSeeks = GUILayout.Toggle(cfg.logSeeks, Lang.T(" 寫主控台"), GUILayout.Width(80));
                GUILayout.Label(tpi.LastSeek, Small());
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("  卡頓補償 maximumDeltaTime"), Small(), GUILayout.Width(180));
                foreach (float v in MAXDT)
                {
                    bool on = Mathf.Abs(cfg.maxDeltaTime - v) < 0.001f;
                    string nm = v <= 0f ? Lang.T("不動") : v.ToString("0.##") + "s";
                    if (GUILayout.Toggle(on, " " + nm, "Button", GUILayout.Width(58)) && !on)
                    {
                        cfg.maxDeltaTime = v;
                        ApplyMaxDelta();
                    }
                }
                GUILayout.Label(Lang.T("目前 ") + Time.maximumDeltaTime.ToString("F3") + "s", Small());
                GUILayout.EndHorizontal();
            }

            // --- 卡頓計量（診斷用，直接說明不同步是誰造成的）---
            GUILayout.BeginHorizontal();
            string stall = StallCount == 0
                ? "  卡頓 0 次 —— 主執行緒沒停頓過"
                : "  卡頓 " + StallCount + " 次，時間軸累計少掉 " + StallLost.ToString("F2") + " s"
                  + "（最久 " + (StallWorst * 1000f).ToString("F0") + " ms"
                  + (StallGap > 0f ? "，間隔 " + StallGap.ToString("F1") + " s" : "") + "）";
            GUILayout.Label(stall, Small(), GUILayout.Width(400));
            if (GUILayout.Button(Lang.T("歸零"), GUILayout.Width(50))) ResetStallMeter();
            GUILayout.EndHorizontal();
            if (StallLost > 0.1f)
                GUILayout.Label(Lang.T("  → 音訊走真實時間、時間軸走 deltaTime，所以音訊此刻應領先約 ")
                                + StallLost.ToString("F2") + Lang.T(" s。")
                                + Lang.T("這個數字若對得上上面的重新定位量，問題就是停頓、不是對應點。"), Small());

            GUILayout.Space(6f);

            // --- 色彩 ---
            GUILayout.Label(Lang.T("<b>色彩（sRGBWrite 是正解，其餘留著比對）</b>"), Rich());
            GUILayout.BeginHorizontal();
            string[] nms = { "原樣", "Linear", "sRGB", "sRGBWrite" };
            for (int m = 0; m < 4; m++)
            {
                bool on = cfg.colorMode == m;
                if (GUILayout.Toggle(on, " " + Lang.T(nms[m]), "Button", GUILayout.Width(84)) && !on)
                {
                    cfg.colorMode = m;
                    ApplyColorMode();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("目前: ") + (CutOverlay.Instance != null
                                ? CutOverlay.Instance.ColorModeName() : "-")
                            + Lang.T("    RT 尺寸 ") + cfg.videoWidth + "x" + cfg.videoHeight
                            + Lang.T("（非 1080p 的片子改 json）"), Small());
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

                GUILayout.Label(Lang.T("<b>過場的觸發方式</b>"), Rich());
                GUILayout.BeginHorizontal();
                string[] tm = { "只在跟播時（預設）", "時間軸一走就觸發" };
                for (int m = 0; m < 2; m++)
                {
                    bool on = cfg.triggerMode == m;
                    if (GUILayout.Toggle(on, " " + Lang.T(tm[m]), "Button", GUILayout.Width(150)) && !on)
                        cfg.triggerMode = m;
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(Lang.T("「只在跟播時」＝按了上面的「從頭播放」或「跟播中」才會播過場。")
                                + Lang.T("在 Timeline 面板調東西、拉進度條都不會被影片打斷。"), Small());
                GUILayout.Space(6f);

                GUILayout.Label(Lang.T("<b>診斷</b>"), Rich());
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Lang.T("重新掃描 Timeline"), GUILayout.Width(130))) TimelineBridge.Scan();
                if (GUILayout.Button(Lang.T("倒出成員→剪貼簿"), GUILayout.Width(130)))
                    GUIUtility.systemCopyBuffer = TimelineBridge.DumpMembers();
                if (GUILayout.Button(Lang.T("手動 Pause"), GUILayout.Width(90))) TimelineBridge.Pause();
                if (GUILayout.Button(Lang.T("手動 Resume"), GUILayout.Width(90))) TimelineBridge.Resume();
                if (GUILayout.Button(Lang.T("複製目前時間"), GUILayout.Width(100)))
                    GUIUtility.systemCopyBuffer = TimelineBridge.GetTime().ToString("F2");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Lang.T("重新偵測卡片路徑"), GUILayout.Width(130)))
                { ScenePathProbe.Reset(); ScenePathProbe.Detect(); }
                string cp = ScenePathProbe.Detect();
                GUILayout.Label(string.IsNullOrEmpty(cp)
                    ? Lang.T("問不到目前的卡片路徑 —— 改用總長度比對")
                    : Path.GetFileName(cp) + Lang.T("　（來源 ") + ScenePathProbe.Source + Lang.T("）"), Small());
                GUILayout.EndHorizontal();
                GUILayout.Label(Lang.T("工具列按鈕：") + Lang.T(ToolbarButton.Status), Small());
                GUILayout.Label(Lang.T("暫停方式 ") + TimelineBridge.PauseMode
                                + (TimelineBridge.CanSeek ? "" : Lang.T("　（時間軸跳不動）"))
                                + (TimelineBridge.Freezing ? Lang.T("　凍結模式作用中") : ""), Small());
                GUILayout.EndVertical();
            }

            if (CutOverlay.Instance != null)
                GUILayout.Label("Overlay: " + Lang.T(CutOverlay.Instance.Status));

            GUILayout.Label(Lang.T("播放中按 ESC 或空白鍵可跳過（滑鼠點擊不算，免得誤觸）。卡住就按「強制復原」。"), Small());

            // ---- VR 視角 ----
            GUILayout.BeginHorizontal();
            int vs = CurrentSceneIndex();
            float[] vhave;
            bool has = ViewStore.TryGet(vs, out vhave);
            // 只有一段的卡片不要寫「場景 1」—— 那看起來像有分段，
            // 會讓人以為其他段落跑到哪去了。直接說整張卡。
            int sc = SceneCount();
            GUILayout.Label(Lang.T("VR 視角：") + (sc <= 1 ? Lang.T("整張卡") : Lang.T("場景 ") + (vs + 1) + "/" + sc)
                            + (has ? Lang.T("（已存）") : Lang.T("（未存）")), GUILayout.Width(170));
            if (GUILayout.Button(Lang.T("存目前視角"), GUILayout.Width(90))) SaveViewpointHere();
            if (has && GUILayout.Button(Lang.T("刪除"), GUILayout.Width(50)))
            {
                ViewStore.Remove(vs);
                viewApplied = -1;
                message = ViewStore.LastReport;
            }
            if (has && GUILayout.Button(Lang.T("套用"), GUILayout.Width(50)))
            {
                VrLink.RequestGoto(vhave);
                viewApplied = vs;
            }
            if (cfgAutoView != null)
                cfgAutoView.Value = GUILayout.Toggle(cfgAutoView.Value, Lang.T(" 切場景自動套用"),
                                                     GUILayout.Width(120));
            GUILayout.EndHorizontal();
            GUILayout.Label(ViewStore.LastReport, Small());

            // 工具列圖示的開關擺在整個面板的最右下角，跟 F6 / F9 一致。
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            cfgToolbarButton.Value = GUILayout.Toggle(cfgToolbarButton.Value,
                                                      " " + Lang.T("顯示工具列圖示"), GUILayout.Width(130));
            GUILayout.EndHorizontal();

            DrawLangAndReset();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// Path.GetFileName 對含有非法字元的字串會丟 ArgumentException。
        /// 面板只是要顯示一行字，不該為此整個掛掉 —— 自己切最後一段就好。
        /// </summary>
        static string SafeName(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { return Path.GetFileName(p); }
            catch
            {
                int i = p.LastIndexOfAny(new char[] { '/', '\\' });
                return i >= 0 && i < p.Length - 1 ? p.Substring(i + 1) : p;
            }
        }

        // 這幾個樣式是從 GUI.skin 複製出來的，所以「照哪一份 skin 複製的」要記住。
        //
        // 原本只判斷 null：第一次畫面板如果是在桌面模式，抄到的就是 Unity 內建 skin
        // 的 label／button —— 半透明底、偏灰的字。後來進 VR、VrSkin 換上不透明 skin，
        // 這幾個樣式還是舊的那一份，於是面板上用到它們的地方（說明文字、圖示按鈕）
        // 看起來完全沒被換到。反過來也一樣：VR 裡建好之後回桌面會繼續用 VR 那份。
        // 記住來源 skin、不一樣就重建，兩個方向都對。
        static GUISkin _styleSkin;
        static void CheckSkin()
        {
            if (_styleSkin == GUI.skin) return;
            _styleSkin = GUI.skin;
            _iconBtn = null; _richBtn = null; _rich = null; _small = null;
        }

        static GUIStyle _iconBtn;
        static GUIStyle IconBtn()
        {
            CheckSkin();
            if (_iconBtn == null)
            {
                _iconBtn = new GUIStyle(GUI.skin.button);
                _iconBtn.padding = new RectOffset(2, 2, 2, 2);
                _iconBtn.imagePosition = ImagePosition.ImageOnly;
            }
            return _iconBtn;
        }

        static GUIStyle _richBtn;
        static GUIStyle RichBtn()
        {
            CheckSkin();
            if (_richBtn == null) { _richBtn = new GUIStyle(GUI.skin.button); _richBtn.richText = true; }
            return _richBtn;
        }

        static GUIStyle _rich, _small;
        static GUIStyle Rich()
        {
            CheckSkin();
            if (_rich == null) { _rich = new GUIStyle(GUI.skin.label); _rich.richText = true; }
            return _rich;
        }

        // ------------------------------------------------------------ 進度條
        //
        // IMGUI 沒有現成的「可拖曳 + 有章節標記」的元件，所以自己畫。
        // 用 GUILayoutUtility.GetRect 佔位置，再用一張 1x1 白圖 + GUI.color 上色
        // 把每一塊填出來 —— 這是 IMGUI 裡畫實色方塊最省事也最不會出事的作法。

        bool scrubbing;
        float scrubT;
        float nextScrubSeek;

        static Texture2D _px;
        static Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                _px.hideFlags = HideFlags.HideAndDontSave;
                _px.wrapMode = TextureWrapMode.Clamp;
            }
            return _px;
        }

        static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Px());
            GUI.color = old;
        }

        /// <summary>
        /// 章節標記。真的畫成三角形 —— 用四條寬度遞減的橫線疊出來，
        /// 比拉一根直線更像播放器上的那個箭頭，成本也只有四次 DrawTexture。
        /// </summary>
        static void Tri(float cx, float top, float w, float h, Color c, bool pointDown)
        {
            int rows = Mathf.Max(2, Mathf.RoundToInt(h));
            for (int i = 0; i < rows; i++)
            {
                float k = pointDown ? (float)i / (rows - 1) : 1f - (float)i / (rows - 1);
                float ww = w * (1f - k);
                if (ww < 1f) ww = 1f;
                Fill(new Rect(cx - ww * 0.5f, top + i, ww, 1f), c);
            }
        }

        /// <summary>
        /// 進度條上的時間。用純秒數，跟面板最上面那一行和「段落」清單一致 ——
        /// 看到 148.63s 就能直接對上清單裡那一列，換成 02:28.63 反而要心算。
        /// </summary>
        static string Clock(float s)
        {
            if (s < 0f) s = 0f;
            return s.ToString("F2") + "s";
        }

        /// <summary>
        /// 可拖曳的進度條，上面帶每一段場景的章節標記。
        ///
        /// 拖的時候是邊拖邊跳（節流到每 0.05 秒一次），放開再補最後一次 ——
        /// 一大張合併卡每一次 seek 都要重算所有軌道，每幀都跳會直接卡住。
        /// </summary>
        void DrawSeekBar()
        {
            // 控制項 ID 一定要每一次 OnGUI 都拿，而且順序不能變，
            // 否則 IMGUI 會把事件派到錯的元件上（症狀是拖一拖就失去焦點）。
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Rect row = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));

            float dur = TimelineBridge.Ready ? TimelineBridge.GetDuration() : -1f;
            if (dur <= 0f)
            {
                if (Event.current.type == EventType.Repaint)
                    GUI.Label(row, Lang.T("<color=#7f8c8d>時間軸未就緒，進度條無法使用</color>"), Rich());
                scrubbing = false;
                return;
            }

            float now = TimelineBridge.GetTime();
            if (now < 0f) now = 0f;
            float shown = scrubbing ? scrubT : now;

            // 過場中時間軸被釘住，這件事本來寫在面板最上面那一排，那一排拿掉了，
            // 所以挪到這裡 —— 不然「拖了進度條卻沒反應」會變成沒有線索的怪事。
            bool held = TimelineBridge.Holding;
            float LEFT = 78f, RIGHT = held ? 116f : 70f;
            Rect bar = new Rect(row.x + LEFT, row.y + 13f,
                                Mathf.Max(20f, row.width - LEFT - RIGHT), 6f);
            Rect hit = new Rect(bar.x - 6f, row.y, bar.width + 12f, row.height);

            // --- 事件 ---
            Event ev = Event.current;
            EventType et = ev.GetTypeForControl(id);
            if (et == EventType.MouseDown && ev.button == 0 && hit.Contains(ev.mousePosition))
            {
                GUIUtility.hotControl = id;
                scrubbing = true;
                scrubT = PosToTime(ev.mousePosition.x, bar, dur);
                ApplyScrub(scrubT, false);
                ev.Use();
            }
            else if (et == EventType.MouseDrag && GUIUtility.hotControl == id)
            {
                scrubT = PosToTime(ev.mousePosition.x, bar, dur);
                ApplyScrub(scrubT, false);
                ev.Use();
            }
            else if (et == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                scrubbing = false;
                ApplyScrub(scrubT, true);
                ev.Use();
            }

            if (ev.type != EventType.Repaint) return;

            // --- 畫 ---
            Fill(new Rect(bar.x, bar.y, bar.width, bar.height), new Color(1f, 1f, 1f, 0.18f));
            float px = bar.width * Mathf.Clamp01(shown / dur);
            Fill(new Rect(bar.x, bar.y, px, bar.height), new Color(0.31f, 0.67f, 0.96f, 0.95f));

            // 章節：每一段場景的起點。跟「段落」清單裡那幾列「場景 N 開始」同一份資料。
            if (cfg != null && cfg.tracks != null)
            {
                var mark = new Color(0.31f, 0.67f, 0.96f, 1f);
                for (int i = 0; i < cfg.tracks.Length; i++)
                {
                    float f = cfg.tracks[i].from;
                    if (f <= 0.001f || f >= dur) continue;
                    float cx = bar.x + bar.width * (f / dur);
                    Tri(cx, bar.y - 7f, 7f, 5f, mark, true);    // ▼ 在上
                    Tri(cx, bar.yMax + 2f, 7f, 5f, mark, false); // ▲ 在下
                }
            }

            // 播放頭
            float hx = bar.x + px;
            Fill(new Rect(hx - 1.5f, bar.y - 5f, 3f, bar.height + 10f),
                 scrubbing ? Color.white : new Color(1f, 1f, 1f, 0.9f));

            // 時間：左邊是現在、右邊是總長
            GUI.Label(new Rect(row.x, row.y + 5f, LEFT - 6f, 20f),
                      (scrubbing ? "<color=#ffffff><b>" : "") + Clock(shown)
                      + (scrubbing ? "</b></color>" : ""), Rich());
            GUI.Label(new Rect(bar.xMax + 8f, row.y + 5f, RIGHT - 10f, 20f),
                      "<color=#95a5a6>" + Clock(dur) + "</color>"
                      + (held ? Lang.T("　<color=#2980b9>過場中</color>") : ""), Rich());
        }

        static float PosToTime(float mx, Rect bar, float dur)
        {
            float k = Mathf.Clamp01((mx - bar.x) / Mathf.Max(1f, bar.width));
            return k * dur;
        }

        /// <summary>
        /// 進度條拖到某一點。
        ///
        /// 拖到哪裡就從哪裡繼續，**不動播放／暫停狀態** —— 播著拖就是播著跳過去，
        /// 停著拖就是停著看那一格，跟一般播放器一樣。
        /// </summary>
        void ApplyScrub(float t, bool final)
        {
            if (!final && Time.realtimeSinceStartup < nextScrubSeek) return;
            nextScrubSeek = Time.realtimeSinceStartup + 0.05f;

            // 過場播到一半去拉進度條＝不想看了，收掉它。釘住也要一起鬆開，
            // 不然下一幀 TickFreeze 會把時間寫回過場開始的位置。
            var ov = CutOverlay.Instance;
            if (ov != null && ov.IsBusy) ov.Stop();
            TimelineBridge.Release();

            if (final) TimelineBridge.EnsureSeekable();
            TimelineBridge.SetTime(t);
            lastTime = t;

            // 拉過去之後，前面的過場一律算已播、後面的一律恢復待播。
            // 不標的話時間軸一動就會把沿路經過的每一支過場全部補播一遍。
            if (cfg != null && cfg.cuts != null)
                for (int i = 0; i < cfg.cuts.Length; i++)
                    if (cfg.cuts[i] != null) cfg.cuts[i].done = cfg.cuts[i].t < t - 0.01f;
        }

        // ------------------------------------------------------------ 語言與重置

        ConfigEntry<int> cfgLang;
        float resetArmedUntil;

        /// <summary>
        /// 設置視窗最底下那一列：左邊語言、右邊重置為預設。
        ///
        /// 重置刻意做成兩段式（按一次變成「再按一次確認」，3 秒內沒再按就取消）——
        /// 這顆鈕會把所有設定一次抹掉，而它就在關閉鈕旁邊，誤觸的代價太大。
        /// </summary>
        void DrawLangAndReset()
        {
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();

            if (GUILayout.Button(Lang.ButtonLabel, GUILayout.Width(170), GUILayout.Height(22)))
            {
                cfgLang.Value = Lang.Next(Lang.Current);
                Lang.Set(cfgLang.Value);
            }

            GUILayout.FlexibleSpace();

            bool armed = Time.realtimeSinceStartup < resetArmedUntil;
            if (GUILayout.Button(armed ? Lang.T("再按一次確認") : Lang.T("重置為預設"),
                                 GUILayout.Width(armed ? 170f : 130f), GUILayout.Height(22)))
            {
                if (armed) { resetArmedUntil = 0f; ResetConfigToDefaults(); }
                else resetArmedUntil = Time.realtimeSinceStartup + 3f;
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 把這支插件的所有設定還原成預設值 —— 包含沒有畫在面板上的那些。
        ///
        /// 直接走 BepInEx 的設定表而不是一條一條寫死：漏掉一條不會有任何提示，
        /// 而「重置之後還有東西沒回到預設」是最難查的那種問題。
        /// 走設定表的話，以後新增設定也自動涵蓋。
        /// </summary>
        void ResetConfigToDefaults()
        {
            int n = 0;
            try
            {
                foreach (var kv in Config)
                {
                    try
                    {
                        if (kv.Value == null || kv.Value.DefaultValue == null) continue;
                        kv.Value.BoxedValue = kv.Value.DefaultValue;
                        n++;
                    }
                    catch { }
                }
                Config.Save();
            }
            catch (Exception e)
            {
                Logger.LogWarning("[Reset] 還原預設值時出錯：" + e.Message);
            }
            Logger.LogInfo("[Reset] 已把 " + n + " 項設定還原成預設值");
        }

        static GUIStyle Small()
        {
            CheckSkin();
            // 這裡原本有 fontSize = 10。字級調整一律拿掉 ——
            // 說明文字在頭顯裡本來就最難讀，再縮只會更糟。
            if (_small == null) { _small = new GUIStyle(GUI.skin.label); _small.wordWrap = true; }
            return _small;
        }
    }
}
