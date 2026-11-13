"""「:flip」ルールのテスト用の小さなPSD（flip.psd）を作る（高速PSD立ち絵と同じもの）。psd-tools が必要: pip install psd-tools"""
import logging, sys
logging.disable(logging.CRITICAL)
from PIL import Image
from psd_tools import PSDImage
from psd_tools.api.layers import PixelLayer, Group
from psd_tools.constants import Compression

out = sys.argv[1] if len(sys.argv) > 1 else "flip.psd"
psd = PSDImage.new("RGBA", (64, 64), depth=8)
n = [0]

def px(parent, name, visible=True, x=0, y=0):
    n[0] += 1
    im = Image.new("RGBA", (8, 8), ((n[0] * 37) % 256, (n[0] * 91) % 256, (n[0] * 53) % 256, 255))
    l = PixelLayer.frompil(im, psd, name, y, x, Compression.RLE)
    l.visible = visible
    parent.append(l)
    return l

def grp(parent, name, visible=True):
    g = Group.new(psd, name=name)
    g.visible = visible
    parent.append(g)
    return g

# 追加した順に下から積まれる。ここでは下→上の順に書く（同じフォルダでは、通常用を反転用より上に置く）
cloth = grp(psd, "服")
uf = grp(cloth, "上:flipx", visible=False)   # 反転用フォルダ（中身は通常用と同じ名前）
px(uf, "柄B", visible=False, x=40)
px(uf, "柄A", visible=False, x=40)
u = grp(cloth, "上")
px(u, "柄B", visible=False)
px(u, "柄A", visible=True)

mouth_f = grp(psd, "口:flipx", visible=False)
px(mouth_f, "閉", visible=False, x=40, y=16)
px(mouth_f, "開", visible=False, x=40, y=16)
mouth = grp(psd, "口")
px(mouth, "閉", visible=True, y=16)
px(mouth, "開", visible=False, y=16)

px(psd, "保存反転:flipx", visible=True, x=40, y=24)   # 保存時は反転用だけが表示
px(psd, "保存反転", visible=False, y=24)
px(psd, "孤立:flipx", visible=True, y=32)             # 相手がいない → 普通の要素
px(psd, "顔:flipxy", visible=False, x=40, y=40)
px(psd, "顔:flipx:flipy", visible=False, x=40, y=40)
px(psd, "顔", visible=True, y=40)
px(psd, "!丸:flipx", visible=False, x=40, y=48)
px(psd, "!丸", visible=True, y=48)
psd.save(out, encoding="shift_jis")
print("saved", out)
