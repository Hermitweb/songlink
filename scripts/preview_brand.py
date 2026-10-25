#!/usr/bin/env python3
"""
生成品牌图标总览图（交付预览用）。

自包含：自己拉起已发布的应用、自己抓窗口/任务栏/托盘截图，不依赖
scripts/desktop-*.png 或 .tmp/ 下的临时文件。

用法：
    python scripts/preview_brand.py
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import time
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageGrab

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "assets" / "brand" / "brand-preview.png"
EXE = ROOT / "dist" / "SongLink-win-x64" / "SongLink.exe"

BG = (246, 247, 250)
INK = (34, 37, 45)
MUTED = (122, 130, 144)

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.FindWindowW.restype = wt.HWND
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
# 64 位下 HWND 是 64 位，不显式声明 argtypes 会被 ctypes 按 c_int 截断
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowTextLengthW.argtypes = [wt.HWND]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.EnumWindows.argtypes = [ctypes.c_void_p, wt.LPARAM]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.c_void_p]
user32.GetWindowDC.argtypes = [wt.HWND]
user32.GetWindowDC.restype = wt.HDC
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
user32.SetForegroundWindow.argtypes = [wt.HWND]

# GDI 句柄在 64 位下也是 64 位，不声明 argtypes 会被 ctypes 按 c_int 截断
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleDC.restype = wt.HDC
gdi32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = wt.HBITMAP
gdi32.SelectObject.argtypes = [wt.HDC, wt.HBITMAP]
gdi32.SelectObject.restype = wt.HBITMAP
gdi32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.DeleteObject.argtypes = [wt.HBITMAP]
gdi32.DeleteDC.argtypes = [wt.HDC]


def font(size):
    for name in ("msyh.ttc", "msyhbd.ttc", "simhei.ttf"):
        p = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / name
        if p.exists():
            try:
                return ImageFont.truetype(str(p), size)
            except OSError:
                continue
    return ImageFont.load_default()


F_TITLE, F_SUB, F_CARD = font(20), font(13), font(12)


# --------------------------------------------------------------------------- #
# 截图
# --------------------------------------------------------------------------- #
def capture_window(hwnd) -> Image.Image:
    """用 PrintWindow 抓指定窗口（WebView2 是 GPU 合成，普通截图会黑屏）。

    必须先把窗口恢复并置前，否则 WebView2 的渲染面不会被合成进来。
    """
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE
    user32.SetForegroundWindow(hwnd)
    time.sleep(1.2)

    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top

    hdc = user32.GetWindowDC(hwnd)
    mem_dc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem_dc, bmp)
    user32.PrintWindow(hwnd, mem_dc, 2)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount, bi.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem_dc, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(hwnd, hdc)
    # biHeight 传负数 => GetDIBits 输出的是自上而下的位图，不需要再翻转
    return Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)


def brand_color():
    """从生成的品牌图标里取底色，作为识别托盘/任务栏图标的指纹。"""
    im = Image.open(ROOT / "src/SongLink.Host.Core/wwwroot/brand/logo-64.png").convert("RGBA")
    px = im.load()
    best = None
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 200:
                continue
            if best is None or (b - r) > (best[2] - best[0]):
                best = (r, g, b)
    return best


def find_brand_icon(shot: Image.Image, region: tuple[int, int, int, int], pick: str = "first"):
    """在任务栏/托盘区域里找出品牌麦克风图标的位置。

    用品牌底色当指纹（比"偏蓝就当品牌"准得多 —— 酷狗那个蓝色 K 会被误判）。
    """
    tr, tg, tb = brand_color()
    px = shot.load()
    x0, y0, x1, y1 = region
    hits = []
    for x in range(x0, min(x1, shot.width)):
        for y in range(y0, min(y1, shot.height)):
            r, g, b = px[x, y]
            if abs(r - tr) <= 14 and abs(g - tg) <= 14 and abs(b - tb) <= 14:
                hits.append(x)
    if not hits:
        return None
    cols = sorted(set(hits))
    groups, cur = [], [cols[0]]
    for c in cols[1:]:
        if c - cur[-1] <= 6:
            cur.append(c)
        else:
            groups.append(cur)
            cur = [c]
    groups.append(cur)
    g = groups[0] if pick == "first" else groups[-1]
    cx = (g[0] + g[-1]) // 2
    return (cx - 16, y0 + 4, cx + 16, y0 + 36)


# --------------------------------------------------------------------------- #
# 画布
# --------------------------------------------------------------------------- #
def card(draw, x, y, w, h, title):
    draw.rounded_rectangle([x, y, x + w, y + h], radius=14, fill=(255, 255, 255), outline=(226, 229, 236))
    draw.text((x + 14, y + h - 24), title, font=F_CARD, fill=MUTED)


def paste_center(sheet, img, x, y, w, h):
    img = img.convert("RGBA")
    sc = min((w - 16) / img.width, (h - 46) / img.height, 1.0)
    im = img.resize((max(1, int(img.width * sc)), max(1, int(img.height * sc))), Image.LANCZOS)
    bg = Image.new("RGBA", im.size, (255, 255, 255, 255))
    bg.alpha_composite(im)
    sheet.paste(bg.convert("RGB"), (x + (w - im.width) // 2, y + 10 + (h - 46 - im.height) // 2))


def ico(path, size):
    im = Image.open(path)
    im.size = (size, size)
    im.load()
    return im.convert("RGBA")


def find_songlink_window():
    """按标题找 SongLink 主窗体。"""
    EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    found = []

    def cb(h, _):
        n = user32.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(h, b, n + 1)
            if b.value.startswith("SongLink 点歌台"):
                found.append(h)
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found[0] if found else None


def main() -> int:
    # WebView2 是 GPU 合成，只有窗口在前台时 PrintWindow 才能把内容抓进来；
    # 而已经跑着的实例通常被别的窗口盖住。所以重来一遍：杀掉旧的、拉起新的，
    # 新实例启动时天然在前台。
    subprocess.run(["taskkill", "/F", "/IM", "SongLink.exe"], capture_output=True)
    time.sleep(1.5)

    started = None
    hwnd = None
    if EXE.exists():
        started = subprocess.Popen([str(EXE)])
        for _ in range(40):
            time.sleep(0.75)
            hwnd = find_songlink_window()
            if hwnd:
                break
        time.sleep(6)   # 等 WebView2 首帧
    else:
        print(f"[preview] 找不到已发布程序 {EXE}，跳过实机截图")

    panel_shot = None
    if hwnd:
        try:
            panel_shot = capture_window(hwnd)
        except Exception as ex:
            print(f"[preview] 窗口截图失败: {ex}")

    full = ImageGrab.grab()
    sheet = Image.new("RGB", (1160, 760), BG)
    d = ImageDraw.Draw(sheet)
    d.text((28, 20), "SongLink 点歌台 — 品牌标识图标", font=F_TITLE, fill=INK)
    d.text((28, 48), "全部由 scripts/generate-brand-icons.py 从 assets/brand/songlink-source-1536.jpg 派生", font=F_SUB, fill=MUTED)

    # --- 行 1 ---
    y = 84
    card(d, 28, y, 548, 132, "app.ico — exe / 任务栏 / 快捷方式 (16→256)")
    x = 40
    for s in (16, 20, 24, 32, 48, 64):
        paste_center(sheet, ico(ROOT / "src/SongLink.Host.Desktop/app.ico", s), x, y, 72, 112)
        x += 74
    paste_center(sheet, ico(ROOT / "src/SongLink.Host.Desktop/app.ico", 96), 486, y, 80, 112)

    card(d, 594, y, 538, 132, "大尺寸用整幅插画 (96/128/256)")
    paste_center(sheet, Image.open(ROOT / "src/SongLink.Host.Core/wwwroot/brand/illustration-512.png"), 606, y, 120, 112)
    paste_center(sheet, ico(ROOT / "src/SongLink.Host.Desktop/app.ico", 128), 736, y, 140, 112)

    # --- 行 2 ---
    y = 232
    card(d, 28, y, 270, 150, "installer/setup.ico + 向导图")
    paste_center(sheet, ico(ROOT / "installer/setup.ico", 64), 40, y, 92, 128)
    sheet.paste(Image.open(ROOT / "installer/wizard.bmp").convert("RGB").resize((76, 146), Image.LANCZOS), (156, y + 6))

    card(d, 316, y, 270, 150, "wwwroot/favicon.ico + 顶栏 logo")
    paste_center(sheet, ico(ROOT / "src/SongLink.Host.Core/wwwroot/favicon.ico", 48), 328, y, 88, 128)
    paste_center(sheet, Image.open(ROOT / "src/SongLink.Host.Core/wwwroot/brand/logo-64.png"), 420, y, 88, 128)
    paste_center(sheet, Image.open(ROOT / "src/SongLink.Host.Core/wwwroot/brand/logo-32.png"), 512, y + 34, 62, 86)

    card(d, 604, y, 528, 150, "Android mipmap — 启动图标 (mdpi→xxxhdpi)")
    x = 616
    for dpi in ("mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi"):
        p = ROOT / f"mobile/android-remote/app/src/main/res/mipmap-{dpi}/ic_launcher.png"
        im = Image.open(p).convert("RGBA").resize((48, 48), Image.LANCZOS)
        bg = Image.new("RGBA", (48, 48), (255, 255, 255, 255)); bg.alpha_composite(im)
        sheet.paste(bg.convert("RGB"), (x, y + 34))
        x += 54
    p = ROOT / "mobile/android-remote/app/src/main/res/mipmap-xxxhdpi/ic_launcher_round.png"
    im = Image.open(p).convert("RGBA").resize((48, 48), Image.LANCZOS)
    bg = Image.new("RGBA", (48, 48), (255, 255, 255, 255)); bg.alpha_composite(im)
    sheet.paste(bg.convert("RGB"), (x + 20, y + 34))

    # --- 行 3 ---
    y = 398
    card(d, 28, y, 548, 336, "Web 点歌台面板 — 顶栏 logo + 空状态品牌插画")
    if panel_shot:
        sheet.paste(panel_shot.crop((0, 0, 596, 250)).resize((520, 218), Image.LANCZOS), (40, y + 10))
    d.text((40, y + 240), "标题栏图标 / 顶栏 logo / 封面空状态插画", font=F_CARD, fill=MUTED)

    card(d, 594, y, 538, 336, "Windows 任务栏 / 系统托盘 (实机截图)")
    sw, sh = full.size
    tray_box = find_brand_icon(full, (int(sw * 0.78), sh - 46, sw, sh))
    if tray_box:
        sheet.paste(full.crop(tray_box).resize(((tray_box[2] - tray_box[0]) * 6, (tray_box[3] - tray_box[1]) * 6), Image.LANCZOS), (626, y + 20))
        d.text((612, y + 186), "系统托盘通知图标", font=F_CARD, fill=MUTED)

    tb_box = find_brand_icon(full, (int(sw * 0.30), sh - 46, int(sw * 0.70), sh), pick="last")
    if tb_box:
        sheet.paste(full.crop(tb_box).resize(((tb_box[2] - tb_box[0]) * 6, (tb_box[3] - tb_box[1]) * 6), Image.LANCZOS), (800, y + 20))
        d.text((786, y + 186), "任务栏按钮", font=F_CARD, fill=MUTED)

    if panel_shot:
        sheet.paste(panel_shot.crop((10, 1, 30, 21)).resize((20 * 6, 20 * 6), Image.LANCZOS), (980, y + 20))
        d.text((956, y + 186), "标题栏 / Alt+Tab", font=F_CARD, fill=MUTED)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUT)
    print(f"saved {OUT}")

    if started:
        started.terminate()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
