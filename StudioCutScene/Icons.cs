using System;
using System.Collections.Generic;
using UnityEngine;

namespace StudioCutScene
{
    /// <summary>
    /// 面板用的小圖示，全部用程式畫出來（24×24）。
    ///
    /// 為什麼不帶圖檔：插件維持單一 dll，不用處理資源路徑、也不會有找不到圖的狀況。
    /// 邊緣用 3×3 超取樣算覆蓋率當 alpha，所以斜邊和圓角是平滑的，不會有鋸齒。
    /// 圓角一律給得很大，看起來圓潤一點。
    /// </summary>
    public static class Icons
    {
        const int N = 24;
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        public static Texture2D Play    { get { return Get("play"); } }
        public static Texture2D Pause   { get { return Get("pause"); } }
        public static Texture2D Stop    { get { return Get("stop"); } }
        public static Texture2D Replay  { get { return Get("replay"); } }
        public static Texture2D Start   { get { return Get("start"); } }
        public static Texture2D Reset   { get { return Get("reset"); } }

        static Texture2D Get(string name)
        {
            Texture2D t;
            if (cache.TryGetValue(name, out t) && t != null) return t;
            t = Build(name);
            cache[name] = t;
            return t;
        }

        // ---------------------------------------------------------------- 形狀

        static bool InRoundRect(float x, float y, float cx, float cy, float hw, float hh, float r)
        {
            float dx = Mathf.Abs(x - cx) - (hw - r);
            float dy = Mathf.Abs(y - cy) - (hh - r);
            if (dx <= 0f && dy <= 0f) return true;
            dx = Mathf.Max(dx, 0f); dy = Mathf.Max(dy, 0f);
            return dx * dx + dy * dy <= r * r;
        }


        static float Side(float px, float py, float ax, float ay, float bx, float by)
        {
            return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        }

        /// <summary>
        /// 圓角三角形＝把三個頂點朝重心各縮進 r，再在縮進後的頂點放圓盤補回去。
        ///
        /// 血淚：第一版寫成 Lerp(v, 重心, r * 0.55f)，把 r 當成插值比例。
        /// r = 2.2 時比例變成 1.21 —— 直接越過重心、三角形翻面，
        /// 結果整顆按鈕變成一塊實心色塊（播放鍵看起來就是個正方形）。
        /// 縮進距離要除以「頂點到重心的距離」才是比例。
        /// </summary>
        static bool InRoundTri(float x, float y,
                               float ax, float ay, float bx, float by, float cx, float cy, float r)
        {
            float gx = (ax + bx + cx) / 3f, gy = (ay + by + cy) / 3f;

            float t1 = Shrink(ax, ay, gx, gy, r);
            float t2 = Shrink(bx, by, gx, gy, r);
            float t3 = Shrink(cx, cy, gx, gy, r);
            float ax2 = Mathf.Lerp(ax, gx, t1), ay2 = Mathf.Lerp(ay, gy, t1);
            float bx2 = Mathf.Lerp(bx, gx, t2), by2 = Mathf.Lerp(by, gy, t2);
            float cx2 = Mathf.Lerp(cx, gx, t3), cy2 = Mathf.Lerp(cy, gy, t3);

            float s1 = Side(x, y, ax2, ay2, bx2, by2);
            float s2 = Side(x, y, bx2, by2, cx2, cy2);
            float s3 = Side(x, y, cx2, cy2, ax2, ay2);
            if ((s1 >= 0f && s2 >= 0f && s3 >= 0f) || (s1 <= 0f && s2 <= 0f && s3 <= 0f))
                return true;

            // 縮進後的三角形「往外長 r」。用「到三條邊的距離」而不是只在頂點放圓盤 ——
            // 只放圓盤的話，邊的中段會在圓盤和三角形之間留下一格寬的洞。
            float d = Mathf.Min(SegDist(x, y, ax2, ay2, bx2, by2),
                      Mathf.Min(SegDist(x, y, bx2, by2, cx2, cy2),
                                SegDist(x, y, cx2, cy2, ax2, ay2)));
            return d <= r;
        }

        /// <summary>點到線段的距離。</summary>
        static float SegDist(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float len2 = vx * vx + vy * vy;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(((px - ax) * vx + (py - ay) * vy) / len2);
            float dx = px - (ax + vx * t), dy = py - (ay + vy * t);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>頂點朝重心縮進 r 的距離，換算成插值比例。</summary>
        static float Shrink(float vx, float vy, float gx, float gy, float r)
        {
            float d = Mathf.Sqrt((vx - gx) * (vx - gx) + (vy - gy) * (vy - gy));
            return d < 1e-3f ? 0f : Mathf.Clamp(r / d, 0f, 0.8f);
        }

        /// <summary>環，角度以度為單位（0 = 右，逆時針）。</summary>
        static bool InArc(float x, float y, float cx, float cy,
                          float rIn, float rOut, float a0, float a1)
        {
            float dx = x - cx, dy = y - cy;
            float d2 = dx * dx + dy * dy;
            if (d2 < rIn * rIn || d2 > rOut * rOut) return false;
            float a = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            if (a < 0f) a += 360f;
            if (a0 <= a1) return a >= a0 && a <= a1;
            return a >= a0 || a <= a1;          // 跨過 0 度
        }

        // ---------------------------------------------------------------- 產生

        delegate bool Shape(float x, float y);

        static Texture2D Build(string name)
        {
            Color col;
            Shape shape = Pick(name, out col);

            var tex = new Texture2D(N, N, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            for (int py = 0; py < N; py++)
            {
                for (int px = 0; px < N; px++)
                {
                    // 3×3 超取樣：覆蓋率當 alpha，邊緣就平滑了
                    int hit = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                            if (shape(px + (sx + 0.5f) / 3f, py + (sy + 0.5f) / 3f)) hit++;
                    float a = hit / 9f;
                    tex.SetPixel(px, py, new Color(col.r, col.g, col.b, col.a * a));
                }
            }
            tex.Apply();
            return tex;
        }

        static Shape Pick(string name, out Color col)
        {
            switch (name)
            {
                case "play":
                    col = new Color(0.97f, 0.97f, 0.97f);
                    return delegate(float x, float y) {
                        return InRoundTri(x, y, 7.5f, 4.5f, 7.5f, 19.5f, 19.5f, 12f, 1.8f);
                    };

                case "pause":
                    col = new Color(0.43f, 0.76f, 0.90f);
                    return delegate(float x, float y) {
                        return InRoundRect(x, y, 9f, 12f, 2.4f, 8f, 2.0f)
                            || InRoundRect(x, y, 15f, 12f, 2.4f, 8f, 2.0f);
                    };

                case "stop":
                    col = new Color(0.88f, 0.36f, 0.36f);
                    return delegate(float x, float y) {
                        return InRoundRect(x, y, 12f, 12f, 7.5f, 7.5f, 3.2f);
                    };

                case "replay":
                    col = new Color(0.95f, 0.78f, 0.22f);
                    return delegate(float x, float y) {
                        // 缺一角的環 + 箭頭，表示「繞回去再來一次」
                        return InArc(x, y, 12f, 12f, 5.0f, 8.0f, 300f, 220f)
                            || InRoundTri(x, y, 15.5f, 1.5f, 15.5f, 9.5f, 22f, 5.5f, 1.6f);
                    };

                case "start":
                    // 跟播放鍵同一個樣子（白色三角形）—— 段落那一列的意思也是「從這裡播」
                    col = new Color(0.97f, 0.97f, 0.97f);
                    return delegate(float x, float y) {
                        return InRoundTri(x, y, 7.5f, 4.5f, 7.5f, 19.5f, 19.5f, 12f, 1.8f);
                    };

                case "reset":
                    col = new Color(0.62f, 0.67f, 0.73f);
                    return delegate(float x, float y) {
                        // 反方向的環 + 箭頭，跟 replay 一眼分得出來
                        return InArc(x, y, 12f, 12f, 5.0f, 7.6f, 320f, 240f)
                            || InRoundTri(x, y, 8.5f, 1.5f, 8.5f, 9.5f, 2f, 5.5f, 1.6f);
                    };

                default:
                    col = Color.white;
                    return delegate(float x, float y) {
                        return InRoundRect(x, y, 12f, 12f, 7f, 7f, 3f);
                    };
            }
        }
    }
}
