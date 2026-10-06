#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 程星SSH客户端 应用图标 (多尺寸 .ico + 256px PNG)"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OUT_DIR = r"E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\Assets"
S = 1024
SS = 4                      # 超采样倍数
W = S * SS

os.makedirs(OUT_DIR, exist_ok=True)

# ---------- 1. 圆角方形渐变底 ----------
def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))

C1 = (0x4F, 0x8C, 0xF7)     # 蓝
C2 = (0x8B, 0x5C, 0xF6)     # 紫
C3 = (0x22, 0xD3, 0xEE)     # 青 (右下角点缀)

yy, xx = np.mgrid[0:W, 0:W].astype(np.float32)
t = (xx + yy) / (2.0 * (W - 1))                      # 对角渐变
tt = np.clip((t - 0.0) / 1.0, 0, 1)

base = np.zeros((W, W, 3), dtype=np.float32)
for i in range(3):
    a = np.where(tt < 0.55,
                 C1[i] + (C2[i] - C1[i]) * (tt / 0.55),
                 C2[i] + (C3[i] - C2[i]) * ((tt - 0.55) / 0.45))
    base[:, :, i] = a

grad = Image.fromarray(base.astype(np.uint8), "RGB").convert("RGBA")

# 圆角遮罩 (squircle 近似: 半径 22%)
mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=int(W * 0.22), fill=255)

icon = Image.new("RGBA", (W, W), (0, 0, 0, 0))
icon.paste(grad, (0, 0), mask)

# 顶部左侧高光, 增加玻璃质感
glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([-int(W * 0.35), -int(W * 0.55), int(W * 0.85), int(W * 0.45)], fill=(255, 255, 255, 46))
glow = glow.filter(ImageFilter.GaussianBlur(W * 0.06))
icon = Image.alpha_composite(icon, Image.composite(glow, Image.new("RGBA", (W, W), (0, 0, 0, 0)), mask))

# ---------- 2. 终端窗口图形 ----------
layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)

pad = int(W * 0.185)
win_box = [pad, int(W * 0.235), W - pad, W - int(W * 0.215)]
radius = int(W * 0.055)
d.rounded_rectangle(win_box, radius=radius, fill=(15, 18, 32, 168))          # 窗口主体
bar_h = int(W * 0.085)
d.rounded_rectangle([win_box[0], win_box[1], win_box[2], win_box[1] + bar_h * 2],
                    radius=radius, fill=(255, 255, 255, 58))                  # 标题栏
d.rectangle([win_box[0], win_box[1] + bar_h, win_box[2], win_box[1] + bar_h * 2],
            fill=(0, 0, 0, 0))
# 标题栏三个小圆点
dot_r = int(W * 0.014)
cy = win_box[1] + int(bar_h * 0.75)
for k, color in enumerate([(255, 122, 122, 235), (255, 200, 100, 235), (130, 226, 140, 235)]):
    cx = win_box[0] + int(W * 0.045) + k * int(W * 0.042)
    d.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=color)

# 提示符 ">" (两段粗线)
lx = win_box[0] + int(W * 0.062)
ly = win_box[1] + int(W * 0.20)
arm = int(W * 0.062)
thick = int(W * 0.030)
white = (255, 255, 255, 246)
d.line([lx, ly, lx + arm, ly + arm], fill=white, width=thick, joint="curve")
d.line([lx + arm, ly + arm, lx, ly + arm * 2], fill=white, width=thick, joint="curve")
# 下划线 "_"
ux = lx + arm + int(W * 0.035)
uy = ly + arm * 2 + thick // 2
d.line([ux, uy, ux + int(W * 0.145), uy], fill=white, width=thick)

icon = Image.alpha_composite(icon, layer)

# 边缘描边, 提升在浅色背景下的轮廓
edge = Image.new("RGBA", (W, W), (0, 0, 0, 0))
ImageDraw.Draw(edge).rounded_rectangle([0, 0, W - 1, W - 1], radius=int(W * 0.22),
                                       outline=(255, 255, 255, 42), width=int(W * 0.008))
icon = Image.alpha_composite(icon, edge)

# ---------- 3. 输出 ----------
icon256 = icon.resize((256, 256), Image.LANCZOS)
png_path = os.path.join(OUT_DIR, "app.png")
icon256.save(png_path)

ico_path = os.path.join(OUT_DIR, "app.ico")
icon.resize((256, 256), Image.LANCZOS).save(
    ico_path, format="ICO",
    sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])

print(f"已生成: {png_path} ({os.path.getsize(png_path)} 字节)")
print(f"已生成: {ico_path} ({os.path.getsize(ico_path)} 字节)")
for s in (16, 32, 48, 64):
    small = icon.resize((s, s), Image.LANCZOS)
    px = small.load()
    print(f"  {s}x{s} 预览中心像素: {px[s // 2, s // 2]}")
