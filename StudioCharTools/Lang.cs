using System;
using System.Collections.Generic;

namespace StudioCharTools
{
    /// <summary>
    /// 介面語言。繁體中文 / English / 日本語。
    ///
    /// 為什麼用「中文原文當 key」而不是 "panel.title" 這種代號
    /// ----------------------------------------------------
    /// 這三支插件的介面文字是一路長出來的，不是先設計好再實作的。
    /// 要補一套代號等於把每一句都重新命名一次，而每個命名都是一次出錯的機會
    /// （key 打錯不會編譯失敗，只會在畫面上變成一串代號，而且要戴著頭顯才發現）。
    ///
    /// 用原文當 key 的話：
    ///   1. 查不到就原樣顯示中文 —— 翻譯缺一半也不會壞，只是那幾句還是中文
    ///   2. 不必發明名字，也就不會有 key 和內容對不起來的問題
    ///   3. 之後要補翻譯，只要往表裡加一筆，不用回頭改呼叫點
    ///
    /// 代價是字典的 key 比較長、比對字串比較慢。這是每幀幾百次的字典查詢，
    /// 在 IMGUI 那種本來就在配置大量字串的環境裡完全不是瓶頸。
    ///
    /// 三支插件各自編一份這個檔案（跟 VrSkin 一樣）。它們**不是同一個型別**，
    /// 所以共用的狀態只能走 AppDomain 那張表 —— 在任何一支面板上換語言，
    /// 另外兩支下一幀就會跟上。
    /// </summary>
    public static class Lang
    {
        public const int ZH = 0, EN = 1, JA = 2;

        const string K_LANG = "reze.studio.lang";
        const string K_STAMP = "reze.studio.lang.stamp";

        /// <summary>目前語言。直接改這個不會同步給其他插件，請用 Set()。</summary>
        public static int Current = ZH;

        static int seenStamp = -1;

        public static string Name(int lang)
        {
            return lang == EN ? "English" : lang == JA ? "日本語" : "繁體中文";
        }

        /// <summary>按鈕上顯示的字。刻意固定用英文的 "Language"，三種語言的人都看得懂。</summary>
        public static string ButtonLabel { get { return "Language: " + Name(Current); } }

        public static int Next(int lang) { return (lang + 1) % 3; }

        /// <summary>換語言，並公布給另外兩支插件。</summary>
        public static void Set(int lang)
        {
            Current = lang < ZH || lang > JA ? ZH : lang;
            try
            {
                AppDomain.CurrentDomain.SetData(K_LANG, Current);
                object o = AppDomain.CurrentDomain.GetData(K_STAMP);
                int n = o is int ? (int)o : 0;
                seenStamp = n + 1;
                AppDomain.CurrentDomain.SetData(K_STAMP, seenStamp);
            }
            catch { }
        }

        /// <summary>
        /// 每幀叫一次。別人換了語言就跟著換，並寫回自己的設定檔。
        /// 沒裝其他兩支的話這裡什麼都不會發生，Current 就照自己的設定值走。
        /// </summary>
        public static void Follow(int myCfgValue, out int adopted)
        {
            adopted = myCfgValue;
            try
            {
                object st = AppDomain.CurrentDomain.GetData(K_STAMP);
                int n = st is int ? (int)st : 0;
                if (seenStamp < 0) { seenStamp = n; Current = myCfgValue; return; }
                if (n != seenStamp)
                {
                    seenStamp = n;
                    object o = AppDomain.CurrentDomain.GetData(K_LANG);
                    if (o is int)
                    {
                        Current = (int)o;
                        adopted = Current;
                        return;
                    }
                }
            }
            catch { }
            Current = myCfgValue;
        }

        // ------------------------------------------------------------ 查表

        static readonly Dictionary<string, string> en = new Dictionary<string, string>();
        static readonly Dictionary<string, string> ja = new Dictionary<string, string>();

        /// <summary>
        /// 翻譯一句話。查不到就原樣回傳 —— 翻譯表缺的部分會是中文，不會是空白或代號。
        /// </summary>
        public static string T(string zh)
        {
            if (Current == ZH || string.IsNullOrEmpty(zh)) return zh;
            Dictionary<string, string> d = Current == EN ? en : ja;
            string v;
            return d.TryGetValue(zh, out v) && !string.IsNullOrEmpty(v) ? v : zh;
        }

        /// <summary>翻譯表有幾筆，面板上顯示進度用。</summary>
        public static string Coverage
        {
            get { return "EN " + en.Count + " / JA " + ja.Count; }
        }

        static void A(string zh, string e, string j)
        {
            if (!string.IsNullOrEmpty(e)) en[zh] = e;
            if (!string.IsNullOrEmpty(j)) ja[zh] = j;
        }

        static Lang()
        {
            // ---------------------------------------------------------------
            // 翻譯表。格式：A("中文原文", "English", "日本語")
            //
            // key 就是程式裡寫的那一句中文，一字不差（包含前後空白和全形空格）——
            // 因為呼叫端是 Lang.T("那一句")，查表用的就是同一個字串。
            // 少一筆就是那一句維持中文，不會壞，所以可以隨時補。
            //
            // 這份表三支插件各編一份、內容相同。重複是刻意的：
            // 拆成共用組件的話，三支就不能各自單獨安裝了。
            // ---------------------------------------------------------------
            A("    RT 尺寸 ",
              "    RT size ",
              "    RT サイズ ");
            A("  (無)",
              "  (none)",
              "  (なし)");
            A("  → 音訊走真實時間、時間軸走 deltaTime，所以音訊此刻應領先約 ",
              "  → audio runs on real time, the timeline on deltaTime, so audio should now lead by about ",
              "  → 音声は実時間、タイムラインは deltaTime で進むため、音声は今およそ ");
            A("  ▶  設定（音軌 / 色彩 / 診斷）",
              "  ▶  Advanced (audio / colour / diagnostics)",
              "  ▶  詳細設定（音声 / 色 / 診断）");
            A("  ▼  設定",
              "  ▼  Advanced",
              "  ▼  詳細設定");
            A("  卡頓補償 maximumDeltaTime",
              "  Stall compensation (maximumDeltaTime)",
              "  カクつき補償 maximumDeltaTime");
            A("  手柄設置  ",
              "  Controls  ",
              "  コントローラー設定  ");
            A("  手柄設置（開啟中）  ",
              "  Controls (open)  ",
              "  コントローラー設定（表示中）  ");
            A("  設置  ",
              "  Settings  ",
              "  設定  ");
            A("  設置（開啟中）  ",
              "  Settings (open)  ",
              "  設定（表示中）  ");
            A("  重新定位 ",
              "  Reposition ",
              "  再配置 ");
            A(" (FX) 資料夾顯示",
              " Show (FX) folder",
              " (FX) フォルダを表示");
            A(" / 偵測到 ",
              " / detected ",
              " / 検出 ");
            A(" VR 時關閉 (FX)",
              " Hide (FX) in VR",
              " VR 時に (FX) を非表示");
            A(" s。",
              " s.",
              " 秒。");
            A(" 上下翻轉",
              " Flip vertically",
              " 上下反転");
            A(" 不透明視窗外觀",
              " Opaque window skin",
              " 不透明なウィンドウ外観");
            A(" 主介面黏在手上",
              " Mount panel to hand",
              " パネルを手に固定");
            A(" 人物卡",
              " Chara card",
              " キャラカード");
            A(" 介面加黑底",
              " Opaque backing",
              " UI に背景板");
            A(" 個欄位",
              " fields",
              " 項目");
            A(" 個還開著）",
              " still on)",
              " 個が有効のまま）");
            A(" 倍頭腳距離",
              " x head-to-foot",
              " 倍（頭〜足）");
            A(" 允許上下轉",
              " Allow pitch",
              " 上下回転を許可");
            A(" 凍結 timeScale",
              " Freeze timeScale",
              " timeScale を固定");
            A(" 切場景自動套用",
              " Auto-apply on scene change",
              " シーン切替時に自動適用");
            A(" 只在 VR",
              " VR only",
              " VR のみ");
            A(" 右搖桿可移動",
              " Right stick moves",
              " 右スティックで移動");
            A(" 同時叫 CameraSync",
              " Also ask CameraSync",
              " CameraSync にも依頼");
            A(" 同時隱藏男角色",
              " Hide males too",
              " 男性キャラも隠す");
            A(" 啟用",
              " Enabled",
              " 有効");
            A(" 套",
              " apply",
              " 適用");
            A(" 寫主控台",
              " Log to console",
              " コンソールに出力");
            A(" 左搖桿可移動",
              " Left stick moves",
              " 左スティックで移動");
            A(" 按著＝鎖定",
              " Hold to lock",
              " 押し続けて固定");
            A(" 握把＋X/Y＝上下平移",
              " Grip + X/Y = move up/down",
              " グリップ＋X/Y＝上下移動");
            A(" 播放時只畫影片",
              " Video only while playing",
              " 再生中は動画のみ描画");
            A(" 播放時關掉後製特效",
              " Kill post FX while playing",
              " 再生中はポストエフェクトを無効");
            A(" 支點在眼睛",
              " Pivot at eyes",
              " 支点は目の位置");
            A(" 服裝卡",
              " Coordinate card",
              " コーデカード");
            A(" 板子正反翻轉",
              " Flip panel",
              " パネルを表裏反転");
            A(" 桌面照樣畫",
              " Also draw on desktop",
              " デスクトップにも描画");
            A(" 次",
              " times",
              " 回");
            A(" 段",
              " segments",
              " 区間");
            A(" 片尾",
              " Ending",
              " エンディング");
            A(" 狀態寫進 log",
              " Log status",
              " 状態をログに出力");
            A(" 秒（待物理靜止）",
              " s (wait for physics)",
              " 秒（物理の静止待ち）");
            A(" 筆",
              " entries",
              " 件");
            A(" 筆未顯示，請用搜尋或分組縮小範圍",
              " not shown; narrow it down with search or grouping",
              " 件が非表示です。検索やグループで絞り込んでください");
            A(" 總開關",
              " Master",
              " マスター");
            A(" 自動載入（比對時間軸總長度）",
              " Auto load (match timeline length)",
              " 自動読み込み（タイムライン長で照合）");
            A(" 自動重播",
              " Auto replay",
              " 自動リプレイ");
            A(" 記住視角時一併寫進 F7 的設定檔",
              " Also write saved view into F7 config",
              " 視点保存時に F7 の設定ファイルにも書き込む");
            A(" 跟著頭轉",
              " Follow head",
              " 頭に追従");
            A(" 跳過所有動畫",
              " Skip all cutscenes",
              " 過場をすべてスキップ");
            A(" 過場",
              " Transition",
              " 過場");
            A(" 開場",
              " Opening",
              " オープニング");
            A(" 開始",
              " start",
              " 開始");
            A(" 開關)",
              " toggle)",
              " 切替)");
            A(" 隱藏主介面",
              " Hide main panel",
              " メインパネルを隠す");
            A(" 顯示工具列圖示",
              " Toolbar icon",
              " ツールバーアイコン");
            A(" 顯示工具列圖示（白色小人）",
              " Toolbar icon (white figure)",
              " ツールバーアイコン（白い人型）");
            A("(根目錄)",
              "(root)",
              "(ルート)");
            A("(無名稱)",
              "(unnamed)",
              "(名前なし)");
            A("(留空=影片自帶)",
              "(blank = use the video's own)",
              "(空欄＝動画内蔵の音声)");
            A("<b>VR 自動關物件</b>",
              "<b>Auto-hide objects in VR</b>",
              "<b>VR 時のオブジェクト自動非表示</b>");
            A("<b>介面清晰度（VR）</b>",
              "<b>UI legibility (VR)</b>",
              "<b>UI の視認性（VR）</b>");
            A("<b>回到相機視角</b>",
              "<b>Return to camera view</b>",
              "<b>カメラ視点に戻る</b>");
            A("<b>快速試播</b>",
              "<b>Quick preview</b>",
              "<b>クイック試聴</b>");
            A("<b>手柄</b>",
              "<b>Controller</b>",
              "<b>コントローラー</b>");
            A("<b>扳機＋搖桿＝繞轉</b>",
              "<b>Trigger + stick = orbit</b>",
              "<b>トリガー＋スティック＝周回</b>");
            A("<b>按鍵綁定</b>",
              "<b>Button bindings</b>",
              "<b>キー割り当て</b>");
            A("<b>播放控制（握把＋扳機按住）</b>",
              "<b>Transport (hold grip + trigger)</b>",
              "<b>再生操作（グリップ＋トリガー押下中）</b>");
            A("<b>段落</b>",
              "<b>Segments</b>",
              "<b>区間</b>");
            A("<b>移動與轉向</b>",
              "<b>Movement and turning</b>",
              "<b>移動と旋回</b>");
            A("<b>色彩（sRGBWrite 是正解，其餘留著比對）</b>",
              "<b>Colour (sRGBWrite is correct; the rest are kept for comparison)</b>",
              "<b>色（sRGBWrite が正解。他は比較用）</b>");
            A("<b>診斷</b>",
              "<b>Diagnostics</b>",
              "<b>診断</b>");
            A("<b>過場的觸發方式</b>",
              "<b>How transitions trigger</b>",
              "<b>過場のトリガー方式</b>");
            A("<b>過場設定檔</b>",
              "<b>CutScene config file</b>",
              "<b>過場設定ファイル</b>");
            A("<b>音軌（跟著時間軸走，可任意 seek）</b>",
              "<b>Audio tracks (follow the timeline, seek anywhere)</b>",
              "<b>音声トラック（タイムラインに追従、自由にシーク可）</b>");
            A("<b>頭顯裡的過場畫面</b>",
              "<b>Cutscene screen in the headset</b>",
              "<b>ヘッドセット内の過場画面</b>");
            A("<color=#c0392b>時間軸未就緒（每秒自動重試）</color>",
              "<color=#c0392b>Timeline not ready (retrying every second)</color>",
              "<color=#c0392b>タイムライン未準備（毎秒再試行）</color>");
            A("<color=#e05b5b>面板繪製出錯：",
              "<color=#e05b5b>Panel draw error: ",
              "<color=#e05b5b>パネル描画エラー：");
            A("<color=#e74c3c><b> 跳過所有動畫</b></color>",
              "<color=#e74c3c><b> Skip all cutscenes</b></color>",
              "<color=#e74c3c><b> 過場をすべてスキップ</b></color>");
            A("<color=#e74c3c>紅色 ✕ ＝ 這個檔案不在了",
              "<color=#e74c3c>Red ✕ = this file is gone",
              "<color=#e74c3c>赤い ✕ ＝ このファイルがありません");
            A("<color=#f1c40f>總長度一樣，分不出來 —— 點一個：</color>",
              "<color=#f1c40f>Same total length, can't tell them apart — pick one:</color>",
              "<color=#f1c40f>総再生時間が同じで判別できません。1 つ選んでください：</color>");
            A("VR 視角：",
              "VR view: ",
              "VR 視点：");
            A("Y 單按＝原處→黏手→隱藏 循環",
              "Y alone = in place → on hand → hidden",
              "Y 単押し＝元の位置→手に固定→非表示 の循環");
            A("Y＝暫停／播放　搖桿右／左＝快轉／倒轉（按著連發）　",
              "Y = pause/play　stick right/left = seek forward/back (repeats while held)　",
              "Y＝一時停止／再生　スティック右／左＝早送り／巻き戻し（押しっぱなしで連続）　");
            A("cf_j_* 前綴比對　cf_s_head 完整名稱　-cf_j_ana 排除。",
              "cf_j_* prefix match　cf_s_head exact name　-cf_j_ana excludes.",
              "cf_j_* は前方一致　cf_s_head は完全一致　-cf_j_ana は除外。");
            A("° 視角",
              "° FOV",
              "° 画角");
            A("← 上一層",
              "← Up",
              "← 上の階層");
            A("── 合卡 ──",
              "── Card merge ──",
              "── カード合成 ──");
            A("── 換角色 ──",
              "── Character swap ──",
              "── キャラ差し替え ──");
            A("── 根治（會寫進場景卡）──",
              "── Permanent fix (written into the scene card) ──",
              "── 根本対処（シーンカードに書き込まれます）──");
            A("── 目前這一份設定就是對的話 ──",
              "── If the current setup is already correct ──",
              "── 現在の設定で正しい場合 ──");
            A("── 縮圖與存檔 ──",
              "── Thumbnails and saving ──",
              "── サムネイルと保存 ──");
            A("✓ 檔名＝角色名「",
              "✓ Filename = character name \"",
              "✓ ファイル名＝キャラ名「");
            A("　(",
              "　(",
              "　(");
            A("　　≈ ",
              "　　≈ ",
              "　　≈ ");
            A("　　寬 ",
              "　　width ",
              "　　幅 ");
            A("　凍結模式作用中",
              "　freeze mode active",
              "　フリーズモード作動中");
            A("　已記住一個視角",
              "　one viewpoint saved",
              "　視点を 1 つ記憶済み");
            A("　還沒記住",
              "　nothing saved yet",
              "　未記憶");
            A("　（來源 ",
              "　(from ",
              "　（取得元 ");
            A("　（時間軸跳不動）",
              "　(timeline won't seek)",
              "　（タイムラインがシークできません）");
            A("「只在跟播時」＝按了上面的「從頭播放」或「跟播中」才會播過場。",
              "\"Only while following\" = transitions play only after you press Play from start, or while following.",
              "「追従中のみ」＝上の「最初から再生」または「追従中」のときだけ過場を再生します。");
            A("「維持新卡身材」要從舊卡留下的 ABMX 骨頭",
              "ABMX bones kept from the old card by \"keep body\"",
              "「体型を維持」で旧カードから残す ABMX ボーン");
            A("」的話，換人後會自動帶入這份鎖",
              "\", this lock is applied automatically after a swap",
              "」なら、差し替え後にこの固定が自動で適用されます");
            A("」，換人後會自動帶入（可在設置關閉）",
              "\", applied automatically after a swap (can be turned off in Settings)",
              "」、差し替え後に自動適用されます（設定でオフにできます）");
            A("一鍵修復碰撞器綁定（換人後吸附）",
              "Repair collider bindings (post-swap sticking)",
              "コライダー結合を一括修復（差し替え後の吸着）");
            A("一鍵添加飾品",
              "Add accessories to all",
              "アクセサリを一括追加");
            A("一鍵重置所有姿勢",
              "Reset every pose",
              "全ポーズを一括リセット");
            A("一鍵（女角色）",
              "All females",
              "一括（女性キャラ）");
            A("一鍵（男角色）",
              "All males",
              "一括（男性キャラ）");
            A("上限 ",
              "Max ",
              "上限 ");
            A("下限 ",
              "Min ",
              "下限 ");
            A("不透明 ",
              "Opacity ",
              "不透明度 ");
            A("主介面：在原處",
              "Panel: in place",
              "パネル：元の位置");
            A("主介面：在手上",
              "Panel: on hand",
              "パネル：手に固定");
            A("主介面：隱藏",
              "Panel: hidden",
              "パネル：非表示");
            A("以其他角色為範本修復",
              "Repair using another character as the template",
              "他のキャラを手本にして修復");
            A("保持服裝換人",
              "Swap, keep outfit",
              "服装を維持して差し替え");
            A("倒出成員→剪貼簿",
              "Dump members → clipboard",
              "メンバーをダンプ→クリップボード");
            A("偏離 ",
              "Offset ",
              "ずれ ");
            A("停止",
              "Stop",
              "停止");
            A("儲存",
              "Save",
              "保存");
            A("儲存並關閉",
              "Save and close",
              "保存して閉じる");
            A("全不選",
              "None",
              "全解除");
            A("全選",
              "All",
              "全選択");
            A("全部",
              "All",
              "すべて");
            A("全部解鎖",
              "Unlock all",
              "すべて解除");
            A("共 ",
              "Total ",
              "合計 ");
            A("共用來源片: ",
              "Shared source video: ",
              "共通ソース動画: ");
            A("再按一次確認",
              "Click again to confirm",
              "もう一度押して確定");
            A("刪第 N 格時，被選中的服裝槽也會刪它的第 N 格",
              "Deleting slot N also deletes slot N on the selected outfits",
              "N 番目を削除すると、選択中のコーデでも N 番目が削除されます");
            A("刪除",
              "Delete",
              "削除");
            A("刪除時會連帶清掉 ",
              "Deleting also clears ",
              "削除時に併せて消去されます: ");
            A("取景高度 ",
              "Frame height ",
              "構図の高さ ");
            A("取消",
              "Cancel",
              "キャンセル");
            A("取現值",
              "Use current",
              "現在値を取得");
            A("另有 ",
              "Also ",
              "他に ");
            A("只鎖一個",
              "Lock only this",
              "これだけ固定");
            A("診斷",
              "Diag",
              "診断");
            A("倒出形態鍵（這個角色）",
              "Dump blendshapes (this character)",
              "シェイプキーを書き出し（このキャラ）");
            A("倒出形態鍵（全部角色）",
              "Dump blendshapes (all characters)",
              "シェイプキーを書き出し（全キャラ）");
            A("已存到（路徑已複製）：",
              "Saved (path copied): ",
              "保存しました（パスをコピー済み）：");
            A("倒出失敗：",
              "Dump failed: ",
              "書き出し失敗：");
            A("只顯示已鎖定",
              "Locked only",
              "固定済みのみ表示");
            A("只顯示非零",
              "Non-zero only",
              "非ゼロのみ表示");
            A("同步刪除的服裝槽（點成紅字就會一起刪同一格）",
              "Outfit slots deleted in sync (red = same slot gets deleted too)",
              "連動して削除するコーデ枠（赤にすると同じ枠も削除）");
            A("哪一隻手",
              "Which hand",
              "どちらの手");
            A("問不到目前的卡片路徑 —— 改用總長度比對",
              "Can't determine the current card path — falling back to length matching",
              "現在のカードパスを取得できません。総再生時間での照合に切り替えます");
            A("回到記住的視角",
              "Go to saved view",
              "記憶した視点に戻る");
            A("在 Timeline 面板調東西、拉進度條都不會被影片打斷。",
              "Editing in the Timeline panel or dragging the playhead won't be interrupted by a video.",
              "Timeline パネルでの編集やシーク中に動画で中断されません。");
            A("型態鍵",
              "Blendshape",
              "ブレンドシェイプ");
            A("型態鍵鎖定",
              "Blendshape lock",
              "ブレンドシェイプ固定");
            A("場景 ",
              "Scene ",
              "シーン ");
            A("場景裡沒有找到角色。",
              "No characters found in the scene.",
              "シーン内にキャラが見つかりません。");
            A("大小寫有差：cf_j_ 是骨架、cf_J_ 是臉。",
              "Case matters: cf_j_ is the skeleton, cf_J_ is the face.",
              "大文字小文字が区別されます：cf_j_ は骨格、cf_J_ は顔。");
            A("子資料夾:",
              "Subfolders:",
              "サブフォルダ:");
            A("存人物卡",
              "Save chara card",
              "キャラカード保存");
            A("存全部換裝",
              "Save all outfits",
              "全コーデを保存");
            A("存到 Temp 資料夾",
              "Save to Temp folder",
              "Temp フォルダへ保存");
            A("存姿勢",
              "Save pose",
              "ポーズ保存");
            A("存成角色名「",
              "Save under character name \"",
              "キャラ名「");
            A("存服裝卡",
              "Save coordinate card",
              "コーデカード保存");
            A("存目前視角",
              "Save current view",
              "現在の視点を保存");
            A("對象: ",
              "Target: ",
              "対象: ");
            A("對象已失效",
              "Target is gone",
              "対象が無効です");
            A("工具列按鈕：",
              "Toolbar button: ",
              "ツールバーボタン：");
            A("工單資料夾（kkbridge 監看的那個，留空就用預設）",
              "Job folder (the one kkbridge watches; blank = default)",
              "作業フォルダ（kkbridge が監視するもの。空欄で既定）");
            A("已播",
              "played",
              "再生済み");
            A("強制中止合卡流程",
              "Force-abort the merge",
              "合成処理を強制中断");
            A("強制復原",
              "Force recover",
              "強制復帰");
            A("影片",
              "Video",
              "動画");
            A("手動 Pause",
              "Pause manually",
              "手動 Pause");
            A("手動 Resume",
              "Resume manually",
              "手動 Resume");
            A("手柄設置",
              "Controls",
              "コントローラー設定");
            A("扳機＋X＝記住視角、扳機＋Y＝回到記住的視角　",
              "Trigger+X = save view, Trigger+Y = return to it　",
              "トリガー＋X＝視点を記憶、トリガー＋Y＝記憶した視点へ　");
            A("扳機＋搖桿＝繞轉（支點在原點，上下和左右各一根軸）　",
              "Trigger+stick = orbit (pivot at origin, one axis each for vertical and horizontal)　",
              "トリガー＋スティック＝周回（支点は原点、上下と左右で軸を個別指定）　");
            A("把現在的綁定記成基準",
              "Record the current binding as the baseline",
              "現在の結合を基準として記録");
            A("把選取的加入清單",
              "Add selection to list",
              "選択中のものをリストに追加");
            A("抬離 ",
              "Lift ",
              "浮かせ量 ");
            A("拍照前等待 ",
              "Settle ",
              "撮影前の待機 ");
            A("換人",
              "Swap character",
              "キャラ差し替え");
            A("換人時 KKPE 會把新角色的所有動骨都綁到場上每一顆碰撞器上",
              "On a swap, KKPE binds every dynamic bone of the new character to every collider in the scene",
              "差し替え時、KKPE は新キャラの全ダイナミックボーンをシーン内の全コライダーに結合します");
            A("換衣服",
              "Change clothes",
              "着替え");
            A("握把＋扳機按住＝播放控制（這隻手暫停移動）：　",
              "Hold grip+trigger = transport mode (movement suspended on that hand):　",
              "グリップ＋トリガー押下＝再生操作（その手の移動は停止）：　");
            A("握把＋搖桿＝原地轉身（支點在眼睛）　握把＋X/Y＝下降／上升　",
              "Grip+stick = turn in place (pivot at eyes)　Grip+X/Y = down/up　",
              "グリップ＋スティック＝その場で旋回（支点は目）　グリップ＋X/Y＝下降／上昇　");
            A("搖桿上推滿 1 秒＝重播　搖桿下推滿 1 秒＝停止",
              "Stick up held 1 s = replay　Stick down held 1 s = stop",
              "スティック上を 1 秒＝最初から再生　下を 1 秒＝停止");
            A("搖桿＝平移　",
              "Stick = move　",
              "スティック＝移動　");
            A("搜尋:",
              "Search:",
              "検索:");
            A("搜尋模式：跨所有分組",
              "Search mode: across all groups",
              "検索モード：全グループ横断");
            A("搜尋：",
              "Search: ",
              "検索：");
            A("搜索",
              "Search",
              "検索");
            A("播放中按 ESC 或空白鍵可跳過（滑鼠點擊不算，免得誤觸）。卡住就按「強制復原」。",
              "Press ESC or Space while playing to skip (mouse clicks don't count, to avoid misfires). If it hangs, press Force recover.",
              "再生中に ESC またはスペースでスキップ（誤操作防止のためマウスクリックは無効）。固まったら「強制復帰」を押してください。");
            A("擷圖時只顯示該角色",
              "Solo this character while capturing",
              "撮影時はこのキャラのみ表示");
            A("整張卡",
              "Whole card",
              "カード全体");
            A("新出現的動骨一律不吸，存檔後對這張卡永久有效。",
              "New dynamic bones are never grabbed; saved into the card permanently.",
              "新しく現れたダイナミックボーンは一切吸着しません。保存後このカードに永続適用されます。");
            A("時間軸 ",
              "Timeline ",
              "タイムライン ");
            A("暫停方式 ",
              "Pause method ",
              "一時停止の方式 ");
            A("暫停／播放＝",
              "Pause/play = ",
              "一時停止／再生＝");
            A("最大",
              "Max",
              "最大");
            A("最小",
              "Min",
              "最小");
            A("未選取角色",
              "No character selected",
              "キャラ未選択");
            A("整組縮放",
              "Group scale",
              "グループ拡縮");
            A("勾選本類",
              "Tick this group",
              "このグループを選択");
            A("勾選頭部類",
              "Tick head/hair",
              "頭・髪を選択");
            A("全不勾",
              "Untick all",
              "全解除");
            A("已勾 {0} 個",
              "{0} ticked",
              "{0} 個選択中");
            A("等比",
              "Uniform",
              "等比");
            A("中心",
              "Pivot",
              "中心");
            A("頭部骨頭",
              "Head bone",
              "頭ボーン");
            A("各自掛點",
              "Own anchor",
              "各自の親");
            A("套用",
              "Apply",
              "適用");
            A("縮放",
              "Scale",
              "拡縮");
            A("縮放歸位",
              "Reset scale",
              "拡縮リセット");
            A("位置歸位",
              "Reset pos",
              "位置リセット");
            A("位置（X 左右 / Y 上下 / Z 前後）",
              "Position (X left-right / Y up-down / Z front-back)",
              "位置（X 左右 / Y 上下 / Z 前後）");
            A("位置（各自掛點的軸向）",
              "Position (each anchor's axes)",
              "位置（各親の軸）");
            A("確定",
              "Confirm",
              "確定");
            A("復原上一次確定",
              "Undo last confirm",
              "直前の確定を戻す");
            A("已調整 {0} 個飾品",
              "Adjusted {0} accessories",
              "{0} 個のアクセを調整");
            A("（其他服裝槽 {0} 個）",
              " ({0} in other outfits)",
              "（他の衣装 {0} 個）");
            A("；{0} 格可能只改到畫面",
              "; {0} slots may only change on screen",
              "；{0} 枠は画面のみの可能性");
            A("已復原上一次確定的調整",
              "Last confirmed adjustment undone",
              "直前の確定を元に戻しました");
            A("拖滑桿即時預覽；「確定」後滑桿歸位，可以再接著調",
              "Drag sliders to preview live; Confirm resets the sliders so you can keep adjusting",
              "スライダーでリアルタイム確認。「確定」でスライダーが戻り、続けて調整できます");
            A("先在下面的清單勾選要一起調整的飾品",
              "Tick the accessories to adjust in the list below",
              "下のリストで調整するアクセを選択してください");
            A("同步服裝槽（紅字選中，同步所有操作，需同欄位同物件）",
              "Synced outfits (red = selected; all operations sync; needs same slot and same item)",
              "同期する衣装（赤=選択、全操作を同期、同じ枠・同じアイテムのみ）");
            A("目前只操作這一套",
              "Only this outfit",
              "この衣装のみ操作");
            A("操作時同步 {0} 套",
              "Syncing {0} outfits",
              "{0} 着を同期");
            A("刪除、縮放、移動第 N 格時，選中的服裝槽第 N 格若是同一個飾品也會一起改",
              "Removing, scaling or moving slot N also changes slot N of the selected outfits when it holds the same item",
              "N 番の削除・拡縮・移動時、選択した衣装の N 番が同じアイテムなら一緒に変更");
            A("有無法同步之服裝槽：",
              "Outfits that could not be synced: ",
              "同期できなかった衣装：");
            A("、",
              ", ",
              "、");
            A("（同步 {0} 套）",
              " (synced {0} outfits)",
              "（{0} 着同期）");
            A("換角色套用場景原角色的著色器",
              "Apply scene character's shaders on swap",
              "入れ替え時にシーンのキャラのシェーダーを適用");
            A("關",
              "Off",
              "オフ");
            A("詢問",
              "Ask",
              "確認");
            A("自動",
              "Auto",
              "自動");
            A("要把場景原角色的著色器套到新角色嗎？",
              "Apply the scene character's shaders to the new character?",
              "シーンの元キャラのシェーダーを新キャラに適用しますか？");
            A("（只換身體、臉、眼睛、眉毛、牙齒、舌頭的著色器，參數與貼圖不動）",
              "(only the shaders of body, face, eyes, brows, teeth and tongue; parameters and textures are left alone)",
              "（体・顔・目・眉・歯・舌のシェーダーのみ変更。パラメータとテクスチャはそのまま）");
            A("套用著色器",
              "Apply shaders",
              "シェーダーを適用");
            A("不套用",
              "Don't apply",
              "適用しない");
            A("取消換人",
              "Cancel swap",
              "入れ替えをキャンセル");
            A("套用著色器？",
              "Apply shaders?",
              "シェーダーを適用？");
            A("{0} 個角色",
              "{0} characters",
              "{0} 人");
            A("🎨 已套用場景原角色的著色器（{0} 個材質）",
              "🎨 Scene character's shaders applied ({0} materials)",
              "🎨 シーンの元キャラのシェーダーを適用（{0} 材質）");
            A("本類全部隱藏",
              "Hide all in this group",
              "このグループを全て非表示");
            A("本類全部顯示",
              "Show all in this group",
              "このグループを全て表示");
            A("歸零",
              "Zero",
              "ゼロに");
            A("沿手柄 ",
              "Along controller ",
              "コントローラー方向 ");
            A("泛光／色彩校正是在 cullingMask 之後才跑的，偏紅泛光就是它",
              "Bloom and colour grading run after cullingMask — the red glow is them",
              "ブルームやカラーグレーディングは cullingMask の後に走ります。赤っぽい光はこれが原因です");
            A("添加飾品",
              "Add accessories",
              "アクセサリを追加");
            A("清單",
              "List",
              "リスト");
            A("清掉",
              "Clear",
              "消去");
            A("清掉錯誤",
              "Clear errors",
              "エラーを消去");
            A("清空",
              "Empty",
              "空にする");
            A("清除所有記錄",
              "Clear all records",
              "全記録を消去");
            A("熱鍵 ",
              "Hotkey ",
              "ホットキー ");
            A("狀態: ",
              "Status: ",
              "状態: ");
            A("現在就套用一次",
              "Apply once now",
              "今すぐ一度適用");
            A("用角色名",
              "Use character name",
              "キャラ名を使う");
            A("用預設",
              "Use default",
              "既定を使う");
            A("略過這一步（不帶入服裝卡，直接繼續）",
              "Skip this step (no coordinate card, continue)",
              "この手順をスキップ（コーデカードなしで続行）");
            A("目前 ",
              "Current ",
              "現在 ");
            A("目前: ",
              "Current: ",
              "現在: ");
            A("目前只刪這一套",
              "Delete only this outfit",
              "このコーデのみ削除");
            A("目前鎖定 ",
              "Locked ",
              "固定中 ");
            A("目錄: ",
              "Folder: ",
              "フォルダ: ");
            A("碰撞器",
              "Colliders",
              "コライダー");
            A("碰撞器綁定修復",
              "Collider binding repair",
              "コライダー結合の修復");
            A("移除",
              "Remove",
              "削除");
            A("移除最後加入的",
              "Remove last added",
              "最後に追加したものを削除");
            A("立刻回歸",
              "Return now",
              "今すぐ戻る");
            A("立刻重找",
              "Search again now",
              "今すぐ再検索");
            A("縮圖大小：不縮小（最清晰）",
              "Thumbnail size: no downscaling (sharpest)",
              "サムネサイズ：縮小なし（最も鮮明）");
            A("縮圖高度上限 ",
              "Max thumbnail height ",
              "サムネ高さ上限 ");
            A("縮放 ",
              "Scale ",
              "縮尺 ");
            A("複製目前時間",
              "Copy current time",
              "現在時刻をコピー");
            A("角度 ",
              "Angle ",
              "角度 ");
            A("記住現在的視角",
              "Save current view",
              "現在の視点を記憶");
            A("設置",
              "Settings",
              "設定");
            A("設置（開啟中）",
              "Settings (open)",
              "設定（表示中）");
            A("診斷",
              "Diagnostics",
              "診断");
            A("試播這支",
              "Preview this",
              "これを試聴");
            A("距離 ",
              "Distance ",
              "距離 ");
            A("載入 / 重載",
              "Load / reload",
              "読み込み／再読み込み");
            A("這個數字若對得上上面的重新定位量，問題就是停頓、不是對應點。",
              "If this matches the reposition amount above, the problem is the stall, not the anchors.",
              "この数値が上の再配置量と一致するなら、原因は停止であって対応点ではありません。");
            A("這個角色全部不吸",
              "Never grab anything on this character",
              "このキャラは一切吸着しない");
            A("這個資料夾裡沒有卡片。",
              "No cards in this folder.",
              "このフォルダにカードがありません。");
            A("這就是 HSPE 面板上的 Enable New Dynamic Bones。關掉之後",
              "This is Enable New Dynamic Bones on the HSPE panel. With it off,",
              "これは HSPE パネルの Enable New Dynamic Bones です。オフにすると");
            A("選取",
              "Select",
              "選択");
            A("選擇場景角色 (",
              "Scene characters (",
              "シーンキャラ選択 (");
            A("還原成預設清單",
              "Restore default list",
              "既定のリストに戻す");
            A("還原記錄的綁定",
              "Restore recorded binding",
              "記録した結合を復元");
            A("還原記錄的綁定（無記錄）",
              "Restore recorded binding (nothing recorded)",
              "記録した結合を復元（記録なし）");
            A("配音",
              "Voice",
              "ボイス");
            A("重新偵測卡片路徑",
              "Re-detect card path",
              "カードパスを再検出");
            A("重新掃描",
              "Rescan",
              "再スキャン");
            A("重新掃描 Timeline",
              "Rescan Timeline",
              "Timeline を再スキャン");
            A("重置",
              "Reset",
              "リセット");
            A("重置姿勢",
              "Reset pose",
              "ポーズをリセット");
            A("重置為預設",
              "Reset to defaults",
              "初期設定に戻す");
            A("重載音檔",
              "Reload audio",
              "音声を再読み込み");
            A("鎖定",
              "Lock",
              "固定");
            A("開啟資料夾",
              "Open folder",
              "フォルダを開く");
            A("關掉所有碰撞器的「自動加入新動骨」（",
              "Turn off \"auto-add new dynamic bones\" on every collider (",
              "全コライダーの「新規ダイナミックボーンを自動追加」をオフ（");
            A("關閉",
              "Close",
              "閉じる");
            A("附加",
              "Append",
              "追加");
            A("隱藏",
              "Hide",
              "非表示");
            A("音訊",
              "Audio",
              "音声");
            A("音量",
              "Volume",
              "音量");
            A("預設",
              "Default",
              "既定");
            A("頭在畫面高度 ",
              "Head Y in frame ",
              "画面内の頭の高さ ");
            A("顯示",
              "Show",
              "表示");
            A("顯示 ",
              "Show ",
              "表示 ");
            A("顯示工具列圖示",
              "Toolbar icon",
              "ツールバーアイコン");
            A("顯示空格",
              "Show empty slots",
              "空き枠を表示");
            A("飾品",
              "Accessories",
              "アクセサリ");
            A("飾品欄管理",
              "Accessory manager",
              "アクセサリ管理");
            A("點角色名稱，直接跳出選卡視窗替換 (保留身材)：",
              "Click a name to open the card picker and swap (keeping the body):",
              "名前をクリックするとカード選択が開き、体型を維持して差し替えます：");
            A("（1 上緣 / 0.5 正中）",
              "(1 = top edge / 0.5 = centre)",
              "（1 = 上端 / 0.5 = 中央）");
            A("（F6 / F7 / F9 共用）",
              "(shared by F6 / F7 / F9)",
              "（F6 / F7 / F9 共通）");
            A("（不存在，會自動建立）",
              "(does not exist, will be created)",
              "（存在しません。自動作成されます）");
            A("（全身）",
              "(full body)",
              "（全身）");
            A("（共用來源影片也找不到）",
              "(the shared source video is missing too)",
              "（共通ソース動画も見つかりません）");
            A("（半身）",
              "(half body)",
              "（半身）");
            A("（大頭照）",
              "(headshot)",
              "（顔アップ）");
            A("（存在）",
              "(exists)",
              "（存在します）");
            A("（對應關係若正確應為 0）",
              "(should be 0 if the anchors are right)",
              "（対応点が正しければ 0 になります）");
            A("（尚未載入任何設定檔。下面的「快速試播」不需要設定檔。）",
              "(No config loaded. Quick preview below doesn't need one.)",
              "（設定ファイル未読み込み。下のクイック試聴には不要です。）");
            A("（已存）",
              "(saved)",
              "（保存済み）");
            A("（未存）",
              "(not saved)",
              "（未保存）");
            A("（目前）",
              "(current)",
              "（現在）");
            A("（空）",
              "(empty)",
              "（空）");
            A("（非 1080p 的片子改 json）",
              "(edit the json for non-1080p videos)",
              "（1080p 以外の動画は json を編集）");
            A("（預設啟用），J694 這種小碰撞器就會開始吸頭髮與下半身。",
              "(on by default), small colliders like J694 start grabbing hair and the lower body.",
              "（既定でオン）、J694 のような小さなコライダーが髪や下半身を吸着し始めます。");
            A("）",
              "）",
              " ）");
            A("，請用工具重新產生 json，或直接改 json 裡的路徑。</color>",
              ", regenerate the json with your tool, or fix the path inside the json.</color>",
              "。ツールで json を再生成するか、json 内のパスを直接修正してください。</color>");
            A("👨 男角色 (",
              "👨 Male (",
              "👨 男性キャラ (");
            A("👩 女角色 (",
              "👩 Female (",
              "👩 女性キャラ (");

            // ---- 補翻（2026-09 公開前）----
            A("頭部",
              "Head",
              "頭");
            A("頭頂",
              "Head top",
              "頭頂");
            A("頭側",
              "Head side",
              "頭側");
            A("馬尾",
              "Ponytail",
              "ポニーテール");
            A("雙馬尾 L",
              "Twin tail L",
              "ツインテール L");
            A("雙馬尾 R",
              "Twin tail R",
              "ツインテール R");
            A("髮夾",
              "Hair pin",
              "髪留め");
            A("髮夾 R",
              "Hair pin R",
              "髪留め R");
            A("耳環 L",
              "Earring L",
              "イヤリング L");
            A("耳環 R",
              "Earring R",
              "イヤリング R");
            A("鼻",
              "Nose",
              "鼻");
            A("口",
              "Mouth",
              "口");
            A("頸",
              "Neck",
              "首");
            A("胸",
              "Chest",
              "胸");
            A("胸前",
              "Chest front",
              "胸前");
            A("腰",
              "Waist",
              "腰");
            A("腰前",
              "Waist front",
              "腰前");
            A("腰後",
              "Waist back",
              "腰後");
            A("腰 L",
              "Waist L",
              "腰 L");
            A("腰 R",
              "Waist R",
              "腰 R");
            A("背",
              "Back",
              "背中");
            A("背 L",
              "Back L",
              "背中 L");
            A("背 R",
              "Back R",
              "背中 R");
            A("肩 L",
              "Shoulder L",
              "肩 L");
            A("肩 R",
              "Shoulder R",
              "肩 R");
            A("手臂 L",
              "Arm L",
              "腕 L");
            A("手臂 R",
              "Arm R",
              "腕 R");
            A("手腕 L",
              "Wrist L",
              "手首 L");
            A("手腕 R",
              "Wrist R",
              "手首 R");
            A("手 L",
              "Hand L",
              "手 L");
            A("手 R",
              "Hand R",
              "手 R");
            A("腿 L",
              "Leg L",
              "脚 L");
            A("腿 R",
              "Leg R",
              "脚 R");
            A("腳踝 L",
              "Ankle L",
              "足首 L");
            A("腳踝 R",
              "Ankle R",
              "足首 R");
            A("鞋 L",
              "Shoe L",
              "靴 L");
            A("鞋 R",
              "Shoe R",
              "靴 R");
            A("未指定",
              "Unassigned",
              "未指定");
            A("頭部（其他）",
              "Head (other)",
              "頭（その他）");
            A("手部（其他）",
              "Hand (other)",
              "手（その他）");
            A("腿部（其他）",
              "Leg (other)",
              "脚（その他）");
            A("已檢查存出的卡，見主控台",
              "Checked the saved card, see console",
              "保存したカードを確認しました。コンソールを参照");
            A("診斷已輸出到主控台",
              "Diagnostics written to the console",
              "診断をコンソールに出力しました");
            A("偵測不到任何飾品欄位",
              "No accessory slots detected",
              "アクセサリ枠が見つかりません");
            A("身體",
              "Body",
              "体");
            A("眼睛/嘴巴",
              "Eyes/mouth",
              "目/口");
            A("眉毛",
              "Eyebrows",
              "眉");
            A("鼻線",
              "Nose line",
              "鼻線");
            A("牙齒",
              "Teeth",
              "歯");
            A("虎牙",
              "Fangs",
              "八重歯");
            A("舌頭",
              "Tongue",
              "舌");
            A("上睫毛",
              "Upper lashes",
              "上まつげ");
            A("下睫毛",
              "Lower lashes",
              "下まつげ");
            A("眼淚 L",
              "Tears L",
              "涙 L");
            A("眼淚 M",
              "Tears M",
              "涙 M");
            A("眼淚 S",
              "Tears S",
              "涙 S");
            A("左眼白",
              "Left eye white",
              "左白目");
            A("右眼白",
              "Right eye white",
              "右白目");
            A("其他",
              "Other",
              "その他");
            A("固定",
              "Fixed",
              "固定");
            A("範圍",
              "Range",
              "範囲");
            A("添加飾品 {0}/{1}",
              "Adding accessories {0}/{1}",
              "アクセサリ追加 {0}/{1}");
            A("一鍵添加飾品完成（{0} 個角色）",
              "Batch accessory add done ({0} characters)",
              "アクセサリ一括追加完了（{0} キャラ）");
            A("場上沒有角色",
              "No characters in the scene",
              "シーンにキャラがいません");
            A("① 一鍵保持服裝換人（{0}）：{1} 個角色都換成這張人物卡",
              "① Batch swap keeping outfits ({0}): all {1} characters become this card",
              "① 服装そのまま一括入れ替え（{0}）：{1} キャラをこのカードに");
            A("② 選擇要帶入頭髮飾品的服裝卡（可略過）",
              "② Pick an outfit card to bring hair accessories from (optional)",
              "② 髪アクセを持ってくるコーデカードを選択（省略可）");
            A("保持服裝換人（{0}）",
              "Swap keeping outfit ({0})",
              "服装そのまま入れ替え（{0}）");
            A("一鍵{0}完成（{1} 個角色）",
              "Batch {0} done ({1} characters)",
              "一括{0}完了（{1} キャラ）");
            A("沒有角色可以處理",
              "No characters to process",
              "処理するキャラがいません");
            A("處理中 {0} / {1}",
              "Processing {0} / {1}",
              "処理中 {0} / {1}");
            A("人物卡",
              "character card",
              "キャラカード");
            A("一鍵存{0}：成功 {1} 個",
              "Batch save {0}: {1} succeeded",
              "{0}を一括保存：成功 {1} 個");
            A("，失敗 {0} 個",
              ", {0} failed",
              "、失敗 {0} 個");
            A("沒有選取角色",
              "No character selected",
              "キャラが選択されていません");
            A("存全部換裝：",
              "Saving all outfits: ",
              "全コーデ保存：");
            A("存全部換裝：成功 {0} 套",
              "Saving all outfits: {0} succeeded",
              "全コーデ保存：成功 {0} 着");
            A("，失敗 {0} 套",
              ", {0} failed",
              "、失敗 {0} 着");
            A("（已切回原本的第 {0} 套）",
              " (switched back to outfit {0})",
              "（元の {0} 着目に戻しました）");
            A("已中止等待，工單已刪除",
              "Stopped waiting; job deleted",
              "待機を中止し、ジョブを削除しました");
            A("kkbridge 沒有回應——請確認它開著且正在監看 ",
              "kkbridge isn't responding — make sure it's running and watching ",
              "kkbridge が応答しません — 起動して次を監視しているか確認してください ");
            A("{0} 逾時（{1}s）",
              "{0} timed out ({1}s)",
              "{0} タイムアウト（{1}s）");
            A("{0} 失敗: ",
              "{0} failed: ",
              "{0} 失敗: ");
            A("① 選擇要換上的服裝卡 - ",
              "① Pick the outfit card to put on - ",
              "① 着せるコーデカードを選択 - ");
            A("② 選擇要帶回的頭髮／必備飾品（可略過）",
              "② Pick hair / essential accessories to bring back (optional)",
              "② 戻す髪／必須アクセを選択（省略可）");
            A("換裝中...",
              "Changing outfit...",
              "着替え中...");
            A("✅ 服裝替換成功（沒有要帶回的飾品）",
              "✅ Outfit replaced (no accessories to bring back)",
              "✅ 服装を置き換えました（戻すアクセなし）");
            A("服裝已換上，接著併回飾品...",
              "Outfit on, merging accessories back...",
              "服装を着せました。アクセを戻しています...");
            A("選擇要合併飾品的服裝卡",
              "Pick the outfit card to merge accessories from",
              "アクセを合成するコーデカードを選択");
            A("存出人物卡中...",
              "Saving character card...",
              "キャラカードを保存中...");
            A("人物卡存檔失敗，流程中止",
              "Character card save failed, aborted",
              "キャラカードの保存に失敗、中止しました");
            A("合併飾品",
              "Merge accessories",
              "アクセ合成");
            A("套用合併後的人物卡...",
              "Applying the merged character card...",
              "合成したキャラカードを適用中...");
            A("✅ 合卡完成並已套用: ",
              "✅ Merged and applied: ",
              "✅ 合成して適用しました: ");
            A("套用失敗: ",
              "Apply failed: ",
              "適用失敗: ");
            A("① 選擇要換上的人物卡（{0}）",
              "① Pick the character card to put on ({0})",
              "① 入れ替えるキャラカードを選択（{0}）");
            A("存出目前角色（用來做服裝）...",
              "Saving the current character (for the outfit)...",
              "現在のキャラを保存中（服装用）...");
            A("存卡失敗，流程中止",
              "Card save failed, aborted",
              "カード保存に失敗、中止しました");
            A("合併頭髮飾品",
              "Merge hair accessories",
              "髪アクセ合成");
            A("換人中...",
              "Swapping character...",
              "キャラ入れ替え中...");
            A("換人失敗: ",
              "Swap failed: ",
              "入れ替え失敗: ");
            A("存出換好的角色...",
              "Saving the swapped character...",
              "入れ替えたキャラを保存中...");
            A("移植整套換裝",
              "Transfer all outfits",
              "全コーデ移植");
            A("✅ 保持服裝換人（{0}）完成: ",
              "✅ Swap keeping outfit ({0}) done: ",
              "✅ 服装そのまま入れ替え（{0}）完了: ");
            A("套用最終卡失敗: ",
              "Applying the final card failed: ",
              "最終カードの適用に失敗: ");
            A("移植換裝時胸托參數",
              "Push-up settings when transferring outfits",
              "コーデ移植時のパッド設定");
            A("跟著服裝",
              "Follow outfit",
              "服装に合わせる");
            A("保留人物",
              "Keep character's",
              "キャラのまま");
            A("合卡後保留最終人物卡",
              "Keep the final character card after merging",
              "合成後の最終キャラカードを残す");
            A("保留",
              "Keep",
              "残す");
            A("換角色保留角色名稱",
              "Keep the character name when swapping",
              "入れ替え時にキャラ名を維持");
            A("是",
              "Yes",
              "はい");
            A("否",
              "No",
              "いいえ");
            A("換角色重新套用姿勢",
              "Reapply pose after swapping",
              "入れ替え後にポーズを再適用");
            A("換角色還原碰撞器綁定",
              "Restore collider bindings after swapping",
              "入れ替え後にコライダー設定を復元");
            A("換角色自動帶入同名形態鍵",
              "Auto-apply same-name blendshape preset",
              "同名のブレンドシェイプ設定を自動適用");
            A("縮圖依角色範圍裁切",
              "Crop thumbnail to the character",
              "サムネをキャラ範囲で切り抜き");
            A("取景以頭部為中心",
              "Center framing on the head",
              "頭を中心に撮影");
            A("擷圖時隱藏所有介面",
              "Hide all UI while capturing",
              "撮影時に UI をすべて隠す");
            A("存檔後開啟檔案總管",
              "Open Explorer after saving",
              "保存後にエクスプローラーを開く");
            A("設置已儲存",
              "Settings saved",
              "設定を保存しました");
            A("❌ 無法取得目前的 CoordinateType (fileStatus.coordinateType)",
              "❌ Can't get the current CoordinateType (fileStatus.coordinateType)",
              "❌ 現在の CoordinateType を取得できません (fileStatus.coordinateType)");
            A("❌ 找不到 AssignCoordinate(CoordinateType, ChaFileCoordinate)",
              "❌ AssignCoordinate(CoordinateType, ChaFileCoordinate) not found",
              "❌ AssignCoordinate(CoordinateType, ChaFileCoordinate) が見つかりません");
            A("✅ 服裝替換成功！",
              "✅ Outfit replaced!",
              "✅ 服装を置き換えました！");
            A("❌ 換裝服裝發生錯誤: ",
              "❌ Error changing outfit: ",
              "❌ 着替えでエラー: ");
            A("❌ 換裝發生錯誤: ",
              "❌ Error changing outfit: ",
              "❌ 着替えでエラー: ");
            A("❌ ChangeChara 失敗！",
              "❌ ChangeChara failed!",
              "❌ ChangeChara に失敗！");
            A("⏳ 換人中（{0}）…",
              "⏳ Swapping ({0})…",
              "⏳ 入れ替え中（{0}）…");
            A("✅ 換人成功（{0}）",
              "✅ Swapped ({0})",
              "✅ 入れ替え完了（{0}）");
            A("，鎖回 {0} 根體型滑桿",
              ", {0} body sliders locked back",
              "、体型スライダー {0} 本を固定し直しました");
            A("❌ 身材還原失敗: ",
              "❌ Body restore failed: ",
              "❌ 体型の復元に失敗: ");
            A("✅ 換人成功（{0}），姿勢已還原",
              "✅ Swapped ({0}), pose restored",
              "✅ 入れ替え完了（{0}）、ポーズを復元");
            A("　｜ 已帶入形態鍵預設「{0}」",
              "　| blendshape preset \"{0}\" applied",
              "　｜ ブレンドシェイプ設定「{0}」を適用");
            A("🧲 碰撞器綁定已還原（修正 {0} 根動骨）",
              "🧲 Collider bindings restored ({0} dynamic bones fixed)",
              "🧲 コライダー設定を復元（揺れボーン {0} 本を修正）");
            A("讀不到目前的動畫，診斷已印在主控台",
              "Can't read the current animation; diagnostics in the console",
              "現在のアニメを読めません。診断はコンソールに出力");
            A("重建動畫狀態機中...",
              "Rebuilding animation state machine...",
              "アニメのステートマシンを再構築中...");
            A("已重建動畫狀態機 ",
              "Animation state machine rebuilt ",
              "アニメのステートマシンを再構築しました ");
            A("重建失敗，見主控台",
              "Rebuild failed, see console",
              "再構築に失敗、コンソールを参照");
            A("整組換人：",
              "Swap all:",
              "全員入れ替え：");
            A("整組保持服裝：",
              "All, keep outfit:",
              "全員・服装そのまま：");
            A("換人：",
              "Swap:",
              "入れ替え：");
            A("選擇人物卡（{0}） - ",
              "Pick character card ({0}) - ",
              "キャラカードを選択（{0}） - ");
            A("保持服裝：",
              "Keep outfit:",
              "服装そのまま：");
            A("學生服（校內）",
              "School Uniform",
              "学生服（校内）");
            A("學生服（放學）",
              "Going Home",
              "学生服（下校）");
            A("體操服",
              "Gym Clothes",
              "体操着");
            A("泳裝",
              "Swimsuit",
              "水着");
            A("社團",
              "Club",
              "部活");
            A("私服",
              "Casual",
              "私服");
            A("睡衣",
              "Pajamas",
              "パジャマ");
            A("套用上次值",
              "Use last values",
              "前回の値を適用");
            A("制服1",
              "Uniform 1",
              "制服1");
            A("制服2",
              "Uniform 2",
              "制服2");
            A("部活",
              "Club",
              "部活");
            A("浴衣",
              "Yukata",
              "浴衣");
            A("第 {0} 套",
              "Outfit {0}",
              "{0} 着目");
            A("；同步清掉 {0} 套",
              "; also cleared in {0} outfits",
              "；{0} 着でも同時に削除");
            A("，{0} 套碰不到",
              ", {0} outfits unreachable",
              "、{0} 着は触れません");
            A("這張不是人物卡，請拖人物卡進來",
              "That is not a character card. Drop a character card.",
              "キャラカードではありません。キャラカードをドロップしてください");
            A("這張不是服裝卡，請拖服裝卡進來",
              "That is not a coordinate card. Drop a coordinate card.",
              "コーデカードではありません。コーデカードをドロップしてください");
            A("沒有角色可以替換",
              "No characters to swap",
              "入れ替えるキャラがいません");
            A("一鍵替換 {0} 個角色（{1}）",
              "Batch swap {0} characters ({1})",
              "{0} キャラを一括入れ替え（{1}）");
            A("已對 {0} 個角色套用人物卡（{1}）",
              "Applied the character card to {0} characters ({1})",
              "{0} キャラにキャラカードを適用（{1}）");
            A("重置姿勢 {0}/{1}",
              "Resetting pose {0}/{1}",
              "ポーズをリセット中 {0}/{1}");
            A("已重置 {0}/{1} 個角色的姿勢",
              "Reset the pose of {0}/{1} characters",
              "{0}/{1} キャラのポーズをリセットしました");
            A("修復碰撞器綁定 {0}/{1}",
              "Fixing collider bindings {0}/{1}",
              "コライダー設定を修復中 {0}/{1}");
            A("已修復 {0} 個角色的碰撞器綁定，共調整 {1} 根動骨",
              "Fixed collider bindings on {0} characters, {1} dynamic bones adjusted",
              "{0} キャラのコライダー設定を修復、揺れボーン {1} 本を調整");
            A("保留舊卡身材",
              "Keep old body",
              "旧カードの体型");
            A("鎖身高",
              "Lock height",
              "身長固定");
            A("維持新卡身材",
              "Keep new body",
              "新カードの体型");
            A("一般替換",
              "Normal swap",
              "通常入れ替え");
            A("已清除所有碰撞器綁定記錄",
              "Cleared all collider binding records",
              "コライダー設定の記録をすべて削除しました");
            A("一鍵添加飾品：{0} 個角色都套這張服裝卡",
              "Batch add accessories: apply this outfit card to all {0} characters",
              "アクセ一括追加：{0} キャラ全員にこのコーデカードを適用");
            A("已強制復原",
              "Force-restored",
              "強制復元しました");
            A("預載影片…",
              "Preloading video…",
              "動画をプリロード中…");
            A("影片 Prepare 逾時",
              "Video Prepare timed out",
              "動画の Prepare がタイムアウト");
            A("預載音訊…",
              "Preloading audio…",
              "音声をプリロード中…");
            A("(尚無影格)",
              "(no frame yet)",
              "（フレームなし）");
            A("已跳過",
              "Skipped",
              "スキップしました");
            A("播放結束",
              "Playback finished",
              "再生終了");
            A("影片停了 1 秒，判定結束",
              "Video stalled for 1 s, treated as finished",
              "動画が 1 秒止まったので終了と判断");
            A("到達 maxSeconds",
              "Reached maxSeconds",
              "maxSeconds に到達");
            A("（還沒找過）",
              "(not searched yet)",
              "（まだ探していません）");
            A("沒有 Studio CutScene（沒裝，或還沒載入）",
              "No Studio CutScene (not installed or not loaded)",
              "Studio CutScene がありません（未導入か未読み込み）");
            A("CutScene 的版本太舊，找不到 VR 接口（需要 1.12.0 以上）",
              "CutScene is too old, no VR interface (needs 1.12.0+)",
              "CutScene が古く VR 接続がありません（1.12.0 以上が必要）");
            A("已連上 Studio CutScene",
              "Connected to Studio CutScene",
              "Studio CutScene に接続しました");
            A("自動載入：卡片是 {0}，沒有同名的設定檔；時間軸總長度還停在上一張卡的 {1} s，不拿來比對，維持空白。",
              "Auto-load: the card is {0} with no same-named config; the timeline length is still the previous card's {1} s, so it isn't compared — left empty.",
              "自動読み込み：カードは {0} で同名の設定ファイルがありません。タイムラインの長さがまだ前のカードの {1} s なので比較せず、空のままにします。");
            A("正在找設定檔，稍等一下…",
              "Looking for a config, one moment…",
              "設定ファイルを探しています。少々お待ちください…");
            A("重新搜尋設定檔…",
              "Searching for configs again…",
              "設定ファイルを再検索中…");
            A("　｜ 自動：檔名跟卡片對上",
              "　| auto: file name matches the card",
              "　｜ 自動：ファイル名がカードと一致");
            A("自動載入：{0}（檔名跟卡片對上，內容已經是它了）",
              "Auto-load: {0} (file name matches the card; already loaded)",
              "自動読み込み：{0}（ファイル名がカードと一致、読み込み済み）");
            A("自動載入：卡片是 {0}，沒有同名的設定檔 → 維持空白（想自動載入就把設定檔改名成 {1}.cutscene.json）",
              "Auto-load: the card is {0} with no same-named config → left empty (rename a config to {1}.cutscene.json to auto-load it)",
              "自動読み込み：カードは {0} で同名の設定ファイルがありません → 空のまま（自動で読み込むには設定ファイル名を {1}.cutscene.json に）");
            A("自動載入：認不出現在是哪張卡（{0}），等時間軸總長度再比對…",
              "Auto-load: can't tell which card this is ({0}); waiting to compare the timeline length…",
              "自動読み込み：どのカードか判別できません（{0}）。タイムラインの長さで比較するまで待機…");
            A("自動載入：有 {0} 份設定檔的總長度都對得上（差不到 0.05 秒），分不出來，請自己點一個",
              "Auto-load: {0} configs match the total length (within 0.05 s); can't tell them apart, please pick one",
              "自動読み込み：総時間が一致する設定ファイルが {0} 個あり（誤差 0.05 秒以内）、区別できません。選んでください");
            A("自動載入：{0}（總長對得上，內容已經是它了）",
              "Auto-load: {0} (total length matches; already loaded)",
              "自動読み込み：{0}（総時間が一致、読み込み済み）");
            A("　｜ 自動：總長對得上",
              "　| auto: total length matches",
              "　｜ 自動：総時間が一致");
            A("自動載入：看了 {0} 個設定檔，沒有總長 {1} s 對得上的",
              "Auto-load: checked {0} configs, none matches the total length {1} s",
              "自動読み込み：設定ファイル {0} 個を確認、総時間 {1} s に一致するものなし");
            A("；而且認不出現在是哪張卡（{0}），所以連同名比對都沒能用",
              "; and can't tell which card this is ({0}), so name matching couldn't be used",
              "；しかもどのカードか判別できない（{0}）ので、名前での照合もできませんでした");
            A("；卡片是 {0}，但沒有同名的設定檔",
              "; the card is {0} but there is no same-named config",
              "；カードは {0} ですが同名の設定ファイルがありません");
            A("（一個都沒找到，詳情看主控台）",
              " (none found at all; see the console)",
              "（1 つも見つかりません。詳細はコンソール）");
            A("音軌 {0}（已用 pairs 補）",
              "track {0} (filled from pairs)",
              "音声トラック {0}（pairs で補完）");
            A("音軌 {0}（**沒有對應點，會從 0 秒播**）",
              "track {0} (**no sync points, plays from 0 s**)",
              "音声トラック {0}（**対応点なし、0 秒から再生**）");
            A("最外層必須是一個 { } 物件",
              "The top level must be a { } object",
              "最上位は { } オブジェクトである必要があります");
            A("已載入 {0} 段音軌 / {1} 段過場：",
              "Loaded {0} audio tracks / {1} cutscenes: ",
              "音声トラック {0} 個 / ムービー {1} 個を読み込み：");
            A("載入失敗: ",
              "Load failed: ",
              "読み込み失敗: ");
            A("快捷鍵設定",
              "Hotkey settings",
              "ショートカット設定");
            A("點「設置..」之後按下你要的組合（Shift / Ctrl / Alt 可任意搭配）。",
              "Click \"Set..\", then press the combination you want (any mix of Shift / Ctrl / Alt).",
              "「設定..」を押してから、使いたい組み合わせを押してください（Shift / Ctrl / Alt 自由）。");
            A(" 啟用桌面快捷鍵",
              " Enable desktop hotkeys",
              " デスクトップのショートカットを有効");
            A("設置..",
              "Set..",
              "設定..");
            A("回復預設",
              "Defaults",
              "初期値に戻す");
            A("播放",
              "Play",
              "再生");
            A("暫停",
              "Pause",
              "一時停止");
            A("重播",
              "Replay",
              "リプレイ");
            A("上一個場景",
              "Prev scene",
              "前のシーン");
            A("下一個場景",
              "Next scene",
              "次のシーン");
            A("快進",
              "Forward",
              "早送り");
            A("倒轉",
              "Rewind",
              "巻き戻し");
            A("跳過動畫",
              "Skip cutscene",
              "ムービーをスキップ");
            A("<color=#f1c40f>現在這一版沒有配音對照表（≈），當成跟主配音同秒數。</color>",
              "<color=#f1c40f>This version has no voice mapping (≈); treated as the same timing as the main voice.</color>",
              "<color=#f1c40f>このバージョンにはボイス対応表がないため（≈）、メインボイスと同じ秒数として扱います。</color>");
            A("快捷鍵",
              "Hotkeys",
              "ショートカット");
            A("不動",
              "Off",
              "変更なし");
            A("原樣",
              "As is",
              "そのまま");
            A("只在跟播時（預設）",
              "Only while following (default)",
              "追従再生時のみ（既定）");
            A("時間軸一走就觸發",
              "Whenever the timeline moves",
              "タイムラインが進めば発動");
            A("<color=#7f8c8d>時間軸未就緒，進度條無法使用</color>",
              "<color=#7f8c8d>Timeline not ready; the seek bar can't be used</color>",
              "<color=#7f8c8d>タイムラインが準備できていないため、シークバーは使えません</color>");
            A("　<color=#2980b9>過場中</color>",
              "　<color=#2980b9>cutscene playing</color>",
              "　<color=#2980b9>ムービー中</color>");
            A("存視角：拿不到 VR 的視角（沒裝 Studio VR Tools，或現在不在 VR 裡）",
              "Save view: can't get the VR view (Studio VR Tools not installed, or not in VR)",
              "視点保存：VR の視点を取得できません（Studio VR Tools 未導入、または VR 中ではない）");
            A("已把視角存進這張卡",
              "Saved the view to this card",
              "視点をこのカードに保存しました");
            A("已把視角存進場景 {0}",
              "Saved the view to scene {0}",
              "視点をシーン {0} に保存しました");
            A("存視角失敗：",
              "Saving the view failed: ",
              "視点の保存に失敗：");
            A("換場景，已清掉上一張卡的設定檔。",
              "Scene changed; cleared the previous card's config.",
              "シーンが変わったので、前のカードの設定ファイルをクリアしました。");
            A("畫面效果：記號資料夾或上層沒打勾（用卡片本身存的）",
              "Effects: marker folder or a parent is unchecked (using the card's own)",
              "画面効果：マーカーフォルダか親がオフ（カード自体の設定を使用）");
            A("地圖：這一段沒有地圖（已隱藏）",
              "Map: this segment has no map (hidden)",
              "マップ：このシーンにはマップなし（非表示）");
            A("地圖：已手動更換／刪除，這一段不自動切回（換段或重新載入卡片才會再套用）",
              "Map: changed/removed by hand; this segment won't switch back (applies again on the next segment or when the card reloads)",
              "マップ：手動で変更／削除されたため、このシーンでは自動で戻しません（次のシーンかカード再読み込みで再適用）");
            A("地圖：記號資料夾或上層沒打勾（已隱藏）",
              "Map: marker folder or a parent is unchecked (hidden)",
              "マップ：マーカーフォルダか親がオフ（非表示）");
            A("尚未執行",
              "Not run yet",
              "未実行");
            A("已還原換角色前的姿勢",
              "Restored the pose from before the swap",
              "入れ替え前のポーズを復元しました");
            A("姿勢還原失敗",
              "Pose restore failed",
              "ポーズの復元に失敗");
            A("找不到 Studio.PauseCtrl",
              "Studio.PauseCtrl not found",
              "Studio.PauseCtrl が見つかりません");
            A("找不到 Save / Load",
              "Save / Load not found",
              "Save / Load が見つかりません");
            A("PauseCtrl 是實體方法但場上找不到實體",
              "PauseCtrl uses instance methods but no instance is in the scene",
              "PauseCtrl はインスタンスメソッドですが、シーンにインスタンスがありません");
            A("存檔沒有產生檔案",
              "Save produced no file",
              "保存してもファイルができませんでした");
            A("姿勢已往返重置（無位移）",
              "Pose reset by round trip (no offset)",
              "ポーズを往復リセットしました（ずれなし）");
            A("重新套用失敗，見報告",
              "Reapply failed, see report",
              "再適用に失敗、レポートを参照");
            A("找不到骨架",
              "Skeleton not found",
              "ボーンが見つかりません");
            A("沒有異常骨骼（正常）",
              "No abnormal bones (OK)",
              "異常なボーンなし（正常）");
            A("沒有可復原的紀錄",
              "Nothing to undo",
              "元に戻す記録がありません");
            A("取不到 chaFile",
              "Can't get chaFile",
              "chaFile を取得できません");
            A("找不到 SaveCharaFile",
              "SaveCharaFile not found",
              "SaveCharaFile が見つかりません");
            A("呼叫成功但檔案不存在",
              "Call succeeded but the file doesn't exist",
              "呼び出しは成功しましたがファイルがありません");
            A("沒有指定路徑",
              "No path given",
              "パスが指定されていません");
            A("取不到目前服裝資料",
              "Can't get the current outfit data",
              "現在の服装データを取得できません");
            A("找不到 SaveFile",
              "SaveFile not found",
              "SaveFile が見つかりません");
            A("存檔失敗，見報告",
              "Save failed, see report",
              "保存に失敗、レポートを参照");
            A("（還沒建立）",
              "(not created yet)",
              "（未作成）");
            A("找不到 KKAPI，沒有工具列按鈕",
              "KKAPI not found, no toolbar button",
              "KKAPI が見つからないため、ツールバーボタンなし");
            A("KKAPI 沒有 AddLeftToolbarToggle",
              "KKAPI has no AddLeftToolbarToggle",
              "KKAPI に AddLeftToolbarToggle がありません");
            A("已建立",
              "Created",
              "作成済み");
            A("KKAPI 回傳 null",
              "KKAPI returned null",
              "KKAPI が null を返しました");
            A("已停止",
              "Stopped",
              "停止");
            A("未載入",
              "Not loaded",
              "未読み込み");
            A("未啟用",
              "Disabled",
              "無効");
            A("過場中（音訊續播 @ {0}s）",
              "In cutscene (audio continues @ {0}s)",
              "ムービー中（音声継続 @ {0}s）");
            A("過場中（已隨全域暫停）",
              "In cutscene (paused with global pause)",
              "ムービー中（全体の一時停止に連動）");
            A("Timeline 未就緒",
              "Timeline not ready",
              "Timeline 未準備");
            A("此時間點沒有音軌 ({0}s)",
              "No audio track at this time ({0}s)",
              "この時点に音声トラックなし ({0}s)");
            A("音軌路徑無效",
              "Invalid audio track path",
              "音声トラックのパスが無効");
            A("載入中…",
              "Loading…",
              "読み込み中…");
            A("音檔未載入",
              "Audio not loaded",
              "音声未読み込み");
            A("超出音檔範圍 ({0}s / {1}s)",
              "Past the end of the audio ({0}s / {1}s)",
              "音声の範囲外 ({0}s / {1}s)");
            A("暫停 @ {0}s",
              "Paused @ {0}s",
              "一時停止 @ {0}s");
            A("播放中  音檔 {0}s / 目標 {1}s  (差 {2} ms)",
              "Playing  audio {0}s / target {1}s  (off by {2} ms)",
              "再生中  音声 {0}s / 目標 {1}s  (差 {2} ms)");
            A("  無 anchors",
              "  no anchors",
              "  anchors なし");
            A("  配音對照 {0} 點",
              "  voice mapping {0} points",
              "  ボイス対応 {0} 点");
            A("找不到音檔: ",
              "Audio file not found: ",
              "音声ファイルが見つかりません: ");
            A("音檔格式不支援（只吃 .wav / .ogg）: ",
              "Unsupported audio format (.wav / .ogg only): ",
              "未対応の音声形式（.wav / .ogg のみ）: ");
            A("解碼失敗: ",
              "Decode failed: ",
              "デコード失敗: ");
            A("已載入 ",
              "Loaded ",
              "読み込み済み ");
            A("(無)",
              "(none)",
              "（なし）");
            A("視角資料不完整，沒有存",
              "View data incomplete, not saved",
              "視点データが不完全なため保存しません");
            A("認不出現在是哪張卡，視角沒有存檔",
              "Can't tell which card this is; view not saved",
              "どのカードか判別できないため、視点を保存しません");
            A("已存 {0} 個視角：",
              "Saved {0} views: ",
              "視点 {0} 個を保存：");
            A("寫視角檔失敗：",
              "Writing the view file failed: ",
              "視点ファイルの書き込みに失敗：");
            A("認不出現在是哪張卡，視角不會存",
              "Can't tell which card this is; views won't be saved",
              "どのカードか判別できないため、視点は保存されません");
            A("找不到可以放視角檔的資料夾",
              "No folder to put the view file in",
              "視点ファイルを置くフォルダが見つかりません");
            A("這張卡還沒有存過視角",
              "This card has no saved views yet",
              "このカードにはまだ保存した視点がありません");
            A("已載入 {0} 個視角：",
              "Loaded {0} views: ",
              "視点 {0} 個を読み込み：");
            A("讀視角檔失敗：",
              "Reading the view file failed: ",
              "視点ファイルの読み込みに失敗：");
            A("搖桿按下",
              "Stick press",
              "スティック押し");
            A("扳機",
              "Trigger",
              "トリガー");
            A("握把",
              "Grip",
              "グリップ");
            A("搖桿右",
              "Stick right",
              "スティック右");
            A("搖桿左",
              "Stick left",
              "スティック左");
            A("搖桿上",
              "Stick up",
              "スティック上");
            A("搖桿下",
              "Stick down",
              "スティック下");
            A("（未設定）",
              "(not set)",
              "（未設定）");
            A("左手　",
              "Left　",
              "左手　");
            A("右手　",
              "Right　",
              "右手　");
            A("握把＋",
              "Grip+",
              "グリップ＋");
            A("扳機＋",
              "Trigger+",
              "トリガー＋");
            A("找不到可用的著色器，VR 畫面做不出來",
              "No usable shader; the VR screen can't be made",
              "使えるシェーダーがないため、VR 画面を作れません");
            A("已關閉（設定成一律不用）",
              "Off (set to never use)",
              "オフ（使わない設定）");
            A("沒偵測到 VR，走桌面",
              "No VR detected, using desktop",
              "VR を検出できず、デスクトップで表示");
            A("找不到相機",
              "Camera not found",
              "カメラが見つかりません");
            A("（還沒用到）",
              "(not used yet)",
              "（未使用）");
            A("主介面抓手",
              "Grab main panel",
              "メインパネルをつかむ");
            A("記住視角",
              "Remember view",
              "視点を記憶");
            A("暫停／播放",
              "Pause / play",
              "一時停止／再生");
            A("過場正在播的話是「跳過這一段」",
              "While a cutscene is playing: skip this one",
              "ムービー再生中なら「このムービーをスキップ」");
            A("過場中＝跳過這一段",
              "In cutscene = skip it",
              "ムービー中＝スキップ");
            A("推著不放會連發",
              "Hold to repeat",
              "押し続けで連続");
            A("快轉",
              "Fast forward",
              "早送り");
            A("要推滿並按住，見「重播／停止要推滿幾秒」",
              "Push fully and hold; see \"Hold time for replay / stop\"",
              "奥まで倒して長押し。「リプレイ／停止の長押し秒数」参照");
            A("要按住",
              "Hold",
              "長押し");
            A("規則 {0} 條",
              "{0} rules",
              "ルール {0} 件");
            A("，看不懂 {0} 條",
              ", {0} unreadable",
              "、解釈できない {0} 件");
            A("左手",
              "Left",
              "左手");
            A("右手",
              "Right",
              "右手");
            A("設定法：按一下直接按　▾",
              "Binding: press to set　▾",
              "設定方法：直接押す　▾");
            A("設定法：舊的下拉式　▾",
              "Binding: old dropdowns　▾",
              "設定方法：旧ドロップダウン　▾");
            A("<b>播放控制</b>",
              "<b>Playback control</b>",
              "<b>再生操作</b>");
            A(" 啟用（關掉的話上面那幾個遙控 F7 的綁定都不作用）",
              " Enabled (off = the F7 remote bindings above do nothing)",
              " 有効（オフにすると上の F7 リモート操作はすべて無効）");
            A("快轉一次幾秒",
              "Seconds per skip",
              "1 回のスキップ秒数");
            A("連發前等多久",
              "Delay before repeat",
              "連続までの待ち");
            A("連發間隔",
              "Repeat interval",
              "連続の間隔");
            A("重播／停止要推滿幾秒",
              "Hold time for replay / stop",
              "リプレイ／停止の長押し秒数");
            A("搖桿門檻",
              "Stick threshold",
              "スティックのしきい値");
            A("左手移動速度",
              "Left-hand move speed",
              "左手の移動速度");
            A("右手移動速度",
              "Right-hand move speed",
              "右手の移動速度");
            A("左右轉速度",
              "Turn speed",
              "左右回転の速度");
            A("繞轉速度",
              "Orbit speed",
              "周回の速度");
            A("上下轉速度",
              "Pitch speed",
              "上下回転の速度");
            A("搖桿死區",
              "Stick dead zone",
              "スティックのデッドゾーン");
            A("上下軸（水平繞圈）",
              "Vertical axis (orbit)",
              "上下軸（水平に周回）");
            A("左右軸（上下翻）",
              "Side axis (pitch)",
              "左右軸（上下に回転）");
            A("前後軸（左右傾）",
              "Forward axis (roll)",
              "前後軸（左右に傾く）");
            A("搖桿上下",
              "Stick up/down",
              "スティック上下");
            A("搖桿左右",
              "Stick left/right",
              "スティック左右");
            A("<color=#f1c40f><b>請按下你要的組合…</b></color>　",
              "<color=#f1c40f><b>Press the combination you want…</b></color>　",
              "<color=#f1c40f><b>使いたい組み合わせを押してください…</b></color>　");
            A("<color=#95a5a6>（還沒讀到輸入）</color>",
              "<color=#95a5a6>(no input yet)</color>",
              "<color=#95a5a6>（まだ入力なし）</color>");
            A("<color=#f1c40f>等你按…</color>",
              "<color=#f1c40f>waiting…</color>",
              "<color=#f1c40f>入力待ち…</color>");
            A("設定",
              "Set",
              "設定");
            A("清除",
              "Clear",
              "クリア");
            A("搖桿",
              "Stick",
              "スティック");
            A("桿右",
              "S-right",
              "右倒し");
            A("桿左",
              "S-left",
              "左倒し");
            A("桿上",
              "S-up",
              "上倒し");
            A("桿下",
              "S-down",
              "下倒し");
            A(" 握把",
              " Grip",
              " グリップ");
            A(" 扳機",
              " Trigger",
              " トリガー");
            A("⚠ 跟「",
              "⚠ Same binding as \"",
              "⚠「");
            A("」綁到同一組了",
              "\"",
              "」と同じ組み合わせです");
            A("自動偵測",
              "Auto-detect",
              "自動検出");
            A("一律使用",
              "Always",
              "常に使う");
            A("一律不用",
              "Never",
              "使わない");
            A("過場來源：",
              "Cutscene source: ",
              "ムービーの接続先：");
            A("CameraSync 已連上",
              "CameraSync connected",
              "CameraSync 接続済み");
            A("沒有 CameraSync",
              "No CameraSync",
              "CameraSync なし");
            A("回到相機視角",
              "Back to camera view",
              "カメラ視点に戻る");
            A("<color=#f1c40f>Press any key</color>",
              "<color=#f1c40f>Press any key</color>",
              "<color=#f1c40f>キーを押してください</color>");
            A("Y/B",
              "Y/B",
              "Y/B");
            A("X/A",
              "X/A",
              "X/A");
            A("System",
              "System",
              "System");
            A("<color=#95a5a6>按「設定」之後直接在頭顯裡按你要的組合（握把、扳機可以當修飾鍵一起按住；搖桿推到底也算一種）。全部放開就記起來。</color>",
              "<color=#95a5a6>Press \"Set\", then press the combination in the headset (grip and trigger can be held as modifiers; pushing the stick fully counts too). It's saved when everything is released.</color>",
              "<color=#95a5a6>「設定」を押してから、ヘッドセットで使いたい組み合わせを押してください（グリップ・トリガーは修飾キーとして同時押し可、スティックを倒し切るのも可）。全部離すと記録されます。</color>");
            A("卡片旁邊",
              "next to the card",
              "カードと同じフォルダ");
            // ---- 飾品：全部／主要／次要 ----
            A("主要",
              "Main",
              "メイン");
            A("次要",
              "Sub",
              "サブ");
            A("確定？",
              "Sure?",
              "本当に？");
            A("已移除{0}飾品 {1} 個",
              "Removed {1} {0} accessories",
              "{0}アクセを {1} 個削除しました");
            A("（同步清掉 {0} 套）",
              " (also cleared in {0} outfits)",
              "（{0} 着でも同時に削除）");
            A("換角色保留表情",
              "Keep expression when swapping",
              "入れ替え時に表情を維持");
            A("維持新卡身材：頭大小沿用舊卡",
              "Keep new body: keep old head size",
              "新カードの体型：頭の大きさは旧カードのまま");
            // ---- 形態鍵：KKPE 紫色／比例模式 ----
            A("只顯示 KKPE 調過的",
              "Only KKPE-edited",
              "KKPE で調整済みのみ");
            A("比例",
              "Scale",
              "倍率");
            A("倍率 ",
              "Scale ",
              "倍率 ");
            A("這頁 ",
              "This page ",
              "このページ ");
            A(" 個：倍率",
              " keys: scale",
              " 個：倍率");
            A("套用比例",
              "Apply scale",
              "倍率を適用");
            A("清除比例",
              "Clear scale",
              "倍率を解除");
        }
    }
}
