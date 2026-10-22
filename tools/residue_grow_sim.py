"""预判：开运算残渣经区域生长后能否匹配上 GT？

母团救援（L2016 RescueByMother）已被实测证伪：残渣面积 6~21 远小于 GT 框 25~34，
放行后仍匹配不上。缺的是「形态」不是「面积」。

本脚本离线验证唯一可能的补救——**把残渣沿 |test-temp|>阈值 的连通域长回缺陷足迹**：
  1. 复刻开运算（OpeningCircle radius=1，3x3 十字/方形结构元）
  2. 取残渣（开运算后面积<selectMin=22 的连通域）
  3. 以残渣为种子，在「未开运算」的差分区域上做连通域生长（模拟 GrowBoxesToRegion）
  4. 量测生长后 vs GT 的 IoU —— 能过 0.33 才值得上 C# 改代码

判据：若生长后 IoU>=0.33 的比例显著高于残渣原 IoU，则值得动；否则这条路也封死。
"""
import sys, os, csv, json
import numpy as np
from PIL import Image
from collections import deque

W = H = 640
THRESH = 30
SELECT_MIN = 22
OPEN_R = 1          # OpeningCircle radius
MARGIN = 12
CAP = 6             # growMaxScale
BOX_SCALE = 1.5


def align_int(test, temp, tiles=8, max_shift=8, min_gain=0.1):
    tb = (temp >= 128)
    aligned = np.zeros_like(temp)
    for tr in range(tiles):
        for tc in range(tiles):
            x0, x1 = tc * W // tiles, (tc + 1) * W // tiles
            y0, y1 = tr * H // tiles, (tr + 1) * H // tiles
            sx0, sx1 = max(x0, max_shift), min(x1, W - max_shift)
            sy0, sy1 = max(y0, max_shift), min(y1, H - max_shift)
            if sx1 <= sx0 or sy1 <= sy0:
                continue

            def xor(dx, dy):
                return int(np.count_nonzero(tb[sy0:sy1, sx0:sx1] != tb[sy0 + dy:sy1 + dy, sx0 + dx:sx1 + dx]))

            at0 = xor(0, 0); best, bdx, bdy = at0, 0, 0
            for dy in range(-max_shift, max_shift + 1):
                for dx in range(-max_shift, max_shift + 1):
                    if dx == 0 and dy == 0: continue
                    v = xor(dx, dy)
                    if v < best or (v == best and abs(dx) + abs(dy) < abs(bdx) + abs(bdy)):
                        best, bdx, bdy = v, dx, dy
            if at0 == 0 or best > at0 * (1 - min_gain):
                best, bdx, bdy = at0, 0, 0
            tx = np.clip(np.arange(x0, x1) + bdx, 0, W - 1)
            ty = np.clip(np.arange(y0, y1) + bdy, 0, H - 1)
            aligned[y0:y1, x0:x1] = temp[np.ix_(ty, tx)]
    return aligned


def _shift_or(mask, dy, dx, fill=False):
    """平移后或运算（越界填fill），不用 np.roll 以免环绕污染边界。"""
    out = np.full_like(mask, fill)
    h, w = mask.shape
    ys0, ys1 = max(0, dy), min(h, h + dy)
    yd0, yd1 = max(0, -dy), min(h, h - dy)
    xs0, xs1 = max(0, dx), min(w, w + dx)
    xd0, xd1 = max(0, -dx), min(w, w - dx)
    out[yd0:yd1, xd0:xd1] = mask[ys0:ys1, xs0:xs1]
    return out


def _shift_and(mask, dy, dx, fill=True):
    """平移后与运算（越界按 fill 填充；越界为 True 时等价于补白）。"""
    out = np.full_like(mask, fill)
    h, w = mask.shape
    ys0, ys1 = max(0, dy), min(h, h + dy)
    yd0, yd1 = max(0, -dy), min(h, h - dy)
    xs0, xs1 = max(0, dx), min(w, w + dx)
    xd0, xd1 = max(0, -dx), min(w, w - dx)
    out[yd0:yd1, xd0:xd1] &= mask[ys0:ys1, xs0:xs1]
    return out


def opening(mask, r=OPEN_R):
    """近似 OpeningCircle：r=1 时用 3x3 方形腐蚀后膨胀（无环绕）。"""
    er = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dy == 0 and dx == 0:
                continue
            er &= _shift_and(mask, dy, dx, fill=True)
    di = er.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dy == 0 and dx == 0:
                continue
            di |= _shift_or(er, dy, dx, fill=False)
    return di


def label(mask, region=None):
    """连通域标号；region 给定时只在该窗口内洪泛。返回 list of (mask_bool, area)。"""
    H_, W_ = mask.shape
    seen = np.zeros_like(mask)
    comps = []
    ys, xs = np.nonzero(mask)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        comp = np.zeros_like(mask)
        q = deque([(sy, sx)]); seen[sy, sx] = True
        while q:
            y, x = q.popleft()
            comp[y, x] = True
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    if dy == 0 and dx == 0:
                        continue
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H_ and 0 <= nx < W_ and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True; q.append((ny, nx))
        comps.append(comp)
    return comps


def bbox(comp):
    ys, xs = np.nonzero(comp)
    return xs.min(), ys.min(), xs.max() - xs.min() + 1, ys.max() - ys.min() + 1


def grow(comp, rawmask, margin=MARGIN, cap=CAP, box_scale=BOX_SCALE):
    """模拟 GrowBoxesToRegion：从残渣框出发，在未开运算差分上洪泛，取外接框后各向同性缩放。"""
    x, y, w, h = bbox(comp)
    # 外扩 margin，找连通该残渣的完整差分连通域
    wx1, wy1 = max(0, x - margin), max(0, y - margin)
    wx2, wy2 = min(W, x + w + margin), min(H, y + h + margin)
    win = np.zeros_like(rawmask)
    win[wy1:wy2, wx1:wx2] = rawmask[wy1:wy2, wx1:wx2]
    grown = np.zeros_like(rawmask)
    grown |= comp
    # 在窗口内洪泛（8 邻接，与现网一致）
    ys, xs = np.nonzero(comp)
    stack = deque(zip(ys.tolist(), xs.tolist()))
    while stack:
        cy, cx = stack.popleft()
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0: continue
                ny, nx = cy + dy, cx + dx
                if wy1 <= ny < wy2 and wx1 <= nx < wx2 and win[ny, nx] and not grown[ny, nx]:
                    grown[ny, nx] = True; stack.append((ny, nx))
    gx, gy, gw, gh = bbox(grown)
    # 各向同性缩放（GrowBoxScale），中心不变，边长上限 cap× 原边
    cx0, cy0 = gx + gw / 2.0, gy + gh / 2.0
    nw, nh = gw * box_scale, gh * box_scale
    nw = min(nw, w * cap); nh = min(nh, h * cap)
    return (int(round(cx0 - nw / 2)), int(round(cy0 - nh / 2)), int(round(nw)), int(round(nh))), int(grown.sum())


def iou(b1, g):
    x1, y1 = max(b1[0], g[0]), max(b1[1], g[1])
    x2, y2 = min(b1[0] + b1[2], g[0] + g[2]), min(b1[1] + b1[3], g[1] + g[3])
    iw, ih = max(0, x2 - x1), max(0, y2 - y1)
    inter = iw * ih
    if inter == 0: return 0.0
    ua = b1[2] * b1[3] + g[2] * g[3] - inter
    return inter / ua if ua else 0.0


def main():
    samples = sys.argv[1]
    audit_json = sys.argv[2]
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    GM = int(sys.argv[4]) if len(sys.argv) > 4 else MARGIN
    GC = int(sys.argv[5]) if len(sys.argv) > 5 else CAP
    GB = float(sys.argv[6]) if len(sys.argv) > 6 else BOX_SCALE
    print('grow margin=%d cap=%d boxScale=%.2f' % (GM, GC, GB))

    data = json.load(open(audit_json, encoding='utf-8'))
    items = [r for r in data['Items'] if r['Stage'] in ('area<min', '开运算抹掉')]
    print('candidates: area<min=%d, 开运算抹掉=%d, total=%d' % (
        sum(1 for r in items if r['Stage'] == 'area<min'),
        sum(1 for r in items if r['Stage'] == '开运算抹掉'), len(items)))

    out = []
    for r in items[:limit]:
        stem = r['Stem']
        tp, mp = f'{samples}/{stem}_test.jpg', f'{samples}/{stem}_temp.jpg'
        if not (os.path.exists(tp) and os.path.exists(mp)): continue
        test = np.asarray(Image.open(tp).convert('L'), dtype=np.int16)
        temp = np.asarray(Image.open(mp).convert('L'), dtype=np.int16)
        aligned = align_int(test, temp)
        d = np.abs(test - aligned)
        raw = d >= THRESH
        opened = opening(raw, OPEN_R)
        # 只取面积 < SELECT_MIN 的开运算残渣
        comps = [c for c in label(opened) if 0 < c.sum() < SELECT_MIN]
        g = (r['X1'], r['Y1'], r['W'], r['H'])
        # 取与 GT 重叠最多的残渣
        best = None; bi = -1
        for i, c in enumerate(comps):
            x, y, w, h = bbox(c)
            if not (x + w < g[0] or x > g[0] + g[2] or y + h < g[1] or y > g[1] + g[3]):
                if c.sum() > bi:
                    bi = c.sum(); best = (c, (x, y, w, h))
        if best is None:
            out.append({'stem': stem, 'stage': r['Stage'], 'found': False}); continue
        comp, b0 = best
        gb, ga = grow(comp, raw, margin=GM, cap=GC, box_scale=GB)
        out.append({
            'stem': stem, 'stage': r['Stage'], 'found': True,
            'residue_area': int(comp.sum()), 'gt_area': g[2] * g[3],
            'iou_residue': round(iou(b0, g), 3), 'iou_grown': round(iou(gb, g), 3),
            'grew_area': ga, 'box_residue': list(b0), 'box_grown': list(gb),
        })
        print('%s %-8s res=%-3d iou_res=%.3f -> iou_grown=%.3f %s' % (
            stem, r['Stage'], comp.sum(), iou(b0, g), iou(gb, g),
            'HIT' if iou(gb, g) >= 0.33 else ''))

    json.dump(out, open('reports/residue_grow_sim.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=int)
    ok = [o for o in out if o.get('found')]
    if not ok:
        print('no residue found'); return
    before = sum(1 for o in ok if o['iou_residue'] >= 0.33)
    after = sum(1 for o in ok if o['iou_grown'] >= 0.33)
    print('\n=== %d residues===' % len(ok))
    print('IoU>=0.33 before grow: %d / %d' % (before, len(ok)))
    print('IoU>=0.33 after  grow: %d / %d' % (after, len(ok)))
    ir = sorted(o['iou_residue'] for o in ok); ig = sorted(o['iou_grown'] for o in ok)
    print('iou_residue med=%.3f  iou_grown med=%.3f' % (ir[len(ir)//2], ig[len(ig)//2]))


if __name__ == '__main__':
    main()