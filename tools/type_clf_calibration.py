"""工业口径：DL 分型置信度的校准与三档分流。

回答三个问题：
  1. softmax 能不能当可信度用？—— 温度缩放前后 ECE 对比
  2. 高置信段能自动判掉多少、错多少？—— 覆盖率 / 档内错误率
  3. 中间带有多宽、转人工多少？—— 双阈值分流表 + 板级全对率

纪律：温度 T 只用 train1000 的 val 折（按板留出）拟合；official_test 仅评估，不参与选参。
"""
import csv, json, os
import numpy as np
import torch
import torch.nn as nn
from torchvision import models
from sklearn.model_selection import GroupShuffleSplit

SIZE = 96
CLASSES = ['open', 'short', 'mousebite', 'spur', 'copper', 'pin-hole']
OUT = 'reports/type_clf'


def load_split(root, tag):
    rows = []
    with open(os.path.join(root, 'manifest.csv'), encoding='utf-8') as f:
        for r in csv.DictReader(f):
            if r['label'] == '1':
                rows.append(r)
    X = np.load(os.path.join(OUT.replace('type_clf', 'review_clf'), f'{tag}_x.npy'))
    Y = np.load(os.path.join(OUT.replace('type_clf', 'review_clf'), f'{tag}_y.npy'))
    P = np.load(os.path.join(OUT.replace('type_clf', 'review_clf'), f'{tag}_pair.npy'))
    idx = np.where(Y == 1)[0]
    labels = np.array([CLASSES.index(r['type']) for r in rows], dtype=np.int64)
    return X[idx], labels, P[idx]


@torch.no_grad()
def logits_of(model, X, bs=256):
    out = []
    for i in range(0, len(X), bs):
        b = torch.from_numpy(X[i:i + bs]).permute(0, 3, 1, 2).float().div(255).cuda()
        out.append(model(b).cpu())
    return torch.cat(out)


def ece(probs, labels, bins=15):
    pred = probs.argmax(1)
    conf = probs.max(1)
    correct = (pred == labels).astype(np.float64)
    edges = np.linspace(0, 1, bins + 1)
    e = 0.0
    for i in range(bins):
        m = (conf > edges[i]) & (conf <= edges[i + 1])
        if m.sum() == 0:
            continue
        e += m.mean() * abs(correct[m].mean() - conf[m].mean())
    return float(e)


class Temp(nn.Module):
    def __init__(self):
        super().__init__()
        self.log_t = nn.Parameter(torch.zeros(1))

    def forward(self, x):
        return x / torch.exp(self.log_t)


def fit_temperature(logits, labels):
    t = Temp()
    opt = torch.optim.LBFGS([t.log_t], lr=0.1, max_iter=200)
    ce = nn.CrossEntropyLoss()

    def closure():
        opt.zero_grad()
        loss = ce(t(logits), labels)
        loss.backward()
        return loss

    opt.step(closure)
    return float(torch.exp(t.log_t).item())


def main():
    Xtr, ytr, ptr = load_split('data/candidates_train', 'train')
    Xte, yte, pte = load_split('data/candidates_official_test', 'test')

    ck = torch.load(os.path.join(OUT, 'type_clf.pt'), map_location='cpu', weights_only=False)
    m = models.resnet18(weights=None)
    m.fc = nn.Linear(m.fc.in_features, len(CLASSES))
    m.load_state_dict(ck['state_dict'])
    m.eval().cuda()

    uniq = np.unique(ptr)
    rng = np.random.RandomState(42)
    rng.shuffle(uniq)
    va_pairs = set(uniq[int(len(uniq) * 0.8):].tolist())
    va_idx = np.where(np.isin(ptr, list(va_pairs)))[0]
    print('val positives', len(va_idx), 'test positives', len(yte))

    Lva = logits_of(m, Xtr[va_idx])
    Lte = logits_of(m, Xte)

    T = fit_temperature(Lva, torch.from_numpy(ytr[va_idx]))
    print('temperature =', round(T, 4))

    def probs(L, temp=1.0):
        return torch.softmax(L / temp, dim=1).numpy()

    p0 = probs(Lte, 1.0)
    p1 = probs(Lte, T)
    acc = float((p0.argmax(1) == yte).mean())
    print('test acc', round(acc, 4), '| ECE raw', round(ece(p0, yte), 4), '| ECE calibrated', round(ece(p1, yte), 4))

    conf = p1.max(1)
    pred = p1.argmax(1)
    correct = pred == yte

    # ---- 分档扫描：t_hi 越高越保守 ----
    rows = []
    for hi in [0.50, 0.70, 0.80, 0.90, 0.95, 0.97, 0.99, 0.995]:
        hi_sel = conf >= hi
        mid_sel = (conf >= 0.30) & (conf < hi)
        lo_sel = conf < 0.30
        rows.append({
            't_hi': hi,
            'auto_cover': round(float(hi_sel.mean()), 4),
            'auto_err': round(float(1 - correct[hi_sel].mean()), 4) if hi_sel.sum() else None,
            'mid_share': round(float(mid_sel.mean()), 4),
            'low_share': round(float(lo_sel.mean()), 4),
            'auto_n': int(hi_sel.sum()),
            'auto_err_n': int((~correct[hi_sel]).sum()) if hi_sel.sum() else 0,
        })
        print(f"t_hi={hi}: 自动判定覆盖 {rows[-1]['auto_cover']:.1%} 错误 {rows[-1]['auto_err']}, "
              f"中间带 {rows[-1]['mid_share']:.1%}")

    # ---- 板级：一块板上所有候选都判对 ----
    board_ok = {}
    for pair, c in zip(pte, correct):
        board_ok[pair] = board_ok.get(pair, True) and bool(c)
    board_acc = float(np.mean(list(board_ok.values())))
    print('board-level all-correct rate', round(board_acc, 4), f'({len(board_ok)} boards)')

    # ---- 高置信段的错误集中在哪类 ----
    hi_mask = conf >= 0.90
    per_class_err = {}
    for c, name in enumerate(CLASSES):
        m_c = (yte == c)
        if m_c.sum() == 0:
            continue
        per_class_err[name] = {
            'n': int(m_c.sum()),
            'acc': round(float(correct[m_c].mean()), 4),
            'auto_cov': round(float(hi_mask[m_c].mean()), 4),
        }

    res = {
        'temperature': round(T, 4),
        'test_acc': round(acc, 4),
        'ece_raw': round(ece(p0, yte), 4),
        'ece_calibrated': round(ece(p1, yte), 4),
        'n_test': int(len(yte)),
        'board_all_correct': round(board_acc, 4),
        'n_boards': len(board_ok),
        'tiers': rows,
        'per_class': per_class_err,
        'note': 'T 仅用 train1000 val 折（按板）拟合；official_test 只评估。',
    }
    with open(os.path.join(OUT, 'calibration.json'), 'w', encoding='utf-8') as f:
        json.dump(res, f, ensure_ascii=False, indent=2)
    print('saved', os.path.join(OUT, 'calibration.json'))


if __name__ == '__main__':
    main()
