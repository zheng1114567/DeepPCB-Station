#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v1.2b：POS 分型树派生交互特征实验（只用现有列，不跑检测器）。
候选派生特征（全部无量纲/物理可解释）：
  latBite  = lateral*bite        缺口既宽又深 → 贯穿
  areaW    = ln(area)-2*ln(localW)  缺陷面积/线宽²（无量纲尺寸）
  biteHalf = bite-0.5            咬深过半 → 贯穿签名
  splitLat = split*lateral
  latSq    = lateral^2
每个特征集跑 depth=3/5 的 5 折 CV 与 train，只认 CV 提升。
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


def feats(r):
    area = max(1.0, fnum(r, "area"))
    w = max(1.0, fnum(r, "localW"))
    bite = fnum(r, "bite")
    lateral = fnum(r, "lateral")
    f = {
        "bite": bite,
        "lateral": lateral,
        "fill": fnum(r, "fill"),
        "aspect": fnum(r, "aspect"),
        "lnArea": math.log(area),
        "surround": fnum(r, "surround"),
        "lnLocalW": math.log(w),
        "mean": fnum(r, "mean"),
        "split": fnum(r, "split"),
        "splitN": fnum(r, "splitN"),
        # 派生
        "latBite": lateral * bite,
        "areaW": math.log(area) - 2.0 * math.log(w),
        "biteHalf": bite - 0.5,
        "splitLat": fnum(r, "split") * lateral,
        "latSq": lateral * lateral,
        "biteSq": bite * bite,
    }
    return f


SETS = {
    "P0_base(10)": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN"],
    "P1+latBite": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN", "latBite"],
    "P2+areaW": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN", "areaW"],
    "P3+biteHalf": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN", "biteHalf"],
    "P4+splitLat": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN", "splitLat"],
    "P5+latBite+areaW": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN", "latBite", "areaW"],
    "P6_all_derived": ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN",
                       "latBite", "areaW", "biteHalf", "splitLat", "latSq"],
}


def main():
    rows = load(PATH)
    pos = [(feats(r), NAMES[int(fnum(r, "gtType"))]) for r in rows
           if int(fnum(r, "gtType")) > 0 and fnum(r, "polarity") > 0]
    print(f"POS n={len(pos)} {Counter(y for _, y in pos)}")
    for name, used in SETS.items():
        for d in (3, 5):
            acc, accs = cv(pos, d, 40, used)
            tree = build(pos, 0, d, 40, used)
            tr = sum(1 for s in pos if predict(tree, s[0]) == s[1]) / len(pos)
            print(f"  {name:18s} d={d} cv={acc:.4f} train={tr:.4f} gap={tr-acc:+.4f} folds={[round(a,3) for a in accs]}")
    # 最佳集合打印 depth5 树
    for name in ("P1+latBite", "P2+areaW", "P5+latBite+areaW"):
        used = SETS[name]
        tree = build(pos, 0, 5, 40, used)
        print(f"\n----- {name} depth5 -----")
        render = lambda node, ind=0: None
        def render(node, ind=0):
            pad = "    " * ind
            if node.label is not None:
                print(f"{pad}return {node.label};   // n={node.n} {dict(node.dist)}")
                return
            print(f"{pad}if (f.{node.feat} <= {node.thr:.4f})   // n={node.n} {dict(node.dist)}")
            print(f"{pad}{{")
            render(node.left, ind + 1)
            print(f"{pad}}}")
            print(f"{pad}else")
            print(f"{pad}{{")
            render(node.right, ind + 1)
            print(f"{pad}}}")
        render(tree)


if __name__ == "__main__":
    main()
