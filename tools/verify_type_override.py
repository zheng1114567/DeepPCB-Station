"""验证：把 DL 六分类结果覆盖到 pred 的类别上，跑真实贪心匹配，看严格 F 是否真的提升。

不是"预测对了几类"的模拟，而是复刻 PcbIcdarEvaluator 的 IoU≥0.33 + 同类型 + 同图 +
一对一贪心（pred 按 conf 降序），因此类别改变会影响后续匹配分配。
"""
import csv, os, json
import numpy as np
import torch
import torch.nn as nn
from torchvision import models

CLASSES = ['open', 'short', 'mousebite', 'spur', 'copper', 'pin-hole']
GT_TYPE = {1: 'open', 2: 'short', 3: 'mousebite', 4: 'spur', 5: 'copper', 6: 'pin-hole'}
IOU = 0.33


def iou(a, b):
    ax1, ay1, ax2, ay2 = a
    bx1, by1, bx2, by2 = b
    ix1, iy1 = max(ax1, bx1), max(ay1, by1)
    ix2, iy2 = min(ax2, bx2), min(ay2, by2)
    iw, ih = ix2 - ix1 + 1, iy2 - iy1 + 1
    if iw <= 0 or ih <= 0:
        return 0.0
    inter = iw * ih
    ua = (ax2 - ax1 + 1) * (ay2 - ay1 + 1) + (bx2 - bx1 + 1) * (by2 - by1 + 1) - inter
    return inter / ua if ua > 0 else 0.0


def load_gts():
    gts = {}
    for fn in os.listdir('data/official_test'):
        if not fn.endswith('.txt'):
            continue
        pair = fn[:-4]
        arr = []
        with open(os.path.join('data/official_test', fn), encoding='utf-8') as f:
            for line in f:
                p = line.split()
                if len(p) >= 5:
                    x1, y1, x2, y2, t = map(int, p[:5])
                    arr.append({'box': (x1, y1, x2, y2), 'type': GT_TYPE[t], 'm': False})
        gts[pair] = arr
    return gts


def evaluate(preds, gts, key):
    by_pair = {}
    for p in preds:
        by_pair.setdefault(p['pair'], []).append(p)
    tp = 0
    for pair, ps in by_pair.items():
        g = gts.get(pair, [])
        for x in g:
            x['m'] = False
        for p in sorted(ps, key=lambda z: -z['conf']):
            pt = p[key]
            best, bv = None, 0.0
            for cand in g:
                if cand['m'] or cand['type'] != pt:
                    continue
                v = iou(p['box'], cand['box'])
                if v >= IOU and v > bv:
                    bv, best = v, cand
            if best is not None:
                best['m'] = True
                tp += 1
    n_gt = sum(len(v) for v in gts.values())
    P, R = tp / len(preds), tp / n_gt
    F = 2 * P * R / (P + R) if P + R else 0
    return {'tp': tp, 'pred': len(preds), 'gt': n_gt, 'P': round(P, 4), 'R': round(R, 4), 'F': round(F, 4)}


def main():
    rows = list(csv.DictReader(open('data/candidates_official_test/manifest.csv', encoding='utf-8')))
    keep_idx = [i for i, r in enumerate(rows) if r['label'] == '1' or r['source'] == 'fp']
    preds = []
    for i in keep_idx:
        r = rows[i]
        x, y = int(r['box_x']), int(r['box_y'])
        w, h = int(r['box_w']), int(r['box_h'])
        preds.append({'pair': r['pair'], 'box': (x, y, x + w - 1, y + h - 1),
                      'type': r['type'], 'conf': float(r['conf']), 'row': i, 'dltype': None})
    print('preds', len(preds))

    ck = torch.load('reports/type_clf/type_clf.pt', map_location='cpu', weights_only=False)
    m = models.resnet18(weights=None)
    m.fc = nn.Linear(m.fc.in_features, len(CLASSES))
    m.load_state_dict(ck['state_dict'])
    m.eval().cuda()
    X = np.load('reports/review_clf/test_x.npy')[keep_idx]
    with torch.no_grad():
        out = []
        for i in range(0, len(X), 256):
            b = torch.from_numpy(X[i:i + 256]).permute(0, 3, 1, 2).float().div(255).cuda()
            out.append(m(b).argmax(1).cpu().numpy())
    dl = np.concatenate(out)
    for p, k in zip(preds, dl):
        p['dltype'] = CLASSES[int(k)]

    gts = load_gts()
    base = evaluate(preds, gts, 'type')
    new = evaluate(preds, gts, 'dltype')
    print('baseline (rule type):', base)
    print('DL type override   :', new)
    print(f'delta F = {new["F"]-base["F"]:+.4f}  delta TP = {new["tp"]-base["tp"]:+d}')

    changed = sum(1 for p in preds if p['type'] != p['dltype'])
    print('type changed:', changed, '/', len(preds))

    with open('reports/type_clf/verify_override.json', 'w', encoding='utf-8') as f:
        json.dump({'baseline': base, 'dl_override': new, 'changed': changed,
                   'note': '复刻 PcbIcdarEvaluator 贪心匹配；基线须复现 tp=2952'}, f, ensure_ascii=False, indent=2)


if __name__ == '__main__':
    main()
