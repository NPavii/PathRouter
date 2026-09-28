"""Рисует плоскую иконку-верблюда (бактриан, в профиль) и сохраняет .ico + .png."""
from PIL import Image, ImageDraw, ImageFilter

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# --- фон: тёплая плитка со скруглением ---
RAD = 200
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
td = ImageDraw.Draw(tile)
td.rounded_rectangle([0, 0, S - 1, S - 1], radius=RAD, fill=(232, 155, 75, 255))  # песочно-янтарный
# лёгкая тень снизу плитки
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
sd.rounded_rectangle([10, 26, S - 11, S - 5], radius=RAD, fill=(120, 70, 20, 90))
shadow = shadow.filter(ImageFilter.GaussianBlur(18))
img.alpha_composite(shadow)
img.alpha_composite(tile)
d = ImageDraw.Draw(img)

BROWN = (107, 63, 29, 255)      # тело верблюда
BROWN_D = (86, 48, 20, 255)     # ноги/детали потемнее
TEAL = (31, 138, 140, 255)      # седельная попона
TEAL_D = (22, 110, 112, 255)

# масштаб: рисуем в координатах 0..1000, отступ 12
def P(x, y):
    return (12 + x * 1000 / 1024 * 0.96, 12 + y * 1000 / 1024 * 0.96)

def ell(cx, cy, w, h, fill):
    d.ellipse([P(cx - w / 2, cy - h / 2), P(cx + w / 2, cy + h / 2)], fill=fill)

def line(points, width, fill):
    d.line([P(x, y) for x, y in points], fill=fill, width=int(width * 1000 / 1024 * 0.96), joint="curve")

# тень на земле
ell(520, 812, 560, 46, (150, 90, 35, 120))

# хвост
line([(730, 470), (762, 540), (752, 610)], 20, BROWN_D)

# ноги (4)
for x in (350, 455, 585, 690):
    line([(x, 545), (x - 6, 790)], 52, BROWN_D)
    ell(x - 8, 792, 64, 34, BROWN_D)  # копыто

# тело
ell(520, 470, 430, 210, BROWN)
# горбы
ell(415, 355, 170, 150, BROWN)
ell(590, 348, 185, 158, BROWN)
# грудь/круп округление
ell(315, 470, 150, 190, BROWN)
ell(715, 465, 130, 175, BROWN)

# шея
line([(330, 440), (262, 330), (238, 285)], 88, BROWN)
# голова
ell(215, 262, 120, 82, BROWN)
ell(165, 285, 52, 40, BROWN)  # морда
# ухо
ell(245, 218, 34, 26, BROWN_D)
# глаз
ell(222, 252, 13, 13, (40, 22, 10, 255))

# попона между горбами
d.rounded_rectangle([P(478, 300), P(560, 396)], radius=18, fill=TEAL)
line([(498, 396), (498, 428)], 12, TEAL_D)
line([(540, 396), (540, 428)], 12, TEAL_D)
d.polygon([P(488, 300), P(550, 300), P(519, 268)], fill=TEAL_D)  # верхушка попоны

# рамка-акцент по краю плитки
d.rounded_rectangle([6, 6, S - 7, S - 7], radius=RAD, outline=(255, 236, 210, 160), width=10)

img.save("assets/camel_1024.png")

# --- многоразмерный .ico ---
ico = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
img256 = img.resize((256, 256), Image.LANCZOS)
img256.save(
    "assets/app.ico",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print("saved assets/camel_1024.png and assets/app.ico")
