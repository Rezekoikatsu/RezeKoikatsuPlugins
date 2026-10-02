# -*- coding: utf-8 -*-
"""kktl_scan —— 看一張卡的 timeline 裡有哪些軌道，必要時把壞掉的那條拿掉。

為什麼需要這支
==============
Timeline 在載入一條軌道時，會拿 (owner, id) 去它自己的登記表裡找對應的
「interpolable model」，找到之後用那個 model 的規則把 XML 裡的值讀成物件，
播放時再把那個物件轉型成它預期的型別。

問題就出在**不同遊戲的 Timeline 登記的東西不一樣**。同一個 id，KK 版可能
讀成 byte、KKS 版讀成 float；有些 id 根本只有某一版才有。拿 KKS 做的卡
到 KK 裡播，那條軌道每一幀都會丟：

    InvalidCastException: Cannot cast from source type to destination type.
      Timeline.BuiltInInterpolables.CharacterStateMisc()+(...)➞ void [5]
      Timeline.Interpolable.InterpolateBefore(object, object, float)

`InterpolateBefore` 是「還沒到第一格、先維持第一格的值」那條路，所以它
從 0 秒就開始噴，不是播到某個點才炸 —— 這也是為什麼刷得那麼快。

錯誤訊息只講得出是哪個 method、第幾個 lambda，講不出是**哪一條軌道**、
哪一個物件。一張合併卡有好幾千條軌道，手動在 Timeline 介面裡翻不切實際，
所以這支直接把整份清單倒出來，找到之後一行指令拿掉。

用法
====
    # 看這張卡有哪些軌道
    python kktl_scan.py <卡片.png>

    # 只看可疑的（角色狀態類、以及只有某一版遊戲才有的）
    python kktl_scan.py <卡片.png> --suspect

    # 一次掃一整個資料夾（或好幾張卡）
    python kktl_scan.py <資料夾> --suspect
    python kktl_scan.py a.png b.png c.png --suspect

    # 把指定的軌道整條拿掉，寫成新的一張卡（原檔不動）
    python kktl_scan.py <卡片.png> --drop Timeline:tears --out <修好的.png>
    python kktl_scan.py <卡片.png> --drop Timeline:tears,Timeline:blush --out 新卡.png

--drop 的格式是 owner:id，可以給好幾組用逗號分開。owner 留空（例如 :timeScale）
就是不分 owner，只比對 id。
"""

import argparse
import os
import shutil
import re
import sys

import kkscene2 as S


RE_KEYFRAME = re.compile(
    r'<keyframe\b[^>]*/>|<keyframe\b[^>]*>(?:(?!</?keyframe\b).)*?</keyframe>', re.S)

# Timeline 的 CharacterStateMisc 這一組，各版遊戲登記的東西和型別都不一樣，
# 是跨遊戲拿卡最容易中獎的地方（來源：IllusionMods/HSPlugins Timeline.Core）。
#   KK        tears(byte)、blush(float)、femaleNipples(float)
#   AI/HS2    skinWetness(float)、skinGloss(float)、blush、femaleNipples
#   HS        femaleTears(byte)、femaleBlush、femaleNipples、femaleSkinShine
CHARACTER_STATE = {
    "tears", "blush", "femaleNipples", "skinWetness", "skinGloss",
    "femaleTears", "femaleBlush", "femaleSkinShine",
}

# Studio 的節點型別
TYPE_NAME = {0: "角色", 1: "物件", 2: "燈光", 3: "資料夾", 4: "路線", 5: "相機"}

# 這些軌道的 interpolateBefore 第一件事就是把 oci 轉成 OCIChar。
# 指到不是角色的節點時，那個轉型會丟 ——
#   InvalidCastException: Cannot cast from source type to destination type.
# 跟「跨遊戲型別不合」丟的是**同一句話**，所以光看訊息分不出是哪一種，
# 但這一種可以從卡片本身量出來：看 objectIndex 指到的節點是不是 type 0。
# 只放「一定是角色」的。KKPE 的 bonePos / blendShape / collider 這些**物件也有**
# （道具一樣有骨架可以調），原作者的卡上本來就會指到物件，列進來只會製造假警報 ——
# 實測未合併的原始卡就被誤報了 4 種。寧可少報也不要叫人去刪好的軌道。
CHAR_ONLY = {
    "tears", "blush", "femaleNipples", "skinWetness", "skinGloss",
    "femaleTears", "femaleBlush", "femaleSkinShine",
    "charClothes", "charAccessory", "charHair", "charFace",
}

ITEM_ONLY = {"itemColor1", "itemColor2", "itemColor3", "itemAlpha"}

# 合併卡真正需要的 Timeline 內建軌道。其餘的都是「效果」類 ——
# 拿掉會少掉那個效果，但不會讓卡片壞掉。
#
# 為什麼需要「一次全清」這個選項：例外會讓 Timeline 的 Recurse 整棵中斷，
# 所以**一次只看得到一個出錯的群組**。修掉 CharacterStateMisc 之後，
# 遍歷才走得更遠，接著撞到 Item()，修完可能還有下一個。
# 每修一輪要重寫 1.3 GB 的卡再重開遊戲，一層一層剝太慢了，
# 所以給一個「非必要的全部拿掉」，一次問完。
CORE_TIMELINE = {
    "guideObjectPos", "guideObjectRot", "guideObjectScale",
    "objectEnabled", "timeScale", "cameraPos", "cameraRot",
}

# 「已經有證據會炸」的清單 —— 跟 CORE_TIMELINE 的反面（--drop-noncore）不一樣。
#
# --drop-noncore 是一把大刀：只要不在核心清單就砍，不管那條會不會出問題。
# 快，但會砍到無辜的。這一份只收「log 的堆疊真的指名過那個 method」的：
#
#   Timeline.BuiltInInterpolables.CharacterStateMisc()   →  tears、blush、femaleNipples
#   Timeline.BuiltInInterpolables.Item()                 →  itemColor1~3、itemAlpha
#   Timeline.BuiltInInterpolables.GetClothesValue()／
#     InterpolateClothes()                               →  charClothes
#
# 之後再撞到新的群組，把那一組的 id 加進來就好 —— 堆疊會告訴你是哪個 method。
#
# 這份清單是一輪一輪長出來的，而且順序不是偶然：例外會讓 Timeline 的 Recurse
# 整棵中斷，所以一次只看得到**最前面**那個壞掉的群組。修掉一個，遍歷才走得更遠，
# 下一個才現形。實測錯誤數 13（CharacterStateMisc）→ 51（Item）→ 6（Clothes）。
KNOWN_BROKEN = {
    "tears", "blush", "femaleNipples",
    "itemColor1", "itemColor2", "itemColor3", "itemAlpha",
    "charClothes",
}
LIGHT_ONLY = {"lightRange", "lightSpotAngle", "lightStrength", "lightColor"}

# 這些是每張卡都有、不會有跨版問題的，列出來只是為了讓 --suspect 的清單乾淨
COMMON = {
    "guideObjectPos", "guideObjectRot", "guideObjectScale",
    "objectEnabled", "timeScale", "itemColor1", "itemColor2", "itemColor3",
    "constraintEnabled", "cameraPos", "cameraRot",
}


def keyframe_values(blk):
    """每一格「自己的」value 屬性。

    不能整塊 findall —— 每個 <keyframe> 裡面還包著兩個 <curveKeyframe>，
    它們也有 value=，那是曲線參數不是資料。只取 keyframe 開頭標籤上的屬性。
    """
    out = []
    for kf in RE_KEYFRAME.findall(blk):
        head = kf.split(">", 1)[0]
        out.append(re.findall(r'\b(value[A-Za-z]*)="([^"]*)"', head))
    return out


def tracks(scene):
    """[(owner, id, 幾格, [每格的 value 屬性], 這條的原始文字)]"""
    xml = scene.timeline_xml()
    if not xml:
        return []
    body = S.split_root(xml)[1]
    out = []
    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        head = hm.group(0) if hm else blk
        mo = re.search(r'owner="([^"]*)"', head)
        mi = re.search(r'\bid="([^"]*)"', head)
        owner = mo.group(1) if mo else ""
        tid = mi.group(1) if mi else "?"
        moi = re.search(r'objectIndex="(-?\d+)"', head)
        oidx = int(moi.group(1)) if moi else None
        vals = keyframe_values(blk)
        out.append((owner, tid, len(vals), vals, blk, oidx))
    return out


def rank_to_node(scene):
    """objectIndex（RANK）→ 節點。軌道就是靠這個數字認自己要動哪個東西。"""
    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    return {r: by_key[k] for k, r in ranks.items() if k in by_key}


def expected_type(tid):
    """這個 id 的軌道應該指到哪一種節點；沒有限制就回 None。"""
    if tid in CHAR_ONLY:
        return 0
    if tid in ITEM_ONLY:
        return 1
    if tid in LIGHT_ONLY:
        return 2
    return None


def describe_values(all_vals):
    """整條軌道的值長什麼樣：單一數值就給範圍，多分量就給第一格的樣子。

    「範圍」是判斷『刪掉會不會有差』的關鍵 —— 整條從頭到尾都是 0 的軌道，
    刪掉不會少任何看得到的東西；有變化的才是真的在做事。
    """
    if not all_vals:
        return "（沒有關鍵影格）", True
    names = {k for kf in all_vals for k, _ in kf}
    if names == {"value"}:
        nums, texts = [], set()
        for kf in all_vals:
            for _, v in kf:
                texts.add(v)
                try:
                    nums.append(float(v))
                except ValueError:
                    pass
        if len(nums) == len(all_vals):
            lo, hi = min(nums), max(nums)
            flat = abs(hi - lo) < 1e-9
            return ("value 一直是 %g" % lo) if flat else ("value %g ~ %g" % (lo, hi)), flat
        return "value " + "/".join(sorted(texts)[:4]), len(texts) <= 1
    first = ", ".join("%s=%s" % (k, v) for k, v in all_vals[0])
    return first, False


def summarise(rows, suspect_only=False, r2n=None):
    agg = {}
    for owner, tid, nkf, vals, _, oidx in rows:
        key = (owner, tid)
        if key not in agg:
            agg[key] = [0, 0, [], []]
        agg[key][0] += 1
        agg[key][1] += nkf
        agg[key][2].extend(vals)
        agg[key][3].append(oidx)
    lines, wrong = [], []
    for (owner, tid), (ntrack, nkf, vals, idxs) in sorted(agg.items()):
        want = expected_type(tid)
        types, missing = {}, 0
        for oi in idxs:
            if oi is None or oi < 0:
                continue
            node = (r2n or {}).get(oi)
            if node is None:
                missing += 1
            else:
                types[node["type"]] = types.get(node["type"], 0) + 1
        bad = [t for t in types if want is not None and t != want]
        if bad or missing:
            wrong.append((owner, tid, want, types, missing))

        risky = tid in CHARACTER_STATE
        odd = tid not in COMMON and tid not in CHARACTER_STATE
        if suspect_only and not (risky or odd or bad or missing):
            continue
        desc, flat = describe_values(vals)
        tgt = "、".join("%s×%d" % (TYPE_NAME.get(t, "type%d" % t), n)
                       for t, n in sorted(types.items()))
        if missing:
            tgt += ("、" if tgt else "") + "指不到×%d" % missing
        mark = ""
        if bad or missing:
            mark = "  ← 指到不對的東西！"
        elif risky:
            mark = ("  ← 角色狀態類，整條沒有變化，刪掉不會有差"
                    if flat else "  ← 角色狀態類")
        lines.append("  %-16s %-22s %3d 條 /%5d 格  %-20s %-22s%s"
                     % (owner or "(無)", tid, ntrack, nkf, desc[:20], tgt[:22], mark))
    return agg, lines, wrong


def drop(scene, targets):
    """把符合的軌道整條刪掉。targets 是 [(owner 或 None, id)]。"""
    xml = scene.timeline_xml()
    if not xml:
        return 0, []
    head, body, tail = S.split_root(xml)
    keep, last, removed = [], 0, []
    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        h = hm.group(0) if hm else blk
        mo = re.search(r'owner="([^"]*)"', h)
        mi = re.search(r'\bid="([^"]*)"', h)
        owner = mo.group(1) if mo else ""
        tid = mi.group(1) if mi else "?"
        hit = any((ow is None or ow == owner) and tid == i for ow, i in targets)
        if hit:
            keep.append(body[last:m.start()])
            last = m.end()
            removed.append((owner, tid))
    keep.append(body[last:])
    if removed:
        scene.set_timeline_xml(head + "".join(keep) + tail)
    return len(removed), removed


def drop_mismatch(scene):
    """只拿掉「objectIndex 指到不對的節點種類」的那幾條，同 id 的好軌道留著。

    為什麼要有這個：--drop 和 --drop-noncore 都是按 (owner, id) 整批砍。
    但 Item() 這類例外的成因通常不是「這個 id 在這個遊戲不能用」，而是
    **某幾條的 objectIndex 指錯了東西** —— 例如 itemColor1 指到一個資料夾
    或角色。Timeline 的處理程序第一件事就是把 oci 轉成 OCIItem，
    轉不過去就丟 InvalidCastException，而且是在 InterpolateBefore 裡，
    所以從 0 秒開始每一幀都噴，Recurse 整棵中斷 → 整個 Timeline 失效。

    那種情況下把 itemColor1 全砍掉等於把整張卡的道具變色都丟了。
    這裡只砍指錯的那幾條，其餘原封不動。

    「指不到任何節點」的也一起砍：那是軌道指向一個已經不存在的 RANK，
    Timeline 同樣會在取 oci 的時候爆掉。
    """
    xml = scene.timeline_xml()
    if not xml:
        return 0, []
    r2n = rank_to_node(scene)
    head, body, tail = S.split_root(xml)
    keep, last, removed = [], 0, []
    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        h = hm.group(0) if hm else blk
        mi = re.search(r'\bid="([^"]*)"', h)
        mo = re.search(r'owner="([^"]*)"', h)
        moi = re.search(r'objectIndex="(-?\d+)"', h)
        tid = mi.group(1) if mi else "?"
        owner = mo.group(1) if mo else ""
        want = expected_type(tid)
        if want is None or moi is None:
            continue
        oi = int(moi.group(1))
        if oi < 0:                     # 全域軌道（timeScale / cameraFOV…），沒有物件
            continue
        node = r2n.get(oi)
        why = None
        if node is None:
            why = "指不到節點"
        elif node["type"] != want:
            why = "指到%s，這個 id 只能用在%s" % (
                TYPE_NAME.get(node["type"], "type%d" % node["type"]),
                TYPE_NAME.get(want, "type%d" % want))
        if why:
            nm = str((node or {}).get("data", {}).get("name") or "?")
            keep.append(body[last:m.start()])
            last = m.end()
            removed.append((owner, tid, oi, nm, why))
    keep.append(body[last:])
    if removed:
        scene.set_timeline_xml(head + "".join(keep) + tail)
    return len(removed), removed


def parse_targets(txt):
    out = []
    for part in (txt or "").split(","):
        part = part.strip()
        if not part:
            continue
        if ":" in part:
            ow, tid = part.split(":", 1)
            out.append((ow.strip() or None, tid.strip()))
        else:
            out.append((None, part))
    return out


def scan_one(path, suspect_only):
    print("\n讀卡：%s（%.1f MB）" % (os.path.basename(path),
                                     os.path.getsize(path) / 1048576.0))
    sc = S.Scene(path)
    rows = tracks(sc)
    if not rows:
        print("  這張卡沒有 timeline 資料。")
        return sc, {}, []
    r2n = rank_to_node(sc)
    agg, lines, wrong = summarise(rows, suspect_only, r2n)
    print("  共 %d 條軌道、%d 種 (owner, id)%s："
          % (len(rows), len(agg), "，以下只列可疑的" if suspect_only else ""))
    print("\n".join(lines) if lines else "  （沒有可疑的軌道）")

    if wrong:
        print("\n  !! 有軌道指到不對的節點型別 —— 這就是 InvalidCastException 的來源：")
        for owner, tid, want, types, missing in wrong:
            got = "、".join("%s×%d" % (TYPE_NAME.get(t, "type%d" % t), n)
                            for t, n in sorted(types.items()))
            if missing:
                got += ("、" if got else "") + "指不到任何節點×%d" % missing
            print("     %s:%s 應該指到「%s」，實際指到 %s"
                  % (owner, tid, TYPE_NAME.get(want, "？"), got))
        print("     軌道的 interpolate 第一件事就是把 oci 轉成它預期的型別，")
        print("     指到別種節點時那個轉型就丟 InvalidCastException。")
        print("     而且例外會讓 Timeline 的整棵 Recurse 中斷 ——")
        print("     排在它後面的軌道（資料夾啟用、停放…）那一幀通通不會被套用。")
    return sc, agg, wrong


def cards_under(paths):
    """檔案就是檔案，資料夾就把裡面的 png 全部撿起來。"""
    out = []
    for p in paths:
        if os.path.isdir(p):
            out += [os.path.join(p, f) for f in sorted(os.listdir(p))
                    if f.lower().endswith(".png")]
        elif os.path.isfile(p):
            out.append(p)
        else:
            print("找不到：%s" % p)
    return out


def noncore_ids(scene, known_only=False):
    """這張卡裡要拿掉的 Timeline 內建軌道 id。

    known_only=True 只挑「已經有堆疊證據」的那幾個（KNOWN_BROKEN），
    False 則是「不在核心清單的全部」—— 後者比較猛，也比較容易誤傷。
    """
    ids = {t for o, t, _, _, _, _ in tracks(scene) if o == "Timeline"}
    if known_only:
        return sorted(ids & KNOWN_BROKEN)
    return sorted(ids - CORE_TIMELINE)


def show_tracks(path, want_ids):
    """把指定 id 的每一條軌道逐條列出來：指到誰、在哪、幾格。

    為什麼要有這個：總表只給「幾條、指到幾個什麼種類」，但要決定
    「刪掉這條會不會有差」得知道**是哪一個物件**。
    itemAlpha 兩條，是掛在兩個無關緊要的特效上、還是掛在主角身上的道具，
    差很多 —— 而那個答案只有把 objectIndex 換回節點名字才看得到。
    """
    sc = S.Scene(path)
    r2n = rank_to_node(sc)
    pmap = S.parent_map(sc.objects)
    rows = [r for r in tracks(sc) if r[1] in want_ids]
    print()
    print("  == 逐條列出：%s ==" % "、".join(sorted(want_ids)))
    if not rows:
        print("    這張卡沒有這些 id 的軌道。")
        del sc
        return
    for owner, tid, nkf, _vals, _blk, oidx in rows:
        node = r2n.get(oidx) if oidx is not None and oidx >= 0 else None
        if node is None:
            print("    %-16s %-16s objectIndex=%-6s %3d 格   （指不到節點）"
                  % (owner or "(無)", tid, oidx, nkf))
            continue
        d = node["data"]
        chain = [str(x["data"].get("name") or "?")
                 for x in S.ancestors(node, pmap)][::-1]
        print("    %-16s %-16s objectIndex=%-6s %3d 格   dicKey=%-7s %s"
              % (owner or "(無)", tid, oidx, nkf, d.get("dicKey"),
                 str(d.get("name") or "?")[:30]))
        print("        位置 %s" % (" > ".join(chain) if chain else "（根層）"))
    del sc


def clean_one(path, out_path, extra_targets, do_noncore, known_only=False,
              do_mismatch=False):
    """清一張卡並寫出去。回傳拿掉幾條。"""
    sc = S.Scene(path)
    targets = list(extra_targets)
    n_mis = 0
    if do_mismatch:
        n_mis, mis = drop_mismatch(sc)
        if mis:
            print("    指錯東西的軌道拿掉 %d 條：" % n_mis)
            for owner, tid, oi, nm, why in mis[:30]:
                print("      %-16s %-22s objectIndex=%-6s %-24s %s"
                      % (owner or "(無)", tid, oi, nm[:24], why))
            if len(mis) > 30:
                print("      …還有 %d 條" % (len(mis) - 30))
        else:
            print("    沒有指錯東西的軌道。")
    if do_noncore:
        ids = noncore_ids(sc, known_only)
        if ids:
            print("    非必要的 Timeline 軌道：" + "、".join(ids))
            targets += [("Timeline", t) for t in ids]
    if not targets and not n_mis:
        # 沒東西要拿掉也要複製過去 —— 輸出資料夾必須是「可以直接拿去合併」的
        # 完整一套，缺幾張的話等於要人自己記得哪幾張沒清、再手動補齊。
        if out_path:
            shutil.copy2(path, out_path)
            print("    沒有要拿掉的東西，原樣複製過去。")
        else:
            print("    沒有要拿掉的東西。")
        return 0
    n = n_mis
    if targets:
        n2, removed = drop(sc, targets)
        n += n2
        print("    按 id 拿掉 %d 條：%s"
              % (n2, "、".join(sorted({"%s:%s" % r for r in removed}))
                 or "（沒有符合的）"))
    if out_path:
        if n:
            sc.save(out_path)
            print("    寫出 %s（%.1f MB）"
                  % (out_path, os.path.getsize(out_path) / 1048576.0))
        else:
            shutil.copy2(path, out_path)
            print("    沒有符合的軌道，原樣複製過去。")
    del sc
    return n


def main(argv=None):
    ap = argparse.ArgumentParser(prog="kktl_scan", description=__doc__.split("\n")[0])
    ap.add_argument("card", nargs="+", help="場景卡 png，或裝著一堆 png 的資料夾")
    ap.add_argument("--suspect", action="store_true",
                    help="只列可疑的軌道（角色狀態類，和不在常見清單裡的）")
    ap.add_argument("--drop", help="要拿掉的軌道，owner:id，逗號分隔")
    ap.add_argument("--drop-known", action="store_true",
                    help="只拿掉「已經有堆疊證據會丟 InvalidCastException」的那幾種："
                         + "、".join(sorted(KNOWN_BROKEN))
                         + "。比 --drop-noncore 保守，不會誤傷沒問題的軌道")
    ap.add_argument("--show", default=None,
                    help="把某個 id 的每一條軌道逐條列出來（objectIndex 指到誰、"
                         "在哪個資料夾底下、幾格）。逗號分隔可以一次看好幾個。"
                         "總表只給數量，要知道『是哪個物件』就用這個")
    ap.add_argument("--drop-mismatch", action="store_true",
                    help="只拿掉「objectIndex 指到不對的節點種類」或「指不到節點」的軌道。"
                         "同一個 id 的其他軌道原封不動 —— 這是 Item() / "
                         "CharacterStateMisc() 丟 InvalidCastException 最常見的成因，"
                         "也是最不傷卡的修法")
    ap.add_argument("--drop-noncore", action="store_true",
                    help="把 owner=Timeline 裡面「非必要」的軌道全部拿掉（保留 "
                         "guideObject*／objectEnabled／timeScale／camera*）。"
                         "一次解決跨遊戲的 InvalidCastException，不用一層一層試")
    ap.add_argument("--out", help="拿掉之後寫到這個檔（只能配一張卡）")
    ap.add_argument("--out-dir",
                    help="批次輸出資料夾：每張卡各寫一份清乾淨的同名檔。"
                         "合併前一次清掉整個 original 資料夾就用這個")
    a = ap.parse_args(argv)

    targets = cards_under(a.card)
    if not targets:
        return 2
    changing = bool(a.drop or a.drop_noncore or a.drop_known or a.drop_mismatch)

    if changing and a.out and a.out_dir:
        print("--out 和 --out-dir 只能擇一。")
        return 2
    if changing and a.out and len(targets) > 1:
        print("--out 只能配一張卡（多張會互相蓋掉）。多張請改用 --out-dir。")
        return 2
    if changing and not a.out and not a.out_dir:
        print("（沒有指定 --out 或 --out-dir，以下只是看看會拿掉什麼，不會寫檔）")

    if a.out_dir:
        os.makedirs(a.out_dir, exist_ok=True)
        real_out = os.path.realpath(a.out_dir)
        for p in targets:
            # 寫回同一個資料夾就是直接覆蓋原檔，這裡不給 —— 原卡是唯一的備份
            if os.path.realpath(os.path.dirname(p)) == real_out:
                print("--out-dir 不能跟原始卡同一個資料夾（那會蓋掉原檔）：\n  %s" % p)
                return 2

    extra = parse_targets(a.drop) if a.drop else []

    want_show = {x.strip() for x in (a.show or "").replace("，", ",").split(",")
                 if x.strip()}

    all_risky, all_wrong = {}, {}
    total_dropped = 0
    for p in targets:
        sc, agg, wrong = scan_one(p, a.suspect)
        if want_show:
            show_tracks(p, want_show)
        for ow, tid in agg:
            if tid in CHARACTER_STATE:
                all_risky.setdefault((ow, tid), []).append(os.path.basename(p))
        for ow, tid, _, _, _ in wrong:
            all_wrong.setdefault((ow, tid), []).append(os.path.basename(p))
        del sc                                  # 大卡，清完一張放一張
        if changing:
            out = (os.path.join(a.out_dir, os.path.basename(p)) if a.out_dir
                   else a.out)
            total_dropped += clean_one(
                p, out if (a.out or a.out_dir) else None,
                extra, a.drop_noncore or a.drop_known,
                known_only=a.drop_known and not a.drop_noncore,
                do_mismatch=a.drop_mismatch)

    if all_wrong:
        print("\n※ 指錯節點的軌道（最可能就是這些在丟例外）：")
        for (ow, tid), where in sorted(all_wrong.items()):
            print("      %s:%s" % (ow, tid)
                  + ("      出現在：" + "、".join(where) if len(targets) > 1 else ""))
        print("  建議用 --drop-mismatch：只拿掉指錯的那幾條，同 id 的好軌道留著。")
        print("  （--drop <owner>:<id> 是整批砍，會連正常的一起丟。）")

    if all_risky:
        print("\n※ 這幾條是 Timeline 的『角色狀態』那一組，各版遊戲登記的型別不一樣，")
        print("  拿 KKS 的卡到 KK 播最常炸在這裡（InvalidCastException）：")
        for (ow, tid), where in sorted(all_risky.items()):
            print("      --drop %s:%s" % (ow, tid)
                  + ("      出現在：" + "、".join(where) if len(targets) > 1 else ""))

    if changing:
        print("\n總共拿掉 %d 條軌道，處理了 %d 張卡。" % (total_dropped, len(targets)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
