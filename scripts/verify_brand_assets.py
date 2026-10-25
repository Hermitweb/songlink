"""校验发布产物里的品牌图标：直接解析 PE 资源目录，确认 RT_GROUP_ICON / RT_ICON 齐全。"""
import struct
import sys
import zipfile
from pathlib import Path

ROOT = Path(r"E:\workspace\点歌台面板")


def pe_icons(path: Path):
    d = path.read_bytes()
    e = struct.unpack_from("<I", d, 0x3C)[0]
    coff = e + 4
    nsec, = struct.unpack_from("<H", d, coff + 2)
    opt_size, = struct.unpack_from("<H", d, coff + 16)
    opt = coff + 20
    magic, = struct.unpack_from("<H", d, opt)
    dd = opt + (112 if magic == 0x20B else 96)
    res_rva, _ = struct.unpack_from("<II", d, dd + 16)
    secs = []
    s = opt + opt_size
    for i in range(nsec):
        o = s + i * 40
        vs, va, rs, ra = struct.unpack_from("<IIII", d, o + 8)
        secs.append((va, max(vs, rs), ra))

    def r2o(rva):
        for va, sz, ra in secs:
            if va <= rva < va + sz:
                return ra + (rva - va)
        raise ValueError(rva)

    base = r2o(res_rva)
    found = {}

    def walk(off, key):
        nn, ni = struct.unpack_from("<HH", d, off + 12)
        for i in range(nn + ni):
            nid, do = struct.unpack_from("<II", d, off + 16 + i * 8)
            if do & 0x80000000:
                walk(base + (do & 0x7FFFFFFF), key + [nid])
            else:
                rva, size, _, _ = struct.unpack_from("<IIII", d, base + (do & 0x7FFFFFFF))
                found.setdefault(tuple(key + [nid]), (rva, size))

    walk(base, [])
    icons = [k for k in found if k[0] == 3]
    sizes = []
    for k, (rva, size) in found.items():
        if k[0] != 14:
            continue
        o = r2o(rva)
        _, _, cnt = struct.unpack_from("<HHH", d, o)
        for i in range(cnt):
            w, h, _, _, _, bpp, _, _ = struct.unpack_from("<BBBBHHIH", d, o + 6 + i * 14)
            sizes.append((w or 256, bpp))
    return len(icons), sorted(sizes)


def apk_icons(path: Path):
    with zipfile.ZipFile(path) as z:
        return sorted(n for n in z.namelist() if "ic_launcher" in n)


targets = [
    ("桌面 exe", ROOT / "dist/SongLink-win-x64/SongLink.exe"),
    ("安装包 Setup.exe", ROOT / "dist/SongLink_Setup_v1.0.0.exe"),
]

for label, p in targets:
    if not p.exists():
        print(f"{label}: 缺失 {p}")
        continue
    n, sizes = pe_icons(p)
    print(f"{label}: RT_ICON={n}  尺寸={sizes}")

apk = ROOT / "mobile/android-remote/app/build/outputs/apk/debug/app-debug.apk"
if apk.exists():
    names = apk_icons(apk)
    print(f"Android APK: {apk.name} ({apk.stat().st_size/1024/1024:.1f} MB) 图标资源 {len(names)} 个")
    for n in names:
        print(f"    {n}")
else:
    print("Android APK: 未找到")
