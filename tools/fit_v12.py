#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v1.2 双杠杆离线验证：
A. 坍缩拟合树（POS/NEG）在 train1000 dump 上的精度复核；
B. 置信度 logistic 扩特征集 5 折 CV AUC 对比（选出可部署的特征子集）。
"""
import math
import random
from collections import Counter

PATH = "reports/features_train2/features_20261021_013052.tsv"
NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}


def load(path):
    rows, hdr = [], None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            p = line.rstrip("\n").split("\t")
            if hdr is None:
                hdr = p
                continue
            if len(p) == len(hdr):
                rows.append(dict(zip(hdr, p)))
    return rows


def fnum(r, k):
    try:
        return float(r[k])
    except (KeyError, ValueError):
        return 0.0


def ln(v):
    return math.log(max(1.0, v))


# ---------- A. 坍缩拟合树 ----------
NUMKEYS = ("x", "y", "w", "h", "area", "aspect", "fill", "peak", "mean", "localW", "depth",
           "bite", "lateral", "severed", "nets", "parts", "surround", "away", "polarity",
           "edgeFrac", "oppRatio", "matchIou", "gtType", "conf", "split", "splitN")


def flt(r):
    return {k: float(r[k]) for k in NUMKEYS}


def tree_pos(f0):
    f = flt(f0)
    if f["surround"] > 0.9725:
        return "PINHOLE"
    if f["split"] > 0.4747:
        return "OPEN"
    if f["bite"] > 0.6377:
        if ln(f["localW"]) <= 2.7058:
            return "MOUSEBITE" if f["surround"] <= 0.8174 else "PINHOLE"
        return "OPEN"
    if f["split"] <= 0.1655:
        return "MOUSEBITE"
    return "MOUSEBITE" if f["lateral"] <= 3.1548 else "OPEN"


def tree_neg(f0):
    f = flt(f0)
    if f["localW"] <= 2.83:
        return "COPPER"
    if f["parts"] <= 1.5:
        return "SPUR"
    if f["split"] <= 0.1794:
        return "SHORT"
    return "SPUR"


def tree(f0):
    f = flt(f0)
    return tree_pos(f) if f["polarity"] > 0 else tree_neg(f)


def eval_tree(rows):
    m = [r for r in rows if int(fnum(r, "gtType")) > 0]
    conf = Counter()
    ok = 0
    for r in m:
        gt = NAMES[int(fnum(r, "gtType"))]
        pr = tree(r)
        conf[(gt, pr)] += 1
        ok += gt == pr
    for tag, flt in (("POS", lambda r: fnum(r, "polarity") > 0), ("NEG", lambda r: fnum(r, "polarity") < 0)):
        sub = [r for r in m if flt(r)]
        c = sum(1 for r in sub if tree(r) == NAMES[int(fnum(r, "gtType"))])
        print(f"  {tag} acc={c/len(sub):.4f} n={len(sub)}")
    print(f"  ALL acc={ok/len(m):.4f} ({ok}/{len(m)})")
    errs = Counter((gt, pr) for (gt, pr), n in conf.items() if gt != pr for _ in range(n))
    print("  residual:", dict(errs.most_common(10)))
    return ok / len(m)


# ---------- B. 置信度 logistic ----------
def fit_logistic(samples, iters=1200, lr=0.35, l2=1e-3):
    keys = list(samples[0][0].keys())
    w = {k: 0.0 for k in keys}
    b = 0.0
    n = len(samples)
    for _ in range(iters):
        gw = {k: 0.0 for k in keys}
        gb = 0.0
        for x, y in samples:
            z = b + sum(w[k] * x[k] for k in keys)
            p = 1.0 / (1.0 + math.exp(-max(-30.0, min(30.0, z))))
            d = p - y
            gb += d
            for k in keys:
                gw[k] += d * x[k]
        b -= lr * gb / n
        for k in keys:
            w[k] -= lr * (gw[k] / n + l2 * w[k])
    return w, b, keys


def auc(scores, ys):
    ps = [s for s, y in zip(scores, ys) if y == 1]
    ns = [s for s, y in zip(scores, ys) if y == 0]
    if not ps or not ns:
        return float("nan")
    win = 0
    rnd = random.Random(7)
    for _ in range(20000):
        a = ps[rnd.randrange(len(ps))]
        c = ns[rnd.randrange(len(ns))]
        win += 1 if a > c else 0.5 if a == c else 0
    return win / 20000


def conf_sets(r):
    r = flt(r)
    base = {
        "lnArea": ln(r["area"]),
        "lnMean": ln(r["mean"]),
        "edge": r["edgeFrac"],
        "fill": r["fill"],
        "lnAspect": ln(r["aspect"]),
        "surround": r["surround"],
        "bite": r["bite"],
    }
    ext = {
        "lnLocalW": ln(r["localW"]),
        "lnPeak": ln(r["peak"]),
        "parts": r["parts"],
        "split": r["split"],
        "away": r["away"],
        "severed": r["severed"],
        "nets": r["nets"],
        "lnDepth": ln(r["depth"]),
        "splitN": r["splitN"],
        "lateral": r["lateral"],
    }
    return base, ext


SETS = {
    "S0_current(7)": ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite"],
    "S1+lnLocalW": ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite", "lnLocalW"],
    "S2+lnLocalW+lnPeak": ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite", "lnLocalW", "lnPeak"],
    "S3+topo(parts,split,away)": ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite", "lnLocalW", "parts", "split", "away"],
    "S4_all_ext": ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite", "lnLocalW", "lnPeak",
                   "parts", "split", "away", "severed", "nets", "lnDepth", "splitN", "lateral"],
    "S5_no-edge": ["lnArea", "lnMean", "fill", "lnAspect", "surround", "bite", "lnLocalW", "lnPeak", "parts", "split", "away"],
}


def main():
    rows = load(PATH)
    print("===== A. 坍缩拟合树（train1000 dump） =====")
    eval_tree(rows)

    print("\n===== B. 置信度特征集 5 折 CV AUC =====")
    samples = []
    for r in rows:
        b, e = conf_sets(r)
        x = dict(b)
        x.update(e)
        samples.append((x, 1 if int(fnum(r, "gtType")) > 0 else 0))
    n = len(samples)
    idx = list(range(n))
    random.Random(7).shuffle(idx)
    for name, keys in SETS.items():
        accs = []
        for k in range(5):
            te = set(idx[k::5])
            tr = [samples[i] for i in idx if i not in te]
            ts = [samples[i] for i in idx if i in te]
            sub_tr = [({kk: x[kk] for kk in keys}, y) for x, y in tr]
            w, b0, _ = fit_logistic(sub_tr)
            sc = [b0 + sum(w[kk] * x[kk] for kk in keys) for x, y in ts]
            accs.append(auc(sc, [y for _, y in ts]))
        # 全量权重（部署用）
        sub_all = [({kk: x[kk] for kk in keys}, y) for x, y in samples]
        w, b0, _ = fit_logistic(sub_all)
        sc_all = [b0 + sum(w[kk] * x[kk] for kk in keys) for x, y in samples]
        print(f"  {name:26s} cvAUC={sum(accs)/5:.4f} folds={[round(a,3) for a in accs]} trainAUC={auc(sc_all,[y for _,y in samples]):.4f}")
        print(f"      b={b0:.4f} " + " ".join(f"{k}={w[k]:.4f}" for k in keys))


if __name__ == "__main__":
    main()
