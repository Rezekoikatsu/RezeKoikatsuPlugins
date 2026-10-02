using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace StudioCharTools
{
    /// <summary>
    /// F6：場景角色工具。
    /// 來源 = 舊 NCSuffixer 的第 8 項功能（選人 / 換裝 / 無損換身材）
    ///        + 原 BlendShapeLock 插件。
    /// 按 F6 直接開啟「選擇場景角色」清單，每一列可以換人物卡、換衣服、
    /// 選取到工作面板，以及開啟該角色的型態鍵鎖定視窗。
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("CharaStudio")]
    public class CharToolsPlugin : BaseUnityPlugin
    {
        public const string GUID = "reze.studio.chartools";
        public const string PluginName = "Studio Character Tools";
        public const string Version = "1.0.0";

        internal static CharToolsPlugin Instance;

        /// <summary>ChaFileDefine.BodyShapeIdx.Height —— 體型滑桿 0，驅動 cf_n_height。</summary>
        const int ShapeIdxHeight = (int)ChaFileDefine.BodyShapeIdx.Height;

        /// <summary>換人時「舊卡的什麼要留下來」。</summary>
        public enum SwapMode
        {
            /// <summary>保留舊卡身材：100 根滑桿 + ABMX + 胸托基準全部帶回去（原本的「完美換人」）。</summary>
            KeepOldBody = 0,
            /// <summary>鎖身高：只留 Height 一根，其餘讓新卡做自己。</summary>
            LockHeight = 1,
            /// <summary>維持新卡身材：只留會讓姿勢錯位的那幾根（見 ProportionShape）。</summary>
            KeepNewBody = 2,
            /// <summary>一般替換：什麼都不留。</summary>
            Plain = 3,
        }

        /// <summary>
        /// 「維持新卡身材」要從舊卡鎖回來的滑桿。
        ///
        /// 為什麼是這五根：KK 的 44 根體型滑桿裡**沒有任何長度滑桿** ——
        /// 命名規則 W = 幅、Z = 前後，所以 NeckW/NeckZ 是脖子粗細不是脖子長度，
        /// ThighUpW/ElbowZ 之類全是圍度。手腳與脖子的長度在 KK 屬於 KKABMX 的
        /// 骨頭縮放，而這個模式刻意不留 ABMX（留了會連胸部骨一起蓋掉新卡身材，
        /// 那就退化成 KeepOldBody 了）。
        ///
        /// 所以真正會移動關節位置、造成姿勢錯位的只有這幾根：整體縮放、頭的縮放、
        /// 腰的垂直位置（上下半身比例），以及肩寬（手臂根部位置，手有接觸的姿勢會跑）。
        /// 其餘 39 根是圍度與形狀（胸、腰、臀、小腿粗細…），全部讓新卡做自己。
        ///
        /// 直接寫列舉成員而不是數字：萬一遊戲版本換了、成員不在了，會在編譯時就炸，
        /// 不會變成安靜地對到錯的滑桿 —— 那種錯只會表現成「換出來的人偏一點點」，
        /// 查起來極痛苦。
        /// </summary>
        static readonly int[] ProportionShape =
        {
            (int)ChaFileDefine.BodyShapeIdx.Height,
            (int)ChaFileDefine.BodyShapeIdx.HeadSize,
            (int)ChaFileDefine.BodyShapeIdx.WaistY,
            (int)ChaFileDefine.BodyShapeIdx.BodyShoulderW,
            (int)ChaFileDefine.BodyShapeIdx.ShoulderW,
        };

        /// <summary>這個模式要從舊卡存哪幾根滑桿。null = 全部 100 根。</summary>
        static int[] ShapeIndicesFor(SwapMode mode)
        {
            switch (mode)
            {
                case SwapMode.KeepOldBody: return null;
                case SwapMode.LockHeight: return new[] { ShapeIdxHeight };
                case SwapMode.KeepNewBody: return ProportionShape;
                default: return new int[0];          // Plain
            }
        }

        /// <summary>只有「保留舊卡身材」要連 ABMX、胸托基準、臉骨 modifier 修正一起處理。</summary>
        static bool KeepsFullBody(SwapMode mode) { return mode == SwapMode.KeepOldBody; }

        public static string SwapModeName(SwapMode mode)
        {
            switch (mode)
            {
                case SwapMode.KeepOldBody: return Lang.T("保留舊卡身材");
                case SwapMode.LockHeight: return Lang.T("鎖身高");
                case SwapMode.KeepNewBody: return Lang.T("維持新卡身材");
                default: return Lang.T("一般替換");
            }
        }

        private string statusMsg = "等待操作...";

        // 側邊面板：選卡與型態鍵鎖定共用同一個貼齊位置，同時只會有一個開著
        private bool showBlendLock = false;
        private Studio.OCIChar blendTarget;
        private Rect sideRect;                  // 每幀由主視窗位置算出來
        private bool showSettings;              // 設置面板（黏在主視窗右邊）
        private Vector2 settingsScroll;
        private string jobFolder = "";          // 工單資料夾，空字串就用預設
        private Studio.OCIChar sideOci;         // 目前側邊面板服務的角色
        private int sideKind;                   // 1=人物卡 2=服裝卡 3=型態鍵
        private bool autoRestorePose = true;

        /// <summary>換人時保留場景裡的表情（眉眼嘴編號、開合、眨眼、臉紅、眼淚、視線）。</summary>
        private bool keepExpression = true;

        /// <summary>
        /// 「維持新卡身材」時，頭的大小（HeadSize 滑桿、cf_s_head / cf_j_head）要不要沿用舊卡。
        /// 關掉 = 頭也用新卡自己的比例；臉型 mod 是照新卡的頭大小做的，沿用舊卡的頭有時會怪。
        /// </summary>
        private bool keepOldHeadSize = true;
        private bool autoFixDbCollider = true;   // 換人後自動修 Dynamic Bone Collider 綁定
        internal static bool keepCharaName;
        private bool keepFinalChara = true;
        private bool mergePushup = true;      // 胸托參數跟著服裝走（Pushup 是每套換裝一份的）
        private string kkmergeExePath = "";   // 留空就自動找
        private bool hideGuiForCapture = false;
        private float hideGuiSince = -1f;   // 什麼時候被設成 true 的，給看門狗用
        private bool cropThumbToChar = true;
        private int thumbMaxHeight = 0;          // 0 = 不縮小（最清晰）
        private bool headAnchorThumb = true;     // 取景以頭為錨點
        private float thumbFrameScale = 1.3f;    // 框高 = 頭腳距離的幾倍
        private float thumbHeadAtY = 0.82f;      // 頭放在畫面的哪個高度
        private bool hideAllUiForThumb = true;
        private bool soloCharForThumb = true;
        private bool hideMalesForThumb = false;
        private float settleSeconds = 0.8f;
        private bool openFolderAfterSave = true;
        private float mergeTimeout = 300f;
        internal static bool mergeJobRunning;
        private bool mergeCancel;
        private bool mergeWaiting;
        private bool batchRunning;
        private string lastSavedPath;

        /// <summary>
        /// 「維持新卡身材」要從舊卡保留哪幾根 ABMX 骨頭（逗號分隔）。
        /// 空的＝一根都不留（跟以前一樣）。
        ///
        /// 骨頭名字**不要從網路抄**：查到的清單大小寫和編號常常對不上實際的骨架，
        /// 名字錯一個字母就是靜靜地留下零根，換出來「好像哪裡不對」但查不到原因。
        /// 換人時會把舊卡身上實際有的 modifier 名單傾印到 BepInEx log，照那份填。
        /// </summary>
        private string abmxKeepBones = AbmxFilter.DefaultKeepRules;

        /// <summary>換完人之後，自動帶入跟新角色同名的形態鍵預設。</summary>
        private bool autoApplyBlendPreset = true;
        /// <summary>換人時帶入場景原角色的著色器：0 = 關、1 = 每次詢問、2 = 自動。</summary>
        private int carryShaderMode = 1;
        private bool showCarryPrompt;
        private Action<bool> carryPromptAct;
        private string carryPromptWho = "";
        private Rect carryPromptRect = new Rect(0, 0, 420, 150);
        private bool keepSwapCarry;

        // 飾品面板
        private bool showAccPanel = false;
        private Studio.OCIChar accTarget;

        // 碰撞器修復面板
        private bool showDbFix = false;
        private Studio.OCIChar dbFixTarget;
        private Vector2 dbFixScroll;
        private string dbFixText = "";

        private Vector2 accScroll, accCatScroll;
        private string accCategory = "";
        private bool accIncludeEmpty = false;
        // 整組縮放
        private bool accScaleOpen = false;
        private readonly HashSet<int> accScaleSel = new HashSet<int>();
        private Studio.OCIChar accScaleSelFor;
        private Vector3 accScl = Vector3.one, accOff = Vector3.zero;
        private bool accScaleUniform = true, accScaleHead = false;
        private string accSyncWarn = "";
        private AccessoryTools.ScaleSession accSession, accLastCommitted;
        private string accSessionKey = "";
        private Vector3 accLastScl = Vector3.one, accLastOff = Vector3.zero;
        private bool accHasLast = false;
        private string accScaleRef = "cf_j_head";
        private float statusTime;
        private Vector2 statusScroll;

        private Harmony _harmony;

        void Awake()
        {
            Instance = this;
            try
            {
                _harmony = Harmony.CreateAndPatchAll(typeof(FacePtnLock), GUID + ".faceptn");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CharTools] FacePtnLock patch 失敗（不影響主功能）: " + e.Message);
            }
            BindSettings();          // 沒有這行，所有 ConfigEntry 都會是 null
            BlendShapeLock.Init();

            // FacePtnLock 掛失敗時 _harmony 會是 null，場景追蹤自己建一個
            if (_harmony == null) _harmony = new Harmony(GUID);
            PoseFix.InstallSceneTracker(_harmony);
            try { KKAPI.Studio.SaveLoad.StudioSaveLoadApi.RegisterExtraBehaviour<BlendShapeLockSceneController>(GUID); }
            catch (Exception e) { Logger.LogWarning("[CharTools] 場景存檔註冊失敗: " + e.Message); }

            // Studio 左邊工具列的按鈕（跟 Timeline、F7 那幾顆同一排）。
            // 沒裝 KKAPI 也不會怎樣，只是少一顆按鈕。
            ToolbarButton.Create(delegate(bool on)
            {
                showCharPicker = on;
                if (!showCharPicker)
                {
                    if (showSettings) SaveSettingsPanel();
                    CloseSidePanel();
                }
                else charPickerScroll = Vector2.zero;
            }, showCharPicker);
            toolbarRetryUntil = Time.realtimeSinceStartup + 30f;

            // 這一行以前沒有，結果「F6 在 VR 裡打不開」完全沒辦法從 log 判斷 ——
            // 分不清是外掛沒載入、Update 丟例外、還是熱鍵沒收到。
            // F7 / F9 都有這一行，F6 也補上。
            Logger.LogInfo(PluginName + " " + Version + " 已載入，按 " + HotkeyLabel() + " 開啟面板。");
        }

        // =============================================================
        // 設定持久化：欄位照舊使用，每秒把變動寫回設定檔，
        // 這樣 UI 程式碼不用全部改成 .Value。
        // =============================================================
        BepInEx.Configuration.ConfigEntry<bool> cfgAutoRestorePose, cfgCropThumb, cfgHideAllUi,
            cfgSoloChar, cfgHideMales, cfgOpenFolder, cfgMergePushup,
            cfgCharaToTemp, cfgCoordToTemp, cfgKeepName, cfgKeepFinalChara, cfgAutoFixDbCollider,
            cfgAutoApplyBlend, cfgKeepExpression, cfgKeepOldHead;
        BepInEx.Configuration.ConfigEntry<string> cfgExePath, cfgJobFolder, cfgAbmxKeepBones;
        BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> cfgHotkey;
        BepInEx.Configuration.ConfigEntry<float> cfgSettleSeconds;
        BepInEx.Configuration.ConfigEntry<int> cfgThumbMaxH;
        BepInEx.Configuration.ConfigEntry<int> cfgCarryShader;
        BepInEx.Configuration.ConfigEntry<bool> cfgHeadAnchor;
        BepInEx.Configuration.ConfigEntry<bool> cfgToolbarButton;
        BepInEx.Configuration.ConfigEntry<float> cfgFrameScale, cfgHeadAtY;
        float cfgSyncTime;

        void BindSettings()
        {
            // VR 介面外觀的設定已經搬到 F9（Studio VR Tools）統一管理。
            // 這裡原本各自綁一份，三支插件三個地方要改，而且很容易改到不同步。
            // 現在 F9 用 AppDomain 的共用資料槽公布設定，這邊 VrSkin.Follow() 跟著走；
            // F9 沒安裝的話 Follow() 會退回本地預設值，單獨安裝照常運作。
            cfgAutoRestorePose = Config.Bind("Save", "Keep Pose On Swap", true, "");
            cfgCropThumb = Config.Bind("Thumbnail", "Crop To Character", true, "");
            cfgHideAllUi = Config.Bind("Thumbnail", "Hide UI While Capturing", true, "");
            cfgSoloChar = Config.Bind("Thumbnail", "Solo Character", true, "");
            cfgHideMales = Config.Bind("Thumbnail", "Hide Males Too", false, "");
            cfgSettleSeconds = Config.Bind("Thumbnail", "Settle Seconds", 0.8f, "");
            cfgThumbMaxH = Config.Bind("Thumbnail", "Max Height", 0,
                "0 = 不縮小，直接用畫面上原始的畫素（最清晰，也是預設）。\n"
                + "舊版一律縮成 252×352，1080p 縮下去是 3 倍以上的縮小，"
                + "服裝的細紋和花色會糊掉。填數字才會縮，例如 704 會變成 504×704，"
                + "而且用面積平均，同樣尺寸也比舊版乾淨");
            cfgHeadAnchor = Config.Bind("Thumbnail", "Anchor On Head", true,
                "舊做法是把角色全部 Renderer 的包圍盒聯集當取景範圍，"
                + "飾品、特效或某個算壞的 bounds 都會把框整個拖走，"
                + "結果拍出一張背景、頭卡在角落。改用骨頭座標當錨點就不會被污染");
            cfgFrameScale = Config.Bind("Thumbnail", "Frame Scale", 1.3f,
                "1.3 左右全身入鏡，0.6 半身，0.25 大頭照。"
                + "頭腳距離是兩個骨頭座標算出來的精確值，不是包圍盒的估計值");
            cfgHeadAtY = Config.Bind("Thumbnail", "Head Y In Frame", 0.5f,
                "1 = 貼著上緣，0.5 = 正中央，0 = 貼著下緣。"
                + "頭骨頭的原點在頸子上緣、不在頭正中心，那個固定位移由這條吸收");
            cfgOpenFolder = Config.Bind("Save", "Reveal In Explorer", true, "");
            cfgLang = Config.Bind("General", "Language", 0,
                "0 = 繁體中文（預設）　1 = English　2 = 日本語。\n"
                + "面板最下方的 Language 按鈕也可以切，三支插件會一起換");
            Lang.Set(cfgLang.Value);
            cfgToolbarButton = Config.Bind("Interface", "Show Toolbar Button", true,
                "工作室左邊那排工具列上那顆白色小人（右下角有個 R）。"
                + "關掉就只剩熱鍵開面板。改完立刻生效，不用重開遊戲");
            cfgMergePushup = Config.Bind("Merge", "Pushup Follows Outfit", true,
                "Pushup 的胸托資料是每套換裝一份的。關掉就保留目標人物自己的");
            cfgExePath = Config.Bind("Merge", "KKMerge Path", "",
                "留空就自動找 BepInEx\\plugins、遊戲根目錄、UserData\\kkbridge");
            cfgHotkey = Config.Bind("Hotkeys", "Toggle Panel",
                new BepInEx.Configuration.KeyboardShortcut(KeyCode.F6),
                "在這裡改，改完立刻生效，不用重開遊戲");
            cfgJobFolder = Config.Bind("Merge", "Job Folder", "",
                "kkbridge 監看的資料夾。留空就用 UserData\\chara\\female\\Temp");
            cfgKeepName = Config.Bind("Swap", "Keep Original Name", false, "");
            cfgAutoFixDbCollider = Config.Bind("Swap", "Auto Fix Colliders", true,
                "換人後把 KKPE 的 Dynamic Bone Collider 綁定還原成換人前的樣子。"
                + "關掉的話換完人 J694 這類碰撞器會吸住新角色的全部動骨");
            cfgKeepFinalChara = Config.Bind("Merge", "Keep Final Card", false, "");
            cfgCharaToTemp = Config.Bind("Save", "Chara Card To Temp", true, "");
            cfgCoordToTemp = Config.Bind("Save", "Coordinate Card To Temp", true, "");
            cfgAutoApplyBlend = Config.Bind("Swap", "Auto Apply Blendshapes", true, "");
            cfgCarryShader = Config.Bind("Swap", "Carry Scene Shaders", 1,
                "換人時把場景原角色的著色器（MaterialEditor）帶到新角色：0 = 關、1 = 每次詢問、2 = 自動");
            cfgKeepExpression = Config.Bind("Swap", "Keep Expression On Swap", true,
                "換人後把眉／眼／嘴表情、開合、眨眼、臉紅、眼淚、視線改回換人前的樣子。關掉 = 用新卡自己存的表情");
            cfgKeepOldHead = Config.Bind("Swap", "KeepNewBody Keep Old Head Size", true,
                "「維持新卡身材」時頭的大小沿用舊卡（HeadSize 滑桿、cf_s_head、cf_j_head）。關掉 = 頭也用新卡的");
            // 這一條會一直微調（哪幾根留、哪幾根不留要看實卡試），
            // 不存進 config 的話每次開遊戲都被打回預設，調過的全白費。
            cfgAbmxKeepBones = Config.Bind("Swap", "ABMX Keep Rules",
                AbmxFilter.DefaultKeepRules,
                "逗號分隔。cf_j_* 前綴比對、cf_s_head 完整名稱、-cf_j_ana 排除。"
                + "大小寫有差：cf_j_ 是骨架、cf_J_ 是臉。留空 = 一根都不留");

            autoRestorePose = cfgAutoRestorePose.Value;
            cropThumbToChar = cfgCropThumb.Value;
            hideAllUiForThumb = cfgHideAllUi.Value;
            soloCharForThumb = cfgSoloChar.Value;
            hideMalesForThumb = cfgHideMales.Value;
            settleSeconds = cfgSettleSeconds.Value;
            thumbMaxHeight = cfgThumbMaxH.Value;
            headAnchorThumb = cfgHeadAnchor.Value;
            thumbFrameScale = cfgFrameScale.Value;
            thumbHeadAtY = cfgHeadAtY.Value;
            ApplyThumbSettings();
            openFolderAfterSave = cfgOpenFolder.Value;
            mergePushup = cfgMergePushup.Value;
            kkmergeExePath = cfgExePath.Value;
            KKMerge.ExeOverride = kkmergeExePath;
            jobFolder = cfgJobFolder.Value;
            KKMerge.JobFolderOverride = jobFolder;
            keepCharaName = cfgKeepName.Value;
            autoFixDbCollider = cfgAutoFixDbCollider.Value;
            keepFinalChara = cfgKeepFinalChara.Value;
            CardSaver.CharaToTemp = cfgCharaToTemp.Value;
            CardSaver.CoordToTemp = cfgCoordToTemp.Value;
            autoApplyBlendPreset = cfgAutoApplyBlend.Value;
            carryShaderMode = Mathf.Clamp(cfgCarryShader.Value, 0, 2);
            keepExpression = cfgKeepExpression.Value;
            keepOldHeadSize = cfgKeepOldHead.Value;
            abmxKeepBones = cfgAbmxKeepBones.Value ?? "";
            showToolbarButton = cfgToolbarButton.Value;
        }

        /// <summary>工具列按鈕要不要顯示。設置面板的欄位，由 SyncSettings 寫回設定檔。</summary>
        bool showToolbarButton = true;
        bool toolbarButtonShown = true;

        float toolbarRetryUntil;
        float toolbarNextTry;

        /// <summary>
        /// 勾選狀態改了就立刻套用。
        ///
        /// 刻意不是「關掉就不建立」—— 那樣要重開遊戲才看得到變化。
        /// 一律建立、再決定顯不顯示，改完當下就生效。
        ///
        /// 開頭那三十秒要重複套用：KKAPI 的工具列是等工作室載完才排版的，
        /// 我們在 Awake 建按鈕、設定又是關的時候，第一次 SetVisible 很可能
        /// 打在還不存在的控制項上，然後按鈕就這樣冒出來了。
        /// </summary>
        void ApplyToolbarButton()
        {
            bool changed = toolbarButtonShown != showToolbarButton;
            bool retry = !showToolbarButton
                         && Time.realtimeSinceStartup < toolbarRetryUntil
                         && Time.realtimeSinceStartup >= toolbarNextTry;
            if (!changed && !retry) return;

            toolbarButtonShown = showToolbarButton;
            toolbarNextTry = Time.realtimeSinceStartup + 0.5f;
            ToolbarButton.SetVisible(showToolbarButton);
        }

        float nextTint;

        /// <summary>
        /// 把工具列按鈕按下去的顏色從綠色改成黃色。
        /// 綠色在頭顯裡通常代表 Passthrough，撞在一起會誤會。
        ///
        /// KKAPI 只在狀態改變時上色，所以每 0.5 秒補一次就夠，不必每幀。
        /// </summary>
        void TickToolbarTint()
        {
            if (Time.realtimeSinceStartup < nextTint) return;
            nextTint = Time.realtimeSinceStartup + 0.5f;
            ToolbarButton.TintToggled(new Color(1f, 0.82f, 0.2f, 1f));
        }

        void SyncSettings()
        {
            if (cfgAutoRestorePose == null) return;     // BindSettings 還沒跑
            if (Time.realtimeSinceStartup - cfgSyncTime < 1f) return;
            cfgSyncTime = Time.realtimeSinceStartup;

            cfgAutoRestorePose.Value = autoRestorePose;
            cfgCropThumb.Value = cropThumbToChar;
            cfgHideAllUi.Value = hideAllUiForThumb;
            cfgSoloChar.Value = soloCharForThumb;
            cfgHideMales.Value = hideMalesForThumb;
            cfgSettleSeconds.Value = settleSeconds;
            cfgThumbMaxH.Value = thumbMaxHeight;
            cfgHeadAnchor.Value = headAnchorThumb;
            cfgFrameScale.Value = thumbFrameScale;
            cfgHeadAtY.Value = thumbHeadAtY;
            cfgOpenFolder.Value = openFolderAfterSave;
            cfgMergePushup.Value = mergePushup;
            cfgJobFolder.Value = jobFolder ?? "";
            cfgKeepName.Value = keepCharaName;
            cfgAutoFixDbCollider.Value = autoFixDbCollider;
            cfgKeepFinalChara.Value = keepFinalChara;
            cfgCharaToTemp.Value = CardSaver.CharaToTemp;
            cfgCoordToTemp.Value = CardSaver.CoordToTemp;
            cfgAutoApplyBlend.Value = autoApplyBlendPreset;
            cfgCarryShader.Value = carryShaderMode;
            cfgKeepExpression.Value = keepExpression;
            cfgKeepOldHead.Value = keepOldHeadSize;
            cfgAbmxKeepBones.Value = abmxKeepBones ?? "";
            cfgToolbarButton.Value = showToolbarButton;
        }

        void OnDestroy()
        {
            BlendShapeLock.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        // ===== merged from HSPEBlendLinkNullFix =====
        // Fixes ArgumentNullException in
        //   HSPE.AMModules.BlendShapesEditor+BlendRenderer.ApplyLink(float, int, bool)
        // Root cause: ApplyLink looks up a blend-shape name in a dictionary
        // without checking whether that name came back null (which happens
        // whenever the PREVIOUS character's blend shape name at this index
        // has no match on the newly-loaded, heavily custom-named head).
        // Runs from Start() (not Awake()) so HSPE's own Awake() has already
        // finished setting itself up by the time this patch is applied.
        void Start()
        {
            try
            {
                var harmony = new Harmony("com.yourname.studiotoolbox.hspeblendlinknullfix");

                Type blendShapesEditorType = AccessTools.TypeByName("HSPE.AMModules.BlendShapesEditor");
                if (blendShapesEditorType == null)
                {
                    Logger.LogWarning("[HspeFix] Could not find HSPE.AMModules.BlendShapesEditor - is HSPE installed/loaded?");
                    return;
                }

                Type blendRendererType = AccessTools.Inner(blendShapesEditorType, "BlendRenderer");
                if (blendRendererType == null)
                {
                    Logger.LogWarning("[HspeFix] Could not find nested type BlendRenderer inside BlendShapesEditor.");
                    return;
                }

                MethodInfo original = AccessTools.Method(
                    blendRendererType,
                    "ApplyLink",
                    new[] { typeof(float), typeof(int), typeof(bool) });
                if (original == null)
                {
                    Logger.LogWarning("[HspeFix] Could not find ApplyLink(float, int, bool) on BlendRenderer.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(HspeBlendLinkPatches), nameof(HspeBlendLinkPatches.ApplyLinkPrefix));
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                Logger.LogInfo("[HspeFix] HSPE BlendLink Null Fix applied.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[HspeFix] Setup failed: " + e);
            }
        }

        /// <summary>標題列顯示目前設定的快捷鍵。</summary>
        string HotkeyLabel()
        {
            try { return cfgHotkey == null ? "F6" : cfgHotkey.Value.ToString(); }
            catch { return "F6"; }
        }

        /// <summary>
        /// 熱鍵。
        ///
        /// 為什麼不用 KeyboardShortcut.IsDown()
        /// ----------------------------------
        /// 這是「F6 在 VR 裡打不開，但 F7 / F9 都打得開」的唯一差異：
        /// F7 / F9 用的是原始的 Input.GetKeyDown(KeyCode)，只有 F6 走
        /// BepInEx 的 KeyboardShortcut.IsDown()。那個方法有兩點會讓它安靜失效：
        ///
        ///   1. 它讀的是 UnityInput.Current，不是 Unity 的 Input ——
        ///      BepInEx.InputHotkeyBlock 這類外掛會換掉它，判定「正在打字」時
        ///      所有熱鍵一律回 false。
        ///   2. 它會要求**沒有設定在快捷鍵裡的修飾鍵一個都不能按著**。
        ///      VR 下只要有東西讓某個修飾鍵一直處於按下狀態，這個條件就永遠不成立。
        ///
        /// 兩種情況都不會有任何錯誤訊息，就只是按了沒反應。
        /// 所以主鍵改讀原始 Input，修飾鍵自己檢查 —— 設定格式不變。
        /// </summary>
        bool HotkeyDown()
        {
            if (cfgHotkey == null) return Input.GetKeyDown(KeyCode.F6);

            var sc = cfgHotkey.Value;
            KeyCode main = sc.MainKey;
            if (main == KeyCode.None || !Input.GetKeyDown(main)) return false;

            foreach (KeyCode m in sc.Modifiers)
                if (!Input.GetKey(m)) return false;
            return true;
        }

        void Update()
        {
            // 別的插件面板上換了語言就跟著換
            int langNow;
            Lang.Follow(cfgLang.Value, out langNow);
            if (langNow != cfgLang.Value) cfgLang.Value = langNow;

            // SyncSettings 丟例外的話，後面的熱鍵判斷就永遠跑不到 ——
            // 症狀一樣是「F6 按了沒反應」，而且 Catch Unity Event Exceptions
            // 會把例外吃掉，log 裡什麼都看不到。所以各自包，錯一次記一次。
            try { SyncSettings(); ApplyToolbarButton(); TickToolbarTint(); }
            catch (Exception e) { Warn("SyncSettings", e); }

            try
            {
                if (HotkeyDown())
                {
                    showCharPicker = !showCharPicker;
                    ToolbarButton.Sync(showCharPicker);
                    Logger.LogInfo("[CharTools] 面板" + (showCharPicker ? "開啟" : "關閉"));
                    if (!showCharPicker)
                    {
                        if (showSettings) SaveSettingsPanel();   // 關窗前先存
                        CloseSidePanel();
                    }
                    else charPickerScroll = Vector2.zero;
                }
            }
            catch (Exception e) { Warn("Update", e); }
        }

        // 同一個地方的例外只記第一次。每幀記一行會把 log 洗掉，
        // 而且真正有用的資訊就是第一次那一筆。
        readonly System.Collections.Generic.HashSet<string> warned
            = new System.Collections.Generic.HashSet<string>();

        void Warn(string where, Exception e)
        {
            if (!warned.Add(where)) return;
            Logger.LogWarning("[CharTools] " + where + " 丟例外（之後同一處不再重複記）："
                              + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
        }


        void OnGUI()
        {
            // 擷圖期間要把自己的視窗藏起來，但**藏起來之後一定要回得來**。
            //
            // 這個旗標是在協程裡設 true、拍完再設 false 的。協程只要中途斷掉
            // （例外、場景切換、或 VR 下 WaitForEndOfFrame 沒有照預期回來），
            // 它就永遠是 true —— 表現出來就是「F6 按了完全沒反應，面板再也出不來」，
            // 而且不會有任何錯誤訊息。實測在 VR 模式下就是這個症狀。
            //
            // 擷一張圖最多就幾秒，卡超過 5 秒必定是壞掉了，直接自己解開。
            if (hideGuiForCapture)
            {
                if (hideGuiSince > 0f && Time.realtimeSinceStartup - hideGuiSince > 5f)
                {
                    Logger.LogWarning("[CharTools] 擷圖旗標卡住超過 5 秒，自動解除"
                                      + "（協程中斷了？介面本來會一直出不來）");
                    hideGuiForCapture = false;
                    hideGuiSince = -1f;
                    try { ScreenGrab.RestoreUI(); } catch { }
                }
                else return;
            }

            VrSkin.Follow();   // 設定統一由 F9 管，見 VrSkin.Follow 的註解
            GUISkin savedSkin = VrSkin.Begin();
            try { DrawAllWindows(); }
            catch (Exception e) { Warn("OnGUI", e); }
            finally { VrSkin.End(savedSkin); }
        }

        void DrawAllWindows()
        {

            if (showCarryPrompt)
                carryPromptRect = GUILayout.Window(8898, carryPromptRect, CarryPromptWindow, Lang.T("套用著色器？"));
            if (showCharPicker)
                charPickerRect = GUILayout.Window(8889, charPickerRect, CharPickerWindow,
                    Lang.T("選擇場景角色 (") + HotkeyLabel() + ")");

            if (showGenericCardPicker)
            {
                sideRect = new Rect(charPickerRect.x + charPickerRect.width + 8, charPickerRect.y,
                    genericCardPickerRect.width, Mathf.Max(charPickerRect.height, 520f));
                GUILayout.Window(8890, sideRect, DrawGenericCardPicker, genericCardPickerTitle);
            }
            else if (showAccPanel)
            {
                sideRect = new Rect(charPickerRect.x + charPickerRect.width + 8, charPickerRect.y,
                    520f, Mathf.Max(charPickerRect.height, 620f));
                GUILayout.Window(8893, sideRect, AccessoryWindow, Lang.T("飾品欄管理"));
            }
            else if (showBlendLock)
            {
                sideRect = new Rect(charPickerRect.x + charPickerRect.width + 8, charPickerRect.y,
                    560f, Mathf.Max(charPickerRect.height, 620f));
                GUILayout.Window(8891, sideRect, BlendLockWindow, Lang.T("型態鍵鎖定"));
            }
            else if (showDbFix)
            {
                sideRect = new Rect(charPickerRect.x + charPickerRect.width + 8, charPickerRect.y,
                    620f, Mathf.Max(charPickerRect.height, 620f));
                GUILayout.Window(8895, sideRect, DbColliderWindow, Lang.T("碰撞器綁定修復"));
            }
            else if (showSettings)
            {
                sideRect = new Rect(charPickerRect.x + charPickerRect.width + 8, charPickerRect.y,
                    560f, Mathf.Max(charPickerRect.height, 560f));
                GUILayout.Window(8894, sideRect, SettingsWindow, Lang.T("設置"));
            }

        }

        /// <summary>把面板上的縮圖設定推給 ScreenGrab。改完立刻生效，不用重開遊戲。</summary>
        void ApplyThumbSettings()
        {
            ScreenGrab.MaxHeight = thumbMaxHeight;
            ScreenGrab.HeadAnchor = headAnchorThumb;
            ScreenGrab.FrameScale = thumbFrameScale;
            ScreenGrab.HeadAtY = thumbHeadAtY;
        }

        IEnumerator CaptureThenSave(Studio.OCIChar oci, int index, bool female, bool chara)
        {
            // 先讓自己的視窗消失一幀，否則會被拍進縮圖
            hideGuiForCapture = true;
            hideGuiSince = Time.realtimeSinceStartup;
            if (hideAllUiForThumb) ScreenGrab.HideAllUI();

            // 只隱藏其他角色，不動相機——動相機在有工作室相機的場景會拍出奇怪的構圖
            List<VisChange> hidden = soloCharForThumb ? HideOtherCharacters(oci) : null;

            for (int w = 0; w < 4; w++) yield return null;   // 等隱藏反映到畫面
            yield return StartCoroutine(WaitForPhysicsSettle());
            yield return new WaitForEndOfFrame();

            ApplyThumbSettings();
            byte[] png = ScreenGrab.CapturePng(oci, cropThumbToChar);

            RestoreCharacters(hidden);
            ScreenGrab.RestoreUI();
            hideGuiForCapture = false;
            hideGuiSince = -1f;

            string f = chara
                ? CardSaver.SaveCharaCard(oci, index, female, png)
                : CardSaver.SaveCoordinateCard(oci, index, female, png);

            SetStatus(f != null, CardSaver.LastMessage);
            Logger.LogInfo("[CardSaver] " + CardSaver.LastMessage + "\n" + CardSaver.LastReport);
            if (f != null && openFolderAfterSave && !batchRunning && !mergeJobRunning)
                ScreenGrab.RevealInExplorer(f);
            lastSavedPath = f;

            // 存檔過程會動到顯示狀態與工作面板，等一幀讓它安定下來再選取，
            // 否則選取會被後續的還原動作洗掉。
            yield return null;
            SelectCharacterInWorkspace(oci);
        }

        void AccessoryWindow(int id)
        {
            ChaControl cha = null;
            if (accTarget != null) { try { cha = accTarget.charInfo; } catch { } }

            GUILayout.BeginHorizontal();
            GUILayout.Label(cha != null ? Lang.T("對象: ") + GetCharDisplayName(accTarget) : Lang.T("對象已失效"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("關閉"), GUILayout.Width(60))) CloseSidePanel();
            GUILayout.EndHorizontal();

            if (cha == null) return;

            var list = AccessoryTools.List(cha, accIncludeEmpty);
            DrawAccGroupRow(cha, list);

            // 分類列
            var cats = new List<string>();
            foreach (var a in list) if (!cats.Contains(a.Category)) cats.Add(a.Category);
            cats.Sort(StringComparer.Ordinal);

            accCatScroll = GUILayout.BeginScrollView(accCatScroll, GUILayout.Height(72));
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(accCategory.Length == 0, Lang.T("全部"), GUI.skin.button, GUILayout.Width(56)))
                accCategory = "";
            for (int i = 0; i < cats.Count; i++)
            {
                if (i > 0 && i % 4 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                if (GUILayout.Toggle(accCategory == cats[i], cats[i], GUI.skin.button))
                    accCategory = cats[i];
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            accIncludeEmpty = GUILayout.Toggle(accIncludeEmpty, Lang.T("顯示空格"), GUI.skin.button, GUILayout.Width(80));
            if (GUILayout.Button(Lang.T("本類全部顯示"), GUILayout.Width(100)))
                foreach (var a in list) if (InCat(a)) AccessoryTools.SetVisible(cha, a.slot, true);
            if (GUILayout.Button(Lang.T("本類全部隱藏"), GUILayout.Width(100)))
                foreach (var a in list) if (InCat(a)) AccessoryTools.SetVisible(cha, a.slot, false);
            if (GUILayout.Button(Lang.T("診斷"), GUILayout.Width(50))) AccessoryTools.Dump(cha);
            accScaleOpen = GUILayout.Toggle(accScaleOpen, Lang.T("整組縮放"), GUI.skin.button, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            if (accScaleOpen) DrawAccScalePanel(cha, list);

            GUILayout.Space(4);
            GUILayout.Space(4);
            accScroll = GUILayout.BeginScrollView(accScroll);
            int shown = 0;
            foreach (var a in list)
            {
                if (!InCat(a)) continue;
                shown++;

                GUILayout.BeginHorizontal(GUI.skin.box);
                if (accScaleOpen)
                {
                    if (a.IsEmpty) GUILayout.Space(24);
                    else
                    {
                        bool on = accScaleSel.Contains(a.slot);
                        GUI.color = on ? new Color(0.5f, 1f, 0.6f) : Color.white;
                        bool v2 = GUILayout.Toggle(on, on ? "✓" : "", GUI.skin.button, GUILayout.Width(24));
                        GUI.color = Color.white;
                        if (v2 != on) { if (v2) accScaleSel.Add(a.slot); else accScaleSel.Remove(a.slot); }
                    }
                }
                GUILayout.Label((a.slot + 1).ToString(), GUILayout.Width(28));
                GUILayout.Label(a.IsEmpty
                    ? Lang.T("（空）")
                    : a.Category + "  " + (a.name.Length > 0 ? a.name : Lang.T("(無名稱)")));
                GUILayout.FlexibleSpace();

                if (!a.IsEmpty)
                {
                    bool v = GUILayout.Toggle(a.visible, a.visible ? Lang.T("顯示") : Lang.T("隱藏"),
                                              GUI.skin.button, GUILayout.Width(50));
                    if (v != a.visible) AccessoryTools.SetVisible(cha, a.slot, v);

                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    if (GUILayout.Button(Lang.T("移除"), GUILayout.Width(50)))
                    {
                        var sid = AccessoryTools.GetSlotId(cha, a.slot);
                        var bad = new HashSet<int>();
                        bool ok = AccessoryTools.Remove(cha, a.slot);
                        string extra = SyncRemoveOtherCoords(cha, a.slot, sid, bad);
                        SetSyncWarn(bad);
                        SetStatus(ok, AccessoryTools.LastMessage + extra);
                        Logger.LogInfo("[Accessory] " + AccessoryTools.LastMessage + extra
                                       + "\n" + AccessoryTools.LastReport);
                    }
                    GUI.color = Color.white;

                    if (GUILayout.Button("?", GUILayout.Width(24)))
                        AccessoryTools.DumpSlot(cha, a.slot);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Label(Lang.T("顯示 ") + shown + Lang.T(" / 偵測到 ") + list.Count + Lang.T(" 個欄位"));

            DrawSyncCoordRow(cha);

            GUILayout.Label(Lang.T("狀態: ") + TrMsg(AccessoryTools.LastMessage));
            if (accSyncWarn.Length > 0)
            {
                GUI.color = new Color(1f, 0.85f, 0.3f);
                GUILayout.Label(accSyncWarn);
                GUI.color = Color.white;
            }
        }

        // =============================================================
        // 整組縮放／移動：勾選一群飾品（例如拼成頭髮的那些），用滑桿即時調整。
        // 位置也跟著縮，所以整組會像一個物件一樣變大變小，不會散開。
        // 滑桿都是「相對於開始拖之前」：按「確定」把目前結果當新基準（滑桿歸位），「取消」回到開始前。
        // =============================================================
        const float AccSclMin = 0.2f, AccSclMax = 2f, AccOffRange = 50f;

        void DrawAccScalePanel(ChaControl cha, List<AccessoryTools.AccInfo> list)
        {
            if (accScaleSelFor != accTarget)
            {
                accScaleSel.Clear(); accScaleSelFor = accTarget;
                accSession = null; accLastCommitted = null; ResetAccSliders();
            }
            var valid = new HashSet<int>();
            foreach (var a in list) if (!a.IsEmpty) valid.Add(a.slot);
            accScaleSel.RemoveWhere(x => !valid.Contains(x));

            // 選取／中心／同步有變 → 目前的調整直接保留（當作確定），滑桿歸位
            var sel = new List<int>(accScaleSel); sel.Sort();
            string key = AccessoryTools.NowCoordinateType(cha) + "|" + string.Join(",", sel.Select(x => x.ToString()).ToArray())
                         + "|" + accScaleHead + "|" + accScaleRef + "|"
                         + string.Join(",", accSyncCoords.OrderBy(x => x).Select(x => x.ToString()).ToArray());
            if (key != accSessionKey)
            {
                if (accSession != null) { accLastCommitted = accSession; accSession = null; ResetAccSliders(); }
                accSessionKey = key;
            }
            if (accSession != null && !accSession.Valid(cha)) { accSession = null; ResetAccSliders(); }
            if (accLastCommitted != null && !accLastCommitted.Valid(cha)) accLastCommitted = null;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("勾選本類"), GUILayout.Width(80)))
                foreach (var a in list) if (!a.IsEmpty && InCat(a)) accScaleSel.Add(a.slot);
            if (GUILayout.Button(Lang.T("勾選頭部類"), GUILayout.Width(90)))
                foreach (var a in list)
                    if (!a.IsEmpty && (a.parentKey.Contains("head") || a.parentKey.Contains("hair"))) accScaleSel.Add(a.slot);
            if (GUILayout.Button(Lang.T("全不勾"), GUILayout.Width(60))) accScaleSel.Clear();
            GUILayout.Label(string.Format(Lang.T("已勾 {0} 個"), accScaleSel.Count));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("中心"), GUILayout.Width(32));
            if (GUILayout.Toggle(!accScaleHead, Lang.T("各自掛點"), GUI.skin.button, GUILayout.Width(80))) accScaleHead = false;
            if (GUILayout.Toggle(accScaleHead, Lang.T("頭部骨頭"), GUI.skin.button, GUILayout.Width(80))) accScaleHead = true;
            GUI.enabled = accScaleHead;
            accScaleRef = GUILayout.TextField(accScaleRef, GUILayout.Width(110));
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUI.enabled = accScaleSel.Count > 0 && accHasLast;
            bool useLast = GUILayout.Button(Lang.T("套用上次值"), GUILayout.Width(100));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            bool can = accScaleSel.Count > 0;
            GUI.enabled = can;
            Vector3 scl = accScl, off = accOff;
            if (useLast)
            {
                scl = accLastScl; off = accLastOff;
                if (accScaleUniform && !(scl.x == scl.y && scl.y == scl.z)) accScaleUniform = false;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("縮放"), GUILayout.Width(40));
            accScaleUniform = GUILayout.Toggle(accScaleUniform, Lang.T("等比"), GUI.skin.button, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("縮放歸位"), GUILayout.Width(80))) scl = Vector3.one;
            GUILayout.EndHorizontal();
            for (int k = 0; k < 3; k++)
            {
                bool locked = accScaleUniform && k > 0;
                GUI.enabled = can && !locked;
                float v = AccSlider("XYZ".Substring(k, 1), scl[k], AccSclMin, AccSclMax, 0.01f, true);
                if (!locked && v != scl[k]) { if (accScaleUniform) scl = new Vector3(v, v, v); else scl[k] = v; }
            }
            GUI.enabled = can;

            GUILayout.BeginHorizontal();
            GUILayout.Label(accScaleHead ? Lang.T("位置（X 左右 / Y 上下 / Z 前後）") : Lang.T("位置（各自掛點的軸向）"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("位置歸位"), GUILayout.Width(80))) off = Vector3.zero;
            GUILayout.EndHorizontal();
            for (int k = 0; k < 3; k++)
                off[k] = AccSlider("XYZ".Substring(k, 1), off[k], -AccOffRange, AccOffRange, 0.1f, false);

            if (scl != accScl || off != accOff)
            {
                accScl = scl; accOff = off;
                if (accSession == null)
                {
                    accSession = AccessoryTools.BeginScale(cha, sel, accScaleHead, accScaleRef,
                                                           accSyncCoords.Count > 0 ? new List<int>(accSyncCoords) : null);
                    SetSyncWarn(accSession.Unsynced);
                    accLastCommitted = null;
                }
                try { accSession.Apply(accScl, accOff); } catch { }
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = accSession != null;
            GUI.color = new Color(0.6f, 1f, 0.7f);
            if (GUILayout.Button(Lang.T("確定"), GUILayout.Width(80)))
            {
                accLastScl = accScl; accLastOff = accOff; accHasLast = true;
                accLastCommitted = accSession; accSession = null; ResetAccSliders();
                SetStatus(true, string.Format(Lang.T("已調整 {0} 個飾品"), accLastCommitted.SlotCount)
                                + (accLastCommitted.ExtraCount > 0 ? string.Format(Lang.T("（其他服裝槽 {0} 個）"), accLastCommitted.ExtraCount) : "")
                                + (accLastCommitted.OnlyScreen > 0 ? string.Format(Lang.T("；{0} 格可能只改到畫面"), accLastCommitted.OnlyScreen) : ""));
            }
            GUI.color = Color.white;
            if (GUILayout.Button(Lang.T("取消"), GUILayout.Width(80)))
            {
                try { accSession.Revert(); } catch { }
                accSession = null; ResetAccSliders();
            }
            GUI.enabled = accSession == null && accLastCommitted != null;
            if (GUILayout.Button(Lang.T("復原上一次確定")))
            {
                try { accLastCommitted.Revert(); } catch { }
                accLastCommitted = null;
                SetStatus(true, Lang.T("已復原上一次確定的調整"));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(can ? Lang.T("拖滑桿即時預覽；「確定」後滑桿歸位，可以再接著調")
                                : Lang.T("先在下面的清單勾選要一起調整的飾品"));
            GUILayout.EndVertical();
        }

        void ResetAccSliders() { accScl = Vector3.one; accOff = Vector3.zero; }

        /// <summary>一列滑桿：名稱、滑桿、數值、－／＋。pct = 以百分比顯示。</summary>
        float AccSlider(string label, float v, float min, float max, float step, bool pct)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(14));
            float nv = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(200));
            GUILayout.Label(pct ? (nv * 100f).ToString("0") + "%" : nv.ToString("0.0"), GUILayout.Width(48));
            if (GUILayout.Button("-", GUILayout.Width(22))) nv -= step;
            if (GUILayout.Button("+", GUILayout.Width(22))) nv += step;
            GUILayout.EndHorizontal();
            nv = Mathf.Clamp(nv, min, max);
            nv = Mathf.Round(nv / step) * step;
            return Mathf.Abs(nv - v) < step * 0.25f ? v : nv;
        }

        // =============================================================
        // 全部 / 主要 / 次要 一次顯示、隱藏、移除
        // 主要／次要就是 KK 飾品的 hideCategory（跟 Studio 左邊「裝飾」那欄的主要／次要同一套）。
        // 移除要連按兩次（3 秒內）才生效，而且跟單格移除一樣會照「同步刪除的服裝槽」一起清。
        // =============================================================
        private string accGroupArm = "";
        private float accGroupArmTime = -10f;

        void DrawAccGroupRow(ChaControl cha, List<AccessoryTools.AccInfo> list)
        {
            if (accGroupArm.Length > 0 && Time.realtimeSinceStartup - accGroupArmTime > 3f) accGroupArm = "";

            GUILayout.BeginHorizontal();
            for (int g = 0; g < 3; g++)
            {
                var slots = new List<int>();
                foreach (var a in list)
                    if (!a.IsEmpty && (g == 0 || a.hideCategory == g - 1)) slots.Add(a.slot);

                string label = g == 0 ? Lang.T("全部") : g == 1 ? Lang.T("主要") : Lang.T("次要");
                GUILayout.Label(label + " " + slots.Count, GUILayout.Width(62));
                GUI.enabled = slots.Count > 0;
                if (GUILayout.Button(Lang.T("顯示"), GUILayout.Width(40)))
                    foreach (int s in slots) AccessoryTools.SetVisible(cha, s, true);
                if (GUILayout.Button(Lang.T("隱藏"), GUILayout.Width(40)))
                    foreach (int s in slots) AccessoryTools.SetVisible(cha, s, false);

                string key = "g" + g;
                bool armed = accGroupArm == key;
                GUI.color = armed ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button(armed ? Lang.T("確定？") : Lang.T("移除"), GUILayout.Width(48)))
                {
                    if (!armed) { accGroupArm = key; accGroupArmTime = Time.realtimeSinceStartup; }
                    else
                    {
                        accGroupArm = "";
                        int ok = 0, fail = 0;
                        var bad = new HashSet<int>();
                        foreach (int s in slots)
                        {
                            var sid = AccessoryTools.GetSlotId(cha, s);
                            if (AccessoryTools.Remove(cha, s)) ok++; else fail++;
                            SyncRemoveOtherCoords(cha, s, sid, bad);
                        }
                        SetSyncWarn(bad);
                        string msg = string.Format(Lang.T("已移除{0}飾品 {1} 個"), label, ok)
                                     + (fail > 0 ? string.Format(Lang.T("，失敗 {0} 個"), fail) : "")
                                     + (accSyncCoords.Count > 0
                                        ? string.Format(Lang.T("（同步 {0} 套）"), accSyncCoords.Count) : "");
                        SetStatus(fail == 0, msg);
                        Logger.LogInfo("[Accessory] " + msg);
                    }
                }
                GUI.color = Color.white;
                GUI.enabled = true;
                if (g < 2) GUILayout.Space(8);
            }
            GUILayout.EndHorizontal();
        }

        // =============================================================
        // 同步刪除：在別的服裝槽上把「同一格」一起清掉
        // 例如目前這套刪第 1 格，被選中的服裝槽也會刪它的第 1 格
        // =============================================================
        private readonly HashSet<int> accSyncCoords = new HashSet<int>();

        void DrawSyncCoordRow(ChaControl cha)
        {
            int count = AccessoryTools.CoordinateCount(cha);
            if (count <= 1) return;
            int now = AccessoryTools.NowCoordinateType(cha);

            GUILayout.Space(4);
            GUILayout.Label(Lang.T("同步服裝槽（紅字選中，同步所有操作，需同欄位同物件）"));
            GUILayout.BeginHorizontal();
            for (int c = 0; c < count; c++)
            {
                if (c > 0 && c % 4 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }

                if (c == now)
                {
                    GUI.color = new Color(0.6f, 0.85f, 1f);
                    GUILayout.Button(CoordLabel(c) + Lang.T("（目前）"), GUILayout.Width(150));
                    GUI.color = Color.white;
                    continue;
                }

                bool on = accSyncCoords.Contains(c);
                GUI.color = on ? new Color(1f, 0.35f, 0.35f) : Color.white;
                if (GUILayout.Button(CoordLabel(c), GUILayout.Width(150)))
                {
                    if (on) accSyncCoords.Remove(c); else accSyncCoords.Add(c);
                }
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("全選"), GUILayout.Width(60)))
                for (int c = 0; c < count; c++) if (c != now) accSyncCoords.Add(c);
            if (GUILayout.Button(Lang.T("全不選"), GUILayout.Width(70))) accSyncCoords.Clear();
            GUILayout.Label(accSyncCoords.Count == 0
                ? Lang.T("目前只操作這一套")
                : string.Format(Lang.T("操作時同步 {0} 套"), accSyncCoords.Count));
            GUILayout.EndHorizontal();

            GUILayout.Label(Lang.T("刪除、縮放、移動第 N 格時，選中的服裝槽第 N 格若是同一個飾品也會一起改"));
        }

        /// <summary>KK 的前七套有固定名稱，之後的就用編號。</summary>
        static readonly string[] CoordNames =
            { "學生服（校內）", "學生服（放學）", "體操服", "泳裝", "社團", "私服", "睡衣" };

        static string CoordLabel(int c)
        {
            return c < CoordNames.Length ? Lang.T(CoordNames[c]) : string.Format(Lang.T("第 {0} 套"), c + 1);
        }

        /// <summary>把同一格也從被選中的服裝槽清掉（只清同一個飾品），回傳要接在狀態列後面的字；不是同一個飾品的服裝槽放進 bad。</summary>
        string SyncRemoveOtherCoords(ChaControl cha, int slot, AccessoryTools.SlotId sid, HashSet<int> bad)
        {
            if (accSyncCoords.Count == 0) return "";
            int now = AccessoryTools.NowCoordinateType(cha);
            int ok = 0;
            foreach (int c in accSyncCoords)
            {
                if (c == now) continue;
                object pc;
                if (!AccessoryTools.MatchInCoordinate(cha, c, slot, sid, out pc)) { bad.Add(c); continue; }
                if (AccessoryTools.RemoveInCoordinate(cha, c, slot)) ok++; else bad.Add(c);
            }
            return ok == 0 ? "" : string.Format(Lang.T("；同步清掉 {0} 套"), ok);
        }

        /// <summary>最下面的提示：有勾同步、但那一格不是同一個飾品的服裝槽。</summary>
        void SetSyncWarn(ICollection<int> bad)
        {
            if (bad == null || bad.Count == 0) { accSyncWarn = ""; return; }
            var l = new List<int>(bad); l.Sort();
            accSyncWarn = Lang.T("有無法同步之服裝槽：") + string.Join(Lang.T("、"), l.Select(c => CoordLabel(c)).ToArray());
        }

        /// <summary>
        /// 整組角色套用同一張人物卡。
        /// mode 決定這一批要留舊卡的什麼（見 SwapMode）。
        /// </summary>
        void BatchSwapAppearance(List<Studio.OCIChar> targets, SwapMode mode)
        {
            if (targets == null || targets.Count == 0) { SetStatus(false, "沒有角色可以替換"); return; }

            var list = new List<Studio.OCIChar>(targets);
            bool female = list[0].sex == 1;

            OpenGenericCardPicker(
                GetCharaFolder(female),
                string.Format(Lang.T("一鍵替換 {0} 個角色（{1}）"), list.Count, SwapModeName(mode)),
                (file) => WithShaderChoice(string.Format(Lang.T("{0} 個角色"), list.Count), carry =>
                {
                    int n = 0;
                    foreach (var oci in list)
                    {
                        try { DoSwapCharacterAppearance(oci, file, mode, carry); n++; }
                        catch (Exception e) { Logger.LogWarning("[Batch] " + e.Message); }
                    }
                    SetStatus(n > 0, string.Format(Lang.T("已對 {0} 個角色套用人物卡（{1}）"), n, SwapModeName(mode)));
                }), true);
        }

        // =============================================================
        // 一鍵批次：場上每個角色套同一張卡，只選一次
        // =============================================================

        /// <summary>把場上所有角色的動畫狀態機重建一次（修拉伸）。</summary>
        IEnumerator BatchResetPoseRoutine()
        {
            var list = GetAllSceneCharacters();
            if (list.Count == 0) { SetStatus(false, "場上沒有角色"); yield break; }

            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var oci = list[i];
                SetStatus(true, string.Format(Lang.T("重置姿勢 {0}/{1}"), i + 1, list.Count));
                AnimeState a = ReadAnime(oci);
                if (a == null) { Logger.LogWarning("[Batch] 讀不到動畫，跳過"); continue; }
                bool ok = false;
                yield return StartCoroutine(ReapplyAnimeRoutine(oci, a, r => ok = r));
                if (ok) n++;
            }
            SetStatus(n > 0, string.Format(Lang.T("已重置 {0}/{1} 個角色的姿勢"), n, list.Count));
        }

        // =============================================================
        // 碰撞器綁定修復（換人後 J694 之類的 Dynamic Bone Collider 會吸住全身）
        // 問題成因與作法寫在 DBColliderFix.cs 開頭。
        // =============================================================

        /// <summary>場上每個角色都跑一次碰撞器綁定修復。</summary>
        IEnumerator BatchFixDbColliderRoutine()
        {
            if (!DBColliderFix.Available)
            {
                SetStatus(false, DBColliderFix.Unavailable);
                yield break;
            }

            var list = GetAllSceneCharacters();
            if (list.Count == 0) { SetStatus(false, "場上沒有角色"); yield break; }

            int total = 0, done = 0;
            for (int i = 0; i < list.Count; i++)
            {
                SetStatus(true, string.Format(Lang.T("修復碰撞器綁定 {0}/{1}"), i + 1, list.Count));
                yield return null;
                int n = DBColliderFix.AutoFix(list[i]);
                Logger.LogInfo("[DBColliderFix] " + GetCharDisplayName(list[i]) + "：" + DBColliderFix.LastReport);
                total += n;
                done++;
            }
            SetStatus(true, string.Format(Lang.T("已修復 {0} 個角色的碰撞器綁定，共調整 {1} 根動骨"), done, total));
            if (showDbFix && dbFixTarget != null) dbFixText = DBColliderFix.Describe(dbFixTarget);
        }

        /// <summary>碰撞器綁定面板：看現況、還原、套範本、全部關閉。</summary>
        void DbColliderWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(dbFixTarget != null
                ? Lang.T("對象: ") + GetCharDisplayName(dbFixTarget) : Lang.T("對象已失效"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("關閉"), GUILayout.Width(60))) CloseSidePanel();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            if (!DBColliderFix.Available)
            {
                GUILayout.Label("⚠ " + DBColliderFix.Unavailable);
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }
            if (dbFixTarget == null)
            {
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }

            GUILayout.Label(Lang.T("換人時 KKPE 會把新角色的所有動骨都綁到場上每一顆碰撞器上")
                          + Lang.T("（預設啟用），J694 這種小碰撞器就會開始吸頭髮與下半身。"), GUI.skin.box);

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("重新掃描"), GUILayout.Height(26)))
                dbFixText = DBColliderFix.Describe(dbFixTarget);

            GUI.color = new Color(0.6f, 1f, 0.7f);
            if (GUILayout.Button(DBColliderFix.HasRemembered(dbFixTarget)
                                 ? Lang.T("還原記錄的綁定") : Lang.T("還原記錄的綁定（無記錄）"), GUILayout.Height(26)))
            {
                DBColliderFix.Apply(dbFixTarget, DBColliderFix.GetRemembered(dbFixTarget));
                SetStatus(true, DBColliderFix.LastReport);
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.color = new Color(0.7f, 0.85f, 1f);
            if (GUILayout.Button(Lang.T("以其他角色為範本修復"), GUILayout.Height(26)))
            {
                DBColliderFix.Apply(dbFixTarget, DBColliderFix.TemplateFromOthers(dbFixTarget));
                SetStatus(true, DBColliderFix.LastReport);
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            GUI.color = new Color(1f, 0.7f, 0.5f);
            if (GUILayout.Button(Lang.T("這個角色全部不吸"), GUILayout.Height(26)))
            {
                DBColliderFix.Apply(dbFixTarget, DBColliderFix.EmptySnapshot(dbFixTarget));
                SetStatus(true, DBColliderFix.LastReport);
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label(Lang.T("── 目前這一份設定就是對的話 ──"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("把現在的綁定記成基準"), GUILayout.Height(24)))
            {
                DBColliderFix.Capture(dbFixTarget, true);
                SetStatus(true, DBColliderFix.LastReport);
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            if (GUILayout.Button(Lang.T("清除所有記錄"), GUILayout.Width(120), GUILayout.Height(24)))
            {
                DBColliderFix.ForgetAll();
                SetStatus(true, "已清除所有碰撞器綁定記錄");
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label(Lang.T("── 根治（會寫進場景卡）──"));
            GUI.color = new Color(1f, 0.85f, 0.5f);
            if (GUILayout.Button(Lang.T("關掉所有碰撞器的「自動加入新動骨」（")
                                 + DBColliderFix.CountAutoAddOn() + Lang.T(" 個還開著）"), GUILayout.Height(26)))
            {
                DBColliderFix.DisableAutoAddPermanently();
                SetStatus(true, DBColliderFix.LastReport);
                dbFixText = DBColliderFix.Describe(dbFixTarget);
            }
            GUI.color = Color.white;
            GUILayout.Label(Lang.T("這就是 HSPE 面板上的 Enable New Dynamic Bones。關掉之後")
                          + Lang.T("新出現的動骨一律不吸，存檔後對這張卡永久有效。"), GUI.skin.box);

            GUILayout.Space(6);
            if (dbFixText.Length == 0) dbFixText = DBColliderFix.Describe(dbFixTarget);
            dbFixScroll = GUILayout.BeginScrollView(dbFixScroll);
            GUILayout.TextArea(dbFixText, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        /// <summary>場上每個角色都附加同一張服裝卡的飾品。</summary>
        void BatchAddAccessories()
        {
            var list = GetAllSceneCharacters();
            if (list.Count == 0) { SetStatus(false, "場上沒有角色"); return; }

            OpenGenericCardPicker(GetCoordinateFolder(),
                string.Format(Lang.T("一鍵添加飾品：{0} 個角色都套這張服裝卡"), list.Count),
                (coordFile) =>
                {
                    if (string.IsNullOrEmpty(coordFile)) return;
                    StartCoroutine(BatchAddAccessoriesRoutine(list, coordFile));
                }, true);
        }

        IEnumerator BatchAddAccessoriesRoutine(List<Studio.OCIChar> list, string coordFile)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                SetStatus(true, string.Format(Lang.T("添加飾品 {0}/{1}"), i + 1, list.Count));
                SelectCharacterInWorkspace(list[i]);
                yield return StartCoroutine(MergeJobRoutine(list[i], i + 1, coordFile));
                n++;
            }
            SetStatus(true, string.Format(Lang.T("一鍵添加飾品完成（{0} 個角色）"), n));
        }

        /// <summary>場上每個角色都換成同一張人物卡，各自的服裝保留。</summary>
        void BatchKeepOutfitSwap(List<Studio.OCIChar> targets, SwapMode mode)
        {
            var list = targets != null ? new List<Studio.OCIChar>(targets) : GetAllSceneCharacters();
            if (list.Count == 0) { SetStatus(false, "場上沒有角色"); return; }
            bool female = list[0].sex == 1;

            OpenGenericCardPicker(GetCharaFolder(female),
                string.Format(Lang.T("① 一鍵保持服裝換人（{0}）：{1} 個角色都換成這張人物卡"), SwapModeName(mode), list.Count),
                (charaFile) =>
                {
                    if (string.IsNullOrEmpty(charaFile)) return;

                    string bodyDir = Path.Combine(GetCoordinateFolder(), "Body");
                    if (!Directory.Exists(bodyDir)) bodyDir = GetCoordinateFolder();

                    OpenGenericCardPicker(bodyDir,
                        "② 選擇要帶入頭髮飾品的服裝卡（可略過）",
                        (coordFile) =>
                            StartCoroutine(BatchKeepOutfitSwapRoutine(list, charaFile, coordFile, mode)),
                        true, true);
                }, true);
        }

        IEnumerator BatchKeepOutfitSwapRoutine(List<Studio.OCIChar> list,
                                               string charaFile, string coordFile, SwapMode mode)
        {
            string what = string.Format(Lang.T("保持服裝換人（{0}）"), SwapModeName(mode));
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                SetStatus(true, what + " " + (i + 1) + "/" + list.Count);
                SelectCharacterInWorkspace(list[i]);
                keepSwapCharaCard = charaFile;
                keepSwapCoordCard = coordFile;
                keepSwapMode = mode;
                yield return StartCoroutine(KeepOutfitSwapRoutine(list[i], i + 1));
                n++;
            }
            SetStatus(true, string.Format(Lang.T("一鍵{0}完成（{1} 個角色）"), what, n));
        }

        /// <summary>kind: 0=人物卡 1=服裝卡 2=姿勢。逐一處理，存卡要各自擷圖。</summary>
        IEnumerator BatchSaveRoutine(List<Studio.OCIChar> targets, int kind)
        {
            if (targets == null || targets.Count == 0)
            {
                SetStatus(false, "沒有角色可以處理");
                yield break;
            }

            var list = new List<Studio.OCIChar>(targets);
            int ok = 0, fail = 0;
            batchRunning = true;          // 批次途中不要每存一張就跳一次檔案總管
            string last = null;

            for (int i = 0; i < list.Count; i++)
            {
                var oci = list[i];
                bool female = IsFemale(oci);
                SetStatus(true, string.Format(Lang.T("處理中 {0} / {1}"), i + 1, list.Count));
                SelectCharacterInWorkspace(oci);

                if (kind == 2)
                {
                    List<VisChange> hid = soloCharForThumb ? HideOtherCharacters(oci) : null;
                    yield return StartCoroutine(WaitForPhysicsSettle());

                    var f = PoseFix.SavePoseNamed(oci, i + 1, female);
                    if (f != null) { ok++; last = f; } else fail++;

                    RestoreCharacters(hid);
                    SelectCharacterInWorkspace(oci);
                    yield return null;
                    continue;
                }

                for (int w = 0; w < 3; w++) yield return null;     // 讓上一個角色的還原先完成
                yield return StartCoroutine(CaptureThenSave(oci, i + 1, female, kind == 0));
                if (CardSaver.LastMessage != null && CardSaver.LastMessage.Contains("已存出"))
                { ok++; last = lastSavedPath; }
                else fail++;

                yield return null;
            }

            batchRunning = false;
            if (last != null && openFolderAfterSave) ScreenGrab.RevealInExplorer(last);

            string what = Lang.T(kind == 0 ? "人物卡" : (kind == 1 ? "服裝卡" : "姿勢"));
            SetStatus(fail == 0, string.Format(Lang.T("一鍵存{0}：成功 {1} 個"), what, ok)
                                 + (fail > 0 ? string.Format(Lang.T("，失敗 {0} 個"), fail) : ""));
        }

        // =============================================================
        // 一鍵存出一個角色的全部換裝
        //
        // KK 的 CoordinateType 固定七個槽（制服1/制服2/體操服/泳裝/社團/私服/睡衣），
        // 這裡輪流切到每一個、等模型重建完、拍一張縮圖、存成服裝卡，最後切回原本那套。
        //
        // 為什麼不直接讀 chaFile.coordinate[i] 存檔就好：
        // CardSaver.SaveCoordinateCard 存的是「**目前**穿在身上的那一套」
        // （它走 CurrentCoordinate(cha)），而且縮圖也得真的換上去才拍得到。
        // 所以非得一套一套穿上去不可。
        // =============================================================

        // =============================================================
        // 給外部腳本（VNGE / IronPython）用的公開介面
        //
        // 為什麼要另外開一組 public static：
        // Instance 是 internal，而 **IronPython 只看得到 public 成員** ——
        // 跟 Screencap 的 ScreenshotManager.Instance 抓不到是同一個坑
        // （'type' object has no attribute 'Instance'）。
        //
        // 有了這個，腳本就能重用 F6 已經驗證過的流程，不必在 Python 那邊重寫一套。
        // 換人那一套尤其不該重寫：體型滑桿要存哪幾根、ABMX 白名單、還原的時序，
        // 每一項都是踩過坑才調對的。
        // =============================================================

        public static string ApiLastError = "";

        /// <summary>
        /// 換人。mode：0=保留舊卡身材　1=鎖身高　2=維持新卡身材　3=一般替換。
        ///
        /// **非同步**：這個方法只是把流程送出去，實際完成要等 2 秒上下
        /// （還原姿勢、形態鍵那些還在後面）。呼叫端自己等夠再做下一步。
        /// </summary>
        public static bool ApiSwapChara(Studio.OCIChar oci, string cardPath, int mode)
        {
            try
            {
                if (Instance == null) { ApiLastError = "F6 插件還沒初始化"; return false; }
                if (oci == null || string.IsNullOrEmpty(cardPath))
                { ApiLastError = "參數不完整"; return false; }

                // 不用 (SwapMode)mode 硬轉 —— 那會綁死列舉的宣告順序，
                // 以後有人調整順序就會靜靜地換錯模式
                SwapMode m;
                switch (mode)
                {
                    case 0: m = SwapMode.KeepOldBody; break;
                    case 1: m = SwapMode.LockHeight; break;
                    case 3: m = SwapMode.Plain; break;
                    default: m = SwapMode.KeepNewBody; break;
                }
                Instance.DoSwapCharacterAppearance(oci, cardPath, m);
                ApiLastError = "";
                return true;
            }
            catch (Exception e)
            {
                ApiLastError = "換人失敗：" + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 用 F6 自己的擷圖拍一張 PNG（cropToChar = 依角色範圍裁切）。
        /// 腳本拿它當服裝卡縮圖，結果就會跟 F6 手動存卡完全一致 ——
        /// 這正是「腳本存的縮圖跟 F6 不一樣、而且糊」的解法：
        /// 全螢幕畫面縮成縮圖當然糊，裁到角色身上才看得清服裝。
        /// </summary>
        public static byte[] ApiCapturePng(Studio.OCIChar oci, bool cropToChar)
        {
            try
            {
                // 面板上改過的縮圖設定要先推過去，不然腳本會用到開遊戲時的舊值
                if (Instance != null) Instance.ApplyThumbSettings();
                byte[] png = ScreenGrab.CapturePng(oci, cropToChar);
                ApiLastError = png == null ? "擷圖回傳 null" : "";
                return png;
            }
            catch (Exception e)
            {
                ApiLastError = "擷圖失敗：" + e.Message;
                return null;
            }
        }

        /// <summary>
        /// 存服裝卡到指定路徑，縮圖由呼叫端給。
        /// 走的是 F6 自己那條（CardSaver.SaveCoordinateCardTo），
        /// 所以外掛資料（MaterialEditor 之類）跟 F6 手動存卡完全一致。
        /// 回傳實際寫出的路徑，失敗回 null（原因看 ApiLastError）。
        /// </summary>
        public static string ApiSaveCoordinateCardAs(Studio.OCIChar oci, string fullPath, byte[] thumbPng)
        {
            try
            {
                string p = CardSaver.SaveCoordinateCardTo(oci, fullPath, thumbPng);
                ApiLastError = p == null ? CardSaver.LastMessage : "";
                return p;
            }
            catch (Exception e)
            {
                ApiLastError = "存服裝卡失敗：" + e.Message;
                return null;
            }
        }

        /// <summary>
        /// 把一張 PNG 裁到「角色所佔的範圍」，**不縮放**。
        ///
        /// 為什麼是事後裁切而不是拍的時候就框好：
        /// 拍的時候縮小視野等於用更少的像素去拍，清晰度會掉；
        /// 先用完整解析度拍、再從裡面切出角色那一塊，留下的每個像素都是原生的。
        ///
        /// 怎麼找範圍：掃描像素，把「跟四個角落同色、或完全透明」的當成背景，
        /// 其餘的取外接矩形。這樣透明背景和純色背景都適用，
        /// 不必去算相機投影（那要處理擷圖解析度跟視窗解析度不一致的問題）。
        ///
        /// pad 是往外留的邊（像素）。ratio 大於 0 時會把結果調整成那個長寬比
        /// （例如 0.75 = 3:4 直幅），方式是往外擴而不是切掉，所以不會把角色切到。
        /// </summary>
        public static byte[] ApiCropPngToChar(byte[] png, int pad, float ratio)
        {
            if (png == null || png.Length == 0) { ApiLastError = "沒有輸入影像"; return png; }
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!tex.LoadImage(png)) { ApiLastError = "PNG 解不開"; return png; }

                int w = tex.width, h = tex.height;
                Color32[] px = tex.GetPixels32();

                // 背景色取四個角落的平均 —— 只取一個角落的話，剛好那一點有東西就全毀
                Color32 bg = px[0];
                bool hasAlpha = false;
                for (int i = 0; i < px.Length; i += 97)      // 抽樣就夠了
                    if (px[i].a < 250) { hasAlpha = true; break; }

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        Color32 c = px[row + x];
                        bool isBg = hasAlpha
                            ? c.a < 8
                            : (Mathf.Abs(c.r - bg.r) < 6 && Mathf.Abs(c.g - bg.g) < 6
                               && Mathf.Abs(c.b - bg.b) < 6);
                        if (isBg) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < minX || maxY < minY) { ApiLastError = "整張都是背景，不裁切"; return png; }

                minX = Mathf.Max(0, minX - pad); minY = Mathf.Max(0, minY - pad);
                maxX = Mathf.Min(w - 1, maxX + pad); maxY = Mathf.Min(h - 1, maxY + pad);

                int cw = maxX - minX + 1, ch = maxY - minY + 1;

                // 調長寬比：只往外擴，不切掉角色
                if (ratio > 0.01f)
                {
                    int wantW = Mathf.RoundToInt(ch * ratio);
                    if (wantW > cw)
                    {
                        int grow = wantW - cw;
                        minX = Mathf.Max(0, minX - grow / 2);
                        maxX = Mathf.Min(w - 1, minX + wantW - 1);
                    }
                    else
                    {
                        int wantH = Mathf.RoundToInt(cw / ratio);
                        int grow = wantH - ch;
                        if (grow > 0)
                        {
                            minY = Mathf.Max(0, minY - grow / 2);
                            maxY = Mathf.Min(h - 1, minY + wantH - 1);
                        }
                    }
                    cw = maxX - minX + 1; ch = maxY - minY + 1;
                }

                Color32[] outPx = new Color32[cw * ch];
                for (int y = 0; y < ch; y++)
                    Array.Copy(px, (minY + y) * w + minX, outPx, y * cw, cw);

                var outTex = new Texture2D(cw, ch, TextureFormat.ARGB32, false);
                outTex.SetPixels32(outPx);
                outTex.Apply();
                byte[] result = outTex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(outTex);

                ApiLastError = "";
                return result;
            }
            catch (Exception e)
            {
                ApiLastError = "裁切失敗：" + e.Message;
                return png;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>換到第 slot 套換裝（0..6）。給腳本用的公開版本。</summary>
        public static bool ApiSetCoordinateSlot(Studio.OCIChar oci, int slot)
        {
            try
            {
                if (Instance == null) { ApiLastError = "F6 插件還沒初始化"; return false; }
                return Instance.SetCoordinateSlot(oci, slot);
            }
            catch (Exception e)
            {
                ApiLastError = "切換換裝失敗：" + e.Message;
                return false;
            }
        }

        /// <summary>
        /// KK 的換裝槽數。這個數字是從 Assembly-CSharp 的 ChaFileDefine.CoordinateType
        /// 實際讀出來的：School01 / School02 / Gym / Swim / Club / Plain / Pajamas，固定七個。
        /// 名稱顯示沿用插件既有的 CoordLabel()，不另外開一份對照表。
        /// </summary>
        const int CoordSlotCount = 7;

        /// <summary>把 int 轉成對方要的參數型別（列舉或 int）。轉不了回 null。</summary>
        static object ToSlotArg(Type t, int slot)
        {
            try
            {
                if (t.IsEnum) return Enum.ToObject(t, slot);
                if (t == typeof(int)) return slot;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 換到第 slot 套換裝。
        ///
        /// 優先用工作室自己的 OCIChar.SetCoordinateInfo —— 直接叫 ChaControl 的話，
        /// 工作室面板上的換裝選單不會跟著更新，之後手動操作會對不上。
        /// 它不存在才退回 ChaControl.ChangeCoordinateTypeAndReload / ChangeCoordinateType。
        /// 全程反射，簽章在不同版本略有差異也不會編譯不過。
        /// </summary>
        bool SetCoordinateSlot(Studio.OCIChar oci, int slot)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public
                                   | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            try
            {
                foreach (MethodInfo m in oci.GetType().GetMethods(F))
                {
                    if (m.Name != "SetCoordinateInfo") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    object a = ToSlotArg(ps[0].ParameterType, slot);
                    if (a == null) continue;
                    m.Invoke(oci, new[] { a });
                    return true;
                }

                var cha = oci.charInfo;
                string[] names = { "ChangeCoordinateTypeAndReload", "ChangeCoordinateType" };
                foreach (string want in names)
                {
                    foreach (MethodInfo m in cha.GetType().GetMethods(F))
                    {
                        if (m.Name != want) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 0) continue;
                        object a0 = ToSlotArg(ps[0].ParameterType, slot);
                        if (a0 == null) continue;

                        object[] args = new object[ps.Length];
                        args[0] = a0;
                        for (int i = 1; i < ps.Length; i++)
                            args[i] = ps[i].ParameterType == typeof(bool)
                                ? (object)true                      // 通常是 reload，要 true
                                : (ps[i].ParameterType.IsValueType
                                    ? Activator.CreateInstance(ps[i].ParameterType) : null);
                        m.Invoke(cha, args);
                        return true;
                    }
                }
                Logger.LogWarning("[存全部換裝] 找不到可用的換裝方法");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[存全部換裝] 切到第 " + slot + " 套失敗: " + e.Message);
            }
            return false;
        }

        static int CurrentCoordinateSlot(Studio.OCIChar oci)
        {
            try { return oci.charInfo.fileStatus.coordinateType; }
            catch { return 0; }
        }

        IEnumerator SaveAllCoordinatesRoutine(Studio.OCIChar oci, int index)
        {
            if (oci == null) { SetStatus(false, "沒有選取角色"); yield break; }

            bool female = IsFemale(oci);
            int orig = CurrentCoordinateSlot(oci);
            int ok = 0, fail = 0;
            string last = null;

            batchRunning = true;      // 中途不要每存一張就跳一次檔案總管

            for (int slot = 0; slot < CoordSlotCount; slot++)
            {
                SetStatus(true, Lang.T("存全部換裝：") + CoordLabel(slot)
                                + "（" + (slot + 1) + " / " + CoordSlotCount + "）");

                if (!SetCoordinateSlot(oci, slot)) { fail++; continue; }

                // 換裝會重建模型與材質，一定要等它做完再拍，否則會拍到上一套或半成品。
                // 幾幀 + 設定裡的「拍照前等待秒數」，跟單張存卡用的是同一套標準。
                for (int w = 0; w < 6; w++) yield return null;
                yield return new WaitForSeconds(Mathf.Max(0.3f, settleSeconds));

                // 服裝卡的檔名前綴會帶入這個編號，所以傳槽號而不是角色編號，
                // 七張才不會擠在同一個名字上（時間戳也會不同，這是雙保險）
                yield return StartCoroutine(CaptureThenSave(oci, slot + 1, female, false));

                if (CardSaver.LastMessage != null && CardSaver.LastMessage.Contains("已存出"))
                { ok++; last = lastSavedPath; }
                else fail++;

                yield return null;
            }

            // 一定要換回原本那套 —— 不還原的話等於把使用者的場景改掉了
            SetCoordinateSlot(oci, orig);
            for (int w = 0; w < 4; w++) yield return null;

            batchRunning = false;
            if (last != null && openFolderAfterSave) ScreenGrab.RevealInExplorer(last);

            SetStatus(fail == 0, string.Format(Lang.T("存全部換裝：成功 {0} 套"), ok)
                                 + (fail > 0 ? string.Format(Lang.T("，失敗 {0} 套"), fail) : "")
                                 + string.Format(Lang.T("（已切回原本的第 {0} 套）"), orig + 1));
        }

        /// <summary>
        /// 暫時隱藏除了指定角色以外的所有角色，回傳「原本是顯示的」那些，
        /// 這樣還原時不會把本來就被你關掉的角色打開。
        /// </summary>
        /// <summary>
        /// 角色剛被啟用時，頭髮和飾品的動態骨骼會晃一陣子，太快按快門就會拍到飄起來的樣子。
        /// 逐幀判斷是否靜止不夠可靠（動態骨骼會一直有極小的擾動），改成固定等一段時間。
        /// </summary>
        IEnumerator WaitForPhysicsSettle()
        {
            if (settleSeconds <= 0f) yield break;
            yield return new WaitForSeconds(settleSeconds);
        }

        /// <summary>拍照期間被改動過顯示狀態的角色，還原時要回到原本的值。</summary>
        class VisChange
        {
            public Studio.OCIChar oci;
            public bool orig;
        }

        /// <summary>
        /// 只留下指定角色：其他角色隱藏，而且要拍的那個一定會被打開
        /// （批次時它可能本來就是禁用的）。回傳所有被改過的，還原時各自回到原值。
        /// </summary>
        List<VisChange> HideOtherCharacters(Studio.OCIChar keep)
        {
            var changed = new List<VisChange>();
            try
            {
                foreach (var c in GetAllSceneCharacters())
                {
                    bool now = GetCharVisibleSimple(c);

                    if (ReferenceEquals(c, keep))
                    {
                        if (!now)                       // 主角自己一定要開起來
                        {
                            changed.Add(new VisChange { oci = c, orig = false });
                            SetCharVisibleSimple(c, true);
                        }
                        continue;
                    }

                    if (!hideMalesForThumb && c.sex == 0) continue;   // 男角色可選擇不動
                    if (!now) continue;                              // 本來就隱藏，不動它

                    changed.Add(new VisChange { oci = c, orig = true });
                    SetCharVisibleSimple(c, false);
                }
            }
            catch (Exception e) { Logger.LogWarning("[Thumb] 隱藏角色失敗: " + e.Message); }
            return changed;
        }

        void RestoreCharacters(List<VisChange> list)
        {
            if (list == null) return;
            foreach (var v in list)
            {
                try { SetCharVisibleSimple(v.oci, v.orig); } catch { }
            }
        }

        // =============================================================
        // 呼叫外部工具 kkmerge.exe 合卡
        //
        // MaterialEditor / MoreAccessories / Sideloader / objimport 這些
        // 擴充資料在遊戲內搬不動，所以交給離線工具在檔案層級處理：
        // 存出人物卡 → 呼叫 kkmerge.exe → 讀回產出的卡。
        // 工具沒裝好就當場失敗，不會卡在那裡空等。
        // =============================================================

        /// <summary>等一個 kkmerge 動作跑完，成功就把產出路徑交給 onDone。</summary>
        IEnumerator RunTask(KKMerge.Task task, string what, Action<string> onDone)
        {
            what = Lang.T(what);
            mergeWaiting = true;
            mergeCancel = false;
            float start = Time.realtimeSinceStartup;

            while (!task.Done)
            {
                task.Poll();
                if (task.Done) break;

                if (mergeCancel)
                {
                    task.CleanUp();
                    mergeWaiting = false;
                    SetStatus(false, "已中止等待，工單已刪除");
                    yield break;
                }

                int waited = Mathf.RoundToInt(Time.realtimeSinceStartup - start);
                if (waited >= 8)
                    SetStatus(false, what + "... [" + waited + "s] "
                              + Lang.T("kkbridge 沒有回應——請確認它開著且正在監看 ")
                              + KKMerge.JobFolder());
                else
                    SetStatus(true, what + "... [" + waited + "s]");

                if (waited > mergeTimeout)
                {
                    task.CleanUp();
                    mergeWaiting = false;
                    SetStatus(false, string.Format(Lang.T("{0} 逾時（{1}s）"), what, waited));
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }

            mergeWaiting = false;

            if (!string.IsNullOrEmpty(task.Json))
            {
                Logger.LogInfo("[kkmerge] " + task.Json);
                string warn = KKMerge.ExtractWarnings(task.Json);
                if (!string.IsNullOrEmpty(warn))
                    Logger.LogWarning("[kkmerge] 有未搬移的換裝層級資料: " + warn);
            }

            if (!task.Ok || string.IsNullOrEmpty(task.Result))
            {
                SetStatus(false, string.Format(Lang.T("{0} 失敗: "), what) + task.Error);
                Logger.LogWarning("[kkmerge] " + task.Error);
                yield break;
            }

            if (onDone != null) onDone(task.Result);
        }

        /// <summary>成品檔名：(G) 1.名字_2026_0912_2253_19_746.png，跟手動存卡同一個格式。</summary>
        string OutCard(Studio.OCIChar oci, int index)
        {
            string name;
            try { name = PoseFix.NamePrefix(oci, index, IsFemale(oci)) + "_" + PoseFix.TimeStamp(); }
            catch { name = "merged_" + DateTime.Now.ToString("yyyy_MMdd_HHmm_ss_fff"); }
            return Path.Combine(KKMerge.JobFolder(), name + ".png");
        }

        /// <summary>中繼卡放同一個資料夾，用完就刪，所以名字只要不撞就好。</summary>
        string TempCard(string tag)
        {
            return Path.Combine(KKMerge.JobFolder(),
                tag + "_" + DateTime.Now.ToString("HHmmss_fff") + ".png");
        }

        /// <summary>刪掉中繼檔，刪不掉也不要吵。</summary>
        void Discard(params string[] paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception e) { Logger.LogWarning("[kkmerge] 刪不掉中繼檔 " + p + ": " + e.Message); }
            }
        }

        // -------------------------------------------------------------
        // 附加飾品：把服裝卡的飾品接到場上角色目前這套換裝後面
        // -------------------------------------------------------------
        // =============================================================
        // 換衣服：選兩張卡
        //   ① 要換上的服裝卡 —— 直接換裝（不動人物）
        //   ② 頭髮／必備飾品的服裝卡 —— 換完之後把飾品併回去（可略過）
        //
        // 第二步走的就是「添加飾品」那條路（存卡 → kkmerge append → 讀回來），
        // 所以 kkbridge / kkmerge 不用改：append 只讀服裝卡的 accessory.parts，
        // 從來不碰衣服本體，第①張換上的服裝不會被蓋掉。
        // =============================================================
        void StartDressThenAccessories(Studio.OCIChar oci, int index)
        {
            var target = oci;
            OpenGenericCardPicker(GetCoordinateFolder(),
                Lang.T("① 選擇要換上的服裝卡 - ") + GetCharDisplayName(oci),
                (coordFile) =>
                {
                    if (string.IsNullOrEmpty(coordFile)) return;

                    string bodyDir = Path.Combine(GetCoordinateFolder(), "Body");
                    if (!Directory.Exists(bodyDir)) bodyDir = GetCoordinateFolder();

                    OpenGenericCardPicker(bodyDir,
                        "② 選擇要帶回的頭髮／必備飾品（可略過）",
                        (accFile) => StartCoroutine(
                            DressThenAccessoriesRoutine(target, index, coordFile, accFile)),
                        true, true);
                }, true);
        }

        IEnumerator DressThenAccessoriesRoutine(Studio.OCIChar oci, int index,
                                                string coordFile, string accCoordFile)
        {
            SetStatus(true, "換裝中...");
            yield return null;
            ApplyCoordinateFile(oci, coordFile);

            // 等 Reload 把服裝真的套上去，不然接著存卡會存到舊的那一套
            yield return new WaitForSeconds(0.4f);

            if (string.IsNullOrEmpty(accCoordFile))
            {
                SetStatus(true, "✅ 服裝替換成功（沒有要帶回的飾品）");
                yield break;
            }

            SetStatus(true, "服裝已換上，接著併回飾品...");
            yield return StartCoroutine(MergeJobRoutine(oci, index, accCoordFile));
        }

        void StartMergeJob(Studio.OCIChar oci, int index)
        {
            var target = oci;
            OpenGenericCardPicker(GetCoordinateFolder(),
                "選擇要合併飾品的服裝卡",
                (coordFile) => StartCoroutine(MergeJobRoutine(target, index, coordFile)),
                true);
        }

        IEnumerator MergeJobRoutine(Studio.OCIChar oci, int index, string coordFile)
        {
            mergeJobRunning = true;         // 這趟不要跳檔案總管，也要存進 (temp)
            SetStatus(true, "存出人物卡中...");
            yield return StartCoroutine(CaptureThenSave(oci, index, IsFemale(oci), true));
            mergeJobRunning = false;

            string charaFile = lastSavedPath;
            if (string.IsNullOrEmpty(charaFile))
            {
                SetStatus(false, "人物卡存檔失敗，流程中止");
                yield break;
            }

            int ct = Mathf.Max(0, GetCoordType(oci));
            string outCard = OutCard(oci, index);
            Logger.LogInfo("[kkmerge] 附加飾品 服裝槽=" + ct
                           + "\n  人物卡: " + charaFile
                           + "\n  服裝卡: " + coordFile);

            var task = KKMerge.Append(charaFile, coordFile, ct, outCard);

            string final = null;
            yield return StartCoroutine(RunTask(task, "合併飾品", r => final = r));
            if (string.IsNullOrEmpty(final)) yield break;

            SetStatus(true, "套用合併後的人物卡...");
            yield return null;

            try
            {
                DoSwapCharacterAppearance(oci, final);
                SetStatus(true, Lang.T("✅ 合卡完成並已套用: ") + Path.GetFileName(final));
            }
            catch (Exception e)
            {
                SetStatus(false, Lang.T("套用失敗: ") + e.Message);
                Logger.LogWarning("[kkmerge] " + e);
            }

            Discard(charaFile);                       // 存出來的中繼人物卡
            if (!keepFinalChara) Discard(final);
        }

        // =============================================================
        // 保持服裝換人
        //
        // 順序很重要：先把「現在場上這套服裝」連飾品一起固定下來，
        // 再換人、再把整套移植到換好的人身上。
        // 先換人的話，場景原本的服裝就已經被新卡的服裝取代了。
        //
        // 跟舊版的差別：中間不再落地成服裝卡。madevil.kk.ass、
        // Accessory_States、KSOX 這些外掛在服裝卡格式裡的樣子沒有樣本，
        // 經過那一站會靜靜地掉。人物卡對人物卡兩邊格式相同，整包搬得過去。
        // =============================================================
        private string keepSwapCharaCard;
        private string keepSwapCoordCard;
        /// <summary>這一輪保持服裝換人用哪一種模式。</summary>
        private SwapMode keepSwapMode;

        void StartKeepOutfitSwap(Studio.OCIChar oci, int index, SwapMode mode)
        {
            keepSwapCharaCard = null;
            keepSwapCoordCard = null;
            keepSwapMode = mode;
            var target = oci;

            OpenGenericCardPicker(GetCharaFolder(IsFemale(oci)),
                string.Format(Lang.T("① 選擇要換上的人物卡（{0}）"), SwapModeName(mode)),
                (charaFile) =>
                {
                    keepSwapCharaCard = charaFile;

                    string bodyDir = Path.Combine(GetCoordinateFolder(), "Body");
                    if (!Directory.Exists(bodyDir)) bodyDir = GetCoordinateFolder();

                    OpenGenericCardPicker(bodyDir,
                        "② 選擇要帶入頭髮飾品的服裝卡（可略過）",
                        (coordFile) =>
                        {
                            keepSwapCoordCard = coordFile;   // null = 略過
                            WithShaderChoice(GetCharDisplayName(target), carry =>
                            {
                                keepSwapCarry = carry;
                                StartCoroutine(KeepOutfitSwapRoutine(target, index));
                            });
                        }, true, true);
                }, true);
        }

        IEnumerator KeepOutfitSwapRoutine(Studio.OCIChar oci, int index)
        {
            int ct = Mathf.Max(0, GetCoordType(oci));
            Logger.LogInfo("[kkmerge] 保持服裝換人，場上服裝槽=" + ct);

            // ---- 1. 存出場上角色 ----
            mergeJobRunning = true;
            SetStatus(true, "存出目前角色（用來做服裝）...");
            yield return StartCoroutine(CaptureThenSave(oci, index, IsFemale(oci), true));
            mergeJobRunning = false;

            string baseChara = lastSavedPath;
            if (string.IsNullOrEmpty(baseChara)) { SetStatus(false, "存卡失敗，流程中止"); yield break; }

            // ---- 2. 把頭髮飾品那張服裝卡接上去（沒選就跳過）----
            string dressed = baseChara;
            if (!string.IsNullOrEmpty(keepSwapCoordCard))
            {
                string merged = TempCard("dressed");
                var t1 = KKMerge.Append(baseChara, keepSwapCoordCard, ct, merged);
                yield return StartCoroutine(RunTask(t1, "合併頭髮飾品", r => dressed = r));
                if (dressed == baseChara) yield break;   // 失敗，RunTask 已經報過了
            }

            // ---- 3. 在工作室裡換人，再存成臨時人物卡 ----
            //      這步刻意留給工作室做：直接合併兩張原始卡會有各種設定差異，
            //      換完再存才能保住場景裡的身形與位置。
            SetStatus(true, "換人中...");
            // 只鎖身高模式在這一步就決定了：換完人存出去的那張卡帶的是
            // 新卡自己的身材 + 舊身高，後面移植服裝不會再動到身材。
            try { DoSwapCharacterAppearance(oci, keepSwapCharaCard, keepSwapMode, keepSwapCarry); }
            catch (Exception e) { SetStatus(false, Lang.T("換人失敗: ") + e.Message); yield break; }
            yield return new WaitForSeconds(3f);

            mergeJobRunning = true;
            SetStatus(true, "存出換好的角色...");
            yield return StartCoroutine(CaptureThenSave(oci, index, IsFemale(oci), true));
            mergeJobRunning = false;

            string swapped = lastSavedPath;
            if (string.IsNullOrEmpty(swapped)) { SetStatus(false, "存卡失敗，流程中止"); yield break; }

            // ---- 4. 把第 1~2 步那套服裝整套移植到新人身上 ----
            // 這時候人已經換過了，OutCard 拿到的就是新卡的角色名
            string finalCard = null;
            string outCard = OutCard(oci, index);
            var t2 = KKMerge.Transplant(dressed, ct, swapped, ct, outCard,
                                        mergePushup ? "follow_outfit" : "keep_target",
                                        "keep_target");
            yield return StartCoroutine(RunTask(t2, "移植整套換裝", r => finalCard = r));
            if (string.IsNullOrEmpty(finalCard)) yield break;

            // ---- 5. 換上最終成品 ----
            try
            {
                DoSwapCharacterAppearance(oci, finalCard, keepSwapMode, keepSwapCarry);
                SetStatus(true, string.Format(Lang.T("✅ 保持服裝換人（{0}）完成: "), SwapModeName(keepSwapMode))
                                + Path.GetFileName(finalCard));
            }
            catch (Exception e) { SetStatus(false, Lang.T("套用最終卡失敗: ") + e.Message); }

            // 中繼檔一律刪掉，只有最終成品看設定要不要留
            Discard(baseChara, swapped);
            if (dressed != baseChara) Discard(dressed);
            if (!keepFinalChara) Discard(finalCard);
        }

        // =============================================================
        // 設置面板：黏在主視窗右邊，按 F6 或面板上的儲存鍵都會寫回設定檔
        // =============================================================
        string jobFolderProbed;      // 上次問的是哪個路徑
        bool jobFolderThere;
        float jobFolderAt = -99f;

        bool JobFolderExists()
        {
            string d = string.IsNullOrEmpty(jobFolder) ? KKMerge.DefaultJobFolder() : jobFolder;
            if (d == jobFolderProbed && Time.realtimeSinceStartup - jobFolderAt < 0.5f)
                return jobFolderThere;
            jobFolderProbed = d;
            jobFolderAt = Time.realtimeSinceStartup;
            try { jobFolderThere = Directory.Exists(d); }
            catch { jobFolderThere = false; }
            return jobFolderThere;
        }

        void SettingsWindow(int id)
        {
            // 內容一律夾在視窗寬度之內。
            //
            // 上一版滑桿被拉到視窗外面看不到，原因不是我上次寫的那個。
            // 真正的原因是 IMGUI 的一條規則：**沒開換行的控制項，它的「最小寬度」
            // 就是內容的完整寬度**，而 GUILayout 永遠不會把子項壓到最小寬度以下。
            // 所以只在外面包一層 BeginVertical(Width(...)) 是沒有用的 ——
            // 一個內容很長的 TextField（那串 ABMX 骨頭規則上百個字）
            // 會把整個捲動區撐到一千多像素寬，其他會延展的控制項（滑桿）
            // 跟著延展到那個寬度，於是滑鈕跑到視窗外面。
            //
            // 從螢幕截圖就能反推：0.8 秒那條（0～2）滑鈕在 40% 的位置卻落在 x≈462，
            // 推得出實際軌道寬 ~1110 px，是視窗的兩倍。
            //
            // 正確做法是**每一個會撐寬的控制項都給明確寬度**：
            // GUILayout.Width() 會同時設定 min 和 max，這樣它的最小寬度就是這個值，
            // 再長的內容也撐不開。長說明文字則改用會換行的樣式。
            float contentW = Mathf.Max(200f, sideRect.width - 56f);

            settingsScroll = GUILayout.BeginScrollView(settingsScroll, false, true,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView);

            GUILayout.Label(Lang.T("── 合卡 ──"));
            GUILayout.Label(Lang.T("工單資料夾（kkbridge 監看的那個，留空就用預設）"),
                            Wrap(), GUILayout.Width(contentW));
            jobFolder = GUILayout.TextField(jobFolder ?? "", GUILayout.Width(contentW));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("用預設"), GUILayout.Width(80)))
                jobFolder = KKMerge.DefaultJobFolder();
            if (GUILayout.Button(Lang.T("開啟資料夾"), GUILayout.Width(100)))
            {
                try
                {
                    string d = string.IsNullOrEmpty(jobFolder)
                        ? KKMerge.DefaultJobFolder() : jobFolder;
                    Directory.CreateDirectory(d);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\"");
                }
                catch (Exception e) { Logger.LogWarning(e.Message); }
            }
            // 這一行以前是每趟 OnGUI 就去打一次磁碟。碰到網路磁碟或睡眠中的硬碟，
            // 光是這個 Directory.Exists 就能讓每一幀多幾毫秒 —— 而它問的是
            // 「使用者一分鐘才改一次的一個路徑」。半秒問一次綽綽有餘。
            GUILayout.Label(JobFolderExists() ? Lang.T("（存在）") : Lang.T("（不存在，會自動建立）"));
            GUILayout.EndHorizontal();

            mergePushup = OptRow("移植換裝時胸托參數", mergePushup, "跟著服裝", "保留人物");
            keepFinalChara = OptRow("合卡後保留最終人物卡", keepFinalChara, "保留", "刪除");

            GUILayout.Space(8);
            GUILayout.Label(Lang.T("── 換角色 ──"));
            keepCharaName = OptRow("換角色保留角色名稱", keepCharaName, "是", "否");
            autoRestorePose = OptRow("換角色重新套用姿勢", autoRestorePose, "是", "否");
            keepExpression = OptRow("換角色保留表情", keepExpression, "是", "否");
            autoFixDbCollider = OptRow("換角色還原碰撞器綁定", autoFixDbCollider, "是", "否");
            autoApplyBlendPreset = OptRow("換角色自動帶入同名形態鍵", autoApplyBlendPreset, "是", "否");
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("換角色套用場景原角色的著色器"), GUILayout.Width(OptLabelW));
            if (GUILayout.Toggle(carryShaderMode == 0, " " + Lang.T("關"), GUILayout.Width(OptRadioW))) carryShaderMode = 0;
            if (GUILayout.Toggle(carryShaderMode == 1, " " + Lang.T("詢問"), GUILayout.Width(OptRadioW))) carryShaderMode = 1;
            if (GUILayout.Toggle(carryShaderMode == 2, " " + Lang.T("自動"), GUILayout.Width(OptRadioW))) carryShaderMode = 2;
            GUILayout.EndHorizontal();

            keepOldHeadSize = OptRow("維持新卡身材：頭大小沿用舊卡", keepOldHeadSize, "是", "否");
            GUILayout.Label(Lang.T("「維持新卡身材」要從舊卡留下的 ABMX 骨頭"),
                            Wrap(), GUILayout.Width(contentW));
            // 輸入框自己占一整行，跟上面的工單資料夾一樣寬。
            // 跟按鈕擠在同一行的話剩不到一半，內容根本看不到頭。
            // 這一個是主嫌：規則字串上百個字，不給寬度的話它的最小寬度就是那串字的寬度。
            abmxKeepBones = GUILayout.TextField(abmxKeepBones ?? "", GUILayout.Width(contentW));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("預設"), GUILayout.Width(60)))
                abmxKeepBones = AbmxFilter.DefaultKeepRules;
            if (GUILayout.Button(Lang.T("清空"), GUILayout.Width(60)))
                abmxKeepBones = "";
            GUILayout.EndHorizontal();
            // 用會換行的樣式 + 明確寬度。預設的 label 不換行，
            // 這一行的完整寬度就會變成整個捲動區的最小寬度。
            GUILayout.Label(Lang.T("cf_j_* 前綴比對　cf_s_head 完整名稱　-cf_j_ana 排除。")
                            + Lang.T("大小寫有差：cf_j_ 是骨架、cf_J_ 是臉。"),
                            Wrap(), GUILayout.Width(contentW));

            GUILayout.Space(8);
            GUILayout.Label(Lang.T("── 縮圖與存檔 ──"));
            cropThumbToChar = OptRow("縮圖依角色範圍裁切", cropThumbToChar, "是", "否");
            headAnchorThumb = OptRow("取景以頭部為中心", headAnchorThumb, "是", "否");

            // 這幾條滑桿改成「標籤一行、滑桿自己一整行」。
            // 原本標籤占掉 230px、滑桿只剩窗寬減 230 —— 短得很難微調，
            // 而且標籤文字一長就把滑桿擠成一小截。現在滑桿跟資料夾輸入框一樣寬。
            if (headAnchorThumb)
            {
                GUILayout.Label(Lang.T("取景高度 ") + thumbFrameScale.ToString("F2") + Lang.T(" 倍頭腳距離")
                                + (thumbFrameScale >= 1.1f ? Lang.T("（全身）")
                                   : thumbFrameScale >= 0.45f ? Lang.T("（半身）") : Lang.T("（大頭照）")),
                                Wrap(), GUILayout.Width(contentW));
                thumbFrameScale = Mathf.Round(
                    GUILayout.HorizontalSlider(thumbFrameScale, 0.15f, 2f,
                                               GUILayout.Width(contentW)) * 20f) / 20f;

                GUILayout.Label(Lang.T("頭在畫面高度 ") + thumbHeadAtY.ToString("F2")
                                + Lang.T("（1 上緣 / 0.5 正中）"), Wrap(), GUILayout.Width(contentW));
                thumbHeadAtY = Mathf.Round(
                    GUILayout.HorizontalSlider(thumbHeadAtY, 0.2f, 0.95f,
                                               GUILayout.Width(contentW)) * 20f) / 20f;
            }

            GUILayout.Label(thumbMaxHeight <= 0
                                ? Lang.T("縮圖大小：不縮小（最清晰）")
                                : Lang.T("縮圖高度上限 ") + thumbMaxHeight + " px",
                            Wrap(), GUILayout.Width(contentW));
            // 0 / 352 / 704 / 1056 / 1408 五段，0 放在最左邊當預設
            int step = Mathf.RoundToInt(GUILayout.HorizontalSlider(thumbMaxHeight / 352f, 0f, 4f,
                                                                   GUILayout.Width(contentW)));
            thumbMaxHeight = step * 352;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("擷圖時只顯示該角色"), GUILayout.Width(OptLabelW));
            soloCharForThumb = Radio(soloCharForThumb, "是", "否");
            GUILayout.Space(8);
            hideMalesForThumb = GUILayout.Toggle(hideMalesForThumb, Lang.T(" 同時隱藏男角色"),
                                                 GUILayout.Width(120));
            GUILayout.EndHorizontal();

            GUILayout.Label(Lang.T("拍照前等待 ") + settleSeconds.ToString("F1") + Lang.T(" 秒（待物理靜止）"),
                            Wrap(), GUILayout.Width(contentW));
            settleSeconds = Mathf.Round(
                GUILayout.HorizontalSlider(settleSeconds, 0f, 2f,
                                           GUILayout.Width(contentW)) * 10f) / 10f;

            hideAllUiForThumb = OptRow("擷圖時隱藏所有介面", hideAllUiForThumb, "是", "否");
            openFolderAfterSave = OptRow("存檔後開啟檔案總管", openFolderAfterSave, "是", "否");

            // 「顯示工具列按鈕」原本在這裡，已經移到主視窗「設置」按鈕的下面 ——
            // 那是個常按的開關，不該藏在還要先開一層的面板裡。

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("存到 Temp 資料夾"), GUILayout.Width(OptLabelW));
            CardSaver.CharaToTemp = GUILayout.Toggle(CardSaver.CharaToTemp, Lang.T(" 人物卡"), GUILayout.Width(70));
            CardSaver.CoordToTemp = GUILayout.Toggle(CardSaver.CoordToTemp, Lang.T(" 服裝卡"), GUILayout.Width(70));
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();

            DrawLangAndReset();

            GUILayout.Space(4);
            GUI.color = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button(Lang.T("儲存並關閉"), GUILayout.Height(28)))
            {
                SaveSettingsPanel();
                showSettings = false;
            }
            GUI.color = Color.white;
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
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

        /// <summary>把設置面板的內容立刻寫回設定檔，下次開遊戲還在。</summary>
        void SaveSettingsPanel()
        {
            KKMerge.JobFolderOverride = jobFolder ?? "";
            cfgSyncTime = 0f;                      // 逼 SyncSettings 這一幀就寫回
            SyncSettings();
            try { Config.Save(); }
            catch (Exception e) { Logger.LogWarning("[CharTools] 設定寫檔失敗: " + e.Message); }
            SetStatus(true, "設置已儲存");
        }

        /// <summary>
        /// 會換行的標籤樣式。
        ///
        /// 內建 skin 的 label 是**不換行**的，而不換行的控制項，
        /// 它的最小寬度就是整段文字的寬度 —— 一行長說明就能把整個捲動區撐爆。
        ///
        /// skin 換掉之後要重建（VrSkin 在 VR 裡會換一份不透明的），
        /// 所以記住是照哪一份 skin 複製的。
        /// </summary>
        GUIStyle wrapStyle;
        GUISkin wrapStyleSkin;

        GUIStyle Wrap()
        {
            if (wrapStyle == null || wrapStyleSkin != GUI.skin)
            {
                wrapStyleSkin = GUI.skin;
                wrapStyle = new GUIStyle(GUI.skin.label);
                wrapStyle.wordWrap = true;
            }
            return wrapStyle;
        }

        // 設定列：標籤靠左，兩個互斥選項靠後對齊。
        //
        // 230 太寬了：最長的標籤（「換角色自動帶入同名形態鍵」）也只要 ~170，
        // 而「擷圖時只顯示該角色」那一列後面還跟著「同時隱藏男角色」——
        // 230 + 84 + 84 + 120 就超過視窗可用寬度，整列被推出去，
        // 捲動區就會長出橫向捲軸、畫面整個往左偏。
        const float OptLabelW = 190f;

        // 64 太窄：「保留人物」四個中文字加上核取方塊就超過，右邊那個會被切掉一半。
        // 中文字比拉丁字母寬，這種固定寬度一律照最長的那組標籤抓。
        const float OptRadioW = 84f;

        static bool Radio(bool value, string onText, string offText)
        {
            if (GUILayout.Toggle(value, " " + Lang.T(onText), GUILayout.Width(OptRadioW))) value = true;
            if (GUILayout.Toggle(!value, " " + Lang.T(offText), GUILayout.Width(OptRadioW))) value = false;
            return value;
        }

        static bool OptRow(string label, bool value, string onText, string offText)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T(label), GUILayout.Width(OptLabelW));
            value = Radio(value, onText, offText);
            GUILayout.EndHorizontal();
            return value;
        }

        /// <summary>場上角色目前穿的是第幾套服裝。取不到回 -1，讓捏人自行判斷。</summary>
        int GetCoordType(Studio.OCIChar oci)
        {
            try { return Convert.ToInt32(GetPropertyOrField(oci.charInfo.fileStatus, "coordinateType")); }
            catch { return -1; }
        }

        bool InCat(AccessoryTools.AccInfo a)
        {
            return accCategory.Length == 0 || a.Category == accCategory;
        }

        void BlendLockWindow(int id)
        {
            ChaControl cha = null;
            if (blendTarget != null)
            {
                try { cha = blendTarget.charInfo; } catch { }
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(cha != null ? Lang.T("對象: ") + GetCharDisplayName(blendTarget) : Lang.T("對象已失效"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("關閉"), GUILayout.Width(60))) CloseSidePanel();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            BlendShapeLockUI.Draw(cha);
        }

        // 共用的縮圖選卡視窗 (換角色跟換衣服都走這個，不再用原生檔案總管，
        // 樣式照 KKPEHeightLock.cs 的縮圖網格 + 資料夾導覽做)
        private class CardEntry
        {
            public string path;
            public string name;
            public Texture2D texture;
        }

        private bool showGenericCardPicker = false;

        private Rect genericCardPickerRect = new Rect(460, 20, 640, 520);

        private Vector2 genericCardScroll;

        private string genericCardPickerTitle = "";

        private string genericCardBrowseRoot = "";

        private string genericCardCurrentDir = "";

        private string genericCardSearch = "";

        private List<CardEntry> genericCardEntries = new List<CardEntry>();

        private List<string> genericCardSubFolders = new List<string>();

        private Action<string> genericCardOnPicked;

        private bool genericCardReturnToCharList;

        private int genericCardsProcessedThisFrame;

        // 場景角色清單選取器 (點名字直接跳原生選卡對話框)
        private bool showCharPicker = false;

        // 660 寬：第二行現在有「保持服裝只鎖身高」這種 8 個字的按鈕，600 塞不下
        // （固定寬度的按鈕加起來就超過視窗內寬，超過的部分會被裁掉）。
        // x 從 20 移到 360：20 會壓在工作室左邊那排工具按鈕上，
        // 在 VR 裡又特別難拖動視窗，一開始就擋住等於不能用。
        private Rect charPickerRect = new Rect(360, 20, 660, 830);

        private Vector2 charPickerScroll;

        // UserData/coordinate 資料夾的相對路徑，用 Application.dataPath
        // (指向 XXX_Data 資料夾) 往上一層找到遊戲根目錄再組出來，不寫死
        // 絕對路徑，換裝機/搬家都不用改。
        string GetCoordinateFolder()
        {
            try
            {
                string dataPath = UnityEngine.Application.dataPath; // .../Koikatu_Data
                string gameRoot = System.IO.Path.GetDirectoryName(dataPath);
                string coordDir = System.IO.Path.Combine(System.IO.Path.Combine(gameRoot, "UserData"), "coordinate");
                return coordDir;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[Coordinate] GetCoordinateFolder failed: " + e);
                return null;
            }
        }

        /// <summary>依性別回傳人物卡資料夾。</summary>
        string GetCharaFolder(bool female)
        {
            string f = GetCharaFemaleFolder();
            if (female || string.IsNullOrEmpty(f)) return f;
            try
            {
                string male = Path.Combine(Path.GetDirectoryName(f), "male");
                return Directory.Exists(male) ? male : f;
            }
            catch { return f; }
        }

        string GetCharaFemaleFolder()
        {
            try
            {
                string dataPath = UnityEngine.Application.dataPath;
                string gameRoot = System.IO.Path.GetDirectoryName(dataPath);
                return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(gameRoot, "UserData"), "chara"), "female");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CardPicker] GetCharaFemaleFolder failed: " + e);
                return null;
            }
        }

        // 打開共用選卡視窗。rootFolder 決定要瀏覽哪個資料夾 (角色卡或服裝卡)，
        // onPicked 是選好之後要做的事 (換角色 or 換衣服)，returnToCharList
        // 決定選完/取消之後要不要自動跳回場景角色清單，方便連續操作。
        /// <summary>再按一次同一顆按鈕就收起來；按別顆就換成那一顆的內容。</summary>
        bool ToggleSidePanel(Studio.OCIChar oci, int kind)
        {
            bool sameAsOpen = (showGenericCardPicker || showBlendLock || showAccPanel || showDbFix)
                              && sideKind == kind
                              && ReferenceEquals(sideOci, oci);
            CloseSidePanel();
            if (sameAsOpen) return false;      // 收起，不再開新的

            sideOci = oci;
            sideKind = kind;
            return true;
        }

        void CloseSidePanel()
        {
            showGenericCardPicker = false;
            showBlendLock = false;
            showAccPanel = false;
            showSettings = false;
            showDbFix = false;
            sideKind = 0;
            sideOci = null;
        }

        /// <summary>這次的選卡可不可以略過（略過時用 null 呼叫 callback）。</summary>
        private bool genericCardSkippable;

        void OpenGenericCardPicker(string rootFolder, string title, Action<string> onPicked,
                                   bool returnToCharList, bool skippable = false)
        {
            genericCardSkippable = skippable;
            showBlendLock = false;
            showAccPanel = false;
            // 角色清單保持開著，選卡視窗停在旁邊，選完可以直接換下一個角色
            showGenericCardPicker = true;
            genericCardPickerTitle = Lang.T(title);
            genericCardBrowseRoot = rootFolder;
            genericCardCurrentDir = "";
            genericCardSearch = "";
            genericCardScroll = Vector2.zero;
            genericCardOnPicked = onPicked;
            genericCardReturnToCharList = returnToCharList;
            LoadGenericCardList();
        }

        void CloseGenericCardPicker()
        {
            showGenericCardPicker = false;
            sideKind = 0;
            sideOci = null;
            if (genericCardReturnToCharList) showCharPicker = true;   // 捲動位置刻意保留
        }

        void LoadGenericCardList()
        {
            genericCardEntries = new List<CardEntry>();
            genericCardSubFolders = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(genericCardBrowseRoot)) return;
                string dir = string.IsNullOrEmpty(genericCardCurrentDir)
                    ? genericCardBrowseRoot
                    : System.IO.Path.Combine(genericCardBrowseRoot, genericCardCurrentDir);

                if (!System.IO.Directory.Exists(dir))
                {
                    Logger.LogWarning("[CardPicker] Folder not found: " + dir);
                    return;
                }

                foreach (var d in System.IO.Directory.GetDirectories(dir))
                {
                    string name = System.IO.Path.GetFileName(d);
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("_"))
                        genericCardSubFolders.Add(name);
                }
                genericCardSubFolders.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (var f in System.IO.Directory.GetFiles(dir, "*.png"))
                {
                    string fname = System.IO.Path.GetFileName(f);
                    if (fname.StartsWith("_")) continue;
                    genericCardEntries.Add(new CardEntry
                    {
                        path = f,
                        name = System.IO.Path.GetFileNameWithoutExtension(f)
                    });
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CardPicker] LoadGenericCardList error: " + e);
            }
        }

        void LazyLoadGenericCards(int maxPerFrame)
        {
            genericCardsProcessedThisFrame = 0;
            foreach (var entry in genericCardEntries)
            {
                if (genericCardsProcessedThisFrame >= maxPerFrame) return;
                if (!string.IsNullOrEmpty(genericCardSearch) &&
                    entry.name.IndexOf(genericCardSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (entry.texture == null)
                {
                    try { entry.texture = LoadCardThumbnail(entry.path); }
                    catch { }
                    genericCardsProcessedThisFrame++;
                }
            }
        }

        // 讀卡片縮圖。優先用 KKAPI 的 PngAssist (裁切過的正式縮圖)，沒裝
        // KKAPI 就退回直接把整張 PNG 當圖片讀 (畫質沒那麼漂亮但至少能動)。
        Texture2D LoadCardThumbnail(string path)
        {
            try
            {
                Type pngAssistType = AccessTools.TypeByName("KKAPI.Utilities.PngAssist");
                if (pngAssistType != null)
                {
                    MethodInfo m = AccessTools.Method(pngAssistType, "LoadTexture", new[] { typeof(string) });
                    if (m != null)
                        return m.Invoke(null, new object[] { path }) as Texture2D;
                }
            }
            catch { }

            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                return tex;
            }
            catch
            {
                return null;
            }
        }

        void DrawGenericCardPicker(int id)
        {
            GUILayout.BeginVertical();
            LazyLoadGenericCards(6);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("← 上一層"), GUILayout.Width(80)))
            {
                if (genericCardCurrentDir.Length > 0)
                {
                    int idx = genericCardCurrentDir.LastIndexOfAny(new[] { '\\', '/' });
                    genericCardCurrentDir = idx >= 0 ? genericCardCurrentDir.Substring(0, idx) : "";
                    LoadGenericCardList();
                }
            }
            GUILayout.Label(Lang.T("目錄: ") + (genericCardCurrentDir.Length > 0 ? genericCardCurrentDir : Lang.T("(根目錄)")), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("搜尋:"), GUILayout.Width(45));
            genericCardSearch = GUILayout.TextField(genericCardSearch ?? "", GUILayout.MinWidth(180));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (genericCardSubFolders.Count > 0)
            {
                GUILayout.Label(Lang.T("子資料夾:"));
                string pickedSub = null;
                foreach (var sub in genericCardSubFolders)
                {
                    if (GUILayout.Button("📁 " + sub, GUILayout.Height(24)))
                        pickedSub = sub;
                }
                if (pickedSub != null)
                {
                    genericCardCurrentDir = string.IsNullOrEmpty(genericCardCurrentDir) ? pickedSub : genericCardCurrentDir + "/" + pickedSub;
                    LoadGenericCardList();
                }
                GUILayout.Space(4);
            }

            if (genericCardEntries.Count == 0)
            {
                GUILayout.Label(Lang.T("這個資料夾裡沒有卡片。"));
            }
            else
            {
                List<CardEntry> filtered = new List<CardEntry>();
                foreach (var e in genericCardEntries)
                {
                    if (!string.IsNullOrEmpty(genericCardSearch) &&
                        e.name.IndexOf(genericCardSearch, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    filtered.Add(e);
                }

                genericCardScroll = GUILayout.BeginScrollView(genericCardScroll);
                const float thumbWidth = 110f;
                int columns = Mathf.Max(1, (int)((genericCardPickerRect.width - 40f) / (thumbWidth + 8f)));

                string pickedPath = null;
                int index = 0;
                while (index < filtered.Count)
                {
                    GUILayout.BeginHorizontal();
                    for (int c = 0; c < columns && index < filtered.Count; c++, index++)
                    {
                        CardEntry entry = filtered[index];
                        GUILayout.BeginVertical(GUILayout.Width(thumbWidth));

                        if (entry.texture != null)
                            GUILayout.Label(entry.texture, GUILayout.Width(thumbWidth), GUILayout.Height(thumbWidth));
                        else
                            GUILayout.Box("…", GUILayout.Width(thumbWidth), GUILayout.Height(thumbWidth));

                        string displayName = entry.name.Length > 14 ? entry.name.Substring(0, 14) + "…" : entry.name;
                        if (GUILayout.Button(displayName, GUILayout.Width(thumbWidth)))
                            pickedPath = entry.path;

                        GUILayout.EndVertical();
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();

                if (pickedPath != null)
                {
                    Action<string> callback = genericCardOnPicked;
                    CloseGenericCardPicker();
                    if (callback != null) callback(pickedPath);
                }
            }

            GUILayout.Space(4);
            if (genericCardSkippable)
            {
                GUI.color = new Color(0.75f, 0.9f, 1f);
                if (GUILayout.Button(Lang.T("略過這一步（不帶入服裝卡，直接繼續）"), GUILayout.Height(26)))
                {
                    Action<string> skip = genericCardOnPicked;
                    CloseGenericCardPicker();
                    if (skip != null) skip(null);       // null = 使用者選擇略過
                }
                GUI.color = Color.white;
            }
            if (GUILayout.Button(Lang.T("取消"), GUILayout.Height(24)))
            {
                CloseGenericCardPicker();
            }

            GUILayout.EndVertical();
        }

        // 讀取一張服裝卡 (.png)，套用到指定角色身上 (只換服裝，不動臉/身材)。
        // 不再自己組裝「讀檔→套用→刷新」這整套流程 - 就算用遊戲原生功能，
        // 這個角色也會在 DynamicBoneDistributionEditor 這個外掛炸掉
        // (NullReferenceException in DBDEDynamicBoneEdit.ApplyAll)，證實
        // 換裝失敗跟我們的程式碼無關，是別的外掛本身的問題，不可能繞過。
        // 改成單純幫你在工作面板裡選中這個角色，換裝的部分交回遊戲原生
        // 功能自己處理，你原本的痛點(場景深處的角色不好選)還是有解決到。
        /// <summary>目前是不是已經選著這個節點了。</summary>
        bool IsAlreadySelected(object treeCtrl, object node)
        {
            try
            {
                // 單選欄位
                object single = GetPropertyOrField(treeCtrl, "selectNode");
                if (single != null && ReferenceEquals(single, node)) return true;

                // 多選集合
                foreach (var name in new[] { "selectNodes", "selectedNodes" })
                {
                    object many = GetPropertyOrField(treeCtrl, name);
                    var col = many as System.Collections.IEnumerable;
                    if (col == null) continue;
                    foreach (var o in col)
                        if (ReferenceEquals(o, node)) return true;
                }
            }
            catch { }
            return false;
        }

        void SelectCharacterInWorkspace(Studio.OCIChar oci)
        {
            try
            {
                Studio.Studio studio = StudioInstance();
                object treeCtrl = GetMember(studio, "treeNodeCtrl");
                object treeNodeObj = GetPropertyOrField(oci, "treeNodeObject");
                if (treeCtrl == null || treeNodeObj == null)
                {
                    Logger.LogWarning("[CharPicker] 取不到角色的樹狀節點");
                    return;
                }

                // KK 的簽名是 SelectSingle(TreeNodeObject, bool)，之前只接受
                // 單參數的方法，所以永遠匹配不到，一路掉到「直接設欄位」的退路——
                // 那個只改變數不會真的選取，所以沒先手動點過就沒有效果。
                // 改成：只要第一個參數收得下節點就用，其餘參數自動補預設值。
                // SelectSingle 是「切換」語意：對已選取的節點再呼叫一次會取消選取。
                // 動作前選一次、動作後又選一次，第二次就把它關掉了。
                // 所以已經選著就直接跳過。
                if (IsAlreadySelected(treeCtrl, treeNodeObj))
                {
                    Logger.LogInfo("[CharPicker] 已經是選取狀態，略過");
                    return;
                }

                bool selected = false;
                foreach (string methodName in new[] { "SelectSingle", "Select", "SelectNode", "OnSelect" })
                {
                    foreach (MethodInfo m in treeCtrl.GetType().GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != methodName) continue;

                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length < 1 || !ps[0].ParameterType.IsInstanceOfType(treeNodeObj)) continue;

                        object[] args = new object[ps.Length];
                        args[0] = treeNodeObj;
                        for (int a = 1; a < ps.Length; a++)
                        {
                            Type pt = ps[a].ParameterType;
                            if (pt == typeof(bool)) args[a] = true;      // deselect others
                            else args[a] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                        }

                        try
                        {
                            m.Invoke(treeCtrl, args);
                            selected = true;
                            Logger.LogInfo("[CharPicker] 用 " + methodName + "("
                                           + ps.Length + " 參數) 選取成功");
                            break;
                        }
                        catch (Exception eTry)
                        {
                            Logger.LogWarning("[CharPicker] " + methodName + " 呼叫失敗: "
                                              + eTry.GetBaseException().Message);
                        }
                    }
                    if (selected) break;
                }

                if (!selected)
                {
                    SetPropertyOrField(treeCtrl, "selectNode", treeNodeObj);
                    Logger.LogWarning("[CharPicker] 找不到可用的選取方法，只設了 selectNode 欄位。"
                                      + " 可用方法如下：");
                    foreach (MethodInfo m in treeCtrl.GetType().GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var names = new List<string>();
                        foreach (var pp in m.GetParameters()) names.Add(pp.ParameterType.Name);
                        Logger.LogWarning("    " + m.Name + "(" + string.Join(", ", names.ToArray()) + ")");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CharPicker] 選取角色失敗: " + e);
            }
        }

        // 這個遊戲版本的真實簽章 (跟 autocharamoments.py 原本假設的不一樣，
        // 用診斷版挖出來的)：
        //   AssignCoordinate(CoordinateType type, ChaFileCoordinate srcCoorde) - 兩個參數，要先給目前的類型
        //   Reload(bool noChangeClothes, bool noChangeHead, bool noChangeHair, bool noChangeBody) - 4 個參數，而且是「不要改」的意思
        void ApplyCoordinateFile(Studio.OCIChar oci, string path)
        {
            try
            {
                object chaCtrl = oci.charInfo;
                Type chaCtrlType = chaCtrl.GetType();

                object nowCoordinate = GetMember(chaCtrl, "nowCoordinate");
                if (nowCoordinate == null) { statusMsg = "❌ 無法取得 nowCoordinate"; return; }

                MethodInfo loadFileMethod = nowCoordinate.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "LoadFile" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (loadFileMethod == null) { statusMsg = "❌ 找不到 LoadFile(string)"; return; }

                object loadResult = loadFileMethod.Invoke(nowCoordinate, new object[] { path });
                bool loadOk = !(loadResult is bool) || (bool)loadResult;
                if (!loadOk) { statusMsg = "❌ 讀取服裝卡失敗，請確認檔案格式！"; return; }

                // AssignCoordinate 需要「目前實際穿著的 CoordinateType」當
                // 第一個參數，從 fileStatus.coordinateType 讀出來。
                object fileStatus = GetMember(chaCtrl, "fileStatus");
                object currentCoordType = fileStatus != null ? GetPropertyOrField(fileStatus, "coordinateType") : null;
                if (currentCoordType == null)
                {
                    statusMsg = "❌ 無法取得目前的 CoordinateType (fileStatus.coordinateType)";
                    return;
                }

                MethodInfo assignMethod = chaCtrlType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "AssignCoordinate" && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.IsInstanceOfType(nowCoordinate));
                if (assignMethod == null)
                {
                    statusMsg = "❌ 找不到 AssignCoordinate(CoordinateType, ChaFileCoordinate)";
                    return;
                }
                assignMethod.Invoke(chaCtrl, new object[] { currentCoordType, nowCoordinate });

                MethodInfo reloadMethod = chaCtrlType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "Reload" && m.GetParameters().Length == 4
                        && m.GetParameters().All(p => p.ParameterType == typeof(bool)));
                if (reloadMethod != null)
                {
                    // 參數是「不要改」的意思：只放行服裝變更 (noChangeClothes=false)，
                    // 頭/髮/身體都維持不變 (true)。
                    reloadMethod.Invoke(chaCtrl, new object[] { false, true, true, true });
                }

                object sexObj = GetPropertyOrField(chaCtrl, "Sex");
                if (sexObj != null && Convert.ToInt32(sexObj) == 1)
                {
                    MethodInfo bustMethod = GetMethodByName(chaCtrlType, "UpdateBustSoftnessAndGravity");
                    if (bustMethod != null) bustMethod.Invoke(chaCtrl, null);
                }

                statusMsg = "✅ 服裝替換成功！";
            }
            catch (Exception ex)
            {
                statusMsg = Lang.T("❌ 換裝服裝發生錯誤: ") + ex.Message;
                Logger.LogWarning("[Coordinate] ApplyCoordinateFile error: " + ex);
            }
        }

        void SwapCharacterAppearance(string path)
        {
            try
            {
                Studio.Studio studio = StudioInstance();
                object treeCtrl = GetMember(studio, "treeNodeCtrl");
                object selectNode = GetMember(treeCtrl, "selectNode");
                if (selectNode == null) { statusMsg = "❌ 請先在左側選擇一個角色！"; return; }

                IDictionary dicInfo = GetMember(studio, "dicInfo") as IDictionary;
                if (dicInfo == null || !dicInfo.Contains(selectNode)) { statusMsg = "❌ 無法取得節點資訊！"; return; }

                Studio.OCIChar ofem = dicInfo[selectNode] as Studio.OCIChar;
                if (ofem == null) { statusMsg = "❌ 選擇的節點不是角色！"; return; }

                DoSwapCharacterAppearance(ofem, path);
            }
            catch (Exception ex)
            {
                statusMsg = Lang.T("❌ 換裝發生錯誤: ") + ex.Message;
            }
        }

        // Same logic as SwapCharacterAppearance above, but takes the target
        // character directly instead of reading Studio's own tree selection -
        // used by the scene character picker (8b), so characters buried deep
        // in folders don't need to be manually selected in Studio's tree first.
        // =============================================================
        // 換人時帶入場景原角色的著色器（ShaderCarry）
        // =============================================================
        /// <summary>照設定決定要不要帶著色器；「詢問」時先跳出確認視窗，按了才真的換人。</summary>
        void WithShaderChoice(string who, Action<bool> act)
        {
            if (carryShaderMode == 0 || !ShaderCarry.Available) { act(false); return; }
            if (carryShaderMode == 2) { act(true); return; }
            carryPromptAct = act;
            carryPromptWho = who ?? "";
            carryPromptRect.x = (Screen.width - carryPromptRect.width) / 2f;
            carryPromptRect.y = (Screen.height - carryPromptRect.height) / 2f;
            showCarryPrompt = true;
        }

        void CarryPromptWindow(int id)
        {
            GUILayout.Label(Lang.T("要把場景原角色的著色器套到新角色嗎？"));
            GUILayout.Label(Lang.T("對象: ") + carryPromptWho);
            GUILayout.Label(Lang.T("（只換身體、臉、眼睛、眉毛、牙齒、舌頭的著色器，參數與貼圖不動）"));
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.color = new Color(0.6f, 1f, 0.7f);
            if (GUILayout.Button(Lang.T("套用著色器"), GUILayout.Height(26))) FinishCarryPrompt(true, true);
            GUI.color = Color.white;
            if (GUILayout.Button(Lang.T("不套用"), GUILayout.Height(26))) FinishCarryPrompt(true, false);
            if (GUILayout.Button(Lang.T("取消換人"), GUILayout.Height(26))) FinishCarryPrompt(false, false);
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        void FinishCarryPrompt(bool run, bool carry)
        {
            showCarryPrompt = false;
            var act = carryPromptAct;
            carryPromptAct = null;
            if (run && act != null)
            {
                try { act(carry); }
                catch (Exception e) { SetStatus(false, Lang.T("換人失敗: ") + e.Message); }
            }
        }

        /// <summary>等新角色的 MaterialEditor 讀完自己的卡再套；套兩次，擋掉晚到的重新載入。</summary>
        IEnumerator ShaderCarryRoutine(Studio.OCIChar ofem, ShaderCarry.Snapshot snap)
        {
            int total = 0;
            foreach (float wait in new[] { 1.5f, 1.5f })
            {
                yield return new WaitForSeconds(wait);
                ChaControl cha = null;
                try { cha = ofem.charInfo; } catch { }
                if (cha == null) yield break;
                int n = ShaderCarry.Apply(cha, snap);
                total += n;
                if (n > 0) Logger.LogInfo("[ShaderCarry] 套用場景原角色的著色器：\n" + ShaderCarry.LastReport);
            }
            statusMsg += "\n" + string.Format(Lang.T("🎨 已套用場景原角色的著色器（{0} 個材質）"), total);
        }

        void DoSwapCharacterAppearance(Studio.OCIChar ofem, string path)
        {
            DoSwapCharacterAppearance(ofem, path, SwapMode.KeepOldBody);
        }

        /// <summary>
        /// 四種換人模式，差別只在「舊卡的什麼要留下來」：
        ///
        ///   KeepOldBody  100 根滑桿 + ABMX + 胸托基準全部帶回去。新卡等於只換了臉跟衣服。
        ///                胸圍差很多的兩張卡這樣換會穿模 —— 新卡的服裝、去碼、材質都是
        ///                照她自己的胸型做的，硬套舊胸型就破。
        ///   LockHeight   只留 Height（驅動 cf_n_height 的那一根）。身高保住是為了不破壞
        ///                場景裡的相對位置與姿勢，其他的讓新角色做自己。
        ///   KeepNewBody  只留會讓姿勢錯位的那幾根（見 ProportionShape），其餘 39 根用新卡的。
        ///   Plain        什麼都不留。
        ///
        /// ABMX 只有 KeepOldBody 會帶回去。其餘三種刻意不留 —— 舊卡的骨架縮放
        /// （含胸部骨）會把新卡自己的身材整個蓋掉，那就失去這三種模式的意義了。
        /// </summary>
        void DoSwapCharacterAppearance(Studio.OCIChar ofem, string path, SwapMode mode)
        {
            DoSwapCharacterAppearance(ofem, path, mode, null);
        }

        /// <summary>carry：要不要把場景原角色的著色器帶到新角色。null = 照設定（只有「自動」才帶）。</summary>
        void DoSwapCharacterAppearance(Studio.OCIChar ofem, string path, SwapMode mode, bool? carry)
        {
            try
            {
                bool doCarry = carry ?? (carryShaderMode == 2);
                ShaderCarry.Snapshot shaderSnap = null;
                if (doCarry && ShaderCarry.Available && ofem != null)
                {
                    shaderSnap = ShaderCarry.Capture(ofem.charInfo);
                    Logger.LogInfo("[ShaderCarry] 換人前記下場景原角色的著色器：\n" + ShaderCarry.LastReport);
                }
                if (ofem == null) { statusMsg = "❌ 選擇的節點不是角色！"; return; }

                bool keepFull = KeepsFullBody(mode);

                // ---- Save everything BEFORE the native replace ----
                // 索引跟數值一起存，還原那邊就不用再分模式判斷 —— 存了哪幾根就寫回哪幾根。
                List<float> savedShape = new List<float>();
                List<int> savedShapeIdx = new List<int>();
                int[] want = ShapeIndicesFor(mode);
                if (mode == SwapMode.KeepNewBody && !keepOldHeadSize && want != null)
                    want = want.Where(i => i != (int)ChaFileDefine.BodyShapeIdx.HeadSize).ToArray();
                if (want != null)
                {
                    foreach (int idx in want)
                    {
                        try
                        {
                            savedShape.Add(ofem.charInfo.GetShapeBodyValue(idx));
                            savedShapeIdx.Add(idx);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning("[SwapMode] 讀不到體型滑桿 " + idx + ": " + e.Message);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < 100; i++)
                    {
                        try { savedShape.Add(ofem.charInfo.GetShapeBodyValue(i)); savedShapeIdx.Add(i); }
                        catch { break; }
                    }
                }

                string savedLastname = null;
                string savedFirstname = null;
                try
                {
                    // 預設沿用新卡的名字；開了「保留原本的角色名稱」才把舊名字帶回去
                    if (keepCharaName)
                    {
                        savedLastname = ofem.charInfo.chaFile.parameter.lastname;
                        savedFirstname = ofem.charInfo.chaFile.parameter.firstname;
                    }
                }
                catch (Exception exName)
                {
                    Logger.LogWarning("[KeepBody] Save name failed: " + exName);
                }

                // 胸托（KK_Pushup）的原始胸型。只有完美換人要保留 ——
                // 不存這個的話，換完人只要一改穿脫狀態，KK_Pushup 就會拿新卡的
                // BaseData 重算胸部，胸型整個跳回新卡的。詳見 PushupFix.cs。
                object savedPushupBase = null;
                if (keepFull)
                {
                    savedPushupBase = PushupFix.Capture(ofem);
                    Logger.LogInfo("[PushupFix] 換人前：" + PushupFix.LastReport);
                }

                // 舊卡身上實際有哪幾根骨頭被 ABMX 改過。
                // **一定要在 ChangeChara 之前抓** —— 換完人拿到的是新卡的名單，
                // 拿它去過濾舊卡的資料整個反了。跟姿勢要先存是同一個道理。
                List<string> oldAbmxBones = AbmxFilter.CaptureNames(ofem);
                if (oldAbmxBones.Count > 0)
                    Logger.LogInfo("[AbmxFilter] 換人前的舊卡 " + AbmxFilter.Dump(oldAbmxBones));

                // 「保留舊卡身材」整包帶回去；「維持新卡身材」也帶回去，但等一下
                // 會照白名單把不要的移除（手腳/脖子的長度比例在 KK 的體型滑桿裡
                // 根本沒有，只能靠 ABMX 留）。另外兩種模式一根都不留。
                bool wantAbmx = keepFull || mode == SwapMode.KeepNewBody;
                ExtensibleSaveFormat.PluginData savedAbmx = null;
                if (wantAbmx)
                {
                    try
                    {
                        savedAbmx = ExtensibleSaveFormat.ExtendedSave.GetExtendedDataById(ofem.charInfo.chaFile, "KKABMPlugin.ABMData");
                    }
                    catch (Exception exAbmxSave)
                    {
                        Logger.LogWarning("[KeepBody] Save ABMX data failed: " + exAbmxSave);
                    }
                }

                // ---- 換之前先把目前姿勢存下來 ----
                // 換完之後 FK/IK 狀態會壞掉造成拉伸，事後再存已經來不及
                // （存到的就是壞掉的姿勢），所以一定要在 ChangeChara 之前存。
                // 換人會把動畫狀態機打壞，只還原骨骼數值救不回來（這就是為什麼
                // 手動到「動畫→角色→任意動作」才會好）。先把目前的動畫記下來，
                // 還原姿勢之後對同一個動畫重新指派一次。
                AnimeState savedAnime = ReadAnime(ofem);

                // 表情也要換之前存：ChangeChara 會把 ChaFileStatus 整個換成新卡的，
                // 眉眼嘴編號、臉紅、眼淚、視線都會變成新卡存檔時的樣子。
                ExpressionFix.Snapshot savedFace = null;
                if (keepExpression)
                {
                    savedFace = ExpressionFix.Capture(ofem);
                    Logger.LogInfo("[ExpressionFix] 換人前 " + ExpressionFix.LastReport);
                }

                string savedPose = null;
                if (autoRestorePose)
                {
                    savedPose = PoseFix.SavePoseTemp(ofem);
                    if (savedPose == null)
                        Logger.LogWarning("[KeepBody] 姿勢暫存失敗，換完不會自動還原: " + PoseFix.LastReport);
                }

                // ---- 換之前記下 KKPE 碰撞器綁定，並擋掉「自動加入新動骨」----
                // HSPE 換人時會走 DynamicBonesEditor.RefreshDynamicBoneList()，
                // 把新角色的每一根動骨都塞進場上每一顆已編輯過的碰撞器，
                // 預設值是「啟用」，所以 J694 這種小碰撞器換完人就開始吸頭髮、
                // 陰道 pivot、屁股。先把 addNewDynamicBonesAsDefault 壓成 false，
                // 換完再照記錄把原本該開的那幾根打開。
                DBColliderFix.Snapshot dbSnap = null;
                DBColliderFix.AutoAddGuard dbGuard = null;
                if (autoFixDbCollider && DBColliderFix.Available)
                {
                    dbSnap = DBColliderFix.Capture(ofem, false);
                    if (dbSnap != null)
                        Logger.LogInfo("[DBColliderFix] 換人前 " + DBColliderFix.LastReport);
                    dbGuard = DBColliderFix.SuppressAutoAdd();
                }

                // 註：換人時 DBDE 會噴一個 KeyNotFoundException
                // （DBDECharaController.ApplyCurrentDelayed）。已查過：那是 DBDE 自己排的
                // 兩次套用在競速，延遲的那次跑在它字典重建之前所以查不到 outfit key，
                // 緊接著的第二次才是有效的。協程被殺掉不影響換人流程，動骨設定有套上，
                // 所以刻意不去擋它 —— 留著當訊號，哪天 DBDE 真的壞了才看得出來。

                // ---- Full native replace (reflection-based, tolerant of
                // whichever ChangeChara overload exists) ----
                bool changed = false;
                foreach (MethodInfo m in ofem.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                    .Where(x => x.Name == "ChangeChara")
                    .OrderBy(x => x.GetParameters().Length))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                    object[] args = new object[ps.Length];
                    args[0] = path;
                    for (int i = 1; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    try
                    {
                        m.Invoke(ofem, args);
                        changed = true;
                        break;
                    }
                    catch (Exception exChange)
                    {
                        Logger.LogWarning("[KeepBody] ChangeChara overload " + m + " failed: " + exChange);
                    }
                }

                if (!changed)
                {
                    DBColliderFix.RestoreAutoAdd(dbGuard);
                    statusMsg = "❌ ChangeChara 失敗！";
                    return;
                }

                // ---- 維持新卡身材：舊卡的骨架 + 新卡的臉 ----
                // ChangeChara 剛把新卡讀進 chaFile，這時拿到的 ABMData 就是新卡自己的。
                // 以前是「舊卡整包寫回去再刪掉規則外的」，刪完剩下的是空白、不是新卡的 ——
                // 新卡用 ABMX 修過的臉就不見了，場景原角色有 ABMX 的地圖換出來臉特別怪。
                bool abmxMerged = false;
                if (mode == SwapMode.KeepNewBody)
                {
                    try
                    {
                        var newAbmx = ExtensibleSaveFormat.ExtendedSave.GetExtendedDataById(
                            ofem.charInfo.chaFile, "KKABMPlugin.ABMData");
                        bool mergeOk;
                        var merged = AbmxFilter.Merge(savedAbmx, newAbmx, EffectiveKeepRules(), out mergeOk);
                        Logger.LogInfo("[AbmxFilter] 合併舊卡骨架＋新卡臉：" + AbmxFilter.LastReport);
                        if (mergeOk) { savedAbmx = merged; abmxMerged = true; }
                    }
                    catch (Exception exMerge)
                    {
                        Logger.LogWarning("[AbmxFilter] 合併失敗，改用舊做法: " + exMerge.Message);
                    }
                }

                statusMsg = string.Format(Lang.T("⏳ 換人中（{0}）…"), SwapModeName(mode));
                if (shaderSnap != null && shaderSnap.Entries.Count > 0)
                    StartCoroutine(ShaderCarryRoutine(ofem, shaderSnap));
                StartCoroutine(KeepBodyRestoreRoutine(ofem, savedShape, savedShapeIdx, savedAbmx,
                                                      savedLastname, savedFirstname,
                                                      savedPose, savedAnime, dbSnap, dbGuard, mode,
                                                      savedPushupBase, oldAbmxBones, savedFace, abmxMerged));
            }
            catch (Exception ex)
            {
                statusMsg = Lang.T("❌ 換裝發生錯誤: ") + ex.Message;
            }
        }

        // Staged restore, ported directly from the Python autocharamoments
        // do_replace_keep_body / replace_keep_body_stage2 /
        // finish_replace_keep_body_native sequence. The two delays exist
        // specifically to dodge a race against KKABMX's own async
        // "Creating bone dictionary" step.
        IEnumerator KeepBodyRestoreRoutine(Studio.OCIChar ofem, List<float> savedShape,
            List<int> savedShapeIdx,
            ExtensibleSaveFormat.PluginData savedAbmx, string savedLastname, string savedFirstname,
            string savedPose, AnimeState savedAnime,
            DBColliderFix.Snapshot dbSnap, DBColliderFix.AutoAddGuard dbGuard, SwapMode mode,
            object savedPushupBase, List<string> oldAbmxBones,
            ExpressionFix.Snapshot savedFace = null, bool abmxMerged = false)
        {
            yield return new WaitForSeconds(0.2f);

            // Restore the whole KKABMX blob wholesale - proven to survive
            // KKABMX's own async rebuild step, unlike editing live modifiers
            // directly.
            try
            {
                // 合併過的話就算是空的（null）也要寫 —— 代表「一根都不要」，
                // 不寫的話 KKABMX 會照新卡原本那包讀，規則內的骨頭就變成新卡的。
                if (savedAbmx != null || abmxMerged)
                    ExtensibleSaveFormat.ExtendedSave.SetExtendedDataById(ofem.charInfo.chaFile, "KKABMPlugin.ABMData", savedAbmx);
            }
            catch (Exception exAbmxRestore)
            {
                Logger.LogWarning("[KeepBody] Restore ABMX data failed: " + exAbmxRestore);
            }

            yield return new WaitForSeconds(0.4f);

            try
            {
                // 存的時候索引跟數值是成對記下來的，所以這裡四種模式共用一個迴圈：
                // 存了哪幾根就寫回哪幾根，Plain 模式存了 0 根、這個迴圈自然不做事。
                for (int i = 0; i < savedShape.Count && i < savedShapeIdx.Count; i++)
                {
                    try { ofem.charInfo.SetShapeBodyValue(savedShapeIdx[i], savedShape[i]); }
                    catch (Exception e)
                    {
                        Logger.LogWarning("[SwapMode] 寫回體型滑桿 " + savedShapeIdx[i] + " 失敗: " + e.Message);
                    }
                }

                try
                {
                    if (savedLastname != null) ofem.charInfo.chaFile.parameter.lastname = savedLastname;
                    if (savedFirstname != null) ofem.charInfo.chaFile.parameter.firstname = savedFirstname;
                }
                catch (Exception exNameRestore)
                {
                    Logger.LogWarning("[KeepBody] Restore name failed: " + exNameRestore);
                }

                try
                {
                    MethodInfo updateShapeMethod = ofem.charInfo.GetType().GetMethod("UpdateShapeBodyValueFromCustomInfo",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (updateShapeMethod != null)
                        updateShapeMethod.Invoke(ofem.charInfo, null);
                }
                catch (Exception exUpdateShape)
                {
                    Logger.LogWarning("[KeepBody] UpdateShapeBodyValueFromCustomInfo failed: " + exUpdateShape);
                }

                // 「維持新卡身材」：整包還原之後只留白名單那幾根，其餘移掉，
                // 讓新卡的臉和胸部骨回到她自己的預設。
                // 不動 blob 而是用 KKABMX 自己的 API 移除 —— 解 LZ4+MessagePack 再壓回去
                // 會把 float32 升成 float64、int32 壓成 fixint，用 object 接資料的外掛就炸。
                //
                // **位置很重要，不要往前搬。** 原本這段放在 0.2 秒那一段（剛還原完 blob
                // 之後）就完全沒有效果：log 裡「移除 79 根」的下一行就是
                // `Modding API] Character load/reload`，KKAPI 的重載事件在我們刪完之後
                // 才發，KKABMX 收到之後又去讀 chaFile 裡那包 ABMData，94 根原封不動長回來。
                // 換出來的人看起來就跟「保留舊卡身材」一模一樣，臉還是舊卡的比例。
                // 下面修鼻子嘴巴那段之所以一直有效，正是因為它在 0.6 秒這裡、重載事件之後。
                if (!abmxMerged) PruneAbmxKeep(ofem, mode, "第一次");

                // Force-clear KKABMX modifiers on the two face bones that
                // consistently end up wrong after the whole-blob ABMX
                // restore - don't try to restore a "correct" value (proved
                // unreliable), just remove the modifier so the bone falls
                // back to the new character's own unmodified default.
                // 沒有寫回 ABMX 的模式不需要這一段 —— 真的跑下去反而會把
                // 新卡自己的鼻子/嘴巴 modifier 刪掉。
                if (KeepsFullBody(mode))
                {
                try
                {
                    UnityEngine.GameObject rootGo = ofem.charInfo.gameObject;
                    UnityEngine.MonoBehaviour[] allBehaviours = rootGo.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true);
                    object boneComp = null;
                    foreach (UnityEngine.MonoBehaviour comp in allBehaviours)
                    {
                        if (comp.GetType().Name == "BoneController")
                        {
                            boneComp = comp;
                            break;
                        }
                    }

                    if (boneComp != null)
                    {
                        Type boneControllerType = boneComp.GetType();
                        MethodInfo getAllModifiers = boneControllerType.GetMethod("GetAllModifiers", Type.EmptyTypes);
                        MethodInfo removeModifier = boneControllerType.GetMethod("RemoveModifier");
                        if (getAllModifiers != null && removeModifier != null)
                        {
                            HashSet<string> targets = new HashSet<string> { "cf_J_NoseBase", "cf_J_MouthBase_rx" };
                            IEnumerable allMods = (IEnumerable)getAllModifiers.Invoke(boneComp, null);
                            List<object> toRemove = new List<object>();
                            foreach (object mod in allMods)
                            {
                                PropertyInfo boneNameProp = mod.GetType().GetProperty("BoneName");
                                string boneName = boneNameProp != null ? boneNameProp.GetValue(mod, null) as string : null;
                                if (boneName != null && targets.Contains(boneName))
                                    toRemove.Add(mod);
                            }
                            foreach (object mod in toRemove)
                            {
                                try { removeModifier.Invoke(boneComp, new object[] { mod }); }
                                catch (Exception exRemoveMod)
                                {
                                    Logger.LogWarning("[KeepBody] RemoveModifier failed: " + exRemoveMod);
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("[KeepBody] BoneController not found for face-bone reset");
                    }
                }
                catch (Exception exFaceBones)
                {
                    Logger.LogWarning("[KeepBody] Force-reset face bones failed: " + exFaceBones);
                }
                }

                try
                {
                    if (ofem.charInfo.chaFile.parameter.sex == 1)
                    {
                        MethodInfo updateBustMethod = ofem.charInfo.GetType().GetMethod("UpdateBustSoftnessAndGravity",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (updateBustMethod != null)
                            updateBustMethod.Invoke(ofem.charInfo, null);
                    }
                }
                catch (Exception exBust)
                {
                    Logger.LogWarning("[KeepBody] UpdateBustSoftnessAndGravity failed: " + exBust);
                }

                // 胸托：把舊角色的原始胸型寫回 KK_Pushup 的 BaseData。
                // 一定要在身材滑桿都寫完之後才做，因為它會照 BaseData + 新卡的
                // 胸托參數重算一次胸部，順序顛倒就會被 UpdateShape 蓋掉。
                if (savedPushupBase != null)
                {
                    bool puOk = PushupFix.Apply(ofem, savedPushupBase);
                    Logger.LogInfo("[PushupFix] 換人後：" + PushupFix.LastReport);
                    if (!puOk)
                        Logger.LogWarning("[PushupFix] 胸型沒接回去，改穿脫狀態時胸部可能會跳成新卡的");
                }

                statusMsg = string.Format(Lang.T("✅ 換人成功（{0}）"), SwapModeName(mode))
                            + (savedShape.Count > 0 ? string.Format(Lang.T("，鎖回 {0} 根體型滑桿"), savedShape.Count) : "");
            }
            catch (Exception exRestore)
            {
                statusMsg = Lang.T("❌ 身材還原失敗: ") + exRestore.Message;
                Logger.LogError("[KeepBody] Restore error: " + exRestore);
            }

            // ---- 最後才還原姿勢 ----
            // 等身材與骨架都套用完再讀，否則會被後面的步驟蓋掉
            if (savedPose != null)
            {
                yield return new WaitForSeconds(0.3f);
                if (PoseFix.RestoreAndCleanup(ofem, savedPose))
                    statusMsg = string.Format(Lang.T("✅ 換人成功（{0}），姿勢已還原"), SwapModeName(mode));
                else
                    Logger.LogWarning("[KeepBody] 姿勢還原失敗: " + PoseFix.LastReport);
            }

            // ---- 表情寫回 ----
            // 姿勢之後、形態鍵預設之前：形態鍵鎖是 LateUpdate 疊在表情上面的，
            // 預設裡有鎖臉部的鍵，就會照鎖的值蓋過表情（那是預設該管的事）。
            if (savedFace != null)
            {
                try
                {
                    ExpressionFix.Restore(ofem, savedFace);
                    Logger.LogInfo("[ExpressionFix] " + ExpressionFix.LastReport);
                }
                catch (Exception exFace)
                {
                    Logger.LogWarning("[ExpressionFix] 表情寫回失敗: " + exFace.Message);
                }
            }

            // ---- 最後：自動帶入跟新角色同名的形態鍵預設 ----
            // 放在姿勢之後才做，理由跟姿勢放最後一樣 —— 前面每一步都可能重建模型，
            // 太早套上去會被蓋掉。名字對不上就什麼都不做，不會亂套別人的鎖。
            if (autoApplyBlendPreset)
            {
                try
                {
                    if (BlendShapeLock.AutoApply(ofem.charInfo))
                    {
                        string nm = BlendShapeLock.NameOf(ofem.charInfo);
                        statusMsg += string.Format(Lang.T("　｜ 已帶入形態鍵預設「{0}」"), nm);
                        Logger.LogInfo("[BlendShapeLock] 自動帶入預設: " + nm);
                    }
                }
                catch (Exception exBlend)
                {
                    Logger.LogWarning("[BlendShapeLock] 自動帶入失敗: " + exBlend.Message);
                }
            }

            // ---- 最後重新指派一次原本的動畫 ----
            // 不換內容，只是讓工作室重新走一次 LoadAnime，把動畫狀態機重建起來。
            // 這等同於手動去「動畫 → 角色 → 任意動作」再點回來。
            if (savedAnime != null)
            {
                yield return new WaitForSeconds(0.2f);
                bool animeOk = false;
                yield return StartCoroutine(ReapplyAnimeRoutine(ofem, savedAnime, r => animeOk = r));
                if (animeOk)
                    Logger.LogInfo("[KeepBody] 已重建動畫狀態機 " + savedAnime);
                else
                    Logger.LogWarning("[KeepBody] 動畫狀態機重建失敗，"
                                      + "若肢體有拉伸請手動到「動畫→角色」點一次任意動作");
            }

            // 重新指派動畫之後再寫一次表情（冪等）—— 有些動作讀進來會帶自己的表情
            if (savedFace != null && savedAnime != null)
            {
                try { ExpressionFix.Restore(ofem, savedFace); } catch { }
            }

            // ---- 白名單再刷一次 ----
            // 上面每一步（姿勢還原、形態鍵帶入、重新指派動畫）都可能讓 KKABMX 再重建一次，
            // 只要它又去讀 chaFile 裡那包 ABMData，剛移掉的那幾十根就會原封不動長回來。
            // 這一次是冪等的 —— 第一次就成功的話這裡會印「移除 0 根」，什麼都不會發生；
            // 真的看到第二次還在移除，就代表重載事件比 0.6 秒那一刀更晚，值得再往後挪。
            if (mode == SwapMode.KeepNewBody && !abmxMerged)
            {
                yield return new WaitForSeconds(0.5f);
                PruneAbmxKeep(ofem, mode, "第二次");
            }

            // ---- 碰撞器綁定還原 ----
            // RefreshDynamicBoneList 在 OnCharacterReplaced 裡會被呼叫兩次，
            // 而且 DynamicBonesEditor 還有 _headlessReconstructionTimeout 會延遲
            // 重建骨頭清單，所以分三次套用，最後才把「自動加入新動骨」放回去。
            if (dbSnap != null)
            {
                float[] waits = { 0.3f, 0.8f, 1.2f };
                int changed = 0;
                for (int i = 0; i < waits.Length; i++)
                {
                    yield return new WaitForSeconds(waits[i]);
                    changed += DBColliderFix.Apply(ofem, dbSnap);
                }
                Logger.LogInfo("[DBColliderFix] 換人後還原：" + DBColliderFix.LastReport
                               + "（累計變更 " + changed + " 根）");
                if (changed > 0)
                    statusMsg += "\n" + string.Format(Lang.T("🧲 碰撞器綁定已還原（修正 {0} 根動骨）"), changed);
            }
            DBColliderFix.RestoreAutoAdd(dbGuard);
        }

        /// <summary>
        /// 照白名單把「維持新卡身材」不該留的 ABMX modifier 移掉。冪等，可以重複叫。
        ///
        /// 時機是這件事唯一的難點。ChangeChara 之後 KKAPI 會發一次
        /// `Character load/reload`，KKABMX 收到就重讀 chaFile 裡的 ABMData，
        /// 把整包 modifier 重新建起來。在那之前刪，等於沒刪 —— log 上會漂亮地
        /// 印「移除 79 根」，畫面上卻跟「保留舊卡身材」一模一樣。
        /// 所以呼叫點一律放在重載事件之後（0.6 秒那一段起跳），而且多刷一次保險。
        /// </summary>
        /// <summary>設定裡的規則，外加「頭大小不沿用舊卡」時自動排除的頭骨。</summary>
        List<string> EffectiveKeepRules()
        {
            List<string> rules = AbmxFilter.Parse(abmxKeepBones);
            if (!keepOldHeadSize) { rules.Add("-cf_s_head"); rules.Add("-cf_j_head"); }
            return rules;
        }

        void PruneAbmxKeep(Studio.OCIChar ofem, SwapMode mode, string tag)
        {
            if (mode != SwapMode.KeepNewBody) return;
            try
            {
                List<string> rules = EffectiveKeepRules();
                AbmxFilter.KeepOnly(ofem, rules);        // 規則是空的 = 全部移除（舊行為）
                Logger.LogInfo("[AbmxFilter] " + tag + " 規則 [" + abmxKeepBones + "] → "
                               + AbmxFilter.LastReport);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[AbmxFilter] " + tag + " 篩選失敗: " + e.Message);
            }
        }

        // =============================================================
        // 動畫狀態機修復
        //
        // 換人之後肢體拉伸，存讀姿勢救不回來，但到「動畫→角色→任意動作」點一下
        // 就好——代表壞的不是骨骼數值，是動畫狀態機。姿勢檔只還原骨骼，動畫選單
        // 則是重新指派一個 AnimationClip。所以這裡記下換人前的動畫編號，換完
        // 重新指派同一組。
        // =============================================================

        /// <summary>動畫狀態：group / category / no，外加播放進度與速度。</summary>
        class AnimeState
        {
            public int group, category, no;
            public float normalizedTime = -1f;
            public float speed = -1f;
            public string via = "";
            public override string ToString()
            {
                return group + "/" + category + "/" + no
                    + (normalizedTime >= 0f ? " @" + normalizedTime.ToString("F3") : "")
                    + " (" + via + ")";
            }
        }

        /// <summary>OCIChar 底下可能放著動畫資訊的幾個容器，依序試。</summary>
        IEnumerable<KeyValuePair<string, object>> AnimeContainers(Studio.OCIChar oci)
        {
            yield return new KeyValuePair<string, object>("oci", oci);
            foreach (string n in new[] { "oiCharInfo", "charFileStatus", "charInfo" })
            {
                object o = null;
                try { o = GetPropertyOrField(oci, n); }
                catch { }
                if (o != null) yield return new KeyValuePair<string, object>(n, o);
            }
            object ci = null;
            try { ci = GetPropertyOrField(oci, "charInfo"); }
            catch { }
            if (ci != null)
            {
                foreach (string n in new[] { "fileStatus", "chaFile" })
                {
                    object o = null;
                    try { o = GetPropertyOrField(ci, n); }
                    catch { }
                    if (o != null) yield return new KeyValuePair<string, object>("charInfo." + n, o);
                }
            }
        }

        static int? AsInt(object o)
        {
            try { return o == null ? (int?)null : Convert.ToInt32(o); }
            catch { return null; }
        }

        /// <summary>讀出目前的動畫，取不到回 null（會在主控台留下診斷）。</summary>
        AnimeState ReadAnime(Studio.OCIChar oci)
        {
            if (oci == null) return null;
            foreach (var kv in AnimeContainers(oci))
            {
                object box = kv.Value;

                // (a) 容器上直接有 animeGroup / animeCategory / animeNo
                int? g = AsInt(TryMember(box, "animeGroup"));
                int? c = AsInt(TryMember(box, "animeCategory"));
                int? n = AsInt(TryMember(box, "animeNo"));

                // (b) 容器上有 animeInfo（KK 的 OICharInfo 是這種），裡面才是 group/category/no
                if (g == null || c == null || n == null)
                {
                    object inf = TryMember(box, "animeInfo") ?? TryMember(box, "anime");
                    if (inf != null)
                    {
                        g = AsInt(TryMember(inf, "group"));
                        c = AsInt(TryMember(inf, "category"));
                        n = AsInt(TryMember(inf, "no"));
                        if (g != null && c != null && n != null)
                        {
                            var st2 = new AnimeState { group = g.Value, category = c.Value, no = n.Value,
                                                       via = kv.Key + ".animeInfo" };
                            FillPlayback(box, st2);
                            return st2;
                        }
                    }
                    continue;
                }

                var st = new AnimeState { group = g.Value, category = c.Value, no = n.Value, via = kv.Key };
                FillPlayback(box, st);
                return st;
            }

            DumpAnimeMembers(oci);
            return null;
        }

        void FillPlayback(object box, AnimeState st)
        {
            object t = TryMember(box, "animeNormalizedTime") ?? TryMember(box, "normalizedTime");
            object sp = TryMember(box, "animeSpeed") ?? TryMember(box, "speed");
            try { if (t != null) st.normalizedTime = Convert.ToSingle(t); } catch { }
            try { if (sp != null) st.speed = Convert.ToSingle(sp); } catch { }
        }

        static object TryMember(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
                                                     | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (p != null && p.CanRead) return p.GetValue(o, null);
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                               | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        /// <summary>寫入成員；跟既有的 SetMember 分開，避免簽章撞名。</summary>
        static void SetMemberReflect(object o, string name, object value)
        {
            if (o == null) return;
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public
                                                     | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (p != null && p.CanWrite) { p.SetValue(o, value, null); return; }
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                               | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (f != null) f.SetValue(o, value);
            }
            catch { }
        }

        /// <summary>找不到欄位時，把可疑的成員名稱全部印出來，方便對名字。</summary>
        void DumpAnimeMembers(Studio.OCIChar oci)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[Anime] 診斷 —— 找不到動畫欄位，以下是可疑的成員：");
                foreach (var kv in AnimeContainers(oci))
                {
                    Type t = kv.Value.GetType();
                    sb.AppendLine("  ── " + kv.Key + " : " + t.FullName);
                    var flags = BindingFlags.Instance | BindingFlags.Public
                                | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                    foreach (FieldInfo f in t.GetFields(flags))
                        if (f.Name.ToLower().Contains("anim"))
                            sb.AppendLine("      field  " + f.FieldType.Name + " " + f.Name);
                    foreach (PropertyInfo pr in t.GetProperties(flags))
                        if (pr.Name.ToLower().Contains("anim"))
                            sb.AppendLine("      prop   " + pr.PropertyType.Name + " " + pr.Name);
                }
                sb.AppendLine("  ── OCIChar 上名字含 Anime 的方法：");
                foreach (MethodInfo m in oci.GetType().GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy))
                {
                    if (!m.Name.ToLower().Contains("anim")) continue;
                    var ps = m.GetParameters().Select(x => x.ParameterType.Name).ToArray();
                    sb.AppendLine("      " + m.Name + "(" + string.Join(", ", ps) + ")");
                }
                Logger.LogWarning(sb.ToString());
            }
            catch (Exception e) { Logger.LogWarning("[Anime] 診斷失敗: " + e.Message); }
        }

        /// <summary>單純呼叫一次 LoadAnime(group, category, no)。</summary>
        bool LoadAnimeRaw(Studio.OCIChar oci, int group, int category, int no)
        {
            if (oci == null) return false;
            try
            {
                foreach (MethodInfo m in oci.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                    .Where(x => x.Name == "LoadAnime")
                    .OrderBy(x => x.GetParameters().Length))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 3) continue;
                    bool ints = true;
                    for (int i = 0; i < 3; i++)
                        if (ps[i].ParameterType != typeof(int)) { ints = false; break; }
                    if (!ints) continue;

                    object[] args = new object[ps.Length];
                    args[0] = group; args[1] = category; args[2] = no;
                    for (int i = 3; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType.IsValueType
                            ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    m.Invoke(oci, args);
                    return true;
                }
                Logger.LogWarning("[Anime] 找不到 LoadAnime(int,int,int,...)");
                DumpAnimeMembers(oci);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[Anime] LoadAnime 失敗: " + e);
            }
            return false;
        }

        /// <summary>把播放進度與速度放回去。</summary>
        void RestorePlayback(Studio.OCIChar oci, AnimeState a)
        {
            if (a == null) return;
            foreach (var kv in AnimeContainers(oci))
            {
                if (a.normalizedTime >= 0f)
                    SetMemberReflect(kv.Value, "animeNormalizedTime", a.normalizedTime);
                if (a.speed >= 0f)
                    SetMemberReflect(kv.Value, "animeSpeed", a.speed);
            }
            foreach (string mn in new[] { "SetAnimeNormalizedTime", "SetAnimeSpeed" })
            {
                float v = mn == "SetAnimeSpeed" ? a.speed : a.normalizedTime;
                if (v < 0f) continue;
                MethodInfo sm = oci.GetType().GetMethod(mn, new[] { typeof(float) });
                if (sm == null) continue;
                try { sm.Invoke(oci, new object[] { v }); }
                catch { }
            }
        }

        /// <summary>找一組跟目前不一樣、而且真的載得起來的動畫，當作跳板。</summary>
        static readonly int[][] AnimeFallbacks = new[]
        {
            new[] { 0, 0, 0 }, new[] { 0, 0, 1 }, new[] { 0, 1, 0 },
            new[] { 1, 0, 0 }, new[] { 0, 2, 0 },
        };

        /// <summary>
        /// 重建動畫狀態機。
        ///
        /// 指派「同一組」編號沒有用——工作室看編號沒變就直接跳過，狀態機不會重建。
        /// 手動到「動畫→角色→任意動作」之所以有效，關鍵在於換成了別的動畫。
        /// 所以這裡先切到一個跳板動畫，等一幀讓它真的套用，再切回原本那一組。
        /// </summary>
        IEnumerator ReapplyAnimeRoutine(Studio.OCIChar oci, AnimeState a, Action<bool> onDone)
        {
            bool ok = false;
            if (oci != null && a != null)
            {
                int[] jump = null;
                foreach (int[] cand in AnimeFallbacks)
                {
                    if (cand[0] == a.group && cand[1] == a.category && cand[2] == a.no) continue;
                    if (!LoadAnimeRaw(oci, cand[0], cand[1], cand[2])) continue;
                    yield return null;                      // 等一幀，讓它真的換過去
                    AnimeState now = ReadAnime(oci);
                    if (now != null && (now.group != a.group || now.category != a.category
                                        || now.no != a.no))
                    {
                        jump = cand;                        // 確認真的跳走了
                        break;
                    }
                }

                if (jump == null)
                    Logger.LogWarning("[Anime] 找不到可用的跳板動畫，狀態機可能沒重建");
                else
                    Logger.LogInfo("[Anime] 跳板 " + jump[0] + "/" + jump[1] + "/" + jump[2]);

                yield return null;
                ok = LoadAnimeRaw(oci, a.group, a.category, a.no);
                yield return null;
                RestorePlayback(oci, a);
            }
            if (onDone != null) onDone(ok);
        }

        /// <summary>換完人手動重跑一次動畫指派，給按鈕用。</summary>
        void ReapplyAnime(Studio.OCIChar oci)
        {
            AnimeState a = ReadAnime(oci);
            if (a == null) { SetStatus(false, "讀不到目前的動畫，診斷已印在主控台"); return; }
            SetStatus(true, "重建動畫狀態機中...");
            StartCoroutine(ReapplyAnimeRoutine(oci, a, ok =>
                SetStatus(ok, ok ? Lang.T("已重建動畫狀態機 ") + a : "重建失敗，見主控台")));
        }

        // ---- Scene character picker: list every OCIChar currently in the
        // studio scene (regardless of how deeply nested in folders), click
        // a name to immediately open the native card picker for that
        // specific character. ----
        /// <summary>KK 的 sex：0 = 男，1 = 女。</summary>
        bool IsFemale(Studio.OCIChar oci)
        {
            try { return oci.charInfo.sex == 1; }
            catch { return true; }
        }

        private bool statusIsError;

        /// <summary>
        /// 狀態訊息翻譯。整句查得到就用整句；查不到的話，「前綴: 變動內容」這種
        /// （例如「人物卡已存出: xxx.png」）就只翻冒號前面那段。
        /// </summary>
        static string TrMsg(string m)
        {
            if (string.IsNullOrEmpty(m)) return m;
            string t = Lang.T(m);
            if (t != m) return t;
            foreach (string sep in new[] { ": ", "：" })
            {
                int i = m.IndexOf(sep);
                if (i <= 0) continue;
                string head = m.Substring(0, i + sep.Length);
                string th = Lang.T(head);
                if (th != head) return th + m.Substring(i + sep.Length);
            }
            return m;
        }

        void SetStatus(bool ok, string msg)
        {
            statusMsg = (ok ? "✅ " : "❌ ") + TrMsg(msg);
            statusTime = Time.realtimeSinceStartup;
            statusIsError = !ok;
        }

        List<Studio.OCIChar> GetAllSceneCharacters()
        {
            List<Studio.OCIChar> result = new List<Studio.OCIChar>();
            try
            {
                Studio.Studio studio = StudioInstance();
                IDictionary dicObjectCtrl = GetMember(studio, "dicObjectCtrl") as IDictionary;
                if (dicObjectCtrl == null) return result;
                foreach (object key in dicObjectCtrl.Keys)
                {
                    Studio.OCIChar oci = dicObjectCtrl[key] as Studio.OCIChar;
                    if (oci != null) result.Add(oci);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CharPicker] GetAllSceneCharacters error: " + e);
            }
            return result;
        }

        // 畫面上那份清單不必每次 OnGUI 都重建。
        //
        // IMGUI 一幀至少跑兩趟 OnGUI（Layout + Repaint），有輸入事件還更多趟；
        // 每一趟都重走一次 dicObjectCtrl、配三個 List，開著面板時就是純浪費。
        // 角色是人手動加減的，慢個四分之一秒完全看不出來。
        //
        // 快取只給「畫圖」用。批次操作那些走 GetAllSceneCharacters() 拿最新的 ——
        // 那是按鈕按下去才跑一次，省這個沒意義，而且拿到舊清單會真的做錯事。
        List<Studio.OCIChar> charListCache;
        float charListCacheAt = -99f;

        List<Studio.OCIChar> CachedSceneCharacters()
        {
            if (charListCache != null && Time.realtimeSinceStartup - charListCacheAt < 0.25f)
                return charListCache;
            charListCache = GetAllSceneCharacters();
            charListCacheAt = Time.realtimeSinceStartup;
            return charListCache;
        }

        /// <summary>加人、刪人、換人之後叫一下，下一幀就會重讀。</summary>
        void InvalidateCharList() { charListCacheAt = -99f; splitFrom = null; }

        List<Studio.OCIChar> splitFrom;      // femaleCache / maleCache 是從哪一份切出來的
        List<Studio.OCIChar> femaleCache = new List<Studio.OCIChar>();
        List<Studio.OCIChar> maleCache = new List<Studio.OCIChar>();

        string GetCharDisplayName(Studio.OCIChar oci)
        {
            try
            {
                string name = oci.charInfo.chaFile.parameter.fullname;
                return string.IsNullOrEmpty(name) ? "(未命名角色)" : name;
            }
            catch
            {
                return "(未命名角色)";
            }
        }

        void CharPickerWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(Lang.T("點角色名稱，直接跳出選卡視窗替換 (保留身材)："));

            // 男女分兩欄的清單也跟著快取。每趟 OnGUI 配兩個 List 看起來沒什麼，
            // 但 IMGUI 一幀跑好幾趟、VR 又是兩眼，累積起來就是持續的 GC 壓力。
            List<Studio.OCIChar> chars = CachedSceneCharacters();
            if (!ReferenceEquals(chars, splitFrom))
            {
                splitFrom = chars;
                femaleCache = new List<Studio.OCIChar>();
                maleCache = new List<Studio.OCIChar>();
                foreach (Studio.OCIChar c in chars)
                {
                    if (c.sex == 1) femaleCache.Add(c);
                    else maleCache.Add(c);
                }
            }
            List<Studio.OCIChar> females = femaleCache;
            List<Studio.OCIChar> males = maleCache;

            charPickerScroll = GUILayout.BeginScrollView(charPickerScroll);
            if (chars.Count == 0)
            {
                GUILayout.Label(Lang.T("場景裡沒有找到角色。"));
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("👩 女角色 (") + females.Count + ")", GUILayout.Width(110));
                DrawBatchSwapButtons(females);
                GUILayout.EndHorizontal();
                DrawBatchModeMenu(females);

                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("一鍵（女角色）"), GUILayout.Width(110));
                if (GUILayout.Button(Lang.T("存人物卡"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(females, 0));
                if (GUILayout.Button(Lang.T("存服裝卡"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(females, 1));
                if (GUILayout.Button(Lang.T("存姿勢"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(females, 2));
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                if (females.Count == 0)
                {
                    GUILayout.Label(Lang.T("  (無)"));
                }
                else
                {
                    for (int i = 0; i < females.Count; i++)
                        DrawCharPickerButton(i, females[i]);
                }

                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("👨 男角色 (") + males.Count + ")", GUILayout.Width(110));
                DrawBatchSwapButtons(males);
                GUILayout.EndHorizontal();
                DrawBatchModeMenu(males);

                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("一鍵（男角色）"), GUILayout.Width(110));
                if (GUILayout.Button(Lang.T("存人物卡"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(males, 0));
                if (GUILayout.Button(Lang.T("存服裝卡"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(males, 1));
                if (GUILayout.Button(Lang.T("存姿勢"), GUILayout.Height(22)))
                    StartCoroutine(BatchSaveRoutine(males, 2));
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                if (males.Count == 0)
                {
                    GUILayout.Label(Lang.T("  (無)"));
                }
                else
                {
                    for (int i = 0; i < males.Count; i++)
                        DrawCharPickerButton(i, males[i]);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            if (mergeWaiting)
            {
                GUI.color = new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button(Lang.T("強制中止合卡流程"), GUILayout.Height(26))) mergeCancel = true;
                GUI.color = Color.white;
            }

            GUILayout.BeginHorizontal();
            GUI.color = new Color(0.85f, 0.75f, 1f);
            if (GUILayout.Button(Lang.T("一鍵重置所有姿勢"), GUILayout.Height(26)))
                StartCoroutine(BatchResetPoseRoutine());
            GUI.color = new Color(1f, 0.8f, 0.4f);
            if (GUILayout.Button(Lang.T("一鍵添加飾品"), GUILayout.Height(26)))
                BatchAddAccessories();
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUI.color = new Color(0.6f, 1f, 0.9f);
            if (GUILayout.Button(Lang.T("一鍵修復碰撞器綁定（換人後吸附）"), GUILayout.Height(26)))
                StartCoroutine(BatchFixDbColliderRoutine());
            GUI.color = Color.white;

            GUI.color = showSettings ? new Color(1f, 0.9f, 0.5f) : Color.white;
            if (GUILayout.Button(showSettings ? Lang.T("設置（開啟中）") : Lang.T("設置"), GUILayout.Height(26)))
            {
                if (showSettings) { SaveSettingsPanel(); showSettings = false; }
                else { CloseSidePanel(); showSettings = true; }
            }
            GUI.color = Color.white;

            // 工具列圖示的開關。放在「設置」正下面而不是設置面板裡面 ——
            // 開關圖示是常做的事，不該還要先開一層。
            bool wantIcon = GUILayout.Toggle(showToolbarButton, Lang.T(" 顯示工具列圖示（白色小人）"));
            if (wantIcon != showToolbarButton)
            {
                showToolbarButton = wantIcon;
                cfgSyncTime = 0f;      // 逼 SyncSettings 這一幀就寫回設定檔
            }

            GUILayout.Space(2);
            GUI.color = statusIsError
                ? new Color(1f, 0.45f, 0.45f)
                : ((Time.realtimeSinceStartup - statusTime) < 4f
                   ? new Color(0.7f, 1f, 0.7f) : Color.white);
            statusScroll = GUILayout.BeginScrollView(statusScroll, GUILayout.Height(46));
            GUILayout.Label(Lang.T(statusMsg));
            GUILayout.EndScrollView();
            GUI.color = Color.white;

            if (GUILayout.Button(Lang.T("關閉"), GUILayout.Height(24)))
            {
                showCharPicker = false;
                CloseSidePanel();
            }

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
            // 原本停用拖動的理由（貼齊主視窗）已不存在——這個視窗改成
            // 自動貼齊主視窗位置 (見 OnGUI)，不需要也不應該讓它可以
            // 獨立拖動，不然每一幀都會被重新校正位置蓋掉，拖動會卡頓。
        }

        /// <summary>
        /// 讀取角色的顯示狀態。
        ///
        /// 工作面板那個打勾框實際上操作的是 TreeNodeObject，不是 OCIChar。
        /// SetVisibleSimple 只處理一部分，男角色因此按了沒反應、
        /// 再按一次又只切到一半，就變成整片純色。
        /// 所以優先走 treeNodeObject，那才是遊戲自己用的那條路。
        /// </summary>
        bool GetCharVisibleSimple(Studio.OCIChar oci)
        {
            try
            {
                object node = GetMember(oci, "treeNodeObject");
                if (node != null)
                {
                    object v = GetPropertyOrField(node, "visible");
                    if (v is bool) return (bool)v;
                }
            }
            catch { }

            try
            {
                object info = GetMember(oci, "oiCharInfo");
                if (info != null)
                {
                    object v = GetPropertyOrField(info, "visible");
                    if (v is bool) return (bool)v;
                }
            }
            catch { }

            try
            {
                MethodInfo getVis = GetMethodByName(oci.GetType(), "GetVisibleSimple");
                if (getVis != null)
                {
                    object r = getVis.Invoke(oci, null);
                    if (r is bool) return (bool)r;
                }
            }
            catch { }

            return true;
        }

        void SetCharVisibleSimple(Studio.OCIChar oci, bool flag)
        {
            // 1) TreeNodeObject.SetVisible —— 工作面板打勾框走的就是這個
            try
            {
                object node = GetMember(oci, "treeNodeObject");
                if (node != null)
                {
                    foreach (var m in node.GetType().GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "SetVisible") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 0 || ps[0].ParameterType != typeof(bool)) continue;

                        var args = new object[ps.Length];
                        args[0] = flag;
                        for (int i = 1; i < ps.Length; i++)
                            args[i] = ps[i].ParameterType == typeof(bool)
                                    ? (object)true          // SetVisible(bool, bool child = true)
                                    : (ps[i].ParameterType.IsValueType
                                       ? Activator.CreateInstance(ps[i].ParameterType) : null);

                        m.Invoke(node, args);
                        SyncVisibleFlag(oci, flag);
                        return;
                    }

                    // 沒有 SetVisible 就直接設屬性
                    SetPropertyOrField(node, "visible", flag);
                    SyncVisibleFlag(oci, flag);
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CharPicker] treeNodeObject 設定失敗: " + e.Message);
            }

            // 2) 退回 OCIChar 自己的方法
            try
            {
                MethodInfo setVis = GetMethodByName(oci.GetType(), "SetVisibleSimple");
                if (setVis != null) setVis.Invoke(oci, new object[] { flag });
                else Logger.LogWarning("[CharPicker] 找不到任何可用的顯示切換方法");
                SyncVisibleFlag(oci, flag);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[CharPicker] 設定顯示狀態失敗: " + e.Message);
            }
        }

        /// <summary>場景資料也要同步，否則存檔或下次讀回來又是舊值。</summary>
        void SyncVisibleFlag(Studio.OCIChar oci, bool flag)
        {
            try
            {
                object info = GetMember(oci, "oiCharInfo");
                if (info != null) SetPropertyOrField(info, "visible", flag);
            }
            catch { }
        }

        // ---- 四種模式的選單 ----
        //
        // 原本每個入口都排兩顆按鈕（換人 / 只鎖身高），四種模式就會變成八顆，
        // 一列塞不下也記不住。改成按鈕只留「換人」和「保持服裝換人」，
        // 按下去在底下展開四顆讓人選這一次要怎麼換。
        //
        // 用內嵌展開而不是彈出視窗：IMGUI 開新視窗要自己管焦點、關閉、位置，
        // 而這裡只是一次性的四選一，展開一列最省事，也不會擋住旁邊的東西。

        Studio.OCIChar modeMenuChar;     // 哪一個角色的選單開著
        int modeMenuKind;                // 1 = 換人   2 = 保持服裝換人
        int modeMenuIndex;               // 保持服裝換人要用到的編號
        int batchMenuKind;               // 0 = 沒開   1 = 整組換人   2 = 整組保持服裝換人

        static readonly SwapMode[] AllModes =
        {
            SwapMode.KeepOldBody, SwapMode.LockHeight, SwapMode.KeepNewBody, SwapMode.Plain,
        };
        static readonly Color[] ModeColors =
        {
            new Color(1f, 0.85f, 0.5f),      // 保留舊卡身材
            new Color(0.7f, 0.85f, 1f),      // 鎖身高
            new Color(0.6f, 1f, 0.75f),      // 維持新卡身材
            new Color(0.85f, 0.85f, 0.85f),  // 一般替換
        };

        /// <summary>
        /// 展開四種模式，按下哪一顆就把那個模式交給 onPick 並收起選單。
        ///
        /// 刻意「先把整列畫完，再處理按下的那一顆」：IMGUI 是同一段程式碼跑
        /// Layout 和 Repaint 兩遍，中途 return 或改變狀態會讓兩遍畫出來的控制項
        /// 數量對不上，換來一堆 Mismatched LayoutGroup。記下來、出了迴圈再動作，
        /// 這一列每一遍都畫一樣多的東西。
        /// </summary>
        void DrawSwapModeRow(string title, Action<SwapMode> onPick)
        {
            int hit = -1;
            bool cancel = false;

            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label(Lang.T(title), GUILayout.Width(96));
            for (int i = 0; i < AllModes.Length; i++)
            {
                GUI.color = ModeColors[i];
                if (GUILayout.Button(SwapModeName(AllModes[i]), GUILayout.Height(24))) hit = i;
            }
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            if (GUILayout.Button(Lang.T("取消"), GUILayout.Width(50), GUILayout.Height(24))) cancel = true;
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            if (cancel) { CloseModeMenu(); return; }
            if (hit >= 0)
            {
                SwapMode m = AllModes[hit];
                CloseModeMenu();
                onPick(m);
            }
        }

        void CloseModeMenu()
        {
            modeMenuChar = null;
            modeMenuKind = 0;
            batchMenuKind = 0;
        }

        /// <summary>點同一顆就收起來，點別顆就換過去。</summary>
        void ToggleModeMenu(Studio.OCIChar oci, int kind, int index)
        {
            if (modeMenuChar == oci && modeMenuKind == kind) { CloseModeMenu(); return; }
            batchMenuKind = 0;
            modeMenuChar = oci;
            modeMenuKind = kind;
            modeMenuIndex = index;
        }

        /// <summary>一組角色（同性別）的一鍵換人。按下之後展開四種模式。</summary>
        void DrawBatchSwapButtons(List<Studio.OCIChar> list)
        {
            GUI.color = new Color(1f, 0.85f, 0.5f);
            if (GUILayout.Button(Lang.T("換人"), GUILayout.Height(24)))
            {
                modeMenuChar = null; modeMenuKind = 0;
                batchMenuKind = batchMenuKind == 1 ? 0 : 1;
            }

            GUI.color = new Color(0.5f, 1f, 0.5f);
            if (GUILayout.Button(Lang.T("保持服裝換人"), GUILayout.Height(24)))
            {
                modeMenuChar = null; modeMenuKind = 0;
                batchMenuKind = batchMenuKind == 2 ? 0 : 2;
            }

            GUI.color = Color.white;
        }

        /// <summary>整組的模式選單。畫在 DrawBatchSwapButtons 那一列的下面。</summary>
        void DrawBatchModeMenu(List<Studio.OCIChar> list)
        {
            if (batchMenuKind == 1)
                DrawSwapModeRow("整組換人：", m => BatchSwapAppearance(list, m));
            else if (batchMenuKind == 2)
                DrawSwapModeRow("整組保持服裝：", m => BatchKeepOutfitSwap(list, m));
        }

        void DrawCharPickerButton(int index, Studio.OCIChar oci)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();

            // 對應工作面板那個打勾框。
            // 舊版用一個預設 true 的追蹤字典，所以場景裡本來就被禁用的角色
            // 一開始也會顯示成啟用，要手動點兩下才對得上。
            // 現在每幀直接讀 oiCharInfo.visible，狀態一開始就是正確的。
            bool currentVisible = GetCharVisibleSimple(oci);
            bool newVisible = GUILayout.Toggle(currentVisible, "", GUILayout.Width(18));
            if (newVisible != currentVisible)
                SetCharVisibleSimple(oci, newVisible);

            string label = (index + 1) + ". " + GetCharDisplayName(oci);
            // 點名字 = 換人。按下去在這一列底下展開四種模式，選了才真的開選卡視窗。
            // 原本名字旁邊那顆「只鎖身高」已經拿掉了 —— 它現在是四種模式之一。
            if (GUILayout.Button(label, GUILayout.Height(28)))
            {
                SelectCharacterInWorkspace(oci);          // 先選取，換完可以直接接著操作
                ToggleModeMenu(oci, 1, index + 1);
            }

            if (GUILayout.Button(Lang.T("選取"), GUILayout.Width(48), GUILayout.Height(28)))
            {
                SelectCharacterInWorkspace(oci);
            }

            if (GUILayout.Button(Lang.T("換衣服"), GUILayout.Width(58), GUILayout.Height(28)))
            {
                Studio.OCIChar target = oci;
                int no = index + 1;
                SelectCharacterInWorkspace(oci);
                if (ToggleSidePanel(oci, 2))
                    StartDressThenAccessories(target, no);
            }

            GUI.color = new Color(0.6f, 1f, 0.9f);
            if (GUILayout.Button(Lang.T("碰撞器"), GUILayout.Width(58), GUILayout.Height(28)))
            {
                SelectCharacterInWorkspace(oci);
                if (ToggleSidePanel(oci, 6))
                {
                    dbFixTarget = oci;
                    dbFixText = DBColliderFix.Available ? DBColliderFix.Describe(oci) : "";
                    dbFixScroll = Vector2.zero;
                    showDbFix = true;
                }
            }

            // 這裡原本有一顆「骨頭」按鈕，把該角色身上被 ABMX 改過的骨頭名單印到 log。
            // 那是當初在填白名單時用的 —— 名單已經定下來了，按鈕就沒有再按的場合。
            // （AbmxFilter.CaptureNames 本身還在用，換人流程要靠它比對舊卡。）

            GUI.color = new Color(1f, 0.75f, 0.3f);
            if (GUILayout.Button(Lang.T("型態鍵"), GUILayout.Width(58), GUILayout.Height(28)))
            {
                SelectCharacterInWorkspace(oci);
                if (ToggleSidePanel(oci, 3))
                {
                    blendTarget = oci;
                    showBlendLock = true;
                }
            }
            GUI.color = Color.white;

            GUILayout.EndHorizontal();

            // ---- 第二行：保持服裝換人 / 存卡片 ----
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);

            GUI.color = new Color(0.5f, 1f, 0.5f);
            if (GUILayout.Button(Lang.T("保持服裝換人"), GUILayout.Width(110), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                ToggleModeMenu(oci, 2, index + 1);
            }
            GUI.color = new Color(1f, 0.8f, 0.4f);
            if (GUILayout.Button(Lang.T("添加飾品"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                StartMergeJob(oci, index + 1);
            }
            GUI.color = new Color(0.85f, 0.75f, 1f);
            if (GUILayout.Button(Lang.T("重置姿勢"), GUILayout.Height(24)))
                ReapplyAnime(oci);
            GUI.color = Color.white;

            if (GUILayout.Button(Lang.T("存人物卡"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                StartCoroutine(CaptureThenSave(oci, index + 1, IsFemale(oci), true));
            }

            if (GUILayout.Button(Lang.T("存服裝卡"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                StartCoroutine(CaptureThenSave(oci, index + 1, IsFemale(oci), false));
            }

            // 七個換裝槽輪流穿上、各拍一張、各存一張服裝卡，最後切回原本那套
            if (GUILayout.Button(Lang.T("存全部換裝"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                StartCoroutine(SaveAllCoordinatesRoutine(oci, index));
            }

            if (GUILayout.Button(Lang.T("存姿勢"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                List<VisChange> hid = soloCharForThumb ? HideOtherCharacters(oci) : null;
                var saved = PoseFix.SavePoseNamed(oci, index + 1, IsFemale(oci));
                RestoreCharacters(hid);
                SelectCharacterInWorkspace(oci);
                SetStatus(saved != null, PoseFix.LastMessage);
                Logger.LogInfo("[PoseFix] " + PoseFix.LastMessage + "\n" + PoseFix.LastReport);
                if (saved != null && openFolderAfterSave) ScreenGrab.RevealInExplorer(saved);
            }

            GUI.color = new Color(0.85f, 0.7f, 1f);
            if (GUILayout.Button(Lang.T("飾品"), GUILayout.Height(24)))
            {
                SelectCharacterInWorkspace(oci);
                if (ToggleSidePanel(oci, 5))
                {
                    accTarget = oci;
                    accCategory = "";
                    showAccPanel = true;
                }
            }
            GUI.color = Color.white;

            GUILayout.EndHorizontal();

            // ---- 第三行：四種模式的選單（只有這個角色被點開時才畫）----
            if (modeMenuChar == oci)
            {
                if (modeMenuKind == 1)
                {
                    Studio.OCIChar target = oci;
                    DrawSwapModeRow("換人：", m =>
                    {
                        if (ToggleSidePanel(target, 1))
                            OpenGenericCardPicker(GetCharaFolder(IsFemale(target)),
                                string.Format(Lang.T("選擇人物卡（{0}） - "), SwapModeName(m)) + GetCharDisplayName(target),
                                (file) => WithShaderChoice(GetCharDisplayName(target),
                                    carry => DoSwapCharacterAppearance(target, file, m, carry)), true);
                    });
                }
                else if (modeMenuKind == 2)
                {
                    Studio.OCIChar target = oci;
                    int no = modeMenuIndex;
                    DrawSwapModeRow("保持服裝：", m => StartKeepOutfitSwap(target, no, m));
                }
            }

            GUILayout.EndVertical();
        }

        // ===== merged from HSPEBlendLinkNullFix =====
        // Everything on BlendRenderer is accessed via reflection because
        // BlendRenderer is a private nested class inside BlendShapesEditor.
        private static class HspeBlendLinkPatches
        {
            private static FieldInfo _fLinkedBlendRenderers;
            private static FieldInfo _fLinkKeynameList;
            private static FieldInfo _fOriBlendIndex;
            private static FieldInfo _fBlendIndics;
            private static FieldInfo _fNonMatchBlendCorrection;
            private static FieldInfo _fBlendNames;
            private static FieldInfo _fLinkKeynames; // on the LINKED renderer
            private static MethodInfo _mSetBlendShapeWeightByName;
            private static MethodInfo _mNonDirty;

            private static void EnsureReflectionCached(Type blendRendererType)
            {
                if (_fLinkedBlendRenderers != null)
                    return;

                _fLinkedBlendRenderers = AccessTools.Field(blendRendererType, "_linkedBlendRenderers");
                _fLinkKeynameList = AccessTools.Field(blendRendererType, "_linkKeynameList");
                _fOriBlendIndex = AccessTools.Field(blendRendererType, "_oriBlendIndex");
                _fBlendIndics = AccessTools.Field(blendRendererType, "_blendIndics");
                _fNonMatchBlendCorrection = AccessTools.Field(blendRendererType, "_nonMatchBlendCorrection");
                _fBlendNames = AccessTools.Field(blendRendererType, "_blendNames");
                _fLinkKeynames = AccessTools.Field(blendRendererType, "_linkKeynames");
                _mSetBlendShapeWeightByName = AccessTools.Method(blendRendererType, "SetBlendShapeWeight", new[] { typeof(string), typeof(float) });
                _mNonDirty = AccessTools.Method(blendRendererType, "NonDirty", new[] { typeof(string) });
            }

            // Re-implementation of the private GetOriginBlendShapeName(int index) -
            // no changes to its own logic, the bug is purely in ApplyLink's
            // missing null check on this method's return value.
            private static string GetOriginBlendShapeName(object instance, int index)
            {
                var oriBlendIndex = (IDictionary)_fOriBlendIndex.GetValue(instance);
                var blendIndics = (IDictionary)_fBlendIndics.GetValue(instance);
                var nonMatchBlendCorrection = (IDictionary)_fNonMatchBlendCorrection.GetValue(instance);
                var blendNames = (IList)_fBlendNames.GetValue(instance);

                string blendName = null;
                if (oriBlendIndex.Contains(index))
                {
                    blendName = (string)oriBlendIndex[index];
                    if (nonMatchBlendCorrection.Contains(blendName))
                    {
                        string correctionName = (string)nonMatchBlendCorrection[blendName];
                        if (blendIndics.Contains(correctionName))
                        {
                            blendName = correctionName;
                        }
                        else
                        {
                            nonMatchBlendCorrection.Remove(blendName);
                            blendName = null;
                        }
                    }
                    else
                    {
                        if (!blendIndics.Contains(blendName))
                        {
                            blendName = null;
                        }
                    }
                }
                else
                {
                    if (blendNames.Count > index)
                    {
                        blendName = (string)blendNames[index];
                    }
                }
                return blendName;
            }

            // Prefix for ApplyLink(float weight, int index, bool nonDirty = false).
            // Returning false skips the original (buggy) method entirely.
            public static bool ApplyLinkPrefix(object __instance, float weight, int index, bool nonDirty)
            {
                Type blendRendererType = __instance.GetType();
                EnsureReflectionCached(blendRendererType);

                var linkedBlendRenderers = (IList)_fLinkedBlendRenderers.GetValue(__instance);
                var linkKeynameList = (IList)_fLinkKeynameList.GetValue(__instance);
                var oriBlendIndex = (IDictionary)_fOriBlendIndex.GetValue(__instance);
                var blendIndics = (IDictionary)_fBlendIndics.GetValue(__instance);

                if (linkedBlendRenderers.Count > 0 && index < linkKeynameList.Count)
                {
                    string linkKeyname = null;

                    if (oriBlendIndex.Count > 0)
                    {
                        linkKeyname = GetOriginBlendShapeName(__instance, index);

                        // --- THE ACTUAL FIX: guard against null before the dictionary lookup ---
                        if (linkKeyname != null && blendIndics.Contains(linkKeyname))
                        {
                            int blendIndex = (int)blendIndics[linkKeyname];
                            // --- SECOND FIX: blendIndex indexes the FULL blend shape
                            // list, but linkKeynameList only covers the "eyes"
                            // category (capped at eyesComponentsCount in
                            // CreateLinkKeynames). Skip the remap instead of
                            // indexing out of bounds if it falls outside that range.
                            if (blendIndex >= 0 && blendIndex < linkKeynameList.Count)
                            {
                                linkKeyname = (string)linkKeynameList[blendIndex];
                            }
                        }
                    }
                    else
                    {
                        linkKeyname = (string)linkKeynameList[index];
                    }

                    if (linkKeyname != null)
                    {
                        foreach (var linkedBlendRenderer in linkedBlendRenderers)
                        {
                            var linkKeynames = (IDictionary)_fLinkKeynames.GetValue(linkedBlendRenderer);
                            if (linkKeynames.Contains(linkKeyname))
                            {
                                int linkIndex = (int)linkKeynames[linkKeyname];
                                var blendNamesOfLinked = (IList)_fBlendNames.GetValue(linkedBlendRenderer);
                                if (linkIndex < 0 || linkIndex >= blendNamesOfLinked.Count)
                                {
                                    continue;
                                }
                                string targetBlendName = (string)blendNamesOfLinked[linkIndex];

                                if (nonDirty)
                                {
                                    _mNonDirty.Invoke(linkedBlendRenderer, new object[] { targetBlendName });
                                }
                                else
                                {
                                    _mSetBlendShapeWeightByName.Invoke(linkedBlendRenderer, new object[] { targetBlendName, weight });
                                }
                            }
                        }
                    }
                }

                // We fully replaced the method's behavior above, so skip the original.
                return false;
            }
        }

        // ===== 共用反射小工具 =====
        object GetPropertyOrField(object obj, string name)
        {
            // 跟 GetMember 走同一個快取。原本這裡是屬性找不到才往下一層層翻欄位，
            // 每呼叫一次就重做一遍 —— 在畫圖路徑上這個成本是會看見的。
            if (obj == null) return null;
            MemberInfo mi = LookupMember(obj.GetType(), name);
            return mi == null ? null : ReadMember(obj, mi);
        }

        void SetPropertyOrField(object obj, string name, object value)
        {
            if (obj == null) return;
            Type type = obj.GetType();
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite)
            {
                try
                {
                    p.SetValue(obj, value, null);
                    return;
                }
                catch { }
            }
            while (type != null)
            {
                var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (f != null)
                {
                    try
                    {
                        f.SetValue(obj, value);
                        return;
                    }
                    catch { }
                }
                type = type.BaseType;
            }
        }

        // 反射查到的成員存起來重用。
        //
        // 為什麼：GetMember / GetPropertyOrField 在面板畫圖的路徑上每幀被叫很多次
        // （每個角色一顆打勾框就要查一次 treeNodeObject + visible），
        // 而 Type.GetProperty / GetField 每一次都是真的去翻型別的成員表 ——
        // 它不便宜，加上 BindingFlags.IgnoreCase 還要做不分大小寫的比對。
        // 型別和名字一樣時答案永遠一樣，查一次記著就好。
        //
        // key 用 "型別全名|成員名"：Dictionary<Type,...> 再包一層太囉唆，
        // 而這裡的呼叫量級（幾十種型別 × 十幾個名字）字串接起來也完全不是瓶頸。
        static readonly Dictionary<string, MemberInfo> memberCache = new Dictionary<string, MemberInfo>();

        static MemberInfo LookupMember(Type t, string name)
        {
            string key = t.FullName + "|" + name;
            MemberInfo mi;
            if (memberCache.TryGetValue(key, out mi)) return mi;   // null 也要記，免得每幀重查一次不存在的成員

            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.IgnoreCase;
            // GetProperty 碰到「基底和衍生都宣告了同名屬性」會丟 AmbiguousMatchException。
            // 加上 IgnoreCase 之後撞名的機會又更高一點，包起來當成「沒有屬性」往下找欄位。
            try { mi = t.GetProperty(name, F); }
            catch { mi = null; }
            if (mi == null)
            {
                Type walk = t;
                while (walk != null)
                {
                    FieldInfo f = walk.GetField(name, F);
                    if (f != null) { mi = f; break; }
                    walk = walk.BaseType;
                }
            }
            memberCache[key] = mi;
            return mi;
        }

        static object ReadMember(object obj, MemberInfo mi)
        {
            try
            {
                PropertyInfo p = mi as PropertyInfo;
                if (p != null) return p.GetValue(obj, null);
                FieldInfo f = mi as FieldInfo;
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            MemberInfo mi = LookupMember(obj.GetType(), name);
            return mi == null ? null : ReadMember(obj, mi);
        }

        // ---------------------------------------------------------- Studio 單例

        // FindObjectOfType 會掃整個場景裡所有的元件 —— 一張大卡片有上萬個
        // GameObject，這一呼叫就是實打實的幾毫秒。它以前被放在 GetAllSceneCharacters
        // 裡，而那個是面板畫圖時每次 OnGUI 都會叫的（IMGUI 一幀至少 Layout +
        // Repaint 兩趟，還有輸入事件），於是「F6 面板一開就卡」。
        // Studio 是單例，抓到就一直是同一個，存起來即可；
        // 物件被銷毀（換場景）時 Unity 的 == null 會是 true，那時再找一次。
        static Studio.Studio studioCache;

        static Studio.Studio StudioInstance()
        {
            if (studioCache == null)
                studioCache = UnityEngine.Object.FindObjectOfType<Studio.Studio>();
            return studioCache;
        }

        void SetMember(object obj, string name, object value)
        {
            SetPropertyOrField(obj, name, value);
        }

        MethodInfo GetMethodByName(Type type, string name)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.Name == name) return m;
            }
            return null;
        }
    }
}
