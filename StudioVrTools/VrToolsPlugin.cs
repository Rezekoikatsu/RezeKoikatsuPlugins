using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace StudioVrTools
{
    /// <summary>
    /// Studio VR Tools —— 工作室 VR 相關的東西全部集中在這裡（F9）。
    ///
    /// 為什麼跟 CutScene（F7）分開
    /// --------------------------
    /// 這兩件事的生命週期不一樣。過場播放是「做片子」的功能，VR 是「怎麼看」的功能；
    /// 綁在一起的話，只想用其中一邊的人得整包吃下去，而且 F7 的面板會愈長愈雜。
    /// 分開之後兩邊可以各自單獨存在：
    ///   只裝 CutScene → 過場照樣在桌面播，只是頭顯裡沒畫面
    ///   只裝 VR Tools → 回到相機視角那些照樣能用，只是沒有過場可畫
    /// 兩邊靠 CutSceneBridge 用反射接，沒有組件參照，所以缺一邊不會兩支一起死。
    ///
    /// 目前包含
    /// --------
    ///   1. 頭顯裡的過場畫面（VrScreen）—— IMGUI 進不了眼睛貼圖，改用世界空間的板子
    ///   2. 回到相機視角（CameraSyncBridge）—— 補 KK_VR_CameraSync 缺的那個回歸動作
    /// 之後的手柄、手環選單也放這裡。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("CharaStudio")]
    public class VrToolsPlugin : BaseUnityPlugin
    {
        public const string GUID = "reze.studio.vrtools";
        public const string NAME = "Studio VR Tools";
        public const string VERSION = "1.0.1";

        ConfigEntry<KeyCode> cfgPanelKey;
        ConfigEntry<int> cfgVrMode;
        ConfigEntry<bool> cfgKeepDesktop, cfgFollow, cfgFlipY;
        ConfigEntry<float> cfgDistance, cfgWidth;
        ConfigEntry<KeyboardShortcut> cfgRealignKey;
        ConfigEntry<bool> cfgRealignHold;

        ConfigEntry<bool> cfgCtrlEnable, cfgCtrlPitch, cfgCtrlLeftHandMoves, cfgCtrlRightHandMoves;
        ConfigEntry<bool> cfgCtrlVertical;
        ConfigEntry<int> cfgResetHand, cfgResetButton, cfgMountHand, cfgMountButton;
        ConfigEntry<float> cfgMoveSpeed, cfgTurnSpeed, cfgPitchSpeed, cfgDeadZone;
        ConfigEntry<float> cfgMoveSpeedR, cfgOrbitSpeed;
        ConfigEntry<int> cfgOrbitAxisUD, cfgOrbitAxisLR;
        ConfigEntry<bool> cfgOrbitHeadUD, cfgOrbitHeadLR;
        ConfigEntry<int> cfgSpotHand, cfgSpotSaveButton, cfgSpotGoButton;
        ConfigEntry<bool> cfgToolbarButton;
        ConfigEntry<bool> cfgCutKeyEnable;
        ConfigEntry<int> cfgCutKeyHand, cfgPauseButton;
        ConfigEntry<bool> cfgSpotToJson;
        ConfigEntry<float> cfgSeekStep, cfgSeekDelay, cfgSeekRepeat;
        ConfigEntry<float> cfgTransportHold, cfgTransportThreshold;
        float nextPosePublish;
        ConfigEntry<bool> cfgKillPostFx;
        ConfigEntry<bool> cfgUiBacking, cfgAlsoRealign;
        ConfigEntry<float> cfgUiOpacity;
        ConfigEntry<bool> cfgSolo, cfgMountUi, cfgMountLeft, cfgVrLog, cfgMountFlip;
        ConfigEntry<bool> cfgVrSkin, cfgVrSkinOnlyVr;
        ConfigEntry<bool> cfgHideUi;
        ConfigEntry<bool> cfgFxMute, cfgFxMuteOnlyVr, cfgFxFolder, cfgFxFolderVrOff;
        ConfigEntry<string> cfgFxMuteList;
        ConfigEntry<float> cfgFxMuteSettle;
        ConfigEntry<float> cfgMountDist, cfgMountScale, cfgMountTilt, cfgMountLift;

        readonly VrScreen vr = new VrScreen();
        bool show;
        bool showSettings;
        // 設置子視窗。開在主視窗右邊，不跟主視窗搶位置。
        Rect settingsWin = new Rect(940f, 80f, 470f, 260f);
        Vector2 settingsScroll;
        bool showCtrlSettings;
        Rect ctrlWin = new Rect(880f, 360f, 760f, 470f);
        Vector2 ctrlScroll;
        // x 從 80 移到 360：預設位置會壓到工作室左邊那排工具按鈕，
        // VR 裡拖視窗又特別麻煩，一開始就擋住等於不能用。
        Rect win = new Rect(360f, 80f, 560f, 300f);
        float nextRealign;

        GUIStyle small, rich;

        void Awake()
        {
            cfgPanelKey = Config.Bind("General", "Panel Hotkey", KeyCode.F9, "開關這個面板");

            cfgVrMode = Config.Bind("VR Screen", "Mode", 0,
                "0 = 自動偵測（偵測到 VR 才用）、1 = 一律使用、2 = 一律不用。"
                + "自動偵測看的是 VRSettings 以及場上有沒有 VRGIN 的 VRCamera");
            cfgKeepDesktop = Config.Bind("VR Screen", "Also Draw On Desktop", false,
                "VR 時桌面視窗要不要也畫一份。關掉可以少畫一次全螢幕貼圖，"
                + "但旁邊的人就看不到了，除錯時也不方便");
            cfgFollow = Config.Bind("VR Screen", "Follow Head", true,
                "開著：板子每幀貼在視線正前方。關掉：過場開始時擺一次就不動了（比較不暈）。"
                + "預設改成開著 —— 開場過場是在場景載入完的當下開始的，"
                + "而 CameraSync 會在那前後把視角絕對對齊到工作室相機，"
                + "板子先擺好、視角再跳走，人就什麼都看不到。"
                + "關掉的話仍有看門狗會在板子跑出視野時重擺一次");
            cfgFlipY = Config.Bind("VR Screen", "Flip Vertically", false, "影片在頭顯裡上下顛倒時打開");
            cfgDistance = Config.Bind("VR Screen", "Distance", 4.48f, "板子離眼睛幾公尺");
            cfgWidth = Config.Bind("VR Screen", "Width", 6.07f,
                "板子多寬（公尺）。2 公尺遠 × 3 公尺寬大約是 73 度視角");

            cfgRealignKey = Config.Bind("Camera", "Return To Camera View", new KeyboardShortcut(KeyCode.Home),
                "要求 KK_VR_CameraSync 重新對齊到目前的工作室相機。"
                + "CameraSync 只套用逐幀差值、會保留你用手柄移動出去的偏移，"
                + "絕對對齊只在載入場景時做一次 —— 這個鍵就是手動再做一次。"
                + "沒裝 CameraSync 的話這個鍵沒有作用");
            cfgAlsoRealign = Config.Bind("Camera", "Also Ask CameraSync", true,
                "重置鍵的主要動作是把你自己移動出去的量原樣退回去 —— 那就是"
                + "「CameraSync 這一刻放你的位置」，而且它正在運鏡也成立。\n"
                + "打開這個會**額外**叫 CameraSync 的 RequestInitialAlignment，"
                + "但那是對齊到**場景載入當下**擷取的相機姿勢，不是現在的 —— "
                + "所以預設關著，需要「回到場景剛載入時那個機位」才打開");
            cfgRealignHold = Config.Bind("Camera", "Hold To Keep Returning", true,
                "開著：按住期間每 0.1 秒重新對齊一次，等於把視角鎖在相機上，放開就恢復自由移動。"
                + "關掉：只有按下去的那一下回歸一次");

            cfgUiBacking = Config.Bind("Interface", "Opaque Backing", true,
                "VR 裡外掛介面看起來半透明，是因為 VRGIN 把桌面介面畫進一張 RenderTexture "
                + "再貼到浮空板子上，而那張貼圖的 alpha 不是 1 —— 視窗底色是跟"
                + "**板子後面的場景**混色，場景愈亮字愈糊。這裡在每一塊介面板子正後方"
                + "補一塊不透明黑板，介面就改成跟黑色混，等同桌面上的效果。"
                + "所有外掛一起受惠，不只這幾支");
            cfgMountUi = Config.Bind("Interface", "Mount Panel To Hand", true,
                "把主介面板子每幀擺到手柄前面，像手環選單那樣。"
                + "主介面之所以離得遠，是因為 VRGIN 把它當成擺在世界裡的一塊螢幕"
                + "（位置由它的 Distance / Angle 決定），跟手無關；"
                + "手環選單是 Ermin 另外做的 uGUI，本來就掛在手柄底下");
            cfgMountLeft = Config.Bind("Interface", "Mount To Left Hand", true, "關掉 = 黏右手");
            cfgHideUi = Config.Bind("Interface", "Hide Main Panel", false,
                "把 VRGIN 那塊主介面板子整個藏起來，跟 Ermin 那支 VR 插件右手 A 鍵一樣。\n"
                + "只關板子上 Renderer 的 enabled，不動 VRGIN 自己的啟用狀態 —— "
                + "放回去就還原，不會跟它打架");
            cfgMountFlip = Config.Bind("Interface", "Flip Panel", true,
                "GUIQuad 的正面朝 +Z 還是 -Z 是 VRGIN 建網格時決定的，從外面看不出來。"
                + "猜錯就會看到背面、畫面左右相反。實測這一邊才是對的，所以預設開著");
            cfgMountDist = Config.Bind("Interface", "Mount Distance", 0.12f, "公尺，沿著手柄指向");
            cfgMountScale = Config.Bind("Interface", "Mount Scale", 0.25f,
                "手邊的板子要縮小，不然會糊在臉上。等比例縮，長寬比不變");
            cfgMountTilt = Config.Bind("Interface", "Mount Tilt", 0f,
                "繞手柄的左右軸微調角度。0 = 完全平行於手柄");
            cfgMountLift = Config.Bind("Interface", "Mount Lift", 0.02f,
                "公尺，沿著板子法線離開手柄一點，避免跟手模型打架");

            cfgVrLog = Config.Bind("General", "Log VR Status", true,
                "每 3 秒把手柄、介面板子的狀態各記一行。戴著頭顯看不到面板，"
                + "出問題只能事後看 log —— 前幾輪就是因為 log 裡一行都沒有，只能用猜的");

            cfgVrSkin = Config.Bind("Interface", "Opaque Window Skin", true,
                "把 F9 自己的視窗、按鈕、輸入框換成不透明的純色底。\n"
                + "為什麼有效：VideoExport 在 VR 裡很清楚，翻它的原始碼發現它也是 IMGUI，"
                + "差別只在它把 skin 的底圖換成自己的不透明圖。Unity 內建 skin 的底圖 alpha "
                + "不是 1，VRGIN 又把整個桌面畫進一張貼圖再貼到板子上，視窗底色就跟後面混在一起。"
                + "在板子後面補底板改不了這件事 —— 要在**來源**把 alpha 變成 1。\n"
                + "只換外觀，不動字級");
            cfgVrSkinOnlyVr = Config.Bind("Interface", "Skin Only In VR", true,
                "桌面上本來就看得清楚，換了反而跟其他外掛不一致");
            cfgUiOpacity = Config.Bind("Interface", "Backing Opacity", 0.45f,
                "1 = 完全擋住後面的場景，0 = 完全透明（等於沒有底板）。"
                + "鍵名帶 v2 是因為下限從 0.3 放寬到 0，舊鍵留著的話新範圍套不上");

            cfgFxMute = Config.Bind("Auto Hide", "Enabled", true,
                "載入場景之後，自動把清單裡的工作室物件取消勾選（就是工作區清單那個眼睛）。\n"
                + "為的是 xukmi FX 那類全螢幕後處理：它在單眼算一次、VRGIN 轉雙眼之後再算一次，"
                + "頭顯裡就變成偏色又泛光，桌面上卻完全正常。\n"
                + "**不會動到場景卡** —— 只是把勾勾關掉，跟你自己去點一下一樣");
            cfgFxMuteOnlyVr = Config.Bind("Auto Hide", "Only In VR", true,
                "關掉的話桌面模式也會一起關。桌面通常要留著那些效果，所以預設只在 VR");
            // 鍵名帶 v2：.cfg 裡已經存在的鍵，BepInEx 不會套用新的預設值。
            // 上一版的預設是編號寫法（對不上 Sideloader 的執行期編號），
            // 不換鍵名的話改了預設也沒用 —— 這個坑之前踩過。
            cfgFxMuteList = Config.Bind("Auto Hide", "Name List", VrFxMute.DefaultList,
                "逗號分隔。預設用**名稱**比對（不分大小寫，包含即可）——\n"
                + "「Depth of Field」一條就涵蓋九種景深。比對工作室物品清單裡的原始名稱"
                + "和工作區上顯示的文字，改過名也抓得到。\n"
                + "也可以寫編號 群組:分類:編號（編號可寫範圍 7-15，三欄都可以寫 *），"
                + "Sideloader 會重新配號，場景卡裡那組對不上。\n"
                + "名稱規則也比對**資料夾**，關掉資料夾底下所有東西一起收。"
                + "最快的加入方式是在工作區選好物件／資料夾，再按面板上的"
                + "「把選取的加入清單」。\n"
                + "(FX) 資料夾不在這份清單裡 —— 它有自己的開關。");
            cfgFxFolder = Config.Bind("Auto Hide", "Show FX Folder", true,
                "合併場景之後一定會出現的那個 (FX) 資料夾要不要顯示。\n"
                + "勾 = 顯示（預設）。進 VR 而且「啟用」開著時會自動取消勾選，"
                + "離開 VR 再自動勾回來。中間你自己在工作區點勾勾我們不會跟你搶 ——"
                + "只有這個開關被改動、或場景重載時才會去套用一次");
            cfgFxFolderVrOff = Config.Bind("Auto Hide", "Hide FX Folder In VR", false,
                "進 VR 的時候要不要自動把 (FX) 資料夾取消勾選，離開 VR 再勾回來。\n"
                + "關掉這個，上面那個 (FX) 開關就完全由你自己控制，進出 VR 都不動它");
            cfgFxMuteSettle = Config.Bind("Auto Hide", "Settle Seconds", 1.0f,
                "場景是分批載入的，物件先進清單、visible 才從卡片寫回去。"
                + "太早關會被載入程序覆蓋回去，所以等物件數量不再變動這麼久才動手");

            cfgKillPostFx = Config.Bind("VR Screen", "Kill Post FX While Playing", true,
                "獨佔模式只改 cullingMask（哪幾層進渲染）和清除色，但**後製是在那之後才跑的** ——"
                + "畫面已經是純黑加影片了，泛光、色彩校正、景深還是照樣往上疊。"
                + "影片本身很亮，泛光就把亮部整片糊開，顏色再被校正曲線拉一次，"
                + "就變成你看到的「偏紅又泛光」。桌面那一份走的是 IMGUI overlay，"
                + "根本沒經過這些相機，所以才正常。\n"
                + "判斷方式是元件有沒有自己實作 OnRenderImage，不是比對外掛名單；"
                + "VRGIN / SteamVR 的一律跳過（它們也用 OnRenderImage 做雙眼合成）");

            cfgSolo = Config.Bind("VR Screen", "Solo Video While Playing", true,
                "播過場的時候把相機的 cullingMask 改成只剩影片那一層、清除色設成黑 —— "
                + "地圖、角色、特效通通不進渲染，畫面就是純黑加影片。\n"
                + "比放大黑底可靠：黑底是實體板子，還是要跟場景比誰近，"
                + "地圖有一面牆卡在中間就穿幫。\n"
                + "這**不會動到你的場景**，只是改相機畫不畫，播完原樣還原");

            cfgCtrlEnable = Config.Bind("Controller", "Enabled", true,
                "搖桿平移、握把＋搖桿轉向、指定鍵回到相機視角。"
                + "跟 Ermin 那支 VR 插件自己的握把功能可能會撞到，覺得干擾就關掉");
            cfgResetHand = Config.Bind("Controller", "Reset Hand", 0, "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n0 = 左手，1 = 右手");
            cfgResetButton = Config.Bind("Controller", "Reset Button", VrInput.BtnStick,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n1 = AppMenu（Quest 的左 Y / 右 B）、7 = A（左 X / 右 A）、"
                + "32 = 搖桿按下、33 = 扳機。"
                + "各家 runtime 的對應不保證一樣，對不上就看面板上的即時按鍵顯示再改");
            cfgMountHand = Config.Bind("Controller", "Mount Toggle Hand", 0, "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n0 = 左手，1 = 右手");
            cfgMountButton = Config.Bind("Controller", "Mount Toggle Button", VrInput.BtnAppMenu,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n按一下把主介面抓到手上，再按一下放回原處。"
                + "1 = AppMenu（Quest 的左 Y / 右 B）、7 = A（左 X / 右 A）、"
                + "32 = 搖桿按下、33 = 扳機");
            cfgCtrlLeftHandMoves = Config.Bind("Controller", "Left Stick Moves", true, "");
            cfgCtrlRightHandMoves = Config.Bind("Controller", "Right Stick Moves", true, "");
            cfgMoveSpeed = Config.Bind("Controller", "Left Move Speed", 0.1f, "公尺／秒。走路大約 1.4");
            cfgTurnSpeed = Config.Bind("Controller", "Turn Speed", 15f, "度／秒");
            cfgCtrlPitch = Config.Bind("Controller", "Allow Pitch", true,
                "握把＋搖桿上下。上下轉是 VR 暈眩的主要來源之一，會暈就關掉");
            cfgPitchSpeed = Config.Bind("Controller", "Pitch Speed", 5f, "度／秒，刻意比左右轉慢");
            cfgMoveSpeedR = Config.Bind("Controller", "Right Move Speed", 0.4f,
                "左右手各自一個速度。左手的是「移動速度」那一條。\n"
                + "兩手分開是為了「一手粗調一手微調」—— 左手設 1.5 走位、右手設 0.2 對位");
            const string axisHelp =
                "支點都在 VR 原點（整顆頭跟著繞）：\n"
                + "0 = 世界的上下軸　→ 畫面水平繞圈\n"
                + "1 = 視線的左右軸　→ 整個視角往上翻／往下翻\n"
                + "2 = 視線的前後軸　→ 整個視角左右傾斜，像飛機滾轉\n"
                + "軸一律壓平到水平面 —— 不壓的話頭一低就會斜著轉，地平線是歪的";
            cfgOrbitAxisUD = Config.Bind("Controller", "Orbit Axis Vertical", 1, axisHelp);
            cfgOrbitAxisLR = Config.Bind("Controller", "Orbit Axis Horizontal", 2, axisHelp);

            const string pivotHelp =
                "支點放哪裡：\n"
                + "false = VR 原點　→ 人被沿著圓周甩出去，位置跟著變\n"
                + "true  = 眼睛　　→ 純粹轉方向，人不動\n"
                + "「畫面順時針轉、其他都不變」要的是後者。支點在原點的話，"
                + "滾轉軸離你有一段距離，轉起來會變成「從前方 45 度盪到後方 45 度」的弧線";
            cfgOrbitHeadUD = Config.Bind("Controller", "Orbit Pivot At Head Vertical", false, pivotHelp);
            cfgOrbitHeadLR = Config.Bind("Controller", "Orbit Pivot At Head Horizontal", true, pivotHelp);

            cfgSpotToJson = Config.Bind("Controller", "Save View To CutScene", true,
                "扳機＋X 記住視角時，順便請 Studio CutScene 把這個點寫進這張卡的 "
                + ".cutscene.json，而且是寫在「目前播到第幾段」那一段底下。"
                + "下次載入同一張卡、播到同一段時會自動套回去。"
                + "沒裝 F7 的話這個設定沒有作用");
            cfgCutKeyEnable = Config.Bind("Controller", "Transport Mode Enabled", true,
                "遙控 Studio CutScene（F7）的那一組綁定要不要作用。預設是握把＋扳機按住時：\n"
                + "　搖桿按下＝暫停／播放（正在播過場的話是跳過這一段）\n"
                + "　Y＝上一個場景　X＝下一個場景\n"
                + "　搖桿右／左＝快轉／倒轉，推著不放會連續跳\n"
                + "　搖桿上／下推滿並按住＝重播／停止\n"
                + "以上每一個都可以在面板上重新綁。握把＋扳機按住期間，那隻手的"
                + "移動和轉向會停用，免得快轉的同時畫面在轉。\n"
                + "沒裝 F7 的話這一整組沒有作用");
            cfgCutKeyHand = Config.Bind("Controller", "Transport Hand", 0,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n0 = 左手（預設）　1 = 右手");
            cfgPauseButton = Config.Bind("Controller", "Transport Pause Button", VrInput.BtnAppMenu,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n握把＋扳機按著時，哪個鍵是暫停／播放。1 = Y/B、7 = X/A、32 = 搖桿按下、33 = 扳機。\n"
                + "預設 Y。刻意不用搖桿中鍵 —— 那顆同時是「回到相機視角」的重置鍵，"
                + "按下去畫面會被重置，等於一顆鍵做兩件相衝的事");
            cfgSeekStep = Config.Bind("Controller", "Seek Step Seconds", 1f,
                "搖桿左右推一下跳多少秒");
            cfgSeekDelay = Config.Bind("Controller", "Seek Repeat Delay", 0.4f,
                "推著不放超過這個秒數才開始連續跳。太短的話輕推一下就會連跳好幾格");
            cfgSeekRepeat = Config.Bind("Controller", "Seek Repeat Interval", 0.12f,
                "開始連發之後每隔多久跳一次。預設 0.12 秒配 1 秒的步長，"
                + "大約是 8 倍速；要更快就把步長調大，別把這個調到太小");
            cfgTransportHold = Config.Bind("Controller", "Transport Hold Seconds", 1f,
                "搖桿上／下要推滿並按住這麼久才會觸發。"
                + "這兩個會把現在的播放狀態整個作廢，所以刻意設計成不好誤觸");
            cfgTransportThreshold = Config.Bind("Controller", "Transport Stick Threshold", 0.7f,
                "搖桿推過這個量才算有方向。比移動的死區高很多是故意的："
                + "輕輕碰到不該開始快轉，而上下要求的是「推滿」");
            cfgLang = Config.Bind("General", "Language", 0,
                "0 = 繁體中文（預設）　1 = English　2 = 日本語。\n"
                + "面板最下方的 Language 按鈕也可以切，三支插件會一起換");
            Lang.Set(cfgLang.Value);
            cfgUiScale = Config.Bind("Interface", "UI Scale", 1f,
                "面板的縮放倍率，0.6 ～ 2（1 = 原本大小）。字太小或面板太佔畫面時調這個。\n"
                + "面板的設置裡也可以調。只影響這一支插件的面板");
            Fit.SetScale(cfgUiScale.Value);
            cfgToolbarButton = Config.Bind("General", "Show Toolbar Button", true,
                "工作室左邊那排工具列上那顆 VR 頭顯圖示（右下角有個 R）。"
                + "關掉就只剩 F9 熱鍵。改完立刻生效，不用重開遊戲");
            cfgSpotHand = Config.Bind("Controller", "Save View Hand", 0, "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n0 = 左手，1 = 右手");
            cfgSpotSaveButton = Config.Bind("Controller", "Save View Button", VrInput.BtnA,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n要**按著扳機**再按這個鍵才算。1 = Y/B、7 = X/A、32 = 搖桿按下、33 = 扳機");
            cfgSpotGoButton = Config.Bind("Controller", "Go To View Button", VrInput.BtnAppMenu,
                "【已停用】這一格只在第一次升級時被讀一次，用來把舊設定搬到新的 Bind * 那幾格。改這裡不會有任何效果，請改 Bind * 或用面板上的按鍵綁定。\n一樣要按著扳機。預設是 Y —— 沒按扳機時 Y 是「主介面原處／黏手／隱藏」循環，"
                + "按著扳機才是這個，兩者不會互相干擾");
            cfgOrbitSpeed = Config.Bind("Controller", "Orbit Speed", 15f,
                "度／秒。這個轉法的支點在 VR 原點，整顆頭會沿著圓周被帶著走 ——"
                + "跟握把那個「站在原地轉身」（支點在眼睛）是兩回事");
            cfgCtrlVertical = Config.Bind("Controller", "Grip XY Vertical Move", true,
                "按住握把時，X（左手 X／右手 A）往下、Y（左手 Y／右手 B）往上，"
                + "沿世界的上下垂直升降。速度沿用該手的移動速度。\n"
                + "原本綁在「扳機＋搖桿前後」，但那跟同一支搖桿的左右繞轉共用，"
                + "斜推就會邊升降邊轉，完全不像單純的上下平移 —— 改成按鍵就乾淨了");
            cfgDeadZone = Config.Bind("Controller", "Stick Dead Zone", 0.18f,
                "推超過這個量才算數。太小的話手柄的靜止漂移會讓畫面自己慢慢飄");

            SetupBinds();

            // Studio 左邊工具列的按鈕（跟 Timeline、F6 / F7 那幾顆同一排）。
            // 沒裝 KKAPI 也不會怎樣，只是少一顆按鈕。
            ToolbarButton.Create(delegate(bool on) { show = on; }, show);
            toolbarRetryUntil = Time.realtimeSinceStartup + 30f;

            Logger.LogInfo(NAME + " " + VERSION + " 已載入，按 " + cfgPanelKey.Value + " 開啟面板。");
        }

        void Update()
        {
            // 別的插件面板上換了語言就跟著換
            int langNow;
            Lang.Follow(cfgLang.Value, out langNow);
            if (langNow != cfgLang.Value) cfgLang.Value = langNow;

            // 介面縮放：設定檔被改了（F1 設定管理員、重置為預設）就跟上
            if (Mathf.Abs(Fit.Scale - cfgUiScale.Value) > 0.0001f) Fit.SetScale(cfgUiScale.Value);

            if (Input.GetKeyDown(cfgPanelKey.Value)) { show = !show; ToolbarButton.Sync(show); }
            TickToolbar();
            TickViewLink();
            TickRealign();
            TickCapture();
            TickController();
            TickScreen();
            TickUiBacking();
            TickFxMute();

            // 這裡是三支插件共用的介面外觀設定來源。
            // 放在 Update 而不是 OnGUI：F9 的面板關著的時候也要公布，
            // 不然 F6 / F7 只有在 F9 面板開著時才跟得上，行為飄忽又難查。
            VrSkin.Publish(cfgVrSkin.Value, cfgVrSkinOnlyVr.Value);
        }

        // ------------------------------------------------------------ 工具列按鈕

        float toolbarRetryUntil, toolbarNextTry, nextTint;
        bool toolbarShown = true;

        /// <summary>
        /// 顯示／隱藏工具列按鈕，並把「按下去變綠色」改成黃色。
        ///
        /// 綠色在頭顯裡通常代表 Passthrough，撞在一起會誤會。
        /// KKAPI 只在狀態改變時上色（反組譯確認：image.color = Toggled ? green : white），
        /// 所以每 0.5 秒補塗一次就夠。
        ///
        /// 開頭三十秒要重複套用顯示狀態：KKAPI 的工具列是等工作室載完才排版的，
        /// 我們在 Awake 建按鈕、設定又是關的時候，第一次 SetVisible 很可能
        /// 打在還不存在的控制項上，然後按鈕就這樣冒出來了。
        /// </summary>
        void TickToolbar()
        {
            bool want = cfgToolbarButton.Value;
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

        // ------------------------------------------------------------ 視角的存 / 取

        /// <summary>
        /// 跟 F7 交換視角。
        ///
        /// 兩件事：
        ///   1. 每 0.2 秒公布一次「我現在在哪」。F7 要存點位時直接拿這個，
        ///      不必回頭問我們 —— 跨插件的同步呼叫能免則免。
        ///      0.2 秒的延遲在這裡完全不重要：你按下記住鍵之前早就停在那個點上了。
        ///   2. 收 F7 的「切到這一段了，視角換成這個」。
        /// </summary>
        void TickViewLink()
        {
            // **桌面模式一律不做。**
            //
            // 這一段是無條件每幀跑的，而 VrLocomotion 的 Origin() 會去讀 VRGIN 的
            // VR.Camera —— 那是個「一讀就會生出來」的惰性單例。桌面模式下讀到它，
            // VRGIN 會當場生一個 VRGIN_Camera 物件，然後它的 OnUpdate 每幀丟一次
            // 「VR Manager has not been created yet!」，主控台被洗爆而且只能重開遊戲。
            // VrLocomotion 那邊也擋了一層，這裡再擋一次是為了連反射的成本都省掉。
            if (!VrScreen.VrRunning())
            {
                VrLink.TakeGoto();     // 還是要把佇列吃掉，不然回到 VR 時會補套一個舊的
                return;
            }

            if (Time.realtimeSinceStartup >= nextPosePublish)
            {
                nextPosePublish = Time.realtimeSinceStartup + 0.2f;
                float[] p = VrLocomotion.PoseArray();
                if (p != null) VrLink.PublishPose(p);
            }

            float[] want = VrLink.TakeGoto();
            if (want != null)
            {
                VrLocomotion.ApplyPoseArray(want);
                Logger.LogInfo("[VrTools] " + VrLocomotion.LastReport);
            }
        }

        // ------------------------------------------------------------ VR 自動關物件

        /// <summary>
        /// 設置子視窗：設一次就不會再碰的東西。
        ///
        /// 這些原本擺在主面板上，占掉快一半高度 —— 在頭顯裡得一路捲下去才看得到手柄那區。
        /// 搬到這裡而不是只留在 .cfg：戴著頭顯沒辦法去開 BepInEx 的設定管理器。
        /// </summary>
        /// <summary>
        /// 手柄設置：所有的按鍵綁定和速度。
        ///
        /// 為什麼另外開一個視窗，而不是塞回主面板：
        /// 這些東西設一次就不會再碰，卻占掉主面板一大半高度，
        /// 在頭顯裡還得一路捲下去才看得到真正常用的那幾顆。
        ///
        /// 為什麼不是只丟進 .cfg：
        /// 「左 Y 到底對應哪個 OpenVR 編號」這種事只能邊按邊試，
        /// 而 BepInEx 的設定管理器改完還要切回來 —— 直接在這裡點比較快。
        /// 這個視窗是普通的 IMGUI 視窗，桌面模式一樣打得開、一樣能改。
        /// </summary>
        void DrawCtrlSettings(int id)
        {
            ctrlScroll = GUILayout.BeginScrollView(ctrlScroll);

            // ---- 按鍵綁定 ----
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("<b>按鍵綁定</b>"), rich);
            GUILayout.FlexibleSpace();
            // 右上角：兩種設定法切換。改的是同一份設定，只是編輯方式不同。
            if (GUILayout.Button(cfgBindUiNew.Value
                                 ? Lang.T("設定法：按一下直接按　▾")
                                 : Lang.T("設定法：舊的下拉式　▾"),
                                 Fit.WB(190, cfgBindUiNew.Value ? Lang.T("設定法：按一下直接按　▾") : Lang.T("設定法：舊的下拉式　▾")), GUILayout.Height(22)))
            {
                cfgBindUiNew.Value = !cfgBindUiNew.Value;
                VrCapture.Cancel();
            }
            GUILayout.EndHorizontal();

            if (cfgBindUiNew.Value) DrawBindsNew();
            else DrawBindsOld();

            if (!string.IsNullOrEmpty(bindWarning))
                GUILayout.Label("<color=#e67e22>" + bindWarning + "</color>", rich);

            GUILayout.Space(6f);
            GUILayout.Label(Lang.T("<b>播放控制</b>"), rich);
            cfgCutKeyEnable.Value = GUILayout.Toggle(cfgCutKeyEnable.Value,
                Lang.T(" 啟用（關掉的話上面那幾個遙控 F7 的綁定都不作用）"));

            Slider("快轉一次幾秒", cfgSeekStep, 0.2f, 10f, 0.1f, "F1", " s");
            Slider("連發前等多久", cfgSeekDelay, 0.1f, 1.5f, 0.05f, "F2", " s");
            Slider("連發間隔", cfgSeekRepeat, 0.05f, 0.6f, 0.01f, "F2", " s");
            Slider("重播／停止要推滿幾秒", cfgTransportHold, 0.3f, 3f, 0.1f, "F1", " s");
            Slider("搖桿門檻", cfgTransportThreshold, 0.3f, 0.95f, 0.05f, "F2", "");

            GUILayout.Space(6f);
            GUILayout.Label(Lang.T("<b>移動與轉向</b>"), rich);
            Slider("左手移動速度", cfgMoveSpeed, 0.1f, 4f, 0.1f, "F1", " m/s");
            Slider("右手移動速度", cfgMoveSpeedR, 0.1f, 4f, 0.1f, "F1", " m/s");
            Slider("左右轉速度", cfgTurnSpeed, 10f, 180f, 5f, "F0", "°/s");
            Slider("繞轉速度", cfgOrbitSpeed, 10f, 180f, 5f, "F0", "°/s");
            Slider("上下轉速度", cfgPitchSpeed, 5f, 120f, 5f, "F0", "°/s");
            Slider("搖桿死區", cfgDeadZone, 0.02f, 0.6f, 0.02f, "F2", "");

            GUILayout.BeginHorizontal();
            cfgCtrlLeftHandMoves.Value = GUILayout.Toggle(cfgCtrlLeftHandMoves.Value,
                                                          Lang.T(" 左搖桿可移動"), Fit.WT(110, Lang.T(" 左搖桿可移動")));
            cfgCtrlRightHandMoves.Value = GUILayout.Toggle(cfgCtrlRightHandMoves.Value,
                                                           Lang.T(" 右搖桿可移動"), Fit.WT(110, Lang.T(" 右搖桿可移動")));
            cfgCtrlPitch.Value = GUILayout.Toggle(cfgCtrlPitch.Value, Lang.T(" 允許上下轉"), Fit.WT(104, Lang.T(" 允許上下轉")));
            cfgCtrlVertical.Value = GUILayout.Toggle(cfgCtrlVertical.Value,
                                                     Lang.T(" 握把＋X/Y＝上下平移"), Fit.WT(166, Lang.T(" 握把＋X/Y＝上下平移")));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(Lang.T("<b>扳機＋搖桿＝繞轉</b>"), rich);
            string[] axes = { "上下軸（水平繞圈）", "左右軸（上下翻）", "前後軸（左右傾）" };
            OrbitRow("搖桿上下", cfgOrbitAxisUD, cfgOrbitHeadUD, axes);
            OrbitRow("搖桿左右", cfgOrbitAxisLR, cfgOrbitHeadLR, axes);

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            cfgSpotToJson.Value = GUILayout.Toggle(cfgSpotToJson.Value,
                                                   Lang.T(" 記住視角時一併寫進 F7 的設定檔"), Fit.WT(360, Lang.T(" 記住視角時一併寫進 F7 的設定檔")));
            GUILayout.EndHorizontal();

            // 「左 Y 對應到哪個 OpenVR 按鍵」各家 runtime 不保證一樣，
            // 戴著頭顯又沒辦法邊看 log 邊試。按一下看這一行就知道該綁哪個。
            // 主面板上那一份已經移除，只留在這裡 —— 綁按鍵的時候才需要，
            // 而且它每次都要重掃裝置，不該每幀跑在主面板上。
            GUILayout.Space(4f);
            Status(VrInput.Diagnose());

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            DrawResetButton();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("關閉"), Fit.WB(90, Lang.T("關閉")), GUILayout.Height(22)))
                showCtrlSettings = false;
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        // ------------------------------------------------------------ 綁定的兩種設定法

        BindRow capturing;      // 正在等這一列的輸入

        /// <summary>
        /// 新的設定法：按「設定」，然後直接在頭顯裡按你要的那一組。
        ///
        /// 跟桌面版那個 Press any key 是同一個概念，差別在於手柄沒有事件可讀，
        /// 只能每幀輪詢（見 VrCapture）。所以這裡除了畫，還要負責每幀叫 Tick ——
        /// 不然面板打開著但沒人在讀輸入，按了完全沒反應。
        /// </summary>
        void DrawBindsNew()
        {
            GUILayout.Label(Lang.T(
                "<color=#95a5a6>按「設定」之後直接在頭顯裡按你要的組合"
                + "（握把、扳機可以當修飾鍵一起按住；搖桿推到底也算一種）。"
                + "全部放開就記起來。</color>"), rich);

            for (int i = 0; i < binds.Length; i++) DrawBindRow(binds[i]);

            if (VrCapture.Active)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("<color=#f1c40f><b>請按下你要的組合…</b></color>　")
                                + (string.IsNullOrEmpty(VrCapture.Preview)
                                   ? Lang.T("<color=#95a5a6>（還沒讀到輸入）</color>")
                                   : VrCapture.Preview), rich);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Lang.T("取消"), Fit.WB(70, Lang.T("取消")), GUILayout.Height(22)))
                { VrCapture.Cancel(); capturing = null; }
                GUILayout.EndHorizontal();
            }
        }

        void DrawBindRow(BindRow r)
        {
            bool mine = VrCapture.Active && capturing == r;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T(r.Label), Fit.WL(150, Lang.T(r.Label)));
            GUILayout.Label(mine ? Lang.T("<color=#f1c40f>等你按…</color>")
                                 : r.Bind.Display(), rich, Fit.W(220, mine ? Lang.T("<color=#f1c40f>等你按…</color>") : r.Bind.Display(), rich));
            if (GUILayout.Button(mine ? Lang.T("取消") : Lang.T("設定"),
                                 Fit.WB(60, mine ? Lang.T("取消") : Lang.T("設定")), GUILayout.Height(20)))
            {
                if (mine) { VrCapture.Cancel(); capturing = null; }
                else { capturing = r; VrCapture.Begin(r.Label); }
            }
            if (GUILayout.Button(Lang.T("清除"), Fit.WB(54, Lang.T("清除")), GUILayout.Height(20)))
            {
                r.Bind = new VrBind();
                r.Commit();
            }
            if (GUILayout.Button(Lang.T("預設"), Fit.WB(54, Lang.T("預設")), GUILayout.Height(20)))
            {
                r.Entry.Value = (string)r.Entry.DefaultValue;
                r.Bind = VrBind.Parse(r.Entry.Value);
            }
            if (!string.IsNullOrEmpty(r.Note))
                GUILayout.Label("<color=#95a5a6>" + Lang.T(r.Note) + "</color>", rich);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 舊的設定法：左右手一排鈕、主鍵一排鈕、修飾鍵兩個勾。
        ///
        /// 留著是因為有些情況按不出來 —— 例如想綁「System」，
        /// 或手柄正好連不上但想先把設定改好。改的是同一份綁定，切回去就看得到。
        /// </summary>
        void DrawBindsOld()
        {
            int[] btnIds = { VrInput.BtnAppMenu, VrInput.BtnA, VrInput.BtnStick,
                             VrInput.BtnTrigger, VrInput.BtnGrip };
            string[] btnNames = { "Y/B", "X/A", "搖桿", "扳機", "握把" };
            int[] dirIds = { VrBind.DirRight, VrBind.DirLeft, VrBind.DirUp, VrBind.DirDown };
            string[] dirNames = { "桿右", "桿左", "桿上", "桿下" };
            string[] hands = { "左手", "右手" };

            for (int i = 0; i < binds.Length; i++)
            {
                BindRow r = binds[i];
                VrBind b = r.Bind;
                bool dirty = false;

                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T(r.Label), Fit.WL(150, Lang.T(r.Label)));

                for (int h = 0; h < 2; h++)
                {
                    bool on = b.Left == (h == 0);
                    if (GUILayout.Toggle(on, " " + Lang.T(hands[h]), "Button", Fit.W(50, " " + Lang.T(hands[h]), "Button"))
                        && !on) { b.Left = h == 0; dirty = true; }
                }
                GUILayout.Space(4f);

                bool g = GUILayout.Toggle(b.Grip, Lang.T(" 握把"), "Button", Fit.W(52, Lang.T(" 握把"), "Button"));
                if (g != b.Grip) { b.Grip = g; dirty = true; }
                bool t = GUILayout.Toggle(b.Trigger, Lang.T(" 扳機"), "Button", Fit.W(52, Lang.T(" 扳機"), "Button"));
                if (t != b.Trigger) { b.Trigger = t; dirty = true; }
                GUILayout.Label("＋", GUILayout.Width(18));

                for (int k = 0; k < btnIds.Length; k++)
                {
                    bool on = !b.IsDir && b.Button == btnIds[k];
                    if (GUILayout.Toggle(on, " " + Lang.T(btnNames[k]), "Button", Fit.W(48, " " + Lang.T(btnNames[k]), "Button"))
                        && !on) { b.Button = btnIds[k]; b.Dir = VrBind.DirNone; dirty = true; }
                }
                for (int k = 0; k < dirIds.Length; k++)
                {
                    bool on = b.Dir == dirIds[k];
                    if (GUILayout.Toggle(on, " " + Lang.T(dirNames[k]), "Button", Fit.W(48, " " + Lang.T(dirNames[k]), "Button"))
                        && !on) { b.Dir = dirIds[k]; b.Button = -1; dirty = true; }
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                if (dirty) r.Commit();
            }
        }

        /// <summary>
        /// 每幀讀一次手柄，看使用者有沒有按完。
        ///
        /// 放在 Update 而不是 OnGUI：OnGUI 一幀會跑好幾次（Layout / Repaint），
        /// 在那裡輪詢會把同一次按下算成好幾次，而且 Repaint 之外的幾次讀到的還是同一個狀態。
        /// </summary>
        void TickCapture()
        {
            if (!VrCapture.Active) return;

            // 沒有手柄就別讓它一直等（桌面上開著這個面板很容易忘記）
            if (Input.GetKeyDown(KeyCode.Escape)) { VrCapture.Cancel(); capturing = null; return; }

            VrBind got = VrCapture.Tick();
            if (got == null) return;

            BindRow r = capturing;
            capturing = null;
            if (r == null) return;

            r.Bind = got;
            r.Commit();
            Logger.LogInfo("[VrTools] " + r.Label + " → " + got.Display() + "（" + got + "）");

            // 撞到別人就講一聲。不自動改掉 —— 有些人就是要同一組做兩件事，
            // 但多半是設錯了，而在頭顯裡「按了沒反應」最難查。
            for (int i = 0; i < binds.Length; i++)
            {
                if (binds[i] == r) continue;
                if (binds[i].Bind.ToString() != got.ToString()) continue;
                Logger.LogWarning("[VrTools] 注意：「" + r.Label + "」跟「" + binds[i].Label
                                  + "」綁到同一組（" + got.Display() + "），兩個都會觸發");
                bindWarning = Lang.T("⚠ 跟「") + Lang.T(binds[i].Label) + Lang.T("」綁到同一組了");
                return;
            }
            bindWarning = "";
        }

        string bindWarning = "";

        void OrbitRow(string label, ConfigEntry<int> axis, ConfigEntry<bool> atHead, string[] axes)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T(label), Fit.WL(120, Lang.T(label)));
            for (int i = 0; i < 3; i++)
            {
                bool on = axis.Value == i;
                if (GUILayout.Toggle(on, " " + Lang.T(axes[i]), "Button", Fit.W(136, " " + Lang.T(axes[i]), "Button")) && !on)
                    axis.Value = i;
            }
            GUILayout.Space(6f);
            atHead.Value = GUILayout.Toggle(atHead.Value, Lang.T(" 支點在眼睛"), Fit.WT(104, Lang.T(" 支點在眼睛")));
            GUILayout.EndHorizontal();
        }

        /// <summary>一列「標籤 ＋ 數值 ＋ 滑桿」，數值照 step 對齊。</summary>
        void Slider(string label, ConfigEntry<float> cfg, float min, float max,
                    float step, string fmt, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T(label), Fit.WL(200, Lang.T(label)));
            GUILayout.Label(cfg.Value.ToString(fmt) + unit, GUILayout.Width(72));
            float v = GUILayout.HorizontalSlider(cfg.Value, min, max, GUILayout.Width(240));
            cfg.Value = Mathf.Round(v / step) * step;
            GUILayout.EndHorizontal();
        }

        void DrawSettings(int id)
        {
            settingsScroll = GUILayout.BeginScrollView(settingsScroll);

            GUILayout.Label(Lang.T("<b>頭顯裡的過場畫面</b>"), rich);

            GUILayout.BeginHorizontal();
            string[] modes = { "自動偵測", "一律使用", "一律不用" };
            for (int m = 0; m < 3; m++)
            {
                bool on = cfgVrMode.Value == m;
                if (GUILayout.Toggle(on, " " + Lang.T(modes[m]), "Button", Fit.W(84, " " + Lang.T(modes[m]), "Button")) && !on)
                    cfgVrMode.Value = m;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            cfgKeepDesktop.Value = GUILayout.Toggle(cfgKeepDesktop.Value, Lang.T(" 桌面照樣畫"), Fit.WT(100, Lang.T(" 桌面照樣畫")));
            cfgFollow.Value = GUILayout.Toggle(cfgFollow.Value, Lang.T(" 跟著頭轉"), Fit.WT(92, Lang.T(" 跟著頭轉")));
            cfgFlipY.Value = GUILayout.Toggle(cfgFlipY.Value, Lang.T(" 上下翻轉"), Fit.WT(92, Lang.T(" 上下翻轉")));
            cfgSolo.Value = GUILayout.Toggle(cfgSolo.Value, Lang.T(" 播放時只畫影片"), Fit.WT(130, Lang.T(" 播放時只畫影片")));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            cfgKillPostFx.Value = GUILayout.Toggle(cfgKillPostFx.Value,
                                                   Lang.T(" 播放時關掉後製特效"), Fit.WT(160, Lang.T(" 播放時關掉後製特效")));
            GUILayout.Label(Lang.T("泛光／色彩校正是在 cullingMask 之後才跑的，偏紅泛光就是它"), small);
            GUILayout.EndHorizontal();

            GUILayout.Label(Lang.T("距離 ") + cfgDistance.Value.ToString("F1") + " m"
                            + Lang.T("　　寬 ") + cfgWidth.Value.ToString("F1") + " m"
                            + Lang.T("　　≈ ") + Fov().ToString("F0") + Lang.T("° 視角"), small);
            cfgDistance.Value = GUILayout.HorizontalSlider(cfgDistance.Value, 0.5f, 8f);
            cfgWidth.Value = GUILayout.HorizontalSlider(cfgWidth.Value, 0.5f, 20f);

            Status(vr.Status);
            Status(Lang.T("過場來源：") + Lang.T(CutSceneBridge.LastReport));

            GUILayout.Space(8f);
            GUILayout.Label(Lang.T("<b>回到相機視角</b>"), rich);

            GUILayout.BeginHorizontal();
            cfgRealignHold.Value = GUILayout.Toggle(cfgRealignHold.Value, Lang.T(" 按著＝鎖定"), Fit.WT(110, Lang.T(" 按著＝鎖定")));
            cfgAlsoRealign.Value = GUILayout.Toggle(cfgAlsoRealign.Value,
                                                    Lang.T(" 同時叫 CameraSync"), Fit.WT(150, Lang.T(" 同時叫 CameraSync")));
            GUILayout.Label(Lang.T("熱鍵 ") + cfgRealignKey.Value, small);
            GUILayout.EndHorizontal();
            Status(Lang.T(CameraSyncBridge.Available() ? "CameraSync 已連上" : "沒有 CameraSync"));

            // 「顯示工具列按鈕」原本在這裡，已經移到主視窗「設置」按鈕的左邊 ——
            // 那是個常按的開關，不該藏在還要先開一層的子視窗裡。

            GUILayout.Space(8f);
            if (Fit.ScaleRow()) cfgUiScale.Value = Fit.Scale;
            GUILayout.BeginHorizontal();
            DrawResetButton();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("關閉"), Fit.WB(90, Lang.T("關閉")), GUILayout.Height(22)))
                showSettings = false;
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        /// <summary>板子的水平視角：看得出來螢幕在眼前佔多大，比公尺數直覺。</summary>
        float Fov()
        {
            return 2f * Mathf.Atan(cfgWidth.Value * 0.5f / Mathf.Max(0.3f, cfgDistance.Value))
                   * Mathf.Rad2Deg;
        }

        string fxAddReport = "";
        bool fxVrForced;

        void TickFxMute()
        {
            VrFxMute.Enabled = cfgFxMute.Value;
            VrFxMute.OnlyInVr = cfgFxMuteOnlyVr.Value;
            VrFxMute.ListText = cfgFxMuteList.Value;
            VrFxMute.SettleSeconds = cfgFxMuteSettle.Value;

            // (FX) 開關：進 VR 自動取消勾選，離開自動勾回來。
            // 只在「跨過那個界線的那一下」動它 —— 每幀強制的話你就永遠改不了，
            // 而且面板上的勾會跟你的點擊打架。
            bool fxShouldHide = cfgFxFolderVrOff.Value && cfgFxMute.Value && VrScreen.VrRunning();
            if (fxShouldHide != fxVrForced)
            {
                fxVrForced = fxShouldHide;
                cfgFxFolder.Value = !fxShouldHide;
            }
            VrFxMute.FxFolderOn = cfgFxFolder.Value;

            // 用 VrScreen.VrRunning() 而不是 vr.Active：
            // vr.Active 是「過場板子正在畫」，沒在播過場的時候是 false ——
            // 但物件該不該關跟有沒有在播過場無關，只跟現在是不是 VR 有關。
            VrFxMute.Tick(VrScreen.VrRunning());
        }

        // ------------------------------------------------------------ 過場畫面

        // 這裡原本有「測試圖案」：不用播過場也能在板子上貼一張程式生成的粉紫白格子，
        // 用來把「板子畫不出來」的兩類原因（幾何 vs 著色器）分開。
        // 那兩個問題都已經定位並修好了（頂點色、isSupported），格子圖沒有再用到的場合，
        // 留著只是面板上多一個沒人按的開關。連同旁邊的「著色器」輸入框、
        // 「列出可用的」和「相機」傾印按鈕一起移除 —— 那整區都是那次查錯留下的工具。

        void TickScreen()
        {
            try
            {
                VrScreen.Solo = cfgSolo.Value;
                VrScreen.KillPostFx = cfgKillPostFx.Value;
    
                Texture tex;
                float alpha, aspect;
                bool has = CutSceneBridge.Read(out tex, out alpha, out aspect);

                vr.Tick(has, alpha, tex, aspect,
                        cfgVrMode.Value, cfgDistance.Value, cfgWidth.Value,
                        cfgFollow.Value, cfgFlipY.Value);

                // 我們真的畫出來了，才請 CutScene 省略桌面那一份。
                // 沒畫（沒偵測到 VR、找不到相機、沒在播）就一定要讓桌面畫，
                // 否則使用者會變成兩邊都看不到 —— 那比多畫一次嚴重得多。
                CutSceneBridge.SetSuppressDesktop(vr.Active && has && !cfgKeepDesktop.Value);
            }
            catch (Exception e)
            {
                // VR 這條路壞掉不能拖垮過場本身
                vr.Status = Lang.T("VR 畫面失敗：") + e.GetType().Name + " " + e.Message;
                vr.Active = false;
                CutSceneBridge.SetSuppressDesktop(false);
            }
        }

        // ------------------------------------------------------------ 介面黑底

        void TickUiBacking()
        {
            VrUiBacking.Enabled = cfgUiBacking.Value && vr.Active;
            VrUiBacking.Opacity = cfgUiOpacity.Value;
            VrUiBacking.MountToHand = cfgMountUi.Value && !cfgHideUi.Value && vr.Active;
            VrUiBacking.HideMain = cfgHideUi.Value && vr.Active;
            VrUiBacking.MountLeft = cfgMountLeft.Value;
            VrUiBacking.MountDistance = cfgMountDist.Value;
            VrUiBacking.MountScale = cfgMountScale.Value;
            VrUiBacking.MountTilt = cfgMountTilt.Value;
            VrUiBacking.MountLift = cfgMountLift.Value;
            VrUiBacking.MountFlip = cfgMountFlip.Value;

            // 黏在手上的時候底板也要跟著動，所以就算不加黑底也得跑這一趟
            if (VrUiBacking.Enabled || VrUiBacking.MountToHand || VrUiBacking.HideMain)
                VrUiBacking.Tick();
            else VrUiBacking.HideAll();

            TickVrLog();
        }

        float nextVrLog;

        /// <summary>
        /// 每 3 秒把 VR 這邊的狀態記一行。
        ///
        /// 為什麼要有：戴著頭顯的時候看不到面板，出了問題只能事後看 log。
        /// 前幾輪「搖桿沒反應」查了三次都只能靠猜，就是因為 log 裡一行相關的都沒有 ——
        /// 「讀不到手柄」和「讀到了但按鍵編號不對」這兩種完全不同的失敗，
        /// 從外面看一模一樣。這幾行就是用來把它們分開的。
        /// </summary>
        void TickVrLog()
        {
            if (!cfgVrLog.Value || !vr.Active) return;
            if (Time.realtimeSinceStartup < nextVrLog) return;
            nextVrLog = Time.realtimeSinceStartup + 3f;

            Logger.LogInfo("[VR] 手柄：" + VrInput.LastReport
);
            Logger.LogInfo("[VR] 介面：" + VrUiBacking.LastReport
                           + "　｜ 偏離相機 " + VrLocomotion.OffsetDistance.ToString("F2") + " m"
                           + "　｜ " + VrLocomotion.LastReport);
            // 過場畫面那一區搬離面板之後，這是唯一看得到它狀態的地方了
            Logger.LogInfo("[VR] 過場：" + vr.Status + "　｜ 來源：" + CutSceneBridge.LastReport);
            if (VrLocomotion.LastMove.Length > 0)
                Logger.LogInfo("[VR] 平移：" + VrLocomotion.LastMove);
        }

        // ------------------------------------------------------------ 回到相機視角

        /// <summary>
        /// 按著不放的時候每 0.1 秒送一次而不是每幀送：CameraSync 是在 LateUpdate
        /// 裡看旗標做事的，每幀重設旗標對它的狀態機沒有好處，只會讓它的 log 洗版。
        /// 0.1 秒對「鎖定」的手感來說已經夠密了。
        /// </summary>
        void TickRealign()
        {
            if (cfgRealignKey == null) return;
            KeyboardShortcut k = cfgRealignKey.Value;
            if (k.MainKey == KeyCode.None) return;

            if (k.IsDown()) { DoReset("鍵盤", true); return; }
            if (cfgRealignHold.Value && k.IsPressed()
                && Time.realtimeSinceStartup >= nextRealign)
                DoReset("鍵盤按住", false);   // 按住期間不記 log，不然 log 會被洗掉
        }

        /// <summary>
        /// 回到「綁在相機上」的視角。
        ///
        /// 主動作是把自己用搖桿移動出去的量原樣退回去 —— 退完剩下的就是
        /// CameraSync 這一刻放我們的位置，相機正在運鏡也成立。
        /// 叫 CameraSync 的 RequestInitialAlignment 是**額外**的（預設關），
        /// 因為那會跳到場景載入當下的相機姿勢，不是現在的。
        /// </summary>
        void DoReset(string why, bool log)
        {
            VrLocomotion.ResetToCamera();
            if (cfgAlsoRealign.Value) CameraSyncBridge.Realign(why);
            nextRealign = Time.realtimeSinceStartup + 0.1f;
            if (log)
                Logger.LogInfo("[VrTools] 重置（" + why + "）："
                               + VrLocomotion.LastReport
                               + (cfgAlsoRealign.Value ? "　｜ " + CameraSyncBridge.LastReport : ""));
        }

        // ------------------------------------------------------------ 手柄

        float nextCtrlRealign;

        /// <summary>
        /// 搖桿移動 / 轉向 / 回到相機視角。
        ///
        /// 為什麼移動和轉向共用同一根搖桿、用握把分流
        /// ----------------------------------------
        /// 這是你指定的配置，而且在只有兩根搖桿、又不想跟 Ermin 那支插件
        /// 已經占用的鍵打架的前提下，本來就比「左搖桿走、右搖桿轉」安全 ——
        /// 右搖桿在它的 GripMove 工具裡是有功能的。
        ///
        /// 兩隻手都讀、取推得比較多的那一隻：這樣不用去記是哪一手，
        /// 也不會因為兩手同時推而互相抵消。
        /// </summary>
        // ------------------------------------------------------------ 手柄綁定表

        /// <summary>面板上的一列：功能名稱、存在 .cfg 裡的那一格、跑起來用的那一份。</summary>
        sealed class BindRow
        {
            public string Label;
            public ConfigEntry<string> Entry;
            public VrBind Bind;
            public string Note;

            public void Commit()
            {
                Entry.Value = Bind.ToString();
            }
        }

        ConfigEntry<int> cfgBindVersion;
        ConfigEntry<bool> cfgBindUiNew;

        BindRow[] binds;
        BindRow[] transportBinds;
        BindRow bRealign, bMount, bSaveView, bGoView, bPause, bPrev, bNext,
                bFwd, bRew, bReplay, bStop;

        const string BIND_HELP =
            "格式：<手>:<修飾鍵>+<主鍵>。手＝L 或 R；修飾鍵只有 Grip、Trigger；"
            + "主鍵＝AppMenu(Y/B)、A(X/A)、Stick(搖桿按下)、Trigger、Grip，"
            + "或 Stick>Right / Stick>Left / Stick>Up / Stick>Down（搖桿推的方向）。"
            + "「-」＝不綁。\n"
            + "修飾鍵是**剛好比對**：沒寫 Grip 的綁定，握把按著時就不會觸發。";

        ConfigEntry<string> BindCfg(string key, string def, string extra)
        {
            return Config.Bind("Controller", key, def,
                               (string.IsNullOrEmpty(extra) ? "" : extra + "\n") + BIND_HELP);
        }

        void SetupBinds()
        {
            cfgBindUiNew = Config.Bind("Controller", "Use Press-Any-Key UI", true,
                "手柄設定裡的按鍵綁定介面。\n"
                + "開＝按一下「設定」然後直接在頭顯裡按你要的組合（新的，預設）\n"
                + "關＝舊的下拉式：左右手一顆鈕、按鍵一顆鈕，修飾鍵固定寫死\n"
                + "面板右上角可以隨時切換，兩邊改的是同一份設定");
            cfgBindVersion = Config.Bind("Controller", "Binding Schema Version", 0,
                "內部用。0 = 還沒從舊的「哪隻手 + 哪個鍵」搬過來。不要手動改");

            bRealign = new BindRow { Label = "回到相機視角",
                Entry = BindCfg("Bind Realign", "L:Stick", "把 VR 視角拉回工作室相機") };
            bMount = new BindRow { Label = "主介面抓手",
                Entry = BindCfg("Bind Mount Toggle", "L:AppMenu",
                                "主介面：原處 → 黏在手上 → 隱藏 → 原處") };
            bSaveView = new BindRow { Label = "記住視角",
                Entry = BindCfg("Bind Save View", "L:Trigger+A", "") };
            bGoView = new BindRow { Label = "回到記住的視角",
                Entry = BindCfg("Bind Go To View", "L:Trigger+AppMenu", "") };

            // 以下是「遙控 F7」那一組。預設全部掛在 握把＋扳機 這個修飾鍵底下，
            // 所以按著這兩顆的時候整隻手就是遙控器，放開就回去當移動用的手。
            bPause = new BindRow { Label = "暫停／播放",
                Entry = BindCfg("Bind Pause", "L:Grip+Trigger+Stick",
                                "過場正在播的話是「跳過這一段」"), Note = "過場中＝跳過這一段" };
            bPrev = new BindRow { Label = "上一個場景",
                Entry = BindCfg("Bind Prev Scene", "L:Grip+Trigger+AppMenu", "") };
            bNext = new BindRow { Label = "下一個場景",
                Entry = BindCfg("Bind Next Scene", "L:Grip+Trigger+A", "") };
            bRew = new BindRow { Label = "倒轉",
                Entry = BindCfg("Bind Rewind", "L:Grip+Trigger+Stick>Left",
                                "推著不放會連發"), Note = "推著不放會連發" };
            bFwd = new BindRow { Label = "快轉",
                Entry = BindCfg("Bind Forward", "L:Grip+Trigger+Stick>Right",
                                "推著不放會連發"), Note = "推著不放會連發" };
            bReplay = new BindRow { Label = "重播",
                Entry = BindCfg("Bind Replay", "L:Grip+Trigger+Stick>Up",
                                "要推滿並按住，見「重播／停止要推滿幾秒」"), Note = "要按住" };
            bStop = new BindRow { Label = "停止",
                Entry = BindCfg("Bind Stop", "L:Grip+Trigger+Stick>Down",
                                "要推滿並按住，見「重播／停止要推滿幾秒」"), Note = "要按住" };

            binds = new[] { bRealign, bMount, bSaveView, bGoView,
                            bPause, bPrev, bNext, bRew, bFwd, bReplay, bStop };
            // 遙控 F7 的那一組。單獨列一份是為了判斷「這隻手這一幀是不是遙控器」——
            // 用索引去切 binds 的話，以後插一列進去就會默默切錯。
            transportBinds = new[] { bPause, bPrev, bNext, bRew, bFwd, bReplay, bStop };

            // 舊設定搬過來。只搬「意思沒變」的那四個 ——
            // 暫停鍵從 Y 換成了搖桿按下（Y 和 X 讓給上一個／下一個場景），
            // 硬搬過來只會讓它跟新的場景切換撞在一起。
            if (cfgBindVersion.Value < 1)
            {
                bRealign.Entry.Value = Old(cfgResetHand.Value, false, false, cfgResetButton.Value);
                bMount.Entry.Value = Old(cfgMountHand.Value, false, false, cfgMountButton.Value);
                bSaveView.Entry.Value = Old(cfgSpotHand.Value, false, true, cfgSpotSaveButton.Value);
                bGoView.Entry.Value = Old(cfgSpotHand.Value, false, true, cfgSpotGoButton.Value);

                // 遙控那一組的「哪一隻手」沿用舊的設定，組合鍵維持握把＋扳機
                bool rl = cfgCutKeyHand.Value == 0;
                string h = rl ? "L:Grip+Trigger+" : "R:Grip+Trigger+";
                bPause.Entry.Value = h + "Stick";
                bPrev.Entry.Value = h + "AppMenu";
                bNext.Entry.Value = h + "A";
                bRew.Entry.Value = h + "Stick>Left";
                bFwd.Entry.Value = h + "Stick>Right";
                bReplay.Entry.Value = h + "Stick>Up";
                bStop.Entry.Value = h + "Stick>Down";

                cfgBindVersion.Value = 1;
                Logger.LogInfo("[VrTools] 手柄綁定已改用新格式，舊的「哪隻手 + 哪個鍵」已搬過來。"
                               + "暫停／播放改成握把＋扳機＋搖桿按下（Y 和 X 讓給上一個／下一個場景）");
            }

            ReloadBinds();
        }

        static string Old(int hand, bool grip, bool trigger, int button)
        {
            var b = new VrBind { Left = hand == 0, Grip = grip, Trigger = trigger, Button = button };
            return b.ToString();
        }

        /// <summary>把 .cfg 裡的字串讀成跑起來用的物件。改過設定之後要叫一次。</summary>
        void ReloadBinds()
        {
            for (int i = 0; i < binds.Length; i++)
                binds[i].Bind = VrBind.Parse(binds[i].Entry.Value);
        }

        void TickController()
        {
            if (cfgCtrlEnable == null || !cfgCtrlEnable.Value) return;

            // ---- 遙控 F7 的那一組 ----
            //
            // 預設全部掛在「握把＋扳機」這個修飾鍵底下，按住期間這隻手就是遙控器：
            //
            //   搖桿按下   暫停／播放（正在播過場的話是跳過這一段）
            //   Y          上一個場景　　X　下一個場景
            //   搖桿右／左 快轉／倒轉，推著不放會連發
            //   搖桿上／下 推滿並按住 → 重播／停止
            //
            // 上下為什麼要按住：重播和停止會把現在的播放狀態整個作廢，
            // 誤觸的代價比快轉大得多。左右是可逆的，所以不需要。
            //
            // 以上每一個都是可以改的綁定（見 SetupBinds），上面寫的只是預設值。
            // 按鍵之間的衝突不再靠這裡一個一個擋 —— 每個綁定自己帶修飾鍵，
            // 而 VrBind.ModsOk() 是**剛好比對**：沒寫握把的綁定，握把按著時就不成立。
            //
            // 這一段還是要放在最前面，因為 comboActive 決定了下面的移動與轉向
            // 這一幀要不要跳過這隻手 —— 不擋的話快轉的同時畫面在轉。

            // 正在設定按鍵 → 這一幀什麼都不做，不然設定的同時畫面會被移走、視角被重置。
            if (VrCapture.Active) { comboActive = false; return; }

            comboActive = false;
            if (cfgCutKeyEnable.Value)
            {
                // 哪一隻手在當遙控器：看綁定自己怎麼寫的。
                // 有任何一個遙控功能綁在「握把＋扳機」上，而那隻手正好按著這兩顆，
                // 那隻手這一幀就整個交給遙控器，移動與轉向停用。
                for (int h = 0; h < 2 && !comboActive; h++)
                {
                    bool left = h == 0;
                    if (!VrInput.Press(left, VrInput.BtnGrip) || !VrInput.TriggerHeld(left)) continue;
                    for (int i = 0; i < transportBinds.Length; i++)
                    {
                        VrBind b = transportBinds[i].Bind;
                        if (b.Left == left && b.Grip && b.Trigger)
                        { comboActive = true; comboHand = left; break; }
                    }
                }
                TickTransport();
            }

            // ---- 回到相機視角 ----
            //
            // 以前這裡有一行 resetBusy 在擋「這隻手正在當遙控器」。
            // 現在不需要了：綁定自己帶修飾鍵，而 ModsOk() 是**剛好比對** ——
            // 「搖桿按下」這個綁定沒寫握把，握把按著時它本來就不成立。
            if (bRealign.Bind.Down())
            {
                DoReset("手柄", true);
                nextCtrlRealign = Time.realtimeSinceStartup + 0.1f;
            }
            else if (cfgRealignHold.Value && bRealign.Bind.Held()
                     && Time.realtimeSinceStartup >= nextCtrlRealign)
            {
                // 按住＝鎖在相機上。跟鍵盤那條一樣每 0.1 秒送一次，不是每幀。
                DoReset("手柄按住", false);
                nextCtrlRealign = Time.realtimeSinceStartup + 0.1f;
            }

            // ---- 記住視角 / 回到記住的視角 ----
            if (bSaveView.Bind.Down())
            {
                VrLocomotion.SaveSpot();
                Logger.LogInfo("[VrTools] " + VrLocomotion.LastReport);
                // 順便請 F7 把這個點寫進這張卡的設定檔，下次載入同一張卡就不用再拉一次。
                // F7 沒裝、或這張卡沒有設定檔的話，這一步不會有任何效果，
                // 手柄上的「記住視角」照常運作。
                if (cfgSpotToJson.Value) VrLink.RequestSave();
            }
            if (bGoView.Bind.Down())
            {
                VrLocomotion.GoToSpot();
                Logger.LogInfo("[VrTools] " + VrLocomotion.LastReport);
            }

            // ---- 主介面：原處 → 黏在手上 → 隱藏 → 原處 ----
            //
            // 同一顆鍵循環三個狀態。兩個旗標（黏手、隱藏）共四種組合，
            // 但「又黏手又隱藏」沒有意義，所以循環時一律只讓其中一個是 true。
            if (bMount.Bind.Down())
            {
                if (cfgHideUi.Value)
                {
                    cfgHideUi.Value = false;
                    cfgMountUi.Value = false;
                }
                else if (cfgMountUi.Value)
                {
                    cfgMountUi.Value = false;
                    cfgHideUi.Value = true;
                }
                else
                {
                    cfgMountUi.Value = true;
                }
                Logger.LogInfo("[VrTools] 主介面 → "
                               + (cfgHideUi.Value ? "隱藏"
                                  : cfgMountUi.Value ? "抓到手上" : "放回原處"));
            }

            // ---- 移動 / 轉向 ----
            float dz = Mathf.Clamp(cfgDeadZone.Value, 0.02f, 0.8f);
            float dt = Time.unscaledDeltaTime;   // 過場暫停時間時仍然要能動

            for (int h = 0; h < 2; h++)
            {
                bool left = h == 0;
                if (left && !cfgCtrlLeftHandMoves.Value) continue;
                if (!left && !cfgCtrlRightHandMoves.Value) continue;

                // 這隻手正在當 F7 的遙控器（握把＋扳機按住）→ 移動整段跳過。
                // 不擋的話「握把＋搖桿＝轉向」會跟快轉同時作用，
                // 你想快轉，畫面卻一邊跳一邊轉。上下平移（握把＋X/Y）也一併擋掉，
                // 因為那時候 X 是「記住視角」，同一下做兩件事只會互相干擾。
                if (comboActive && left == comboHand) continue;

                bool grip = VrInput.Press(left, VrInput.BtnGrip);

                // 握把＋X/Y = 上下平移。
                // 這一段要放在死區判斷**之前** —— 它不需要搖桿，
                // 放在後面的話不推搖桿就永遠跑不到。
                if (cfgCtrlVertical.Value && grip)
                {
                    if (VrInput.Press(left, VrInput.BtnA))        // 左 X / 右 A
                        VrLocomotion.MoveVertical(-1f, Speed(left), dt);
                    if (VrInput.Press(left, VrInput.BtnAppMenu))  // 左 Y / 右 B
                        VrLocomotion.MoveVertical(1f, Speed(left), dt);
                }

                Vector2 ax = VrInput.Axis(left, VrInput.BtnStick);

                // 死區之後要重新拉伸回 0～1，否則一過死區就跳到 0.18 的速度，
                // 慢慢推的手感會整個不見
                float mag = ax.magnitude;
                if (mag < dz) continue;
                ax *= (mag - dz) / (1f - dz) / mag;

                if (grip)
                {
                    float yaw = ax.x * cfgTurnSpeed.Value * dt;
                    float pitch = cfgCtrlPitch.Value ? -ax.y * cfgPitchSpeed.Value * dt : 0f;
                    VrLocomotion.Rotate(yaw, pitch);
                }
                else if (TriggerHeld(left))
                {
                    // 扳機按住：搖桿的兩個方向各繞一根軸，支點都在 VR 原點
                    //（整顆頭沿圓周被帶著走），跟握把那個「支點在眼睛、原地轉身」不同。
                    VrLocomotion.Orbit(ax.y * cfgOrbitSpeed.Value * dt,
                                       cfgOrbitAxisUD.Value, cfgOrbitHeadUD.Value);
                    VrLocomotion.Orbit(ax.x * cfgOrbitSpeed.Value * dt,
                                       cfgOrbitAxisLR.Value, cfgOrbitHeadLR.Value);
                }
                else
                {
                    VrLocomotion.Move(ax, Speed(left), dt, false);
                }
            }
        }

        // ------------------------------------------------------------ 播放控制模式

        bool comboActive;        // 這一幀有哪一隻手在當遙控器
        bool comboHand;          // 是哪一隻手（true = 左）
        int seekHeldDir;         // 快轉／倒轉現在按著哪一邊（+1 / -1 / 0）
        float seekNext;          // 下一次連發的時間
        float replaySince = -1f, stopSince = -1f;
        bool replayFired, stopFired;

        /// <summary>
        /// 握把＋扳機按住時的播放控制。放開就整個歸零。
        ///
        /// 方向用「哪一軸比較大」判斷，不是用象限 —— 象限判斷在斜推時會左右上下亂跳，
        /// 而斜推是握著手柄很難避免的。取絕對值大的那一軸，斜一點也不會誤判。
        /// </summary>
        void TickTransport()
        {
            // ---- 一按就送的那幾個 ----
            if (bPause.Bind.Down())
            {
                VrLink.Send("pause-or-skip");
                Logger.LogInfo("[VrTools] 播放控制：暫停／播放");
            }
            if (bPrev.Bind.Down())
            {
                VrLink.Send("prev-scene");
                Logger.LogInfo("[VrTools] 播放控制：上一個場景");
            }
            if (bNext.Bind.Down())
            {
                VrLink.Send("next-scene");
                Logger.LogInfo("[VrTools] 播放控制：下一個場景");
            }

            float th = Mathf.Clamp(cfgTransportThreshold.Value, 0.3f, 0.95f);
            float now = Time.realtimeSinceStartup;

            // ---- 快轉／倒轉：按下去先跳一格，按著超過延遲才開始連發 ----
            //
            // 用綁定自己判斷方向，不再自己算象限 —— DirActive 會先確認修飾鍵剛好符合，
            // 再看搖桿推的是不是那一格。所以快轉綁右、倒轉綁左只是預設值，
            // 想綁成上下、或綁到另一隻手都可以。
            int dir = 0;
            if (bFwd.Bind.DirActive(th)) dir = +1;
            else if (bRew.Bind.DirActive(th)) dir = -1;

            if (dir != seekHeldDir)
            {
                seekHeldDir = dir;
                if (dir != 0)
                {
                    SendSeek(dir * cfgSeekStep.Value);
                    seekNext = now + Mathf.Max(0.05f, cfgSeekDelay.Value);
                }
            }
            else if (dir != 0 && now >= seekNext)
            {
                seekNext = now + Mathf.Max(0.02f, cfgSeekRepeat.Value);
                SendSeek(dir * cfgSeekStep.Value);
            }

            // ---- 重播／停止：要推滿並按住，而且只觸發一次 ----
            //
            // 這兩個會把現在的播放狀態整個作廢，誤觸的代價比快轉大得多，
            // 所以刻意要求按住。左右是可逆的，不需要。
            HoldAction(bReplay.Bind, th, now, ref replaySince, ref replayFired,
                       "replay", "重播");
            HoldAction(bStop.Bind, th, now, ref stopSince, ref stopFired,
                       "stop", "停止");
        }

        void HoldAction(VrBind b, float th, float now, ref float since, ref bool fired,
                        string cmd, string label)
        {
            bool on = b.IsDir ? b.DirActive(th) : b.Held();
            if (!on) { since = -1f; fired = false; return; }
            if (since < 0f) { since = now; fired = false; }
            if (fired) return;
            if (now - since < Mathf.Max(0.2f, cfgTransportHold.Value)) return;
            fired = true;
            VrLink.Send(cmd);
            Logger.LogInfo("[VrTools] 播放控制：" + label);
        }

        void SendSeek(float delta)
        {
            VrLink.Send("seek:" + delta.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 扳機有沒有按住。
        ///
        /// 兩條都看：GetPress(Axis1) 要推到「喀」一聲的死點才算，
        /// 有些 runtime 上 Quest 的扳機根本不回報那個 press。
        /// 所以另外讀扳機的類比值，過半就算 —— 兩條任一成立即可。
        /// </summary>
        /// <summary>左右手各自的移動速度。</summary>
        float Speed(bool left)
        {
            return left ? cfgMoveSpeed.Value : cfgMoveSpeedR.Value;
        }

        /// <summary>定義搬到 VrInput 了 —— 綁定和擷取也要用，三邊必須同一個定義。</summary>
        static bool TriggerHeld(bool left) { return VrInput.TriggerHeld(left); }

        // ------------------------------------------------------------ 面板

        GUISkin styleSkin;

        /// <summary>
        /// 狀態列。空字串就整行不畫 —— 這些 LastReport 在還沒跑過之前是空的，
        /// 以前填「（還沒用到）」當佔位字串，面板上就多出幾行沒有意義的字。
        /// </summary>
        void Status(string s)
        {
            if (!string.IsNullOrEmpty(s)) GUILayout.Label(Lang.T(s), small);
        }

        /// <summary>
        /// small / rich 是從 GUI.skin.label 複製出來的，所以**一定要在 VrSkin.Begin()
        /// 之後**才建。
        ///
        /// 原本建在 Begin 之前，於是這兩個樣式抄到的是 Unity 內建 skin 的 label ——
        /// 半透明底圖、偏灰的字。面板上大部分的說明文字都用 small，
        /// 結果就是「換了不透明外觀，字看起來還是灰的」。換皮沒問題，
        /// 是這兩個樣式從一開始就沒被換到。
        ///
        /// skin 換掉之後要重建，所以記住是照哪一份 skin 建的。
        /// </summary>

        // ------------------------------------------------------------ 語言與重置

        ConfigEntry<int> cfgLang;
        ConfigEntry<float> cfgUiScale;
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
            DrawLangButton();
            GUILayout.FlexibleSpace();
            DrawResetButton();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 語言按鈕。F9 把它放在**主面板**最左下，不是子視窗裡 ——
        /// 換語言是「第一次裝好時做一次」的事，不該還要先開一層設置才找得到。
        /// </summary>
        void DrawLangButton()
        {
            if (GUILayout.Button(Lang.ButtonLabel, GUILayout.Width(170), GUILayout.Height(22)))
            {
                cfgLang.Value = Lang.Next(Lang.Current);
                Lang.Set(cfgLang.Value);
            }
        }

        void DrawResetButton()
        {
            bool armed = Time.realtimeSinceStartup < resetArmedUntil;
            if (GUILayout.Button(armed ? Lang.T("再按一次確認") : Lang.T("重置為預設"),
                                 Fit.WB(armed ? 170f : 130f, armed ? Lang.T("再按一次確認") : Lang.T("重置為預設")), GUILayout.Height(22)))
            {
                if (armed) { resetArmedUntil = 0f; ResetConfigToDefaults(); }
                else resetArmedUntil = Time.realtimeSinceStartup + 3f;
            }
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

        void EnsureStyles()
        {
            if (small != null && rich != null && styleSkin == GUI.skin) return;
            styleSkin = GUI.skin;

            // 這裡原本有 fontSize - 2。字級調整一律拿掉 ——
            // 在頭顯裡本來就已經是最難讀的那一塊，再縮只會更糟。
            small = new GUIStyle(GUI.skin.label);
            small.wordWrap = true;

            rich = new GUIStyle(GUI.skin.label);
            rich.richText = true;
            rich.wordWrap = true;
        }

        int fitLang = -1;

        void OnGUI()
        {
            if (!show) return;
            VrSkin.Publish(cfgVrSkin.Value, cfgVrSkinOnlyVr.Value);
            GUISkin savedSkin = VrSkin.Begin();
            Matrix4x4 savedMatrix = Fit.BeginScale();     // 介面縮放
            bool xua = Xua.Begin();                       // 視窗標題不要被 AutoTranslator 再翻一次
            EnsureStyles();
            // 語言換了：寬度重設成這個語言的底寬（視窗會被內容撐大但不會自己縮回來）。
            if (Fit.LanguageChanged(ref fitLang))
            {
                win.width = 560f * Fit.Wide;
                settingsWin.width = 470f * Fit.Wide;
                ctrlWin.width = 760f * Fit.Wide;
            }
            try
            {
                win = GUILayout.Window(0x56520001, win, Xua.Wrap(DrawWindow),
                                       NAME + " " + VERSION + Lang.T("　(") + cfgPanelKey.Value + ")");
                if (showSettings)
                    settingsWin = GUILayout.Window(0x56520002, settingsWin, Xua.Wrap(DrawSettings), Lang.T("設置"));
                if (showCtrlSettings)
                    ctrlWin = GUILayout.Window(0x56520003, ctrlWin, Xua.Wrap(DrawCtrlSettings), Lang.T("手柄設置"));
            }
            finally
            {
                Xua.End(xua);
                Fit.EndScale(savedMatrix);
                VrSkin.End(savedSkin);
            }
        }

        /// <summary>
        /// 面板刻意只留「會臨時改的東西」和狀態。
        ///
        /// 過場畫面（模式、距離、寬度、跟著頭轉…）和回到相機視角的那幾個開關
        /// 都搬到設定檔了 —— 那些設一次就不會再碰，卻占掉面板一半高度，
        /// 在頭顯裡還得一路捲下去才看得到手柄那區。
        /// 用 BepInEx 的設定管理器（或直接改 .cfg）調整。
        /// </summary>
        void DrawWindow(int id)
        {
            GUILayout.Label(Lang.T("<b>VR 自動關物件</b>"), rich);

            GUILayout.BeginHorizontal();
            cfgFxMute.Value = GUILayout.Toggle(cfgFxMute.Value, Lang.T(" 啟用"), Fit.WT(70, Lang.T(" 啟用")));
            cfgFxMuteOnlyVr.Value = GUILayout.Toggle(cfgFxMuteOnlyVr.Value, Lang.T(" 只在 VR"), Fit.WT(84, Lang.T(" 只在 VR")));
            if (GUILayout.Button(Lang.T("現在就套用一次"), Fit.WB(118, Lang.T("現在就套用一次")))) VrFxMute.ForceNow();
            if (GUILayout.Button(Lang.T("還原成預設清單"), Fit.WB(118, Lang.T("還原成預設清單"))))
            {
                cfgFxMuteList.Value = VrFxMute.DefaultList;
                VrFxMute.ForceNow();
            }
            if (GUILayout.Button(Lang.T("把選取的加入清單"), Fit.WB(140, Lang.T("把選取的加入清單"))))
            {
                string rep;
                cfgFxMuteList.Value = VrFxMute.AddSelectedToList(cfgFxMuteList.Value, out rep);
                fxAddReport = rep;
                VrFxMute.ForceNow();
            }
            GUI.enabled = VrFxMute.CanRemoveLast;
            if (GUILayout.Button(Lang.T("移除最後加入的"), Fit.WB(126, Lang.T("移除最後加入的"))))
            {
                string rep;
                cfgFxMuteList.Value = VrFxMute.RemoveLastAdded(cfgFxMuteList.Value, out rep);
                fxAddReport = rep;
                VrFxMute.ForceNow();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            cfgFxFolder.Value = GUILayout.Toggle(cfgFxFolder.Value, Lang.T(" (FX) 資料夾顯示"),
                                                 Fit.WT(140, Lang.T(" (FX) 資料夾顯示")));
            cfgFxFolderVrOff.Value = GUILayout.Toggle(cfgFxFolderVrOff.Value,
                                                      Lang.T(" VR 時關閉 (FX)"), Fit.WT(140, Lang.T(" VR 時關閉 (FX)")));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("清單"), Fit.WL(36, Lang.T("清單")));
            string edited = GUILayout.TextField(cfgFxMuteList.Value);
            if (edited != cfgFxMuteList.Value) { cfgFxMuteList.Value = edited; VrFxMute.ForceNow(); }
            GUILayout.EndHorizontal();

            Status(fxAddReport);
            Status(VrFxMute.FxReport);
            Status(string.Format(Lang.T("規則 {0} 條"), VrFxMute.RuleCount)
                   + (VrFxMute.BadCount > 0 ? string.Format(Lang.T("，看不懂 {0} 條"), VrFxMute.BadCount) : "")
                   + "　｜ " + Lang.T(VrFxMute.LastReport));

            GUILayout.Space(8f);
            GUILayout.Label(Lang.T("<b>介面清晰度（VR）</b>"), rich);

            GUILayout.BeginHorizontal();
            cfgUiBacking.Value = GUILayout.Toggle(cfgUiBacking.Value, Lang.T(" 介面加黑底"), Fit.WT(110, Lang.T(" 介面加黑底")));
            GUILayout.Label(Lang.T("不透明 ") + cfgUiOpacity.Value.ToString("F2"), Fit.WL(88, Lang.T("不透明 ") + cfgUiOpacity.Value.ToString("F2")));
            cfgUiOpacity.Value = Mathf.Round(
                GUILayout.HorizontalSlider(cfgUiOpacity.Value, 0f, 1f, GUILayout.Width(90)) * 20f) / 20f;
            GUILayout.Space(10f);
            cfgVrSkin.Value = GUILayout.Toggle(cfgVrSkin.Value, Lang.T(" 不透明視窗外觀"), Fit.WT(130, Lang.T(" 不透明視窗外觀")));
            cfgVrSkinOnlyVr.Value = GUILayout.Toggle(cfgVrSkinOnlyVr.Value, Lang.T(" 只在 VR"), Fit.WT(80, Lang.T(" 只在 VR")));
            GUILayout.Label(Lang.T("（F6 / F7 / F9 共用）"), small);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            cfgMountUi.Value = GUILayout.Toggle(cfgMountUi.Value, Lang.T(" 主介面黏在手上"), Fit.WT(126, Lang.T(" 主介面黏在手上")));
            string[] mh = { "左手", "右手" };
            for (int i = 0; i < 2; i++)
            {
                bool on = cfgMountLeft.Value == (i == 0);
                if (GUILayout.Toggle(on, " " + Lang.T(mh[i]), "Button", Fit.W(54, " " + Lang.T(mh[i]), "Button")) && !on)
                    cfgMountLeft.Value = i == 0;
            }
            GUILayout.Space(6f);
            GUILayout.Label(Lang.T("沿手柄 ") + cfgMountDist.Value.ToString("F2"), Fit.WL(84, Lang.T("沿手柄 ") + cfgMountDist.Value.ToString("F2")));
            cfgMountDist.Value = Mathf.Round(
                GUILayout.HorizontalSlider(cfgMountDist.Value, 0f, 0.6f, GUILayout.Width(80)) * 100f) / 100f;
            GUILayout.Label(Lang.T("縮放 ") + cfgMountScale.Value.ToString("F2"), Fit.WL(74, Lang.T("縮放 ") + cfgMountScale.Value.ToString("F2")));
            cfgMountScale.Value = Mathf.Round(
                GUILayout.HorizontalSlider(cfgMountScale.Value, 0.05f, 1.5f, GUILayout.Width(80)) * 100f) / 100f;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("角度 ") + cfgMountTilt.Value.ToString("F0") + "°", Fit.WL(70, Lang.T("角度 ") + cfgMountTilt.Value.ToString("F0") + "°"));
            cfgMountTilt.Value = Mathf.Round(
                GUILayout.HorizontalSlider(cfgMountTilt.Value, -90f, 90f, GUILayout.Width(100)) / 5f) * 5f;
            GUILayout.Label(Lang.T("抬離 ") + cfgMountLift.Value.ToString("F2"), Fit.WL(74, Lang.T("抬離 ") + cfgMountLift.Value.ToString("F2")));
            cfgMountLift.Value = Mathf.Round(
                GUILayout.HorizontalSlider(cfgMountLift.Value, -0.1f, 0.2f, GUILayout.Width(80)) * 100f) / 100f;
            GUILayout.Space(8f);
            cfgMountFlip.Value = GUILayout.Toggle(cfgMountFlip.Value, Lang.T(" 板子正反翻轉"), Fit.WT(114, Lang.T(" 板子正反翻轉")));
            cfgHideUi.Value = GUILayout.Toggle(cfgHideUi.Value, Lang.T(" 隱藏主介面"), Fit.WT(106, Lang.T(" 隱藏主介面")));
            cfgVrLog.Value = GUILayout.Toggle(cfgVrLog.Value, Lang.T(" 狀態寫進 log"), Fit.WT(106, Lang.T(" 狀態寫進 log")));
            GUILayout.EndHorizontal();

            Status(VrUiBacking.LastReport);

            GUILayout.Space(8f);
            GUILayout.Label(Lang.T("<b>手柄</b>"), rich);

            // 這一區只留「會臨時按的動作」和狀態。
            // 所有的按鍵綁定和速度都搬到「手柄設置」子視窗了 ——
            // 那些設一次就不會再碰，卻占掉面板一大半，在頭顯裡還得一路捲下去。
            GUILayout.BeginHorizontal();
            cfgCtrlEnable.Value = GUILayout.Toggle(cfgCtrlEnable.Value, Lang.T(" 啟用"), Fit.WT(66, Lang.T(" 啟用")));
            if (GUILayout.Button(Lang.T("立刻回歸"), Fit.WB(80, Lang.T("立刻回歸")), GUILayout.Height(20)))
                DoReset("面板按鈕", true);
            GUILayout.Label(Lang.T("偏離 ") + VrLocomotion.OffsetDistance.ToString("F2") + " m",
                            small, Fit.W(86, Lang.T("偏離 ") + VrLocomotion.OffsetDistance.ToString("F2") + " m", small));
            GUILayout.Space(8f);
            GUILayout.Label(cfgHideUi.Value ? Lang.T("主介面：隱藏")
                            : cfgMountUi.Value ? Lang.T("主介面：在手上") : Lang.T("主介面：在原處"),
                            small, Fit.W(110, cfgHideUi.Value ? Lang.T("主介面：隱藏") : cfgMountUi.Value ? Lang.T("主介面：在手上") : Lang.T("主介面：在原處"), small));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("記住現在的視角"), Fit.WB(126, Lang.T("記住現在的視角")), GUILayout.Height(20)))
            { VrLocomotion.SaveSpot(); if (cfgSpotToJson.Value) VrLink.RequestSave(); }
            if (GUILayout.Button(Lang.T("回到記住的視角"), Fit.WB(126, Lang.T("回到記住的視角")), GUILayout.Height(20)))
            { VrLocomotion.GoToSpot(); }
            if (GUILayout.Button(Lang.T("清掉"), Fit.WB(60, Lang.T("清掉")), GUILayout.Height(20)))
            { VrLocomotion.ClearSpot(); }
            GUILayout.Label(VrLocomotion.HasSpot ? Lang.T("　已記住一個視角") : Lang.T("　還沒記住"), small);
            GUILayout.EndHorizontal();

            Status(VrInput.LastReport);
            Status(VrLocomotion.LastReport);

            GUILayout.Label(Lang.T("搖桿＝平移　")
                            + Lang.T("握把＋搖桿＝原地轉身（支點在眼睛）　握把＋X/Y＝下降／上升　")
                            + Lang.T("扳機＋搖桿＝繞轉（支點在原點，上下和左右各一根軸）　")
                            + Lang.T("扳機＋X＝記住視角、扳機＋Y＝回到記住的視角　")
                            + Lang.T("Y 單按＝原處→黏手→隱藏 循環"), small);
            GUILayout.Label(Lang.T("握把＋扳機按住＝播放控制（這隻手暫停移動）：　")
                            + Lang.T("Y＝暫停／播放　搖桿右／左＝快轉／倒轉（按著連發）　")
                            + Lang.T("搖桿上推滿 1 秒＝重播　搖桿下推滿 1 秒＝停止"), small);

            GUILayout.BeginHorizontal();
            // 語言按鈕在最左下。
            DrawLangButton();
            GUILayout.FlexibleSpace();
            // 工具列圖示的開關擺在「設置」左邊，不再放進設置子視窗裡 ——
            // 開關圖示是常做的事，不該還要先開一層。
            cfgToolbarButton.Value = GUILayout.Toggle(cfgToolbarButton.Value,
                                                      Lang.T(" 顯示工具列圖示"), Fit.WT(130, Lang.T(" 顯示工具列圖示")));
            showCtrlSettings = GUILayout.Toggle(showCtrlSettings,
                                                showCtrlSettings ? Lang.T("  手柄設置（開啟中）  ") : Lang.T("  手柄設置  "),
                                                "Button", Fit.W(140, showCtrlSettings ? Lang.T("  手柄設置（開啟中）  ") : Lang.T("  手柄設置  "), "Button"), GUILayout.Height(22));
            showSettings = GUILayout.Toggle(showSettings,
                                            showSettings ? Lang.T("  設置（開啟中）  ") : Lang.T("  設置  "),
                                            "Button", Fit.W(120, showSettings ? Lang.T("  設置（開啟中）  ") : Lang.T("  設置  "), "Button"), GUILayout.Height(22));
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }
    }
}
