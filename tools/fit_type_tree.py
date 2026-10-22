#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
用 --feature-dump 产出的 TSV，拟合：
  1) 六类分型树（缺铜/多铜两棵，depth<=5，Gini）→ 打印可直接粘贴成 C# if-else 的规则；
  2) 置信度 logistic 权重 → 打印 PcbConfidence 的常数。

只用 data/full 中「非 official_test」的 1000 对；official_test 500 对只做验收。
训练前会做 5 折交叉验证，train-CV 与留出折的准确率差 > 3pp 即提示过拟合。

用法: python tools/fit_type_tree.py reports/features_train/features_XXXX.tsv
"""
import sys
import math
import random
from collections import Counter

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}


def load(path):
    rows = []
    hdr = None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            p = line.rstrip("\n").split("\t")
            if hdr is None:
                hdr = p
                continue
            if len(p) != len(hdr):
                continue
            rows.append(dict(zip(hdr, p)))
    return rows


def fnum(r, k):
    try:
        return float(r[k])
    except (KeyError, ValueError):
        return 0.0


def feats(r):
    """特征白名单：全部无量纲或对数，避免绝对像素量纲。"""
    area = max(1.0, fnum(r, "area"))
    w = max(1.0, fnum(r, "localW"))
    return {
        "bite": fnum(r, "bite"),                 # 咬深/线宽
        "lateral": fnum(r, "lateral"),           # 短边/线宽
        "fill": fnum(r, "fill"),
        "aspect": fnum(r, "aspect"),
        "lnArea": math.log(area),
        "surround": fnum(r, "surround"),
        "lnLocalW": math.log(w),
        "mean": fnum(r, "mean"),
        "edge": fnum(r, "edgeFrac"),
        "severed": fnum(r, "severed"),
        "parts": fnum(r, "parts"),
        "split": fnum(r, "split"),          # 实测铜被切断度（open/mousebite 的物理判据）
        "splitN": fnum(r, "splitN"),
    }


class Node:
    __slots__ = ("feat", "thr", "left", "right", "label", "n", "dist")

    def __init__(self):
        self.feat = None
        self.thr = None
        self.left = None
        self.right = None
        self.label = None
        self.n = 0
        self.dist = None


def gini(counter, n):
    if n == 0:
        return 0.0
    s = 0.0
    for v in counter.values():
        p = v / n
        s += p * p
    return 1.0 - s


def build(samples, depth, max_depth, min_leaf, feats_used):
    node = Node()
    node.n = len(samples)
    node.dist = Counter(s[1] for s in samples)
    if depth >= max_depth or len(samples) < 2 * min_leaf or len(node.dist) == 1:
        node.label = node.dist.most_common(1)[0][0]
        return node

    base = gini(node.dist, node.n)
    best = (0.0, None, None)
    for f in feats_used:
        vals = sorted(s[0][f] for s in samples)
        uniq = sorted(set(vals))
        if len(uniq) < 2:
            continue
        # 候选切点：分位点，避免逐点扫描
        cand = []
        step = max(1, len(uniq) // 24)
        for i in range(step, len(uniq), step):
            cand.append((uniq[i - 1] + uniq[i]) / 2.0)
        for t in cand:
            lc, rc = Counter(), Counter()
            nl = nr = 0
            for s in samples:
                if s[0][f] <= t:
                    lc[s[1]] += 1
                    nl += 1
                else:
                    rc[s[1]] += 1
                    nr += 1
            if nl < min_leaf or nr < min_leaf:
                continue
            gain = base - (nl / node.n) * gini(lc, nl) - (nr / node.n) * gini(rc, nr)
            if gain > best[0]:
                best = (gain, f, t)
    if best[1] is None or best[0] <= 1e-9:
        node.label = node.dist.most_common(1)[0][0]
        return node

    node.feat, node.thr = best[1], best[2]
    left = [s for s in samples if s[0][node.feat] <= node.thr]
    right = [s for s in samples if s[0][node.feat] > node.thr]
    node.left = build(left, depth + 1, max_depth, min_leaf, feats_used)
    node.right = build(right, depth + 1, max_depth, min_leaf, feats_used)
    return node


def predict(node, x):
    while node.label is None:
        node = node.left if x[node.feat] <= node.thr else node.right
    return node.label


def render(node, indent=0, name="n"):
    pad = "    " * indent
    if node.label is not None:
        print(f"{pad}return {node.label};   // n={node.n} {dict(node.dist)}")
        return
    print(f"{pad}if (f.{node.feat} <= {node.thr:.4f})   // n={node.n} {dict(node.dist)}")
    print(f"{pad}{{")
    render(node.left, indent + 1)
    print(f"{pad}}}")
    print(f"{pad}else")
    print(f"{pad}{{")
    render(node.right, indent + 1)
    print(f"{pad}}}")


def cv(samples, max_depth, min_leaf, feats_used, folds=5, seed=7):
    rnd = random.Random(seed)
    idx = list(range(len(samples)))
    rnd.shuffle(idx)
    accs = []
    for k in range(folds):
        test = set(idx[k::folds])
        tr = [samples[i] for i in idx if i not in test]
        te = [samples[i] for i in idx if i in test]
        t = build(tr, 0, max_depth, min_leaf, feats_used)
        acc = sum(1 for s in te if predict(t, s[0]) == s[1]) / max(1, len(te))
        accs.append(acc)
    return sum(accs) / len(accs), accs


def fit_logistic(samples, iters=1200, lr=0.35, l2=1e-3):
    """samples: [(feature dict, y)]，特征用固定顺序；返回 (w, b)。"""
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


def conf_features(r):
    """置信度特征：与 C# PcbConfidence 一一对应，必须同序同名。"""
    area = max(1.0, fnum(r, "area"))
    mean = max(1.0, fnum(r, "mean"))
    return {
        "lnArea": math.log(area),
        "lnMean": math.log(mean),
        "edge": fnum(r, "edgeFrac"),
        "fill": fnum(r, "fill"),
        "lnAspect": math.log(max(1.0, fnum(r, "aspect"))),
        "surround": fnum(r, "surround"),
        "bite": fnum(r, "bite"),
    }


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "reports/features_train/features.tsv"
    rows = load(path)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"rows={len(rows)} matched={len(matched)} ({100.0*len(matched)/max(1,len(rows)):.1f}%)")

    pos = [(feats(r), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") > 0]
    neg = [(feats(r), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") < 0]
    print("pos samples", len(pos), Counter(y for _, y in pos))
    print("neg samples", len(neg), Counter(y for _, y in neg))

    pu = ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN"]
    nu = ["parts", "split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN"]

    for tag, data, used in (("POS", pos, pu), ("NEG", neg, nu)):
        print(f"\n===== {tag} 5-fold CV =====")
        for d in (3, 4, 5):
            acc, accs = cv(data, d, 40, used)
            print(f"  depth={d} acc={acc:.3f} folds={[round(a,3) for a in accs]}")
        acc, _ = cv(data, 5, 40, used)
        tree = build(data, 0, 5, 40, used)
        train_acc = sum(1 for s in data if predict(tree, s[0]) == s[1]) / len(data)
        print(f"  最终 depth=5 train={train_acc:.3f} cv={acc:.3f} gap={train_acc-acc:.3f}")
        print(f"----- {tag} TREE -----")
        render(tree)

    allrows = [(conf_features(r), 1 if int(fnum(r, "gtType")) > 0 else 0) for r in rows]
    w, b, keys = fit_logistic(allrows)
    print("\n===== CONFIDENCE logistic =====")
    print("features:", keys)
    print(f"b = {b:.4f}")
    for k in keys:
        print(f"{k} = {w[k]:.4f}")
    # 训练集 AUC 粗算
    ps = [(b + sum(w[k] * x[k] for k in keys), y) for x, y in allrows]
    pos_s = [p for p, y in ps if y == 1]
    neg_s = [p for p, y in ps if y == 0]
    if pos_s and neg_s:
        import statistics as st
        win = 0
        for _ in range(20000):
            a = pos_s[random.randrange(len(pos_s))]
            c = neg_s[random.randrange(len(neg_s))]
            win += 1 if a > c else 0.5 if a == c else 0
        print(f"AUC≈{win/20000:.3f}")


if __name__ == "__main__":
    main()
