"""复判分类器的消融检查：AUROC 0.999 是真学到东西，还是只是重学了 diff 亮度？

对 official_test 的已上报候选（3,033 TP / 83 FP）计算简单统计特征，
与 CNN 的 AUROC 对比。若简单特征也接近 0.999，说明 DL 无增量。
"""
import csv, json, os
import numpy as np
from sklearn.metrics import roc_auc_score, roc_curve
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler

OUT = 'reports/review_clf'
root = 'data/candidates_official_test'


def load_manifest(r):
    rows = []
    with open(os.path.join(r, 'manifest.csv'), encoding='utf-8') as f:
        for x in csv.DictReader(f):
            x['y'] = 1 if x['label'] == '1' else 0
            rows.append(x)
    return rows


rows = load_manifest(root)
keep = [i for i, r in enumerate(rows) if r['label'] == '1' or r['source'] == 'fp']
y = np.array([rows[i]['y'] for i in keep])
X = np.load(os.path.join(OUT, 'test_x.npy'))[keep].astype(np.float32)
print('samples', X.shape, 'pos', int(y.sum()), 'neg', int((1 - y).sum()))

R, G, B = X[..., 0], X[..., 1], X[..., 2]
feats = {}
feats['B_mean'] = B.mean(axis=(1, 2))
feats['B_p99'] = np.percentile(B, 99, axis=(1, 2))
feats['B_frac>30'] = (B > 30).mean(axis=(1, 2))
feats['B_max'] = B.max(axis=(1, 2))
feats['RG_mean_absdiff'] = np.abs(R - G).mean(axis=(1, 2))

pw = np.array([float(r['patch_w']) for r in rows])[keep]
ph = np.array([float(r['patch_h']) for r in rows])[keep]
feats['patch_area'] = pw * ph
feats['aspect'] = np.maximum(pw / ph, ph / pw)
feats['conf'] = np.array([float(r['conf']) for r in rows])[keep]
feats['area'] = np.array([float(r['area']) for r in rows])[keep]

print('\n--- 单特征 AUROC ---')
for k, v in feats.items():
    print(f'{k:18s} {roc_auc_score(y, v):.4f}')

# 组合：简单特征 + 逻辑回归，按板分组 CV（避免同板泄漏）
names = list(feats.keys())
M = np.stack([feats[k] for k in names], axis=1)
# 分组键必须是板号（pair 前5位）：00041000/00041001 是同一块板的两张图，
# 按 pair 分折会让同板候选跨折泄漏（同 gate_group_cv.py / crossset_calibration.py 的教训）。
boards = np.array([r['pair'][:5] for r in rows])[keep]
gkf = GroupKFold(n_splits=5)
oof = np.zeros(len(y))
for tr, va in gkf.split(M, y, groups=boards):
    sc = StandardScaler().fit(M[tr])
    lr = LogisticRegression(max_iter=1000, class_weight='balanced').fit(sc.transform(M[tr]), y[tr])
    oof[va] = lr.predict_proba(sc.transform(M[va]))[:, 1]
print(f'\n--- 简单特征 + 逻辑回归（GroupKFold OOF）AUROC {roc_auc_score(y, oof):.4f} ---')
print('coef:', dict(zip(names, np.round(lr.coef_[0], 3))))

# 与 CNN 对比
import torch
import torch.nn as nn
from torchvision import models

ck = torch.load(os.path.join(OUT, 'review_clf.pt'), map_location='cpu', weights_only=False)
m = models.resnet18(weights=None)
m.fc = nn.Linear(m.fc.in_features, 1)
m.load_state_dict(ck['state_dict'])
m.eval().cuda()
with torch.no_grad():
    scores = []
    for i in range(0, len(X), 256):
        b = torch.from_numpy(X[i:i + 256]).permute(0, 3, 1, 2).div(255).cuda()
        scores.append(torch.sigmoid(m(b)).cpu().numpy().ravel())
cnn = np.concatenate(scores)
cnn_auc = roc_auc_score(y, cnn)
print(f'\n--- CNN AUROC {cnn_auc:.4f} vs 简单特征 {roc_auc_score(y, oof):.4f} ---')

# 固定工作点对比：要求 TP 保留 99.5%
for name, s in [('CNN', cnn), ('简单LR', oof), ('B_mean', feats['B_mean'])]:
    order = np.argsort(-s)
    ys = y[order]
    tp_tot = ys.sum(); fp_tot = len(ys) - tp_tot
    pick = None
    for k in range(1, len(ys) + 1):
        tp = ys[:k].sum()
        if tp / tp_tot >= 0.995:
            pick = (tp / tp_tot, (k - tp) / fp_tot); break
    print(f'{name}: TP保留 {pick[0]:.4f}  FP剔除 {pick[1]:.4f}')

res = {'cnn_auc': round(float(cnn_auc), 4),
       'lr_oof_auc': round(float(roc_auc_score(y, oof)), 4),
       'n_pos': int(y.sum()), 'n_neg': int((1 - y).sum()),
       'group_key': 'board(pair[:5])'}
with open(os.path.join(OUT, 'ablation.json'), 'w', encoding='utf-8') as f:
    json.dump(res, f, ensure_ascii=False, indent=2)
print('\nsaved', os.path.join(OUT, 'ablation.json'))
