"""生成 pred 类别覆盖表：把 DL 六分类结果写成评测器能读的 JSON。

key = "stem|px1|py1|px2|py2"（与检测器输出的 d.X/d.Y/d.X+d.W-1/d.Y+d.H-1 一致）
value = 1..6（open/short/mousebite/spur/copper/pin-hole）
"""
import csv, json, os
import numpy as np
import torch
import torch.nn as nn
from torchvision import models

CLASSES = ['open', 'short', 'mousebite', 'spur', 'copper', 'pin-hole']
OUT = 'reports/type_clf/override_official.json'


def main():
    rows = list(csv.DictReader(open('data/candidates_official_test/manifest.csv', encoding='utf-8')))
    keep = [i for i, r in enumerate(rows) if r['label'] == '1' or r['source'] == 'fp']
    X = np.load('reports/review_clf/test_x.npy')[keep]

    ck = torch.load('reports/type_clf/type_clf.pt', map_location='cpu', weights_only=False)
    m = models.resnet18(weights=None)
    m.fc = nn.Linear(m.fc.in_features, len(CLASSES))
    m.load_state_dict(ck['state_dict'])
    m.eval().cuda()
    with torch.no_grad():
        pred = []
        for i in range(0, len(X), 256):
            b = torch.from_numpy(X[i:i + 256]).permute(0, 3, 1, 2).float().div(255).cuda()
            pred.append(m(b).argmax(1).cpu().numpy())
    pred = np.concatenate(pred)

    out = {}
    for j, i in enumerate(keep):
        r = rows[i]
        x, y = int(r['box_x']), int(r['box_y'])
        w, h = int(r['box_w']), int(r['box_h'])
        out[f"{r['pair']}|{x}|{y}|{x + w - 1}|{y + h - 1}"] = int(pred[j]) + 1
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False)
    print('entries', len(out), '-> ', OUT)


if __name__ == '__main__':
    main()
