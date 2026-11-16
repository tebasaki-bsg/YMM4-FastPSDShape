"""フォルダのマスクと通過フォルダの不透明度のテスト用の小さな PSD（mask.psd）を作る（v0.1.11）。
psd-tools ではフォルダにマスクを付けられないので、PSD の形式を直接書く（8bit RGB、無圧縮）。追加のパッケージは不要。

キャンバス 48x8。左から3つの区画に分けて、互いに重ならないようにしている。
  背景      灰色 (128,128,128) の全面
  A（通常） マスク（外は黒）: 範囲 x0-7。x0-3 は 255、x4-7 は 128。中身は赤の x0-15
  B（通過） マスク（外は白）: 範囲 x28-31 は 0（隠す）。中身は青の乗算 x16-31
  C（通過・不透明度 128、マスクなし） 中身は下から緑 x32-47、白 x40-47
"""
import struct, sys

W, H = 48, 8
out = sys.argv[1] if len(sys.argv) > 1 else "mask.psd"


def pascal(name):
    b = name.encode("ascii")
    s = bytes([len(b)]) + b
    while len(s) % 4:
        s += b"\0"
    return s


def block(key, data):
    return b"8BIM" + key + struct.pack(">I", len(data)) + data


class Layer:
    def __init__(self, name, rect=(0, 0, 0, 0), color=None, blend=b"norm", opacity=255,
                 section=None, section_blend=None, mask=None):
        self.name, self.rect, self.color, self.blend, self.opacity = name, rect, color, blend, opacity
        self.section, self.section_blend, self.mask = section, section_blend, mask  # mask = (rect, default, values)

    def channels(self):
        t, l, b, r = self.rect
        n = (b - t) * (r - l)
        if self.color is None:
            chans = [(-1, b""), (0, b""), (1, b""), (2, b"")]
        else:
            cr, cg, cb = self.color
            chans = [(-1, bytes([255]) * n), (0, bytes([cr]) * n), (1, bytes([cg]) * n), (2, bytes([cb]) * n)]
        if self.mask:
            chans.append((-2, bytes(self.mask[2])))
        return chans

    def record(self):
        t, l, b, r = self.rect
        chans = self.channels()
        s = struct.pack(">iiiiH", t, l, b, r, len(chans))
        for cid, data in chans:
            s += struct.pack(">hI", cid, len(data) + 2)
        s += b"8BIM" + self.blend + bytes([self.opacity, 0, 0, 0])
        extra = b""
        if self.mask:
            (mt, ml, mb, mr), default, _ = self.mask
            extra += struct.pack(">IiiiiBBxx", 20, mt, ml, mb, mr, default, 0)
        else:
            extra += struct.pack(">I", 0)
        extra += struct.pack(">I", 0)  # ブレンド条件なし
        extra += pascal(self.name)
        if self.section is not None:
            d = struct.pack(">I", self.section)
            if self.section_blend:
                d += b"8BIM" + self.section_blend
            extra += block(b"lsct", d)
        s += struct.pack(">I", len(extra)) + extra
        return s

    def image(self):
        return b"".join(struct.pack(">H", 0) + data for _, data in self.channels())


def group(name, children_bottom_up, blend=b"norm", opacity=255, mask=None):
    """フォルダ（下から：区切り、中身、フォルダ本体）"""
    end = Layer("</Layer group>", section=3)
    head = Layer(name, blend=blend, opacity=opacity, section=1, section_blend=blend, mask=mask)
    return [end] + children_bottom_up + [head]


def mask_values(rect, f):
    t, l, b, r = rect
    return [f(x, y) for y in range(t, b) for x in range(l, r)]


layers = [Layer("bg", (0, 0, H, W), (128, 128, 128))]
ra = (0, 0, H, 8)
layers += group("A", [Layer("red", (0, 0, H, 16), (255, 0, 0))],
                mask=(ra, 0, mask_values(ra, lambda x, y: 255 if x < 4 else 128)))
rb = (0, 28, H, 32)
layers += group("B", [Layer("blue", (0, 16, H, 32), (0, 0, 255), blend=b"mul ")], blend=b"pass",
                mask=(rb, 255, mask_values(rb, lambda x, y: 0)))
layers += group("C", [Layer("green", (0, 32, H, 48), (0, 255, 0)), Layer("white", (0, 40, H, 48), (255, 255, 255))],
                blend=b"pass", opacity=128)

info = struct.pack(">h", len(layers)) + b"".join(l.record() for l in layers) + b"".join(l.image() for l in layers)
if len(info) % 2:
    info += b"\0"
lmi = struct.pack(">I", len(info)) + info + struct.pack(">I", 0)

psd = b"8BPS" + struct.pack(">H6xHIIHH", 1, 3, H, W, 8, 3)
psd += struct.pack(">I", 0) + struct.pack(">I", 0)
psd += struct.pack(">I", len(lmi)) + lmi
psd += struct.pack(">H", 0) + bytes([128]) * (W * H * 3)  # 統合画像（使わない）
open(out, "wb").write(psd)
print(out, len(psd), "bytes")
