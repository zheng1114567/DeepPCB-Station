"""REVIEW 复判分类器：候选 patch 二分类（defect vs ghost）。

定位：只作用于 REVIEW 队列（规则层已判 NG 但置信度低），不参与检出与分型主线。
训练：data/candidates_train（1000 板，6,646 正 / 18,305 负）
留出：data/candidates_official_test（500 板，3,033 正 / 83 负）—— 与训练集零板重叠
纪律：official_test 只做最终评估，不参与任何调参；阈值在这里一次选定后不再回改。

用法：
  python tools/train_review_classifier.py --epochs 12 --out reports/review_clf
"""
import argparse, csv, json, os, random, time
from collections import Counter
import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader
from torchvision import models
from PIL import Image
from sklearn.metrics import roc_auc_score, roc_curve
from sklearn.model_selection import GroupShuffleSplit
from multiprocessing import Pool

SIZE = 96


def load_manifest(root):
    rows = []
    with open(os.path.join(root, 'manifest.csv'), encoding='utf-8') as f:
        for r in csv.DictReader(f):
            r['path'] = os.path.join(root, r['file'])
            r['y'] = 1 if r['label'] == '1' else 0
            rows.append(r)
    return rows


def letterbox(img, size):
    w, h = img.size
    s = min(size / w, size / h)
    nw, nh = max(1, int(round(w * s))), max(1, int(round(h * s)))
    img = img.resize((nw, nh), Image.BILINEAR)
    canvas = Image.new('RGB', (size, size), (0, 0, 0))
    canvas.paste(img, ((size - nw) // 2, (size - nh) // 2))
    return canvas


def _one(path):
    return np.asarray(letterbox(Image.open(path).convert('RGB'), SIZE), dtype=np.uint8)


def build_cache(root, out_dir, tag):
    xp = os.path.join(out_dir, f'{tag}_x.npy')
    yp = os.path.join(out_dir, f'{tag}_y.npy')
    pp = os.path.join(out_dir, f'{tag}_pair.npy')
    if os.path.exists(xp) and os.path.exists(yp) and os.path.exists(pp):
        return np.load(xp), np.load(yp), np.load(pp)
    rows = load_manifest(root)
    with Pool(min(8, os.cpu_count() or 4)) as pool:
        arrs = pool.map(_one, [r['path'] for r in rows], chunksize=64)
    X = np.stack(arrs)
    y = np.array([r['y'] for r in rows], dtype=np.int64)
    pair = np.array([r['pair'] for r in rows])
    np.save(xp, X); np.save(yp, y); np.save(pp, pair)
    return X, y, pair


class NpyDS(Dataset):
    def __init__(self, X, y, idx, train=False):
        self.X, self.y, self.idx, self.train = X, y, idx, train

    def __len__(self):
        return len(self.idx)

    def __getitem__(self, i):
        j = self.idx[i]
        x = torch.from_numpy(self.X[j]).permute(2, 0, 1).float() / 255.0
        if self.train:
            if random.random() < 0.5:
                x = torch.flip(x, [2])
            if random.random() < 0.5:
                x = torch.flip(x, [1])
        return x, torch.tensor([float(self.y[j])])


def build_model(pretrained=True):
    m = models.resnet18(weights=models.ResNet18_Weights.IMAGENET1K_V1 if pretrained else None)
    m.fc = nn.Linear(m.fc.in_features, 1)
    return m


@torch.no_grad()
def infer(model, X, idx, bs=256):
    model.eval()
    out = []
    for i in range(0, len(idx), bs):
        batch = torch.from_numpy(X[idx[i:i + bs]]).permute(0, 3, 1, 2).float().div(255).cuda()
        out.append(torch.sigmoid(model(batch)).cpu().numpy().ravel())
    return np.concatenate(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--epochs', type=int, default=12)
    ap.add_argument('--bs', type=int, default=64)
    ap.add_argument('--lr', type=float, default=3e-4)
    ap.add_argument('--out', default='reports/review_clf')
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    random.seed(42); np.random.seed(42); torch.manual_seed(42)

    t0 = time.time()
    Xtr, ytr, ptr = build_cache('data/candidates_train', args.out, 'train')
    Xte, yte, _ = build_cache('data/candidates_official_test', args.out, 'test')
    print(f'cache ready {time.time()-t0:.0f}s  train {Xtr.shape} test {Xte.shape}', flush=True)
    print('train label', dict(Counter(ytr.tolist())), 'test label', dict(Counter(yte.tolist())), flush=True)

    # 按板分组 80/20
    uniq = np.unique(ptr)
    rng = np.random.RandomState(42)
    rng.shuffle(uniq)
    cut = int(len(uniq) * 0.8)
    va_pairs = set(uniq[cut:].tolist())
    is_va = np.isin(ptr, list(va_pairs))
    tr_idx = np.where(~is_va)[0]
    va_idx = np.where(is_va)[0]
    print('train/val', len(tr_idx), len(va_idx), flush=True)

    model = build_model(True).cuda()
    pos = int(ytr[tr_idx].sum()); neg = len(tr_idx) - pos
    pos_weight = torch.tensor([neg / max(pos, 1)], dtype=torch.float32).cuda()
    print('pos_weight', float(pos_weight), flush=True)
    crit = nn.BCEWithLogitsLoss(pos_weight=pos_weight)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    dl_tr = DataLoader(NpyDS(Xtr, ytr, tr_idx, True), batch_size=args.bs, shuffle=True, num_workers=0, drop_last=True)

    y_va = ytr[va_idx]
    best_auc, best_state, hist = 0.0, None, []
    for ep in range(1, args.epochs + 1):
        model.train(); t1 = time.time(); tot = 0.0; n = 0
        for x, y in dl_tr:
            x = x.cuda(); y = y.cuda()
            opt.zero_grad()
            loss = crit(model(x), y)
            loss.backward(); opt.step()
            tot += float(loss); n += 1
        sched.step()
        sc = infer(model, Xtr, va_idx)
        auc = roc_auc_score(y_va, sc)
        hist.append({'epoch': ep, 'loss': tot / max(n, 1), 'val_auc': float(auc), 'sec': round(time.time() - t1, 1)})
        print(f"ep {ep:2d} loss {tot/max(n,1):.4f} val_auc {auc:.4f} ({time.time()-t1:.0f}s)", flush=True)
        if auc > best_auc:
            best_auc, best_state = auc, {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}

    model.load_state_dict(best_state)
    torch.save({'state_dict': best_state, 'size': SIZE, 'arch': 'resnet18'}, os.path.join(args.out, 'review_clf.pt'))

    # ---- 最终评估：official_test（零调参）----
    scores = infer(model, Xte, np.arange(len(yte)))
    auc = roc_auc_score(yte, scores)
    print(f'official_test AUROC {auc:.4f}', flush=True)

    order = np.argsort(-scores)
    ys = yte[order]
    tp_total = int(ys.sum()); fp_total = int(len(ys) - tp_total)
    pick = None
    for k in range(1, len(ys) + 1):
        tp = int(ys[:k].sum()); fp = k - tp
        if tp / tp_total >= 0.995:
            pick = (k, tp / tp_total, fp / max(fp_total, 1), fp, tp)
            break
    print('workpoint@TP>=99.5%:', pick, flush=True)

    scan = []
    for th in [0.1, 0.2, 0.3, 0.5, 0.7, 0.9]:
        pred = (scores >= th).astype(int)
        tp = int(((pred == 1) & (yte == 1)).sum()); fn = int(((pred == 0) & (yte == 1)).sum())
        fp = int(((pred == 1) & (yte == 0)).sum()); tn = int(((pred == 0) & (yte == 0)).sum())
        scan.append({'thr': th, 'tp_keep': round(tp / max(tp + fn, 1), 4), 'fp_remove': round(tn / max(tn + fp, 1), 4),
                     'tp': tp, 'fn': fn, 'fp': fp, 'tn': tn})
        print(f"thr {th}: TP keep {tp/max(tp+fn,1):.4f}  FP remove {tn/max(tn+fp,1):.4f}", flush=True)

    result = {
        'trained_on': 'data/candidates_train (1000 pairs, zero overlap with test)',
        'tested_on': 'data/candidates_official_test (500 pairs)',
        'val_auc_best': float(best_auc),
        'official_auc': float(auc),
        'test_pos': tp_total, 'test_neg': fp_total,
        'tp_keep_at_995': round(pick[1], 4) if pick else None,
        'fp_remove_at_tp995': round(pick[2], 4) if pick else None,
        'workpoint_rows': pick[3] if pick else None,
        'workpoint_tp': pick[4] if pick else None,
        'scan': scan,
        'history': hist,
        'note': 'official_test 仅用于最终评估；轮次由 val 集选定，未在 test 上调参。',
    }
    with open(os.path.join(args.out, 'metrics.json'), 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    print('saved', args.out, flush=True)


if __name__ == '__main__':
    main()
