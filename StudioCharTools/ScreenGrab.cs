using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;
using Studio;

namespace StudioCharTools
{
    /// <summary>
    /// 擷取畫面當作卡片縮圖。
    /// KK 的卡片 png 是「圖片 + 資料」串在一起，圖片來自 chaFile.pngData，
    /// 不設它存出來就是空白圖。
    /// </summary>
    public static class ScreenGrab
    {
        /// <summary>卡片縮圖的長寬比基準。實際輸出的畫素數不一定是這個大小，只有比例照這個。</summary>
        public const int CardW = 252;
        public const int CardH = 352;

        /// <summary>
        /// 縮圖輸出的高度上限（畫素）。0 = 不縮小，直接用畫面上原始的畫素。
        ///
        /// 為什麼預設是 0：
        /// 舊版不管畫面多大，一律縮成 252×352 才寫進卡片 —— 那是 KK 卡片縮圖的「標稱」大小，
        /// 但卡片裡的 pngData 根本不限尺寸，遊戲顯示時本來就會自己縮。
        /// 1080p 的畫面縮到 352 高是 3 倍以上的縮小，而縮的方法又是每個目標畫素
        /// 只取一次 GetPixelBilinear（只看來源 2×2），等於嚴重欠採樣 ——
        /// 服裝的細紋、蕾絲、花色會直接糊成一片，要辨識服裝就是看這些。
        ///
        /// 實測佐證：VNGE 腳本那條路存的是未縮放的 1920×1080，反而是最清楚的一版。
        /// 所以預設改成「不縮小」，跟那條路一致。
        ///
        /// 真的嫌檔案大的話把這個設成例如 704，就會縮到 504×704 ——
        /// 而且走的是面積平均（見 Downsample），不是舊的點取樣，同樣尺寸也比舊版乾淨。
        /// </summary>
        public static int MaxHeight = 0;

        // -----------------------------------------------------------
        // 取景：以頭部為錨點
        //
        // 舊做法是把角色**全部** Renderer 的包圍盒聯集起來當取景範圍。
        // 問題是那個聯集很容易被拉爆：飾品、特效、掛在角色底下的工作室物件、
        // 甚至 SkinnedMeshRenderer 自己算錯的 bounds，任何一個離譜的值
        // 都會把整個框拖走 —— 結果就是拍出一張床、角色的頭卡在角落。
        // 而且它是「聯集」，所以只會愈錯愈大，沒有自我修正的機會。
        //
        // 改成以頭為錨點：頭骨頭的座標是單一、明確、不會被別的東西污染的參考點。
        // 框的大小則用「腳到頭」的螢幕距離去算 —— 一樣只用骨頭座標，
        // 從頭到尾不碰任何 Renderer 的 bounds，因為那正是壞掉的來源。
        // -----------------------------------------------------------

        /// <summary>取景以頭部為中心（關掉就回到舊的「全身包圍盒」做法）。</summary>
        public static bool HeadAnchor = true;

        /// <summary>
        /// 取景高度 = 「腳到頭」這段距離的幾倍。
        ///
        /// 為什麼用這個當尺度而不是「頭有多大」：
        /// 頭腳距離是兩個骨頭座標的差，是精確值；「頭有多大」只能靠包圍盒或比例常數去猜，
        /// 而包圍盒正是害整件事壞掉的東西（實測回報過 9459 px 的頭）。
        /// 1.3 左右全身入鏡，0.6 半身，0.25 大頭照。
        /// </summary>
        public static float FrameScale = 1.3f;

        /// <summary>頭要放在畫面的哪個高度。1 = 貼著上緣，0.5 = 正中央，0 = 貼著下緣。</summary>
        public static float HeadAtY = 0.82f;

        static readonly List<Canvas> _hidden = new List<Canvas>();

        /// <summary>
        /// 關掉場上所有 uGUI Canvas（工作室工具列、其他插件的 uGUI 面板都算）。
        /// 用 Canvas.enabled 而不是停用 GameObject，避免觸發 OnEnable 造成副作用。
        /// </summary>
        public static void HideAllUI()
        {
            _hidden.Clear();
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                try
                {
                    if (c == null || !c.enabled) continue;
                    if (!c.gameObject.activeInHierarchy) continue;
                    c.enabled = false;
                    _hidden.Add(c);
                }
                catch { }
            }
        }

        public static void RestoreUI()
        {
            foreach (var c in _hidden)
            {
                try { if (c != null) c.enabled = true; } catch { }
            }
            _hidden.Clear();
        }

        static readonly HashSet<string> _openedDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 開啟檔案總管。
        ///
        /// 不再用視窗標題比對——標題只有資料夾的葉節點名稱，
        /// chara\female\Temp 和 coordinate\Temp 會被當成同一個，反而該開的不開。
        /// 改成記住開過哪些資料夾：第一次用 /select 選中檔案，
        /// 之後同一個資料夾用「只給資料夾」的方式，檔案總管會沿用既有視窗。
        /// </summary>
        public static void RevealInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (dir == null) return;

                string key = Normalize(dir);

                // 這個資料夾這次遊戲裡已經開過就不再開。
                // explorer /select 一定會開新視窗，只給資料夾也不保證重用，
                // 與其每次都多一個視窗，不如第一次開好就讓它留著。
                if (_openedDirs.Contains(key)) return;

                Process.Start("explorer.exe", "/select,\"" + full + "\"");
                _openedDirs.Add(key);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[ScreenGrab] 開啟檔案總管失敗: " + e.Message);
            }
        }

        static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Replace('/', '\\').TrimEnd('\\');
            try { p = Path.GetFullPath(p).TrimEnd('\\'); } catch { }
            return p;
        }

        /// <summary>
        /// 必須在 WaitForEndOfFrame 之後呼叫，否則讀到的是上一幀。
        ///
        /// 取景預設以**頭**為錨點：頭中心決定位置、頭的大小決定框多大（幾個頭高），
        /// 所以不管鏡頭拉多遠、角色躺著還是站著，構圖都一致。
        /// HeadAnchor 關掉、或是這個角色量不到頭，才退回舊的「全身包圍盒」做法。
        /// </summary>
        public static byte[] CapturePng(OCIChar focusOn, bool fitToChar)
        {
            Texture2D full = null, card = null;
            try
            {
                int sw = Screen.width, sh = Screen.height;
                full = new Texture2D(sw, sh, TextureFormat.RGB24, false);
                full.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
                full.Apply();

                float aspect = (float)CardW / CardH;

                int cropW, cropH, x, y;
                Rect box;

                Vector2 headCenter;
                float bodyPx;

                if (fitToChar && HeadAnchor && focusOn != null
                    && TryGetHeadAnchor(focusOn, out headCenter, out bodyPx))
                {
                    // 框高 = 頭腳距離 × 倍率。兩個都是骨頭座標算出來的，拉不爆。
                    float h = Mathf.Max(bodyPx * FrameScale, 8f);
                    float w = h * aspect;

                    // 超出畫面就等比例縮（寬高各自 clamp 會把人壓扁）
                    float shrink = 1f;
                    if (w > sw) shrink = Mathf.Min(shrink, sw / w);
                    if (h > sh) shrink = Mathf.Min(shrink, sh / h);
                    w *= shrink;
                    h *= shrink;

                    // 把頭放在框內 HeadAtY 這個高度上：
                    // 頭在框裡的 y = 框底 + HeadAtY * h = 框心 + (HeadAtY - 0.5) * h
                    // 反推框心即可。Unity 的螢幕座標原點在左下，所以 1 是上緣。
                    cropW = Mathf.RoundToInt(w);
                    cropH = Mathf.RoundToInt(h);
                    float cx = headCenter.x;
                    float cy = headCenter.y - (HeadAtY - 0.5f) * h;

                    x = Mathf.Clamp(Mathf.RoundToInt(cx - cropW * 0.5f), 0, Mathf.Max(0, sw - cropW));
                    y = Mathf.Clamp(Mathf.RoundToInt(cy - cropH * 0.5f), 0, Mathf.Max(0, sh - cropH));
                }
                else if (fitToChar && focusOn != null && TryGetCharScreenRect(focusOn, out box))
                {
                    // 四周各留 12% 的邊
                    float padX = box.width * 0.12f;
                    float padY = box.height * 0.12f;
                    box = new Rect(box.x - padX, box.y - padY,
                                   box.width + padX * 2f, box.height + padY * 2f);

                    // 補成卡片比例：只加大不裁掉，確保整個人都在框裡
                    float w = box.width, h = box.height;
                    if (w / h > aspect) h = w / aspect; else w = h * aspect;

                    // 太小的話放大到畫面高度的一半，避免縮圖糊掉
                    float minH = sh * 0.5f;
                    if (h < minH) { h = minH; w = h * aspect; }

                    // 超出畫面時要「等比例」縮，寬高各自 clamp 會破壞比例，
                    // 存出來就會變成橫向被壓扁的樣子。
                    float shrink = 1f;
                    if (w > sw) shrink = Mathf.Min(shrink, sw / w);
                    if (h > sh) shrink = Mathf.Min(shrink, sh / h);
                    w *= shrink;
                    h *= shrink;

                    float cx = box.center.x, cy = box.center.y;
                    cropW = Mathf.RoundToInt(w);
                    cropH = Mathf.RoundToInt(h);
                    x = Mathf.Clamp(Mathf.RoundToInt(cx - cropW * 0.5f), 0, sw - cropW);
                    y = Mathf.Clamp(Mathf.RoundToInt(cy - cropH * 0.5f), 0, sh - cropH);
                }
                else
                {
                    cropH = sh;
                    cropW = Mathf.RoundToInt(cropH * aspect);
                    if (cropW > sw) { cropW = sw; cropH = Mathf.RoundToInt(cropW / aspect); }
                    x = (sw - cropW) / 2;
                    y = (sh - cropH) / 2;
                }

                card = MakeThumb(full, x, y, cropW, cropH, aspect);
                return card.EncodeToPNG();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[ScreenGrab] 擷取失敗: " + e.Message);
                return null;
            }
            finally
            {
                if (full != null) UnityEngine.Object.Destroy(full);
                if (card != null) UnityEngine.Object.Destroy(card);
            }
        }

        /// <summary>
        /// 取景用的兩個數字：頭在螢幕上的位置，以及「腳到頭」在螢幕上有多長。
        ///
        /// **完全不碰任何 Renderer 的 bounds**，只用骨頭的座標。
        ///
        /// 為什麼：第一版拿 objHead 底下網格的包圍盒去量頭，實測回報
        ///   「[ScreenGrab] 頭部範圍不合理（9459 px）」
        /// —— 9459 px 在 1080p 的畫面上是九倍螢幕高。SkinnedMeshRenderer 的 bounds
        /// 是由 rootBone + 綁定姿勢算出來的，KK 的網格算出來常常離譜，
        /// 而那正是舊取景法（全身包圍盒聯集）會把框拖到床上的同一個原因。
        /// 換句話說我第一版只是把同一個壞掉的來源換了個範圍再用一次。
        ///
        /// 骨頭座標就沒有這個問題：Transform.position 是精確值，不是估計值。
        ///   - 位置錨點：objHeadBone（cf_j_head）的座標
        ///   - 尺度：objBodyBone（角色原點，在腳下）到 objHeadBone 的**螢幕**距離
        /// 兩個欄位都是用 dnfile 在遊戲的 Assembly-CSharp 裡確認過的，
        /// 定義在 ChaInfo 上（不是 ChaControl），不是猜的。
        ///
        /// 用「螢幕距離」而不是世界距離，鏡頭遠近、角色躺著站著都自動成立 ——
        /// 人在畫面上看起來多大，框就多大。
        ///
        /// 頭骨頭的原點在頸子上緣、不在頭的正中心，差了大約半顆頭。
        /// 這裡不去猜那半顆頭有多大（那又要引進比例常數），
        /// 因為它是**固定的位移**，由「頭在畫面高度」那個滑桿一次吸收掉就好，
        /// 而錨點本身的穩定性才是這件事的重點。
        /// </summary>
        static bool TryGetHeadAnchor(OCIChar oci, out Vector2 head, out float bodyPx)
        {
            head = Vector2.zero;
            bodyPx = 0f;
            try
            {
                var cam = Camera.main ?? Camera.current;
                var cha = oci.charInfo;
                if (cam == null || cha == null) return false;

                GameObject headObj = null, rootObj = null;
                try { headObj = cha.objHeadBone; } catch { }
                try { rootObj = cha.objBodyBone; } catch { }
                if (headObj == null) return false;

                Vector3 hs = cam.WorldToScreenPoint(headObj.transform.position);
                if (hs.z <= 0f) return false;          // 在相機後面
                head = new Vector2(hs.x, hs.y);

                if (rootObj != null)
                {
                    Vector3 rs = cam.WorldToScreenPoint(rootObj.transform.position);
                    if (rs.z > 0f)
                        bodyPx = Vector2.Distance(head, new Vector2(rs.x, rs.y));
                }

                // 量不到腳（沒有 objBodyBone、或它在相機後面）就退回畫面高度的一半，
                // 至少位置還是對的，只有框的大小變成固定值。
                if (bodyPx < 8f)
                {
                    bodyPx = Screen.height * 0.5f;
                    UnityEngine.Debug.Log("[ScreenGrab] 量不到頭腳距離，框的大小改用固定值"
                                          + "（位置仍以頭為準）");
                }

                return true;
            }
            catch { return false; }
        }

        /// <summary>把角色所有網格的包圍盒投影到螢幕，算出它實際佔的矩形。</summary>
        static bool TryGetCharScreenRect(OCIChar oci, out Rect rect)
        {
            rect = new Rect();
            try
            {
                var cam = Camera.main ?? Camera.current;
                var cha = oci.charInfo;
                if (cam == null || cha == null) return false;

                Bounds? total = null;
                foreach (var r in cha.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;

                    if (total == null) total = r.bounds;
                    else { var b = total.Value; b.Encapsulate(r.bounds); total = b; }
                }
                if (total == null) return false;

                Bounds bb = total.Value;
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                int visible = 0;

                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? bb.min.x : bb.max.x,
                        (i & 2) == 0 ? bb.min.y : bb.max.y,
                        (i & 4) == 0 ? bb.min.z : bb.max.z);

                    var sp = cam.WorldToScreenPoint(c);
                    if (sp.z <= 0f) continue;      // 在相機後面
                    visible++;

                    if (sp.x < minX) minX = sp.x;
                    if (sp.y < minY) minY = sp.y;
                    if (sp.x > maxX) maxX = sp.x;
                    if (sp.y > maxY) maxY = sp.y;
                }

                if (visible < 4 || maxX <= minX || maxY <= minY) return false;

                rect = new Rect(minX, minY, maxX - minX, maxY - minY);
                return true;
            }
            catch { return false; }
        }

        // 這裡原本有一整組「拍照前把工作室相機的注視點移到角色身上、拍完還原」
        // （CenterCameraOn / RestoreCamera / TryGetHeadScreenPos 和它們的反射輔助
        //   FindVector3Member / ReadV3 / WriteV3，外加 _camCtrl 那幾個欄位）。
        //
        // 那是取景的**第一種**做法：動相機。後來改成不動相機、直接在畫面上裁切
        // （見 CapturePng），因為動相機在有工作室相機物件的場景會拍出奇怪的構圖，
        // 而且會改到使用者的場景狀態。改完之後那一整組就沒有任何呼叫端，
        // 留著只會讓人以為相機還會被動到。已整組移除。

        /// <summary>
        /// 把畫面上那塊裁切區變成縮圖。
        ///
        /// 預設（MaxHeight = 0）就是**原封不動地把那塊畫素搬過來**，一次取樣都不做，
        /// 所以品質上限就是畫面本身 —— 這是最清楚的做法，也是腳本那條路在做的事。
        /// 只有在有設高度上限、而且真的超過時才會縮，而且用面積平均。
        /// </summary>
        static Texture2D MakeThumb(Texture2D src, int sx, int sy, int sw, int sh, float aspect)
        {
            if (MaxHeight <= 0 || sh <= MaxHeight)
            {
                var exact = new Texture2D(sw, sh, TextureFormat.RGB24, false);
                exact.SetPixels(src.GetPixels(sx, sy, sw, sh));
                exact.Apply();
                return exact;
            }

            int dh = MaxHeight;
            int dw = Mathf.Max(1, Mathf.RoundToInt(dh * aspect));
            return Downsample(src, sx, sy, sw, sh, dw, dh);
        }

        /// <summary>
        /// 面積平均縮小（box filter）。
        ///
        /// 舊版是每個目標畫素叫一次 GetPixelBilinear —— 那只看來源的 2×2，
        /// 縮小 3 倍以上時中間那些畫素**根本沒被看過**，細紋和花色會直接消失或變成雜訊。
        /// 這裡改成把來源對應的整塊格子全部平均進去，不漏掉任何一個畫素。
        /// </summary>
        static Texture2D Downsample(Texture2D src, int sx, int sy, int sw, int sh, int dw, int dh)
        {
            Color[] block = src.GetPixels(sx, sy, sw, sh);
            var px = new Color[dw * dh];

            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh;
                int y1 = Mathf.Max(y0 + 1, (y + 1) * sh / dh);

                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw;
                    int x1 = Mathf.Max(x0 + 1, (x + 1) * sw / dw);

                    float r = 0f, g = 0f, b = 0f;
                    int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            Color c = block[row + xx];
                            r += c.r; g += c.g; b += c.b; n++;
                        }
                    }

                    px[y * dw + x] = new Color(r / n, g / n, b / n, 1f);
                }
            }

            var dst = new Texture2D(dw, dh, TextureFormat.RGB24, false);
            dst.SetPixels(px);
            dst.Apply();
            return dst;
        }
    }
}
