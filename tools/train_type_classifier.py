"""分型对照实验：用 CNN 对已上报候选做六分类，与规则分型树（97.3%）对比。

只在「已正确检出的候选」上替换分型，不改检出、不改框几何。
训练：data/candidates_train 的 defect/（6,646，带 6 类标签）
留出：data/candidates_official_test 的 defect/（3,033）—— 与训练集零板重叠
"""
import argparse, csv, json, os, random, time
from collections import Counter
import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader
from torchvision import models
from sklearn.metrics import confusion_matrix, accuracy_score

SIZE = 96
CLASSES = ['open', 'short', 'mousebite', 'spur', 'copper', 'pin-hole']
OUT = 'reports/type_clf'


def load(manifest_root, cache_dir, tag):
    rows = []
    with open(os.path.join(manifest_root, 'manifest.csv'), encoding='utf-8') as f:
        for r in csv.DictReader(f):
            if r['label'] == '1':
                rows.append(r)
    X = np.load(os.path.join(cache_dir, f'{tag}_x.npy'))
    Y = np.load(os.path.join(cache_dir, f'{tag}_y.npy'))
    P = np.load(os.path.join(cache_dir, f'{tag}_pair.npy'))
    idx = np.where(Y == 1)[0]
    assert len(idx) == len(rows), (len(idx), len(rows))
    labels = np.array([CLASSES.index(r['type']) for r in rows], dtype=np.int64)
    return X[idx], labels, P[idx]


class DS(Dataset):
    def __init__(self, X, y, train=False):
        self.X, self.y, self.train = X, y, train

    def __len__(self):
        return len(self.y)

    def __getitem__(self, i):
        x = torch.from_numpy(self.X[i]).permute(2, 0, 1).float() / 255.0
        if self.train:
            if random.random() < 0.5:
                x = torch.flip(x, [2])
            if random.random() < 0.5:
                x = torch.flip(x, [1])
        return x, torch.tensor(self.y[i])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--epochs', type=int, default=12)
    ap.add_argument('--bs', type=int, default=64)
    ap.add_argument('--lr', type=float, default=3e-4)
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    random.seed(42); np.random.seed(42); torch.manual_seed(42)

    Xtr, ytr, ptr = load('data/candidates_train', 'reports/review_clf', 'train')
    Xte, yte, _ = load('data/candidates_official_test', 'reports/review_clf', 'test')
    print('train', Xtr.shape, Counter(ytr.tolist()))
    print('test', Xte.shape, Counter(yte.tolist()))

    uniq = np.unique(ptr); rng = np.random.RandomState(42); rng.shuffle(uniq)
    va_pairs = set(uniq[int(len(uniq) * 0.8):].tolist())
    is_va = np.isin(ptr, list(va_pairs))
    tr_idx = np.where(~is_va)[0]; va_idx = np.where(is_va)[0]

    m = models.resnet18(weights=models.ResNet18_Weights.IMAGENET1K_V1)
    m.fc = nn.Linear(m.fc.in_features, len(CLASSES))
    m = m.cuda()
    crit = nn.CrossEntropyLoss()
    opt = torch.optim.AdamW(m.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    dl = DataLoader(DS(Xtr[tr_idx], ytr[tr_idx], True), batch_size=args.bs, shuffle=True, num_workers=0, drop_last=True)

    def infer(X, bs=256):
        m.eval(); out = []
        with torch.no_grad():
            for i in range(0, len(X), bs):
                b = torch.from_numpy(X[i:i + bs]).permute(0, 3, 1, 2).float().div(255).cuda()
                out.append(m(b).argmax(1).cpu().numpy())
        return np.concatenate(out)

    best, hist = 0.0, []
    for ep in range(1, args.epochs + 1):
        m.train(); t0 = time.time(); tot = n = 0
        for x, y in dl:
            x, y = x.cuda(), y.cuda()
            opt.zero_grad(); loss = crit(m(x), y); loss.backward(); opt.step()
            tot += float(loss); n += 1
        sched.step()
        acc = accuracy_score(ytr[va_idx], infer(Xtr[va_idx]))
        hist.append({'epoch': ep, 'loss': tot / max(n, 1), 'val_acc': float(acc), 'sec': round(time.time() - t0, 1)})
        print(f'ep {ep:2d} loss {tot/max(n,1):.4f} val_acc {acc:.4f} ({time.time()-t0:.0f}s)', flush=True)
        if acc > best:
            best = acc
            torch.save({'state_dict': m.state_dict(), 'classes': CLASSES, 'size': SIZE}, os.path.join(OUT, 'type_clf.pt'))

    ck = torch.load(os.path.join(OUT, 'type_clf.pt'), weights_only=False)
    m.load_state_dict(ck['state_dict'])
    pred = infer(Xte)
    acc = accuracy_score(yte, pred)
    cm = confusion_matrix(yte, pred, labels=list(range(len(CLASSES))))
    print(f'official_test type acc {acc:.4f}  (n={len(yte)})')
    print('per-class acc:', {CLASSES[i]: round(float(cm[i, i] / max(cm[i].sum(), 1)), 4) for i in range(len(CLASSES))})

    # 模拟严格分型 F：严格TP = 分型正确的候选（上限 3033）
    strict_tp = int((pred == yte).sum())
    P = strict_tp / 3116; R = strict_tp / 3140
    f = 2 * P * R / max(P + R, 1e-9)
    print(f'simulated strict F = {f:.4f} (strict_tp {strict_tp}, baseline 2952 / 0.9437)')

    res = {
        'val_acc_best': round(float(best), 4),
        'official_type_acc': round(float(acc), 4),
        'official_n': int(len(yte)),
        'per_class': {CLASSES[i]: round(float(cm[i, i] / max(cm[i].sum(), 1)), 4) for i in range(len(CLASSES))},
        'confusion': cm.tolist(),
        'strict_tp': strict_tp,
        'simulated_strict_f': round(float(f), 4),
        'baseline_strict_tp': 2952,
        'baseline_strict_f': 0.9437,
        'history': hist,
    }
    with open(os.path.join(OUT, 'metrics.json'), 'w', encoding='utf-8') as fh:
        json.dump(res, fh, ensure_ascii=False, indent=2)
    print('saved', OUT)


if __name__ == '__main__':
    main()
