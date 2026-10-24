"""テスト用の小さなPSD（rules.psd）を作る。psd-tools が必要: pip install psd-tools"""
import logging, sys
logging.disable(logging.CRITICAL)
from PIL import Image
from psd_tools import PSDImage
from psd_tools.api.layers import PixelLayer, Group
from psd_tools.constants import Compression

out = sys.argv[1] if len(sys.argv) > 1 else "rules.psd"
psd = PSDImage.new("RGBA", (64, 64), depth=8)
colors = iter([(255,0,0),(0,255,0),(0,0,255),(255,255,0),(0,255,255),(255,0,255),(128,0,0),(0,128,0),(0,0,128),(128,128,0),(0,128,128),(128,0,128),(200,100,50),(50,100,200),(90,90,90)])

def px(parent, name, visible=True, x=0, y=0):
    im = Image.new("RGBA", (8, 8), next(colors) + (255,))
    l = PixelLayer.frompil(im, psd, name, y, x, Compression.RLE)
    l.visible = visible
    parent.append(l)
    return l

def grp(parent, name, visible=True):
    g = Group.new(psd, name=name)
    g.visible = visible
    parent.append(g)
    return g

# 追加した順に下から積まれる。ここでは下→上の順に書く。
body = grp(psd, "!体", visible=False)
px(body, "体")
arm = grp(psd, "腕")              # スライダー化のテスト
px(arm, "腕2", visible=False, x=8)
px(arm, "腕1", visible=True, x=8)
px(arm, "腕0", visible=True, x=8)
mouth = grp(psd, "口")
px(mouth, "*口B", visible=False, x=16)
a = grp(mouth, "*口A")
px(a, "開", visible=False, x=16)
px(a, "半", visible=False, x=16)
px(a, "閉", visible=True, x=16)
eye = grp(psd, "目")
px(eye, "*ジト目", visible=False, x=24)
px(eye, "*閉じ目", visible=True, x=24)
px(eye, "*通常目", visible=True, x=24)
px(psd, "!輪郭", visible=False, x=32)
px(psd, "重複", x=40)
px(psd, "重複", x=40)
psd.save(out, encoding="shift_jis")
print("saved", out)
