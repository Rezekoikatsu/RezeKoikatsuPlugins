#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
kkcutscene_prep.py — StudioCutScene 的素材準備與診斷工具

只需要 numpy + PATH 上的 ffmpeg / ffprobe。

    pip install numpy

--------------------------------------------------------------------------
為什麼需要這支
--------------------------------------------------------------------------
StudioCutScene 的 anchors 是「timeline 秒 ↔ 影片秒」的對應關係。
這個對應只對「同一個剪輯」成立。

同一部作品常常有好幾個 render（不同配音、傷あり/なし、2K/4K…），
長度看起來差不多，但實際上可能整體偏移幾秒、甚至中間有增刪。
一旦 videoFile 和音軌來自不同剪輯，症狀是：

  * 開頭就不同步
  * 播一陣子漂掉 → 插件的 hardSeek 把音訊往回拉 → 同一句話講兩次

**videoFile 和音軌必須從同一支 mp4 出來。** 這支工具就是拿來確認這件事的。

--------------------------------------------------------------------------
指令
--------------------------------------------------------------------------
  probe   <資料夾>                    列出所有 mp4/wav 的長度、串流、有無音軌
  extract <資料夾> [--ar 48000]       每支 mp4 抽一份 48kHz wav（同名）
  align   <基準> <其他...>            互相關量偏移，判斷是不是同一個剪輯
  check   <videoFile> <音軌.wav>      一鍵驗證這兩個能不能配在一起  ← 最常用
  master  <來源.mp4> -o <輸出.mp4>    轉成 1080p 短 GOP 的 videoFile
  parent  <Charcard_src.mp4> <候選...>    用「畫面」比對，找出它是從哪一支轉出來的
  matrix  <資料夾>                    所有 mp4 兩兩比對音訊，列出哪些是同一個剪輯

  基準/其他/videoFile 可以直接給 mp4，內部會自動抽音訊，不落地。
"""

import argparse
import json
import os
import subprocess
import sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kkffmpeg                                             # noqa: E402
kkffmpeg.install()

SR = 8000          # 互相關用的取樣率，夠用而且快
VIDEO_EXT = (".mp4", ".mkv", ".mov", ".webm", ".avi")
AUDIO_EXT = (".wav", ".ogg", ".flac", ".m4a", ".mp3")


# ---------------------------------------------------------------- ffmpeg

def run(cmd, **kw):
    return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kw)


def ffprobe(path):
    r = run(["ffprobe", "-v", "error", "-print_format", "json",
             "-show_format", "-show_streams", path])
    if r.returncode != 0:
        return None
    try:
        return json.loads(r.stdout.decode("utf-8", "replace"))
    except Exception:
        return None


def info(path):
    d = ffprobe(path)
    if not d:
        return None
    fmt = d.get("format", {})
    out = {
        "path": path,
        "dur": float(fmt.get("duration", 0) or 0),
        "size": int(fmt.get("size", 0) or 0),
        "video": None,
        "audio": None,
    }
    for s in d.get("streams", []):
        if s.get("codec_type") == "video" and out["video"] is None:
            fr = s.get("r_frame_rate", "0/1")
            try:
                num, den = fr.split("/")
                fps = float(num) / float(den) if float(den) else 0.0
            except Exception:
                fps = 0.0
            out["video"] = {
                "codec": s.get("codec_name"),
                "w": s.get("width"), "h": s.get("height"),
                "fps": fps,
                "pix": s.get("pix_fmt"),
                "range": s.get("color_range"),
                "space": s.get("color_space"),
                "bitrate": int(s.get("bit_rate", 0) or 0),
            }
        elif s.get("codec_type") == "audio" and out["audio"] is None:
            out["audio"] = {
                "codec": s.get("codec_name"),
                "sr": int(s.get("sample_rate", 0) or 0),
                "ch": s.get("channels"),
            }
    return out


def load_mono(path, sr=SR, start=None, dur=None):
    """把任何檔案解成單聲道 float32，不落地。"""
    cmd = ["ffmpeg", "-v", "error"]
    if start is not None:
        cmd += ["-ss", "%.3f" % start]
    if dur is not None:
        cmd += ["-t", "%.3f" % dur]
    cmd += ["-i", path, "-vn", "-ac", "1", "-ar", str(sr),
            "-f", "f32le", "-acodec", "pcm_f32le", "-"]
    r = run(cmd)
    if r.returncode != 0 or not r.stdout:
        raise RuntimeError("解不出音訊: %s\n%s" % (path, r.stderr.decode("utf-8", "replace")[-400:]))
    return np.frombuffer(r.stdout, dtype=np.float32).astype(np.float64)


# ---------------------------------------------------------------- 互相關

def xcorr_lag(a, b, sr=SR, max_lag=None):
    """
    回傳 (lag_秒, 峰值比)。
    lag > 0 代表 b 比 a 晚（b 要往前裁 lag 秒才會對上 a）。
    峰值比 = 主峰 / 護欄外的次高峰，< 1.6 就是峰不明顯、數字別信。
    """
    n = 1
    need = len(a) + len(b)
    while n < need:
        n *= 2
    fa = np.fft.rfft(a - a.mean(), n)
    fb = np.fft.rfft(b - b.mean(), n)
    cc = np.fft.irfft(fa * np.conj(fb), n)
    cc = np.concatenate((cc[-(len(b) - 1):], cc[:len(a)]))
    lags = np.arange(-(len(b) - 1), len(a))

    if max_lag is not None:
        m = int(max_lag * sr)
        keep = np.abs(lags) <= m
        cc, lags = cc[keep], lags[keep]

    i = int(np.argmax(cc))
    peak = cc[i]

    # 拋物線內插做次取樣點精修
    frac = 0.0
    if 0 < i < len(cc) - 1:
        y0, y1, y2 = cc[i - 1], cc[i], cc[i + 1]
        den = (y0 - 2 * y1 + y2)
        if abs(den) > 1e-12:
            frac = 0.5 * (y0 - y2) / den

    guard = max(1, int(0.25 * sr))
    mask = np.ones(len(cc), dtype=bool)
    lo, hi = max(0, i - guard), min(len(cc), i + guard + 1)
    mask[lo:hi] = False
    second = np.max(cc[mask]) if mask.any() else 0.0
    ratio = float(peak / second) if second > 0 else float("inf")

    return float(-(lags[i] + frac) / sr), ratio


def envelope(x, sr=SR, hop=0.010):
    h = max(1, int(sr * hop))
    n = len(x) // h
    if n < 2:
        return x
    return np.sqrt((x[:n * h].reshape(n, h) ** 2).mean(axis=1) + 1e-12)


def compare(ref_path, other_path, seg=30.0, search=None):
    """量兩個檔案的偏移與流速差。回傳 dict。"""
    a = load_mono(ref_path)
    b = load_mono(other_path)
    da, db = len(a) / SR, len(b) / SR

    lag, ratio = xcorr_lag(a, b, max_lag=search)
    mode = "波形"
    if ratio < 1.6:
        ea, eb = envelope(a), envelope(b)
        lag2, ratio2 = xcorr_lag(ea, eb, sr=100, max_lag=search)
        if ratio2 > ratio:
            lag, ratio, mode = lag2, ratio2, "能量包絡"

    # 頭段 / 尾段各量一次 → 有沒有流速差
    def part_lag(t0):
        pa = load_mono(ref_path, start=t0, dur=seg)
        pb = load_mono(other_path, start=t0 + lag, dur=seg)
        if len(pa) < SR or len(pb) < SR:
            return None
        l, _ = xcorr_lag(pa, pb, max_lag=3.0)
        return l

    span = min(da, db)
    head = part_lag(max(0.0, span * 0.10))
    tail = part_lag(max(0.0, span * 0.85 - seg))

    tempo = None
    if head is not None and tail is not None:
        dist = (span * 0.85 - seg) - (span * 0.10)
        if dist > 30:
            tempo = 1.0 + (tail - head) / dist

    return dict(ref_dur=da, other_dur=db, lag=lag, ratio=ratio, mode=mode,
                head=head, tail=tail, tempo=tempo)


# ---------------------------------------------------------------- 畫面比對

def load_frames(path, start, dur, fps=2.0, w=64, h=36):
    """抓一段畫面成 (幀數, w*h) 的灰階矩陣。一次 ffmpeg 呼叫，不落地。"""
    r = run(["ffmpeg", "-v", "error", "-ss", "%.3f" % start, "-t", "%.3f" % dur,
             "-i", path, "-an",
             "-vf", "fps=%g,scale=%d:%d,format=gray" % (fps, w, h),
             "-f", "rawvideo", "-"])
    if r.returncode != 0 or not r.stdout:
        raise RuntimeError("抓不到畫面: %s\n%s" % (path, r.stderr.decode("utf-8", "replace")[-300:]))
    a = np.frombuffer(r.stdout, dtype=np.uint8).astype(np.float64)
    n = len(a) // (w * h)
    return a[:n * w * h].reshape(n, w * h)


def best_frame_lag(src, cand, fps, max_lag_s):
    """把 src 在 cand 上滑動，回傳 (lag_秒, 最小平均絕對誤差, 次佳誤差)。"""
    k = int(max_lag_s * fps)
    best, best_lag, scores = None, 0.0, []
    for d in range(-k, k + 1):
        if d >= 0:
            a, b = src[:len(src) - d], cand[d:d + len(src) - d]
        else:
            a, b = src[-d:], cand[:len(src) + d]
        m = min(len(a), len(b))
        if m < 5:
            continue
        # 各自去掉整體亮度差，避免只是曝光不同就判成不一樣
        aa = a[:m] - a[:m].mean(axis=1, keepdims=True)
        bb = b[:m] - b[:m].mean(axis=1, keepdims=True)
        err = float(np.abs(aa - bb).mean())
        scores.append((err, d / fps))
        if best is None or err < best:
            best, best_lag = err, d / fps
    scores.sort()
    second = scores[1][0] if len(scores) > 1 else float("inf")
    # 次佳取「離最佳至少 0.5 秒」的那些，才有鑑別度
    far = [e for e, l in scores if abs(l - best_lag) > 0.5]
    if far:
        second = min(far)
    return best_lag, best, second


# ---------------------------------------------------------------- 指令


def cmd_parent(args):
    si = info(args.src)
    if si is None:
        print("讀不到 %s" % args.src); return
    dur = si["dur"]
    win = min(args.window, max(20.0, dur * 0.2))
    start = dur * 0.30
    print("比對片段: 第 %.1f–%.1f 秒，每秒 %g 張，64x36 灰階\n" % (start, start + win, args.fps))

    src = load_frames(args.src, start, win, args.fps)
    print("  %s  取到 %d 張\n" % (os.path.basename(args.src), len(src)))

    rows = []
    for c in args.cands:
        try:
            cand = load_frames(c, start - args.search, win + 2 * args.search, args.fps)
        except Exception as ex:
            print("  %-46s  失敗: %s" % (os.path.basename(c)[:46], ex)); continue
        lag, err, second = best_frame_lag(src, cand, args.fps, args.search)
        lag -= args.search   # 補回前面多抓的那段
        rows.append((err, lag, second, c))

    rows.sort()
    print("%-46s %9s %9s %9s" % ("候選", "誤差", "偏移秒", "鑑別度"))
    print("-" * 80)
    for err, lag, second, c in rows:
        sep = (second / err) if err > 0 else float("inf")
        print("%-46s %9.3f %9.3f %9.2f" % (os.path.basename(c)[:46], err, lag, sep))

    if rows:
        err, lag, second, c = rows[0]
        sep = (second / err) if err > 0 else float("inf")
        print()

        # 兩個候選誤差差不到 5% → 畫面分不出來（通常是只換配音、畫面完全相同）
        if len(rows) > 1 and rows[1][0] <= err * 1.05:
            tied = [os.path.basename(r[3]) for r in rows if r[0] <= err * 1.05]
            print("判定: 分不出來 —— 這幾支的畫面幾乎一模一樣:")
            for t in tied:
                print("        %s" % t)
            print("      （很正常，只換配音的話畫面本來就相同）")
            print("      → 改跑 matrix 用音訊分辨，或直接任選一支、用它自己的 wav 當音軌。")
            return

        if sep >= 1.5:
            print("判定: %s" % os.path.basename(c))
            if abs(lag) < 0.05:
                print("      就是它，而且沒有偏移。")
                print("      → 音軌用這支 mp4 抽出來的 wav，anchors 直接沿用。")
            else:
                print("      就是它，但差 %+.3f 秒。" % lag)
                print("      → anchors 的影片秒數要整組 %+.3f，或直接改用它自己的 wav。" % lag)
        else:
            print("判定: 分不出來（鑑別度 %.2f < 1.5）。" % sep)
            print("      幾支來源的畫面太像（可能只有配音不同）。")
            print("      這種情況下畫面比對沒用，改跑 matrix 用音訊分辨。")


def cmd_matrix(args):
    files = [os.path.join(args.folder, f) for f in sorted(os.listdir(args.folder))
             if f.lower().endswith(VIDEO_EXT)]
    files = [f for f in files if info(f) and info(f)["audio"]]
    if len(files) < 2:
        print("需要至少兩支「有音軌」的影片"); return

    print("兩兩比對音訊（基準 = 第一支）\n")
    base = files[0]
    print("基準: %s\n" % os.path.basename(base))
    print("%-46s %10s %9s %10s  %s" % ("檔案", "長度", "偏移秒", "峰值比", "判定"))
    print("-" * 96)
    for f in files:
        if f == base:
            print("%-46s %10.3f %9s %10s  %s" %
                  (os.path.basename(f)[:46], info(f)["dur"], "-", "-", "（基準）"))
            continue
        c = compare(base, f, seg=args.seg, search=args.search)
        if c["ratio"] < 1.6:
            v = "不同剪輯"
        elif abs(c["lag"]) < 0.05:
            v = "同剪輯、零偏移 ✓"
        else:
            v = "同剪輯，差 %+.3fs" % c["lag"]
        print("%-46s %10.3f %9.3f %10.2f  %s" %
              (os.path.basename(f)[:46], c["other_dur"], c["lag"], c["ratio"], v))

    print("\n「同剪輯、零偏移」的幾支可以共用同一組 anchors，當成配音 variants。")


def cmd_probe(args):
    files = []
    for f in sorted(os.listdir(args.folder)):
        if f.lower().endswith(VIDEO_EXT + AUDIO_EXT):
            files.append(os.path.join(args.folder, f))
    if not files:
        print("這個資料夾沒有影音檔"); return

    print("%-58s %10s %9s  %s" % ("檔案", "長度", "大小MB", "串流"))
    print("-" * 110)
    for p in files:
        i = info(p)
        if not i:
            print("%-58s   (讀不到)" % os.path.basename(p)[:58]); continue
        v = i["video"]; a = i["audio"]
        desc = []
        if v:
            desc.append("%s %dx%d %.3ffps %s%s %s" % (
                v["codec"], v["w"] or 0, v["h"] or 0, v["fps"], v["pix"] or "",
                "/" + v["range"] if v["range"] else "",
                ("%.1fMbps" % (v["bitrate"] / 1e6)) if v["bitrate"] else ""))
        if a:
            desc.append("音訊 %s %dHz %dch" % (a["codec"], a["sr"], a["ch"] or 0))
        elif v:
            desc.append("**無音軌**")
        print("%-58s %10.3f %9.1f  %s" % (
            os.path.basename(p)[:58], i["dur"], i["size"] / 1e6, " | ".join(desc)))

    print("\n提示：長度只差幾毫秒不代表同一個剪輯，要用 align / check 量。")


def cmd_extract(args):
    outdir = args.out or args.folder
    os.makedirs(outdir, exist_ok=True)
    n = 0
    for f in sorted(os.listdir(args.folder)):
        if not f.lower().endswith(VIDEO_EXT):
            continue
        src = os.path.join(args.folder, f)
        dst = os.path.join(outdir, os.path.splitext(f)[0] + ".wav")
        i = info(src)
        if i is not None and i["audio"] is None:
            print("  跳過（沒有音軌）: %s" % f); continue
        if os.path.exists(dst) and not args.force:
            print("  跳過（已存在）: %s" % os.path.basename(dst)); continue
        print("  抽音訊: %s" % f)
        r = run(["ffmpeg", "-v", "error", "-y", "-i", src, "-vn",
                 "-c:a", "pcm_s16le", "-ar", str(args.ar), dst])
        if r.returncode != 0:
            print("    失敗: %s" % r.stderr.decode("utf-8", "replace")[-300:])
        else:
            n += 1
    print("\n完成 %d 個。這些 wav 跟各自的 mp4 保證是同一個剪輯。" % n)


def report(name, c):
    print("\n--- %s ---" % name)
    print("  長度      基準 %.3fs / 目標 %.3fs   差 %+.3fs" %
          (c["ref_dur"], c["other_dur"], c["other_dur"] - c["ref_dur"]))
    print("  偏移      %+.3f s   (%s，峰值比 %.2f)" % (c["lag"], c["mode"], c["ratio"]))
    if c["ratio"] < 1.6:
        print("            ** 峰不明顯，這個數字不可信 —— 很可能根本不是同一個剪輯 **")
    if c["head"] is not None and c["tail"] is not None:
        print("  頭/尾殘差 %+.3f / %+.3f s" % (c["head"], c["tail"]))
        if c["tempo"]:
            print("  流速比    %.6f" % c["tempo"])
            if abs(c["tempo"] - 1.0) > 0.0005:
                print("            ** 有流速差，不是單純平移 **")
    verdict = "同一個剪輯" if (c["ratio"] >= 3.0 and abs(c["lag"]) < 0.05
                            and (c["tempo"] is None or abs(c["tempo"] - 1) < 0.0005)) else None
    if verdict:
        print("  判定      %s，可以直接配對" % verdict)
    elif c["ratio"] >= 3.0:
        print("  判定      同一個剪輯但有偏移 %+.3fs —— anchors 的影片秒數要整組加上這個值" % c["lag"])
    else:
        print("  判定      不是同一個剪輯，不要混用")


def cmd_align(args):
    for o in args.others:
        report(os.path.basename(o), compare(args.ref, o, seg=args.seg, search=args.search))


def cmd_check(args):
    vi = info(args.video)
    if vi is None:
        print("讀不到 %s" % args.video); return
    print("videoFile : %s" % os.path.basename(args.video))
    print("            長度 %.3fs" % vi["dur"], end="")
    if vi["video"]:
        print("  %dx%d %.3ffps" % (vi["video"]["w"], vi["video"]["h"], vi["video"]["fps"]), end="")
    print("  音軌: %s" % ("有（建議轉檔時加 -an）" if vi["audio"] else "無 ✓"))

    ai = info(args.audio)
    if ai is None:
        print("讀不到 %s" % args.audio); return
    print("音軌檔    : %s  長度 %.3fs  %dHz" %
          (os.path.basename(args.audio), ai["dur"],
           ai["audio"]["sr"] if ai["audio"] else 0))

    if not vi["audio"]:
        print("\n!! videoFile 沒有音軌，無法直接比對。")
        print("   請改用「當初轉出 videoFile 的那支原始 mp4」來跑 check。")
        return

    c = compare(args.video, args.audio, seg=args.seg, search=args.search)
    report("videoFile 的音軌  vs  音軌檔", c)

    print("\n結論:")
    if c["ratio"] < 1.6:
        print("  這兩個不是同一個剪輯。anchors 不可能對得上，一定會不同步、也會出現重播。")
        print("  正確做法：用 extract 把 videoFile 那支 mp4 自己的音訊抽出來當音軌。")
    elif abs(c["lag"]) > 0.05:
        print("  同一個剪輯但差 %+.3f 秒。" % c["lag"])
        print("  兩條路：(1) 音軌改用 videoFile 自己抽出來的 wav；")
        print("          (2) 把 anchors 裡所有『影片秒數』整組 %+.3f。" % c["lag"])
    else:
        print("  對得上，可以直接用。")


def cmd_master(args):
    out = args.out
    vf = "scale=-2:%d" % args.height
    cmd = ["ffmpeg", "-y", "-i", args.src, "-vf", vf,
           "-c:v", "libx264", "-preset", args.preset, "-crf", str(args.crf),
           "-pix_fmt", "yuv420p",
           "-g", str(args.gop), "-keyint_min", str(args.gop), "-sc_threshold", "0",
           "-movflags", "+faststart", "-an", out]
    print("關鍵影格每 %d 幀 → seek 定位誤差上限約 %.2f 秒" % (args.gop, args.gop / 60.0))
    print(" ".join('"%s"' % c if " " in c else c for c in cmd))
    if args.dry:
        return
    r = subprocess.run(cmd)
    if r.returncode == 0:
        i = info(out)
        print("\n完成: %s  %.3fs  %.1f MB" % (out, i["dur"], i["size"] / 1e6))
        print("別忘了用同一支來源跑 extract，音軌要跟它同一個剪輯。")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")

    p = sub.add_parser("probe");   p.add_argument("folder"); p.set_defaults(f=cmd_probe)

    p = sub.add_parser("extract"); p.add_argument("folder")
    p.add_argument("--out"); p.add_argument("--ar", type=int, default=48000)
    p.add_argument("--force", action="store_true"); p.set_defaults(f=cmd_extract)

    p = sub.add_parser("align");   p.add_argument("ref"); p.add_argument("others", nargs="+")
    p.add_argument("--seg", type=float, default=30.0)
    p.add_argument("--search", type=float, default=None); p.set_defaults(f=cmd_align)

    p = sub.add_parser("check");   p.add_argument("video"); p.add_argument("audio")
    p.add_argument("--seg", type=float, default=30.0)
    p.add_argument("--search", type=float, default=None); p.set_defaults(f=cmd_check)

    p = sub.add_parser("parent"); p.add_argument("src"); p.add_argument("cands", nargs="+")
    p.add_argument("--window", type=float, default=60.0)
    p.add_argument("--fps", type=float, default=2.0)
    p.add_argument("--search", type=float, default=5.0); p.set_defaults(f=cmd_parent)

    p = sub.add_parser("matrix"); p.add_argument("folder")
    p.add_argument("--seg", type=float, default=30.0)
    p.add_argument("--search", type=float, default=None); p.set_defaults(f=cmd_matrix)

    p = sub.add_parser("master");  p.add_argument("src"); p.add_argument("-o", "--out", required=True)
    p.add_argument("--gop", type=int, default=30); p.add_argument("--height", type=int, default=1080)
    p.add_argument("--crf", type=int, default=20); p.add_argument("--preset", default="medium")
    p.add_argument("--dry", action="store_true"); p.set_defaults(f=cmd_master)

    a = ap.parse_args()
    if not a.cmd:
        ap.print_help(); sys.exit(1)
    a.f(a)


if __name__ == "__main__":
    main()
