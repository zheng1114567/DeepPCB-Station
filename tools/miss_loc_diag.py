"""漏检定位诊断 v2：差分信号强，但为何没检出？

v1 结论推翻「SNR 不足」：110 个漏检 GT 全部 |diff| 峰值 >=30，105 个在 GT 框内
有 >=22 个超阈像素（已达minBlobArea=22）。说明差分阶段看得见，丢失发生在
「连通域提取之后」。本脚本逐个量化：

  1. GT 框内 |diff|>=30 的连通域清单（面积/质心/与 GT 框的偏移/长宽比）
  2. 该连通域是否因面积<minBlobArea 被丢（并列 n_above30 与实际连通域面积）
  3. 质心相对 GT 中心的偏移分布——判定「框对不上」vs 「真没信号」
  4. 该连通域若独立成框，与 GT 的 IoU 会是多少（模拟：把它当候选能否救回）
"""
import sys, os, csv, json
import numpy as np
from PIL import Image
from collections import deque

W = H = 640
TILES, MAXSHIFT, MINGAIN = 8, 8, 0.1


def align_int(test, temp, tiles=TILES, max_shift=MAXSHIFT, min_gain=MINGAIN):
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


def components(mask):
    """4-邻接连通域，返回 (面积, x1,y1,x2,y2, cx,cy) 列表。"""
    lab = -np.ones(mask.shape, dtype=np.int32)
    comps = []
    H_, W_ = mask.shape
    for sy in range(H_):
        row = mask[sy]
        for sx in range(W_):
            if not row[sx] or lab[sy, sx] >= 0:
                continue
            cid = len(comps); q = deque([(sy, sx)]); lab[sy, sx] = cid
            area = 0; x1 = x2 = sx; y1 = y2 = sy; sx_sum = 0; sy_sum = 0
            while q:
                y, x = q.popleft()
                area += 1; sx_sum += x; sy_sum += y
                if x < x1: x1 = x
                if x > x2: x2 = x
                if y < y1: y1 = y
                if y > y2: y2 = y
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H_ and 0 <= nx < W_ and mask[ny, nx] and lab[ny, nx] < 0:
                        lab[ny, nx] = cid; q.append((ny, nx))
            comps.append({'area': area, 'x1': x1, 'y1': y1, 'x2': x2, 'y2': y2,
                          'cx': sx_sum / area, 'cy': sy_sum / area})
    return comps


def iou(a, b):
    ix1, iy1 = max(a['x1'], b['x1']), max(a['y1'], b['y1'])
    ix2, iy2 = min(a['x2'], b['x2']), min(a['y2'], b['y2'])
    iw, ih = max(0, ix2 - ix1), max(0, iy2 - iy1)
    inter = iw * ih
    if inter == 0: return 0.0
    ua = (a['x2'] - a['x1']) * (a['y2'] - a['y1']) + (b['x2'] - b['x1']) * (b['y2'] - b['y1']) - inter
    return inter / ua if ua > 0 else 0.0


def main():
    samples = sys.argv[1]; miss_dump = sys.argv[2]
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    with open(miss_dump, encoding='utf-8') as f:
        rows = list(csv.DictReader(f, delimiter='\t'))[:limit]

    recs = []
    for r in rows:
        stem = r['stem']
        tp, mp = f'{samples}/{stem}_test.jpg', f'{samples}/{stem}_temp.jpg'
        if not (os.path.exists(tp) and os.path.exists(mp)): continue
        test = np.asarray(Image.open(tp).convert('L'), dtype=np.int16)
        temp = np.asarray(Image.open(mp).convert('L'), dtype=np.int16)
        aligned = align_int(test, temp)
        d = np.abs(test - aligned)
        mask = d >= 30
        # 面积下限内缩，避免全图连通；只取与 GT 有交集的连通域
        g = {'x1': int(r['GX1']), 'y1': int(r['GY1']), 'x2': int(r['GX2']), 'y2': int(r['GY2']),
             'cx': (int(r['GX1']) + int(r['GX2'])) / 2, 'cy': (int(r['GY1']) + int(r['GY2'])) / 2}
        # 限制在 GT 外扩 30px 的窗口内做连通域（避免全图洪水）
        mx, my = 30, 30
        wx1, wy1 = max(0, g['x1'] - mx), max(0, g['y1'] - my)
        wx2, wy2 = min(W, g['x2'] + mx), min(H, g['y2'] + my)
        sub = mask[wy1:wy2, wx1:wx2]
        comps = components(sub)
        for c in comps:
            c['x1'] += wx1; c['x2'] += wx1; c['y1'] += wy1; c['y2'] += wy1
        # 与 GT 有交集的
        rel = [c for c in comps
               if not (c['x2'] < g['x1'] or c['x1'] > g['x2'] or c['y2'] < g['y1'] or c['y1'] > g['y2'])]
        rel.sort(key=lambda c: -c['area'])
        best = rel[0] if rel else None
        rec = {
            'stem': stem, 'type': r['type'],
            'gt': g, 'n_rel': len(rel),
            'n_above30_in_gt': int(mask[max(0,g['y1']):g['y2'], max(0,g['x1']):g['x2']].sum()),
        }
        if best:
            rec['best'] = best
            rec['iou_raw'] = round(iou(best, g), 3)
            rec['dcx'] = round(best['cx'] - g['cx'], 1)
            rec['dcy'] = round(best['cy'] - g['cy'], 1)
            rec['area_ratio'] = round(best['area'] / max(1, (g['x2']-g['x1'])*(g['y2']-g['y1'])), 2)
            rec['wh_ratio'] = round((best['x2']-best['x1'])/max(1,(best['y2']-best['y1'])), 2)
        recs.append(rec)
        print('%s %-9s rel=%-3d %s' % (
            stem, r['type'], rec['n_rel'],
            ('area=%-5d iou=%.3f dcx=%+.1f dcy=%+.1f aRatio=%.2f' % (
                rec['best']['area'], rec['iou_raw'], rec['dcx'], rec['dcy'], rec['area_ratio']))
            if best else 'NO RELATIVE COMPONENT'))

    json.dump(recs, open('reports/miss_loc_diag.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    ok = [r for r in recs if 'best' in r]
    print('\n=== summary of %d ===' % len(ok))
    ious = sorted(r['iou_raw'] for r in ok)
    print('raw comp vs GT IoU: p10=%.3f p25=%.3f med=%.3f p75=%.3f' % (
        ious[len(ious)//10], ious[len(ious)//4], ious[len(ious)//2], ious[3*len(ious)//4]))
    import collections
    print('IoU 分档:', dict(collections.Counter(
        '0' if v == 0 else '0-0.2' if v < .2 else '0.2-0.33' if v < .33 else '0.33-0.5' if v < .5 else '>=0.5'
        for v in ious)))
    ar = sorted(r['area_ratio'] for r in ok)
    print('comp/GT 面积比: p10=%.2f med=%.2f p90=%.2f' % (ar[len(ar)//10], ar[len(ar)//2], ar[9*len(ar)//10]))
    aw = sorted(r['best']['area'] for r in ok)
    print('连通域面积: p10=%d med=%d p90=%d  (<22 的有 %d 个)' % (
        aw[len(aw)//10], aw[len(aw)//2], aw[9*len(aw)//10], sum(1 for a in aw if a < 22)))
    d = [abs(r['dcx']) for r in ok]
    d.sort()
    print('|质心偏移 dx|: med=%.1f p75=%.1f p90=%.1f' % (d[len(d)//2], d[3*len(d)//4], d[9*len(d)//10]))


if __name__ == '__main__':
    main()