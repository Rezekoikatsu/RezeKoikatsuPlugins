# -*- coding: utf-8 -*-
"""kkmsgpack.py — msgpack 位元組層級手術工具

沿用角色卡那輪的血淚教訓（來自 kkmerge.py）：

  * float 一律 float32（use_single_float=True）。C# 存的是 0xca，
    msgpack 預設會升成 float64(0xcb)，遊戲讀回去就壞了。
  * 整數的格式寬度要保留。MessagePack-CSharp 反序列化成 object 時，
    fixint -> byte、int32 -> int；Python 重新編碼會把小整數壓成 fixint，
    外掛就 InvalidCastException。
  * 凡是不需要改的東西，一律保留原始位元組。
"""
import struct

import msgpack


def pack(obj) -> bytes:
    return msgpack.Packer(use_bin_type=True, use_single_float=True).pack(obj)


def unpack(b):
    return msgpack.unpackb(b, raw=False, strict_map_key=False)


def skip(buf, pos: int) -> int:
    """回傳 buf 中從 pos 起一個完整 msgpack 物件之後的位置。"""
    c = buf[pos]
    pos += 1
    if c <= 0x7F or c >= 0xE0 or c in (0xC0, 0xC2, 0xC3):
        return pos
    if 0x80 <= c <= 0x8F:
        for _ in range((c & 0x0F) * 2):
            pos = skip(buf, pos)
        return pos
    if 0x90 <= c <= 0x9F:
        for _ in range(c & 0x0F):
            pos = skip(buf, pos)
        return pos
    if 0xA0 <= c <= 0xBF:
        return pos + (c & 0x1F)
    if c == 0xC4:
        return pos + 1 + buf[pos]
    if c == 0xC5:
        return pos + 2 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xC6:
        return pos + 4 + int.from_bytes(buf[pos:pos + 4], "big")
    if c == 0xC7:
        return pos + 2 + buf[pos]
    if c == 0xC8:
        return pos + 3 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xC9:
        return pos + 5 + int.from_bytes(buf[pos:pos + 4], "big")
    fixed = {0xCA: 4, 0xCB: 8, 0xCC: 1, 0xCD: 2, 0xCE: 4, 0xCF: 8,
             0xD0: 1, 0xD1: 2, 0xD2: 4, 0xD3: 8,
             0xD4: 2, 0xD5: 3, 0xD6: 5, 0xD7: 9, 0xD8: 17}
    if c in fixed:
        return pos + fixed[c]
    if c == 0xD9:
        return pos + 1 + buf[pos]
    if c == 0xDA:
        return pos + 2 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xDB:
        return pos + 4 + int.from_bytes(buf[pos:pos + 4], "big")
    if c in (0xDC, 0xDD, 0xDE, 0xDF):
        w = 2 if c in (0xDC, 0xDE) else 4
        n = int.from_bytes(buf[pos:pos + w], "big")
        pos += w
        for _ in range(n * (2 if c in (0xDE, 0xDF) else 1)):
            pos = skip(buf, pos)
        return pos
    raise ValueError(f"未知的 msgpack 型別位元組 0x{c:02x} @ {pos - 1}")


def map_header(n: int) -> bytes:
    if n <= 0x0F:
        return bytes([0x80 | n])
    if n <= 0xFFFF:
        return b"\xde" + n.to_bytes(2, "big")
    return b"\xdf" + n.to_bytes(4, "big")


def arr_header(n: int) -> bytes:
    if n <= 0x0F:
        return bytes([0x90 | n])
    if n <= 0xFFFF:
        return b"\xdc" + n.to_bytes(2, "big")
    return b"\xdd" + n.to_bytes(4, "big")


def map_split(raw):
    """msgpack map -> [(key, key_bytes, value_bytes)]"""
    c = raw[0]
    if 0x80 <= c <= 0x8F:
        n, p = c & 0x0F, 1
    elif c == 0xDE:
        n, p = int.from_bytes(raw[1:3], "big"), 3
    elif c == 0xDF:
        n, p = int.from_bytes(raw[1:5], "big"), 5
    else:
        raise ValueError("不是 msgpack map")
    out = []
    for _ in range(n):
        ks = p
        p = skip(raw, p)
        kb = bytes(raw[ks:p])
        vs = p
        p = skip(raw, p)
        out.append((unpack(kb), kb, bytes(raw[vs:p])))
    return out


def map_build(items) -> bytes:
    return map_header(len(items)) + b"".join(kb + vb for _, kb, vb in items)


def arr_split(raw):
    """msgpack array -> [element_bytes]"""
    c = raw[0]
    if 0x90 <= c <= 0x9F:
        n, p = c & 0x0F, 1
    elif c == 0xDC:
        n, p = int.from_bytes(raw[1:3], "big"), 3
    elif c == 0xDD:
        n, p = int.from_bytes(raw[1:5], "big"), 5
    else:
        raise ValueError("不是 msgpack array")
    out = []
    for _ in range(n):
        st = p
        p = skip(raw, p)
        out.append(bytes(raw[st:p]))
    return out


def arr_build(elems) -> bytes:
    return arr_header(len(elems)) + b"".join(elems)


def encode_like(value, orig: bytes) -> bytes:
    """沿用原始欄位的格式位元組重新編碼，維持 C# 端的型別判讀。"""
    c = orig[0]
    if isinstance(value, bool):
        return b"\xc3" if value else b"\xc2"
    if isinstance(value, int):
        table = {0xD0: ">b", 0xD1: ">h", 0xD2: ">i", 0xD3: ">q",
                 0xCC: ">B", 0xCD: ">H", 0xCE: ">I", 0xCF: ">Q"}
        if c in table:
            try:
                return bytes([c]) + struct.pack(table[c], value)
            except struct.error:
                pass
        return pack(value)
    if isinstance(value, float):
        if c == 0xCB:
            return b"\xcb" + struct.pack(">d", value)
        return b"\xca" + struct.pack(">f", value)
    return pack(value)


def patch_entry(raw: bytes, updates: dict) -> bytes:
    """改 msgpack map 裡的少數欄位，其餘位元組原封不動、格式寬度不變。"""
    out = []
    for key, kb, vb in map_split(raw):
        if key in updates:
            new = updates[key]
            if new != unpack(vb):
                vb = encode_like(new, vb)
        out.append((key, kb, vb))
    return map_build(out)


def patch_key(kb: bytes, new_key) -> bytes:
    """換掉 map 的鍵，沿用原本的整數格式寬度。"""
    return encode_like(new_key, kb)
