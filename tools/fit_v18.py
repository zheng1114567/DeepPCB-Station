#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fit_v18：全特征池 vs 部署特征集（splitB 判据下）——不花新 dump 的最后检验。

问题：树只用了 7 个特征（surround/splitB/bite/localW/lateral/parts + polarity），
dump 的 31 列里还有 aspect/fill/mean/peak/depth/nets/severed/edgeFrac/oppRatio/area/w/h
从未进树。若全特征池的 5 折 CV 显著优于部署树（同折测试集错误数对比），
再决定实现哪个新 Halcon 特征；否则判定「特征已耗尽」，分型树到此为止。

对照组：部署树（splitB，阈值不变）在各折测试集上的错误数 [33,39,35,44,24]（总 175）。
泄漏列（matchIou/gtType/conf）与 dump 专用列（splitT/splitC）一律排除。
"""
import math
import random
import sys
import glob
import os
from collections import Counter

import numpy as np

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}

OPEN_SPLIT, MB_SPLIT, SHORT_SPLIT = 0.4747, 0.1655, 0.1794
OPEN_BITE, THIN_LN_W, DB_SURROUND = 0.6377, 2.7058, 0.8174
PINHOLE_SURROUND, LATERAL_MB, CU_LOCAL_W = 0.9725, 3.1548, 2.83


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


# 部署树（splitB）
def tree_deployed(r):
    if fnum(r, "surround") > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if fnum(r, "splitB") > OPEN_SPLIT:
            return "OPEN"
        if fnum(r, "bite") > OPEN_BITE:
            if ln(fnum(r, "localW")) <= THIN_LN_W:
                return "MOUSEBITE" if fnum(r, "surround") <= DB_SURROUND else "PINHOLE"
            return "OPEN"
        if fnum(r, "splitB") <= MB_SPLIT:
            return "MOUSEBITE"
        return "MOUSEBITE" if fnum(r, "lateral") <= LATERAL_MB else "OPEN"
    if fnum(r, "localW") <= CU_LOCAL_W:
        return "COPPER"
    if fnum(r, "parts") <= 1.5:
        return "SPUR"
    if fnum(r, "splitB") <= SHORT_SPLIT:
        return "SHORT"
    return "SPUR"


# 全特征池（推理时可得的物理量；排除泄漏列 matchIou/gtType/conf、dump 专用 splitT/splitC、坐标 x/y）
POOL = ["bite", "lateral", "fill", "aspect", "lnArea", "surround", "lnLocalW", "mean", "edge",
        "severed", "parts", "splitB", "splitN", "lnPeak", "lnDepth", "nets", "away", "oppRatio", "lnW", "lnH"]


def feats_full(r):
    return {
        "bite": fnum(r, "bite"),
        "lateral": fnum(r, "lateral"),
        "fill": fnum(r, "fill"),
        "aspect": fnum(r, "aspect"),
        "lnArea": ln(fnum(r, "area")),
        "surround": fnum(r, "surround"),
        "lnLocalW": ln(fnum(r, "localW")),
        "mean": fnum(r, "mean"),
        "edge": fnum(r, "edgeFrac"),
        "severed": fnum(r, "severed"),
        "parts": fnum(r, "parts"),
        "splitB": fnum(r, "splitB"),
        "splitN": fnum(r, "splitN"),
        "lnPeak": ln(fnum(r, "peak")),
        "lnDepth": ln(fnum(r, "depth")),
        "nets": fnum(r, "nets"),
        "away": fnum(r, "away"),
        "oppRatio": max(0.0, fnum(r, "oppRatio")),
        "lnW": ln(fnum(r, "w")),
        "lnH": ln(fnum(r, "h")),
    }


class Node:
    __slots__ = ("feat", "thr", "left", "right", "label", "n", "dist")

    def __init__(self):
        self.feat = self.thr = self.left = self.right = self.label = None
        self.n = 0
        self.dist = None


def gini(c, n):
    if n == 0:
        return 0.0
    return 1.0 - sum((v / n) ** 2 for v in c.values())


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
        step = max(1, len(uniq) // 24)
        for i in range(step, len(uniq), step):
            t = (uniq[i - 1] + uniq[i]) / 2.0
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
    if best[1] is None:
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


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("reports/features_train3/features_*.tsv"))[-1]
    rows = load(path)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={os.path.basename(path)} matched={len(matched)}")
    idx_all = list(range(len(rows)))
    random.Random(7).shuffle(idx_all)
    folds = [set(idx_all[k::5]) for k in range(5)]

    # 对照组：部署树各折错误
    dep_folds = []
    for te in folds:
        e = sum(1 for i in te if int(fnum(rows[i], "gtType")) > 0 and NAMES[int(fnum(rows[i], "gtType"))] != tree_deployed(rows[i]))
        dep_folds.append(e)
    print(f"  部署树(splitB) 各折错误={dep_folds} 总={sum(dep_folds)}")

    pos = [(feats_full(r), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") > 0]
    neg = [(feats_full(r), NAMES[int(fnum(r, "gtType"))]) for r in matched if fnum(r, "polarity") < 0]
    print(f"  pos={len(pos)} neg={len(neg)}")

    print("\n===== 全特征池重拟合（5 折 CV，同折测试集） =====")
    for depth in (4, 5, 6):
        for min_leaf in (40, 80):
            fold_errs = []
            for te in folds:
                te_set = te
                pos_te = [i for i in range(len(rows)) if i in te_set and int(fnum(rows[i], "gtType")) > 0 and fnum(rows[i], "polarity") > 0]
                neg_te = [i for i in range(len(rows)) if i in te_set and int(fnum(rows[i], "gtType")) > 0 and fnum(rows[i], "polarity") < 0]
                pos_tr = [i for i in range(len(rows)) if i not in te_set and int(fnum(rows[i], "gtType")) > 0 and fnum(rows[i], "polarity") > 0]
                neg_tr = [i for i in range(len(rows)) if i not in te_set and int(fnum(rows[i], "gtType")) > 0 and fnum(rows[i], "polarity") < 0]
                tp = build([(feats_full(rows[i]), NAMES[int(fnum(rows[i], "gtType"))]) for i in pos_tr], 0, depth, min_leaf, POOL)
                tn = build([(feats_full(rows[i]), NAMES[int(fnum(rows[i], "gtType"))]) for i in neg_tr], 0, depth, min_leaf, POOL)
                e = 0
                for i in pos_te:
                    if predict(tp, feats_full(rows[i])) != NAMES[int(fnum(rows[i], "gtType"))]:
                        e += 1
                for i in neg_te:
                    if predict(tn, feats_full(rows[i])) != NAMES[int(fnum(rows[i], "gtType"))]:
                        e += 1
                fold_errs.append(e)
            print(f"  depth={depth} min_leaf={min_leaf}: 各折错误={fold_errs} 总={sum(fold_errs)}（部署树总=175）")

    # 全量拟合看选了哪些特征
    print("\n===== 全量 depth=5 min_leaf=40：被选中的特征 =====")
    tp = build(pos, 0, 5, 40, POOL)
    tn = build(neg, 0, 5, 40, POOL)
    used = set()

    def walk(nd):
        if nd.label is not None:
            return
        used.add(nd.feat)
        walk(nd.left)
        walk(nd.right)
    walk(tp)
    walk(tn)
    print(f"  POS/NEG 选中特征: {sorted(used)}")
    tr_err = sum(1 for x, y in pos if predict(tp, x) != y) + sum(1 for x, y in neg if predict(tn, x) != y)
    print(f"  全量 train 错误={tr_err}（部署树全量=175）")


if __name__ == "__main__":
    main()
