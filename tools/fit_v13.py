#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v1.2.x split 判据变体验证（train1000 dump，含 splitT/splitC/splitB 列）：

把分型树里的 split 特征分别换成 4 个来源：
  base = Split  线上语义（测试铜、2×线宽窗、显著块 15px²）
  T    = SplitT 模板铜侧（剪掉 blob 后模板铜连通性，排除测试图抗锯齿桥接污染）
  C    = SplitC 测试铜、显著块下限 6px²（捞回被切掉的小残头）
  B    = SplitB 测试铜、1.5×线宽窗（减少邻域无关铜干扰）

每变体重拟合 POS/NEG 两棵 Gini 树（depth 3/4/5 扫描），5 折 CV，
对比：总体 acc、open↔mousebite 混淆对数、train−CV gap（>3pp 报警）。
同时把线上 v1.2.0 硬编码树作为 base 基线复算。
"""
import math
import random
import sys
from collections import Counter

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}

# 线上 v1.2.0 树常量（与 PcbTypeTree.cs 逐位对应）
OPEN_SPLIT = 0.4747
MB_SPLIT = 0.1655
SHORT_SPLIT = 0.1794
OPEN_BITE = 0.6377
THIN_LN_W = 2.7058
DB_SURROUND = 0.8174
PINHOLE_SURROUND = 0.9725
LATERAL_MB = 3.1548
CU_LOCAL_W = 2.83


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


def feats(r, split_src):
    area = max(1.0, fnum(r, "area"))
    w = max(1.0, fnum(r, "localW"))
    return {
        "bite": fnum(r, "bite"),
        "lateral": fnum(r, "lateral"),
        "fill": fnum(r, "fill"),
        "aspect": fnum(r, "aspect"),
        "lnArea": math.log(area),
        "surround": fnum(r, "surround"),
        "lnLocalW": math.log(w),
        "mean": fnum(r, "mean"),
        "edge": fnum(r, "edgeFrac"),
        "severed": fnum(r, "severed"),
        "parts": fnum(r, "parts"),
        "split": fnum(r, split_src),   # ← 变体唯一替换点
        "splitN": fnum(r, "splitN"),
    }


# ---------- 线上 v1.2.0 硬编码树（复算基线） ----------
def ln(v):
    return math.log(max(1.0, v))


def deployed_tree(r, split_src="split"):
    f = {k: fnum(r, k) for k in ("surround", "bite", "localW", "lateral", "parts", "split")}
    sp = fnum(r, split_src)
    if f["surround"] > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if sp > OPEN_SPLIT:
            return "OPEN"
        if f["bite"] > OPEN_BITE:
            if ln(f["localW"]) <= THIN_LN_W:
                return "MOUSEBITE" if f["surround"] <= DB_SURROUND else "PINHOLE"
            return "OPEN"
        if sp <= MB_SPLIT:
            return "MOUSEBITE"
        return "MOUSEBITE" if f["lateral"] <= LATERAL_MB else "OPEN"
    else:
        if f["localW"] <= CU_LOCAL_W:
            return "COPPER"
        if f["parts"] <= 1.5:
            return "SPUR"
        if sp <= SHORT_SPLIT:
            return "SHORT"
        return "SPUR"


# ---------- Gini 树（复用 fit_type_tree 实现） ----------
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


def build(samples, depth, max_depth, min_leaf, used):
    node = Node()
    node.n = len(samples)
    node.dist = Counter(s[1] for s in samples)
    if depth >= max_depth or len(samples) < 2 * min_leaf or len(node.dist) == 1:
        node.label = node.dist.most_common(1)[0][0]
        return node
    base = gini(node.dist, node.n)
    best = (0.0, None, None)
    for f in used:
        uniq = sorted(set(s[0][f] for s in samples))
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
    node.left = build([s for s in samples if s[0][node.feat] <= node.thr], depth + 1, max_depth, min_leaf, used)
    node.right = build([s for s in samples if s[0][node.feat] > node.thr], depth + 1, max_depth, min_leaf, used)
    return node


def predict(node, x):
    while node.label is None:
        node = node.left if x[node.feat] <= node.thr else node.right
    return node.label


def render(node, indent=0):
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


def oof_confusion(rows, split_src, pos_used, neg_used, max_depth=5, min_leaf=40, folds=5, seed=7):
    """out-of-fold 预测 → 总体 acc + 混淆对统计（只看 open↔mousebite / short↔spur）。"""
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    pos = [(feats(r, split_src), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") > 0]
    neg = [(feats(r, split_src), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") < 0]
    oof = {}  # id(row) -> pred
    for data in (pos, neg):
        idx = list(range(len(data)))
        random.Random(seed).shuffle(idx)
        for k in range(folds):
            te = set(idx[k::folds])
            tr = [data[i] for i in idx if i not in te]
            t = build(tr, 0, max_depth, min_leaf, pos_used if data is pos else neg_used)
            for i in te:
                oof[id(data[i])] = predict(t, data[i][0])
    ok = 0
    pairs = Counter()
    per = Counter()
    tot = Counter()
    for r in matched:
        gt = NAMES[int(fnum(r, "gtType"))]
        key = id((feats(r, split_src), gt))
        pr = oof.get(key, "?")
        tot[gt] += 1
        ok += gt == pr
        if gt != pr:
            pairs[(gt, pr)] += 1
    return ok / max(1, len(matched)), pairs, tot


VARIANTS = [
    ("base(Split)", "split"),
    ("T(SplitT模板侧)", "splitT"),
    ("C(SplitC 6px²)", "splitC"),
    ("B(SplitB 1.5xW)", "splitB"),
]

PU = ["split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN"]
NU = ["parts", "split", "bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "splitN"]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else None
    if path is None:
        print("usage: python tools/fit_v13.py <features.tsv>")
        return
    rows = load(path)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={path}")
    print(f"rows={len(rows)} matched={len(matched)}")
    pos_n = sum(1 for r in matched if fnum(r, "polarity") > 0)
    print(f"pos={pos_n} neg={len(matched)-pos_n}")
    print("pos dist:", Counter(NAMES[int(fnum(r, 'gtType'))] for r in matched if fnum(r, "polarity") > 0))
    print("neg dist:", Counter(NAMES[int(fnum(r, 'gtType'))] for r in matched if fnum(r, "polarity") < 0))

    # 列完整性检查
    missing = [c for c in ("split", "splitT", "splitC", "splitB") if c not in rows[0]]
    if missing:
        print(f"!! 缺少列: {missing}（dump 未含 v1.2 变体？）")
        return
    # 变体差异规模
    for name, src in VARIANTS:
        d = [abs(fnum(r, "split") - fnum(r, src)) for r in matched]
        big = sum(1 for x in d if x > 0.3)
        print(f"  |split-{src}| mean={sum(d)/len(d):.3f} >0.3 的比例={100.0*big/len(d):.1f}%")

    # 0. 线上树基线
    print("\n===== 0. 线上 v1.2.0 硬编码树（同 dump 复算） =====")
    for name, src in VARIANTS:
        ok = sum(1 for r in matched if deployed_tree(r, src) == NAMES[int(fnum(r, "gtType"))])
        print(f"  {name:22s} acc={ok/len(matched):.4f}")

    # 1. 四变体 5 折 CV（depth 扫描）
    for name, src in VARIANTS:
        matched2 = [r for r in matched]
        pos = [(feats(r, src), NAMES[int(fnum(r, "gtType"))]) for r in matched2 if fnum(r, "polarity") > 0]
        neg = [(feats(r, src), NAMES[int(fnum(r, "gtType"))]) for r in matched2 if fnum(r, "polarity") < 0]
        print(f"\n===== {name} =====")
        for d in (3, 4, 5):
            accs_p, accs_n = [], []
            for data, used in ((pos, PU), (neg, NU)):
                idx = list(range(len(data)))
                random.Random(7).shuffle(idx)
                for k in range(5):
                    te = set(idx[k::5])
                    tr = [data[i] for i in idx if i not in te]
                    t = build(tr, 0, d, 40, used)
                    a = sum(1 for s in [data[i] for i in te] if predict(t, s[0]) == s[1]) / max(1, len(te))
                    (accs_p if data is pos else accs_n).append(a)
            print(f"  depth={d} posCV={sum(accs_p)/5:.4f} negCV={sum(accs_n)/5:.4f} folds_p={[round(a,3) for a in accs_p]} folds_n={[round(a,3) for a in accs_n]}")

    # 2. 选定 depth 的 out-of-fold 总体 + 混淆对
    print("\n===== out-of-fold 总体与混淆对（depth=5, min_leaf=40） =====")
    for name, src in VARIANTS:
        acc, pairs, tot = oof_confusion(rows, src, PU, NU)
        omb = pairs.get(("OPEN", "MOUSEBITE"), 0) + pairs.get(("MOUSEBITE", "OPEN"), 0)
        ss = pairs.get(("SHORT", "SPUR"), 0) + pairs.get(("SPUR", "SHORT"), 0)
        print(f"  {name:22s} acc={acc:.4f} open<->mb={omb} short<->spur={ss} all={dict(pairs.most_common(8))}")

    # 3. 每变体全量拟合树渲染（深度5），供阈值摘取
    for name, src in VARIANTS:
        pos = [(feats(r, src), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") > 0]
        neg = [(feats(r, src), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") < 0]
        print(f"\n----- {name} POS TREE -----")
        render(build(pos, 0, 5, 40, PU))
        print(f"----- {name} NEG TREE -----")
        render(build(neg, 0, 5, 40, NU))


if __name__ == "__main__":
    main()
