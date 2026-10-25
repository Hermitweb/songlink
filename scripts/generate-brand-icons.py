#!/usr/bin/env python3
"""
SongLink 点歌台 — 品牌图标生成器
================================

从 `assets/brand/songlink-source-1536.jpg`（美术原稿 1536×1536）派生出项目所有
标识图标：

  * Windows exe / 快捷方式 / 任务栏 / 托盘  → app.ico（多尺寸）、tray.ico
  * 安装器 Setup 图标                        → setup.ico
  * Web 面板 favicon + 顶栏 logo              → wwwroot/favicon.ico、brand/logo.png、brand/logo.svg
  * Android 启动图标（自适应 + legacy）        → res/mipmap-*/

设计说明
--------
原稿是一张"叠卡片 + 麦克风"的插画，整张塞进 16×16 托盘图标会糊成一团色块。
所以派生两套：

  mark        —— 从原稿里裁出麦克风本体（连同它所在的卡片渐变背景），
                 两端做羽化后贴在同色系渐变圆角方块上。小尺寸下主体清晰可辨。
  illustration—— 原稿整体，白底转透明。用于 96px 以上的大图标 / 品牌插画。

用法：
    python scripts/generate-brand-icons.py
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "brand" / "songlink-source-1536.jpg"

# 麦克风在原稿中的包围盒（经 probe_brand.py 实测：紫色像素 bbox 326,560 → 658,1170，
# 去掉头顶与底座余量后收紧到下面这个框）
MIC_BOX = (305, 600, 625, 1180)

# 卡片底色系（取自原稿）
C_TOP = (205, 221, 252)      # 顶部 浅蓝
C_MID = (198, 197, 248)      # 中部 薰衣草紫
C_BOT = (216, 203, 249)      # 底部 淡紫
C_PINK = (247, 168, 200)     # 强调 粉


# --------------------------------------------------------------------------- #
# 基础工具
# --------------------------------------------------------------------------- #
def _lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))  # type: ignore[return-value]


def vertical_gradient(size: int) -> Image.Image:
    """竖向三段渐变，色相取自原稿卡片背景，保证麦克风贴上去看不出接缝。"""
    img = Image.new("RGB", (1, size))
    px = img.load()
    for y in range(size):
        t = y / max(1, size - 1)
        if t < 0.5:
            px[0, y] = _lerp(C_TOP, C_MID, t / 0.5)
        else:
            px[0, y] = _lerp(C_MID, C_BOT, (t - 0.5) / 0.5)
    return img.resize((size, size), Image.BILINEAR)


def corner_glow(width: int, height: int | None = None) -> tuple[Image.Image, Image.Image]:
    """右下角粉色辉光，作为画面里唯一的高饱和点，跟原稿右上角的粉色音符呼应。"""
    h = height or width
    glow = Image.new("L", (width, h), 0)
    d = ImageDraw.Draw(glow)
    r = int(max(width, h) * 0.85)
    d.ellipse([width - r, h - r, width + r, h + r], fill=90)
    glow = glow.filter(ImageFilter.GaussianBlur(radius=max(width, h) * 0.18))
    pink = Image.new("RGB", (width, h), C_PINK)
    return pink, glow


def rounded_mask(size: int, radius_ratio: float = 0.225) -> Image.Image:
    """Windows 11 风格的圆角方块遮罩。"""
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    r = int(size * radius_ratio)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=255)
    return mask


def mic_sticker(src: Image.Image, height: int) -> Image.Image:
    """
    从原稿裁出麦克风，做羽化 alpha，让它能无缝贴在渐变方块上。

    羽化用「椭圆 + 模糊」而不是硬边：麦克风本体完全在椭圆安全区内，
    四周的卡片背景被羽化淡出，与方块背景自然衔接。
    """
    crop = src.crop(MIC_BOX).convert("RGB")
    w = max(1, round(crop.width * height / crop.height))
    crop = crop.resize((w, height), Image.LANCZOS)

    # 椭圆安全区：横向收窄到麦克风实际宽度，两端留一点余量
    mx = int(w * 0.10)
    my = int(height * 0.035)
    alpha = Image.new("L", (w, height), 0)
    d = ImageDraw.Draw(alpha)
    d.ellipse([mx, my, w - mx - 1, height - my - 1], fill=255)
    alpha = alpha.filter(ImageFilter.GaussianBlur(radius=max(1.0, w * 0.085)))

    out = crop.convert("RGBA")
    out.putalpha(alpha)
    return out


def mic_scale(size: int) -> float:
    """
    麦克风占画布的比例。

    16/20px 时主体在方块里只占四成，看起来像个色块，所以放大；
    大尺寸留出更多边距更透气。
    """
    if size <= 20:
        return 1.00
    if size <= 32:
        return 0.92
    return 0.86


def make_mark(size: int, src: Image.Image) -> Image.Image:
    """主标识：麦克风 + 渐变圆角方块。小尺寸（托盘/任务栏）用这个。"""
    base = vertical_gradient(size).convert("RGBA")
    pink, glow = corner_glow(size)
    base = Image.composite(pink, base, glow)

    mic = mic_sticker(src, height=int(size * mic_scale(size)))
    base.alpha_composite(mic, ((size - mic.width) // 2, (size - mic.height) // 2))

    mask = rounded_mask(size)
    base.putalpha(mask)
    return base


def make_mic_only(height: int, src: Image.Image) -> Image.Image:
    """
    只有麦克风本体（透明底，边缘羽化）。

    Android 自适应图标的 foreground 层要的就是这个 —— 背景交给 background 层，
    前景再带一个方块底会变成「方块套方块」。
    """
    return mic_sticker(src, height=height)


def _load_font(size: int):
    """找一个能写中文的字体；找不到就退回 Pillow 默认位图字体。"""
    from PIL import ImageFont

    for name in ("msyh.ttc", "msyhbd.ttc", "simhei.ttf", "simsun.ttc"):
        p = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / name
        if p.exists():
            try:
                return ImageFont.truetype(str(p), size)
            except OSError:
                continue
    return ImageFont.load_default()


def make_wizard_images(src: Image.Image, out_dir: Path) -> None:
    """
    Inno Setup 向导图。

    尺寸是安装器写死的：WizardImageFile 必须 164x314，WizardSmallImageFile 必须 55x58。
    位图必须是不带 alpha 的 BMP。
    """
    out_dir.mkdir(parents=True, exist_ok=True)

    # --- 向导主图 164x314 ---
    w, h = 164, 314
    bg = vertical_gradient(h).convert("RGB").resize((w, h), Image.BILINEAR)
    pink, glow = corner_glow(w, h)
    bg = Image.composite(pink, bg, glow)

    logo = make_mark(104, src).convert("RGBA")
    bg.paste(logo, ((w - logo.width) // 2, 46), logo)

    font = _load_font(20)
    text = "SongLink 点歌台"
    bbox = ImageDraw.Draw(bg).textbbox((0, 0), text, font=font)
    tx = (w - (bbox[2] - bbox[0])) // 2
    ImageDraw.Draw(bg).text((tx, 172), text, font=font, fill=(96, 92, 160))

    line_y = 208
    ImageDraw.Draw(bg).line([(30, line_y), (w - 30, line_y)], fill=(186, 180, 226), width=2)
    sub = _load_font(13)
    st = "让每一桌都能点歌"
    sb = ImageDraw.Draw(bg).textbbox((0, 0), st, font=sub)
    ImageDraw.Draw(bg).text(((w - (sb[2] - sb[0])) // 2, line_y + 14), st, font=sub, fill=(140, 136, 180))

    bg.save(out_dir / "wizard.bmp", "BMP")

    # --- 向导小图 55x58 ---
    small = make_mark(48, src).convert("RGBA")
    canvas = Image.new("RGBA", (55, 58), (0, 0, 0, 0))
    canvas.alpha_composite(small, ((55 - small.width) // 2, (58 - small.height) // 2))
    canvas.convert("RGB").save(out_dir / "wizard-small.bmp", "BMP")


def make_illustration_fast(src: Image.Image) -> Image.Image:
    """品牌插画：整张原稿，白底转透明（近白像素 α 归零并做软过渡）。"""
    import numpy as np

    img = src.convert("RGBA")
    arr = np.asarray(img).astype("float32")
    lum = arr[:, :, :3].mean(axis=2)
    alpha = np.clip((250.0 - lum) / 15.0, 0.0, 1.0) * 255.0
    arr[:, :, 3] = alpha
    return Image.fromarray(arr.astype("uint8"), "RGBA")


# --------------------------------------------------------------------------- #
# 输出
# --------------------------------------------------------------------------- #
def save_ico(images: dict[int, Image.Image], path: Path) -> None:
    """
    写多尺寸 .ico。

    两处讲究：
    1. Pillow 的 ICO 编码器要求「基准图」不小于最大目标尺寸，否则大尺寸会被静默丢弃，
       所以这里传最大的那张当基准，其余走 append_images。
    2. 必须用 bitmap_format="bmp"（经典 DIB 条目）而不是 Pillow 默认的 PNG 条目 ——
       MSBuild 把 .ico 编进 exe 的 PE 资源时走的是老式 Win32 资源编译器，
       它只认 BMP 格式的条目；PNG 条目会被解析成空白图标。
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    sizes = sorted(images.keys())
    largest = images[sizes[-1]]
    others = [images[s] for s in sizes[:-1]]
    largest.save(
        path,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=others,
        bitmap_format="bmp",
    )


def main() -> int:
    if not SRC.exists():
        print(f"[brand] 找不到原稿: {SRC}", file=sys.stderr)
        return 1

    src = Image.open(SRC).convert("RGB")
    print(f"[brand] 原稿: {SRC.name} {src.size[0]}x{src.size[1]}")

    # ---- 1. Windows 桌面端：app.ico + tray.ico -------------------------------
    desktop = ROOT / "src" / "SongLink.Host.Desktop"
    small = {s: make_mark(s, src) for s in (16, 20, 24, 32, 40, 48, 64)}
    big = {s: make_illustration_fast(src).resize((s, s), Image.LANCZOS) for s in (96, 128, 256)}

    app_ico = desktop / "app.ico"
    save_ico({**small, **big}, app_ico)
    print(f"[brand] {app_ico.relative_to(ROOT)}  ({len(small) + len(big)} 尺寸)")

    tray_ico = desktop / "Assets" / "tray.ico"
    save_ico({s: small[s] for s in (16, 24, 32)}, tray_ico)
    print(f"[brand] {tray_ico.relative_to(ROOT)}")

    # 纯 mark 的高清 PNG（托盘 256 / 面板用）
    (desktop / "Assets").mkdir(parents=True, exist_ok=True)
    mark512 = make_mark(512, src)
    mark512.save(desktop / "Assets" / "icon-512.png")
    print(f"[brand] {(desktop / 'Assets' / 'icon-512.png').relative_to(ROOT)}")

    # ---- 2. 安装器 Setup 图标 + 向导图 -------------------------------------
    # 安装向导固定用 32px 显示，小尺寸必须清晰，所以全套都用 mark
    setup_ico = ROOT / "installer" / "setup.ico"
    save_ico({s: make_mark(s, src) for s in (16, 24, 32, 48, 64, 128, 256)}, setup_ico)
    print(f"[brand] {setup_ico.relative_to(ROOT)}")

    make_wizard_images(src, ROOT / "installer")
    print(f"[brand] installer/wizard.bmp, installer/wizard-small.bmp")

    # ---- 3. Web 面板 --------------------------------------------------------
    www = ROOT / "src" / "SongLink.Host.Core" / "wwwroot"
    (www / "brand").mkdir(parents=True, exist_ok=True)

    save_ico({s: small[s] for s in (16, 32, 48)}, www / "favicon.ico")
    print(f"[brand] {(www / 'favicon.ico').relative_to(ROOT)}")

    for s in (32, 180, 192):
        make_mark(s, src).save(www / "brand" / f"logo-{s}.png")
    print(f"[brand] {(www / 'brand').relative_to(ROOT)}/logo-*.png")

    # 顶栏用的小尺寸
    make_mark(64, src).save(www / "brand" / "logo-64.png")

    # 面板"现在播放"空状态用的品牌插画。1536 全尺寸 PNG 有 2MB，单文件发布会把它
    # 整个打进 exe，所以面板里只放 512px 的量化版（~60KB），原图留一份在 assets 存档。
    ill = make_illustration_fast(src)
    ill.save(ROOT / "assets" / "brand" / "songlink-illustration.png", optimize=True)
    panel_ill = ill.resize((512, 512), Image.LANCZOS).quantize(colors=256, method=Image.FASTOCTREE)
    panel_ill.save(www / "brand" / "illustration-512.png", optimize=True)
    print(f"[brand] {(www / 'brand' / 'illustration-512.png').relative_to(ROOT)}")

    # ---- 4. Android ---------------------------------------------------------
    res = ROOT / "mobile" / "android-remote" / "app" / "src" / "main" / "res"
    densities = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
    for dpi, px in densities.items():
        d = res / f"mipmap-{dpi}"
        d.mkdir(parents=True, exist_ok=True)
        mark = make_mark(px, src)
        mark.save(d / "ic_launcher.png")
        # 圆形版本：同样的图，圆角半径拉满
        circ = make_mark(px, src)
        cmask = Image.new("L", (px, px), 0)
        ImageDraw.Draw(cmask).ellipse([0, 0, px - 1, px - 1], fill=255)
        circ.putalpha(cmask)
        circ.save(d / "ic_launcher_round.png")

        # 自适应图标前景层：108dp 画布，主体限制在中间 66dp 安全区
        fg_px = round(px * 108 / 48)
        fg = Image.new("RGBA", (fg_px, fg_px), (0, 0, 0, 0))
        inner = make_mic_only(round(fg_px * 0.60), src)
        fg.alpha_composite(inner, ((fg_px - inner.width) // 2, (fg_px - inner.height) // 2))
        fg.save(d / "ic_launcher_foreground.png")
    print(f"[brand] Android mipmap-* 密度 {list(densities)}")

    # 自适应图标背景层（纯渐变，跟 mark 底色一致）
    for dpi, px in densities.items():
        fg_px = round(px * 108 / 48)
        bg = vertical_gradient(fg_px)
        pink, glow = corner_glow(fg_px)
        bg = Image.composite(pink, bg, glow).convert("RGBA")
        bg.save(res / f"mipmap-{dpi}" / "ic_launcher_background.png")

    print("[brand] 完成")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
