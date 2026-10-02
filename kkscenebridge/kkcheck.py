#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkcheck.py — 場景卡解析 / round-trip 驗收（在你自己的電腦上跑）

大卡（500MB~1.1GB）超過傳給 Claude 的上限，所以驗收得在本機做。
這支會對每張卡做：

    serialize(parse(card)) == 原始位元組 ?

一個 byte 都不差才算過。順便印出結構摘要（節點樹、dicKey 範圍、
外掛清單、timeline 時長），報告寫成一個小檔案，那個檔案可以傳給 Claude。

用法：
    pip install kkloader==0.1.23
    python kkcheck.py "D:\\...\\處理步驟\\*.png"
    python kkcheck.py 卡1.png 卡2.png ...

輸出：同目錄下的 kkcheck_report.txt（UTF-8）
"""
import glob
import io
import importlib.util
import os
import struct
import sys
import traceback

# ============================================================ kkloader 修補
_SRC_PATCHES = [
    ('        save_version = "1.1.2.1"',
     '        save_version = self.version or "1.1.2.1"'),
    ('''        # Write shaderType
        data_stream.write(struct.pack("i", self.shaderType))

        # Write skyInfo
        sky_info_bytes, sky_info_len = msg_pack(self.skyInfo)
        data_stream.write(struct.pack("i", sky_info_len))
        data_stream.write(sky_info_bytes)''',
     '''        # Write shaderType
        if self._compare_versions(save_version, "1.1.0.0") >= 0:
            data_stream.write(struct.pack("i", self.shaderType))

        # Write skyInfo
        if self._compare_versions(save_version, "1.1.2.0") >= 0:
            sky_info_bytes, sky_info_len = msg_pack(self.skyInfo)
            data_stream.write(struct.pack("i", sky_info_len))
            data_stream.write(sky_info_bytes)'''),
    ('            ks.mod_data = msg_unpack(load_length(mod_data_stream, "i"))',
     '            _kkex_raw = load_length(mod_data_stream, "i")\n'
     '            ks.mod_raw = _kkex_raw\n'
     '            ks.mod_data = msg_unpack(_kkex_raw)'),
    ('            mod_data_bytes, mod_data_len = msg_pack(self.mod_data)',
     '            if getattr(self, "mod_raw", None) is not None:\n'
     '                mod_data_bytes = self.mod_raw\n'
     '                mod_data_len = len(mod_data_bytes)\n'
     '            else:\n'
     '                mod_data_bytes, mod_data_len = msg_pack(self.mod_data)'),
]


class _RawDict(dict):
    __slots__ = ("raw",)


class _RawList(list):
    __slots__ = ("raw",)


def _raw_loads(s):
    import json as _json
    o = _json.loads(s)
    if isinstance(o, dict):
        o = _RawDict(o)
    elif isinstance(o, list):
        o = _RawList(o)
    else:
        return o
    o.raw = s
    return o


class _JsonShim:
    """C# 的 float→JSON 十進位表示跟 Python repr 不一定一樣
    （0.7910000085830689 vs ...688），沒改過的就原字串吐回去。"""
    import json as _json
    JSONDecodeError = _json.JSONDecodeError

    @staticmethod
    def loads(s, **kw):
        return _raw_loads(s)

    @staticmethod
    def dumps(obj, **kw):
        import json as _json
        raw = getattr(obj, "raw", None)
        if raw is not None:
            try:
                if _raw_loads(raw) == obj:
                    return raw
            except Exception:  # noqa: BLE001
                pass
        return _json.dumps(obj, **kw)


def _patch_chara_raw():
    from kkloader.KoikatuCharaData import KoikatuCharaData
    if getattr(KoikatuCharaData, "_kkfix_raw", False):
        return
    KoikatuCharaData._kkfix_raw = True
    _orig_load = KoikatuCharaData.load.__func__
    _orig_bytes = KoikatuCharaData.__bytes__

    def load(cls, filelike, contains_png=True, **kw):
        if hasattr(filelike, "tell"):
            start = filelike.tell()
            obj = _orig_load(cls, filelike, contains_png=contains_png, **kw)
            end = filelike.tell()
            filelike.seek(start)
            obj._raw_bytes = filelike.read(end - start)
            filelike.seek(end)
            return obj
        return _orig_load(cls, filelike, contains_png=contains_png, **kw)

    def to_bytes(self):
        raw = getattr(self, "_raw_bytes", None)
        if raw is not None and not getattr(self, "_dirty", False):
            return raw
        return _orig_bytes(self)

    KoikatuCharaData.load = classmethod(load)
    KoikatuCharaData.__bytes__ = to_bytes


def _patch_json():
    import kkloader.SceneObjectLoaderBase as base
    import kkloader.KoikatuSceneObjectLoader as objloader
    if getattr(base, "_kkfix_json", False):
        return
    base._kkfix_json = True

    def parse_color_json(json_str):
        import json as _json
        c = _json.loads(json_str)
        out = _RawDict({"r": c.get("r", 0), "g": c.get("g", 0),
                        "b": c.get("b", 0), "a": c.get("a", 1.0)})
        out.raw = json_str
        return out

    base.SceneObjectLoaderBase.parse_color_json = staticmethod(parse_color_json)
    objloader.KoikatuSceneObjectLoader.parse_color_json = staticmethod(parse_color_json)
    base.json = _JsonShim
    objloader.json = _JsonShim


def _scenedata_source(spec):
    """讀 kkloader 的原始碼。打包成 exe 之後 spec.origin 是讀不到的，
    所以 .spec 會把那支 .py 一起打進 kkloader_src/，這裡找得到就用它。"""
    try:
        return open(spec.origin, encoding="utf-8").read()
    except OSError:
        pass
    base = getattr(sys, "_MEIPASS", None)
    if base:
        cand = os.path.join(base, "kkloader_src", "KoikatuSceneData.py")
        if os.path.exists(cand):
            return open(cand, encoding="utf-8").read()
    raise RuntimeError("找不到 kkloader 的 KoikatuSceneData.py 原始碼"
                       "（打包時要把它放進 kkloader_src/）")


def _reload_scenedata_module():
    name = "kkloader.KoikatuSceneData"
    spec = importlib.util.find_spec(name)
    src = _scenedata_source(spec)
    for old, new in _SRC_PATCHES:
        if old in src:
            src = src.replace(old, new)
        elif new not in src:
            raise RuntimeError("kkloader 版本不符，請 pip install kkloader==0.1.23")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(src, spec.origin, "exec"), module.__dict__)
    module.__dict__["json"] = _JsonShim
    import kkloader
    kkloader.KoikatuSceneData = module.KoikatuSceneData
    return module.KoikatuSceneData


_patch_chara_raw()
_patch_json()
KoikatuSceneData = _reload_scenedata_module()

KIND = {0: "char", 1: "item", 2: "light", 3: "folder", 4: "route", 5: "camera", 7: "text"}


# ============================================================ 結構摘要
def collect(objs, out, depth=0, path=""):
    """走遍物件樹，收集節點與所有 dicKey。"""
    for key, o in objs.items():
        t = o["type"]
        d = o["data"]
        name = d.get("name") or d.get("text") or ""
        out["nodes"].append((depth, key, t, d.get("dicKey"), str(name)[:60]))
        out["kinds"][t] = out["kinds"].get(t, 0) + 1
        out["dickeys"].add(key)
        if d.get("dicKey") is not None:
            out["dickeys"].add(d["dicKey"])
        for bk, bv in (d.get("bones") or {}).items():
            if isinstance(bv, dict) and "dicKey" in bv:
                out["dickeys"].add(bv["dicKey"])
                out["bone_keys"] += 1
        for bk, bv in (d.get("ik_targets") or {}).items():
            if isinstance(bv, dict) and "dicKey" in bv:
                out["dickeys"].add(bv["dicKey"])
                out["ik_keys"] += 1
        lat = d.get("lookAtTarget")
        if isinstance(lat, dict) and "dicKey" in lat:
            out["lookat"].append(lat["dicKey"])
        for rp in (d.get("route_points") or d.get("routePoints") or []):
            if isinstance(rp, dict) and "dicKey" in rp:
                out["dickeys"].add(rp["dicKey"])
                out["route_keys"] += 1
        ch = d.get("child")
        if isinstance(ch, dict):
            # 角色的 child 是 {接點: [ObjectInfo...]}
            for ck, lst in ch.items():
                if isinstance(lst, list):
                    for sub in lst:
                        collect({0: sub} if isinstance(sub, dict) and "type" in sub
                                else {}, out, depth + 1)
                elif isinstance(lst, dict) and "type" in lst:
                    collect({ck: lst}, out, depth + 1)
        elif isinstance(ch, list):
            for sub in ch:
                if isinstance(sub, dict) and "type" in sub:
                    collect({sub.get("data", {}).get("dicKey", -1): sub}, out, depth + 1)


def kkex_summary(mod_raw):
    """場景層級 KKEx：列出外掛與各自的 key。"""
    import msgpack
    try:
        top = msgpack.unpackb(mod_raw, raw=False, strict_map_key=False)
    except Exception as e:  # noqa: BLE001
        return [f"(KKEx 解不開: {e})"]
    lines = []
    for guid, val in top.items():
        desc = ""
        if isinstance(val, list) and len(val) > 1 and isinstance(val[1], dict):
            parts = []
            for k, v in val[1].items():
                if isinstance(v, str):
                    tp = "XML" if v.lstrip().startswith("<") else "str"
                    n = len(v)
                elif isinstance(v, (bytes, bytearray)):
                    tp, n = "bin", len(v)
                elif v is None:
                    tp, n = "nil", 0
                else:
                    tp, n = type(v).__name__, 0
                parts.append(f"{k}:{tp}({n})")
            desc = ", ".join(parts)
        lines.append(f"    {guid:<52} {desc[:110]}")
    return sorted(lines)


def check(path, log):
    log(f"\n{'=' * 78}\n{os.path.basename(path)}")
    size = os.path.getsize(path)
    log(f"  檔案大小 {size:,} bytes")
    try:
        orig = open(path, "rb").read()
        sc = KoikatuSceneData.load(io.BytesIO(orig))
    except Exception:  # noqa: BLE001
        log("  !! 解析失敗")
        log(traceback.format_exc())
        return False
    log(f"  版本 {sc.version}   根節點 {len(sc.objects)} 個")

    out = {"nodes": [], "kinds": {}, "dickeys": set(), "lookat": [],
           "bone_keys": 0, "ik_keys": 0, "route_keys": 0}
    try:
        collect(sc.objects, out)
    except Exception:  # noqa: BLE001
        log("  !! 走樹失敗")
        log(traceback.format_exc())

    log("  節點型別：" + ", ".join(f"{KIND.get(k, k)}={v}" for k, v in sorted(out["kinds"].items())))
    dk = out["dickeys"]
    if dk:
        log(f"  dicKey {len(dk)} 個，{min(dk)}..{max(dk)}"
            f"（骨架 {out['bone_keys']}、IK {out['ik_keys']}、route 點 {out['route_keys']}）")
    log(f"  lookAtTarget dicKey: {sorted(set(out['lookat']))[:20]}")
    log("  -- 物件樹 --")
    for depth, key, t, dic, name in out["nodes"][:200]:
        log(f"    {'  ' * depth}key={key:<6} {KIND.get(t, t):<7} dicKey={dic:<6} {name}")
    if len(out["nodes"]) > 200:
        log(f"    ...（還有 {len(out['nodes']) - 200} 個節點）")

    mod_raw = getattr(sc, "mod_raw", None)
    if mod_raw:
        log(f"  -- 場景層級 KKEx（{len(mod_raw):,} bytes）--")
        for line in kkex_summary(mod_raw):
            log(line)

    out_bytes = bytes(sc)
    ok = out_bytes == orig
    if ok:
        log(f"  >> ROUND TRIP OK：{len(orig):,} bytes 完全一致")
    else:
        i = next((i for i in range(min(len(orig), len(out_bytes)))
                  if orig[i] != out_bytes[i]), min(len(orig), len(out_bytes)))
        log(f"  >> ROUND TRIP 失敗：原始 {len(orig):,} / 輸出 {len(out_bytes):,}，第一個差異 @ {i:,}")
        log(f"     orig: {orig[max(0, i - 32):i + 32].hex(' ')}")
        log(f"     out : {out_bytes[max(0, i - 32):i + 32].hex(' ')}")
    return ok


def main(argv):
    paths = []
    for a in argv:
        paths.extend(sorted(glob.glob(a)) or [a])
    if not paths:
        print(__doc__)
        return 1
    report = os.path.join(os.path.dirname(os.path.abspath(paths[0])), "kkcheck_report.txt")
    buf = []

    def log(s):
        buf.append(s)
        try:
            print(s)
        except UnicodeEncodeError:
            print(s.encode("ascii", "replace").decode())

    import kkloader
    log(f"kkloader {getattr(kkloader, '__version__', '?')}  python {sys.version.split()[0]}")
    results = []
    for p in paths:
        try:
            results.append((p, check(p, log)))
        except MemoryError:
            log(f"  !! 記憶體不足：{p}")
            results.append((p, False))
    log("\n" + "=" * 78 + "\n總結")
    for p, ok in results:
        log(f"  {'OK  ' if ok else 'FAIL'}  {os.path.basename(p)}")
    with open(report, "w", encoding="utf-8") as f:
        f.write("\n".join(buf))
    print(f"\n報告寫到：{report}")
    return 0 if all(ok for _, ok in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
