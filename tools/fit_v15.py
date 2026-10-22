#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fit_v15：部署前影响仿真（不花官方评测配额，先在 train1000 dump 上估算）。

1) pooled 检出 mAP11：S4 现状 / BIN(53) 重新拟合 / 排序神谕。
   FN_detection=227（v120_train1000.log：recall 96.7%、TP 6646 → GT≈6873）。
2) 严格分型 mAP11：线上 v1.2.0 树 vs B 折叠树（SplitB 判据），S4 打分不变。
3) B 折叠树混淆矩阵（train1000 全量）。
4) 若 BIN 仿真增益达标，打印 BIN 全套权重供 C# 移植。
"""
import math
import random
import sys
import glob
import os
from collections import Counter

import numpy as np

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}
FN_LOG = {"OPEN": 84, "SHORT": 59, "MOUSEBITE": 186, "SPUR": 53, "COPPER": 31, "PINHOLE": 22}
FN_DET = 227
GT_TOTAL = 6873

# ---- 复用 fit_v14 的机器 ----
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fit_v14 import (load, fnum, s4_score, deployed_tree, base_feats, fit_logistic, auc, ap11,
                     design_matrix, design_binned, CONT, S4_KEYS, CLS_DUMMIES, CLS_INTERACT)


# ---- B 折叠树（SplitB 判据，depth5 拟合后折叠；阈值取自 fit_v13 渲染） ----
def tree_b(r):
    """POS 侧 SplitB 判据 + NEG 侧 SplitB 判据。"""
    surround = fnum(r, "surround")
    splitB = fnum(r, "splitB")
    if surround > 0.9725:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if splitB > 0.2972:
            if splitB > 0.5664:
                return "OPEN"
            if fnum(r, "lateral") <= 1.8849:
                return "MOUSEBITE"
            return "OPEN"
        if fnum(r, "bite") > 0.6485:
            if math.log(max(1.0, fnum(r, "area"))) > 4.9558:
                return "OPEN"
            return "MOUSEBITE"
        return "MOUSEBITE"
    if fnum(r, "localW") <= 1.0397:
        if fnum(r, "fill") <= 0.0489:
            return "COPPER"
        if fnum(r, "aspect") <= 2.4306:
            return "COPPER"
        return "COPPER"
    if fnum(r, "parts") <= 1.5:
        if fnum(r, "surround") <= 0.0729:
            return "SPUR"
        if fnum(r, "lateral") <= 9.4167:
            if fnum(r, "aspect") <= 2.7750:
                return "SPUR"
            return "SPUR"
        return "SPUR"
    if splitB <= 0.0278:
        if fnum(r, "localW") <= 1.5890:
            return "SHORT"
        if fnum(r, "surround") <= 0.1487:
            return "SHORT"
        return "SHORT"
    return "SPUR"


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("reports/features_train3/features_*.tsv"))[-1]
    rows = load(path)
    n = len(rows)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={os.path.basename(path)} rows={n} matched={len(matched)}")

    y = np.array([1.0 if int(fnum(r, "gtType")) > 0 else 0.0 for r in rows])
    s4 = np.array([s4_score(r) for r in rows])
    gt = np.array([NAMES.get(int(fnum(r, "gtType")), "") for r in rows])

    # ---------- 1) pooled 检出 AP11 ----------
    print("\n===== 1) pooled 检出 mAP11 仿真（train1000 结构） =====")
    cur = ap11(s4, y, GT_TOTAL)
    order = np.argsort(-y, kind="stable")
    orc = ap11(y[order], y[order], GT_TOTAL)
    print(f"  S4 现状 AP11={cur*100:.1f}%（日志 90.6%） 排序神谕={orc*100:.1f}%")
    # BIN(53) 全量拟合
    bf_all = [base_feats(r) for r in rows]
    edges_all = {f: np.quantile([x[f] for x in bf_all], [0.2, 0.4, 0.6, 0.8]).tolist() for f in CONT}
    Xb = design_binned(bf_all, edges_all)
    wb = fit_logistic(Xb, y)
    scb = Xb @ wb
    print(f"  BIN(53) 全量 AP11={ap11(scb, y, GT_TOTAL)*100:.1f}% trainAUC={auc(y, scb):.4f}")
    # 5 折 CV 的 out-of-fold AP11（更诚实）
    idx = list(range(n))
    random.Random(7).shuffle(idx)
    oof = np.zeros(n)
    for k in range(5):
        te = set(idx[k::5])
        tr_i = [i for i in idx if i not in te]
        te_i = [i for i in idx if i in te]
        bf_tr = [bf_all[i] for i in tr_i]
        edges = {f: np.quantile([x[f] for x in bf_tr], [0.2, 0.4, 0.6, 0.8]).tolist() for f in CONT}
        w = fit_logistic(design_binned(bf_tr, edges), y[tr_i])
        oof[te_i] = design_binned([bf_all[i] for i in te_i], edges) @ w
    print(f"  BIN(53) OOF AP11={ap11(oof, y, GT_TOTAL)*100:.1f}% oofAUC={auc(y, oof):.4f}")
    # S4 的 OOF 不可能（固定权重），用同折 S4 做对照
    s4_oof_aucs, bin_oof_aucs = [], []
    for k in range(5):
        te = set(idx[k::5])
        te_i = [i for i in idx if i in te]
        s4_oof_aucs.append(auc(y[te_i], s4[te_i]))
        bin_oof_aucs.append(auc(y[te_i], oof[te_i]))
    print(f"  分折 AUC  S4={[round(a,3) for a in s4_oof_aucs]}")
    print(f"  分折 AUC BIN={[round(a,3) for a in bin_oof_aucs]}")

    # ---------- 2) 严格分型 mAP11：树对比 ----------
    print("\n===== 2) 严格 mAP11：v1.2.0 树 vs B 折叠树（S4 打分不变） =====")
    pred_a = np.array([deployed_tree(r) for r in rows])
    pred_b = np.array([tree_b(r) for r in rows])
    print(f"  {'类':10s} {'base':>7s} {'B折叠':>7s} {'Δ':>7s}")
    aps_a, aps_b = [], []
    for c in NAMES.values():
        la = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred_a)])
        lb = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred_b)])
        pa, pb = pred_a == c, pred_b == c
        a = ap11(s4[pa], la[pa], int(la.sum()) + FN_LOG[c])
        b = ap11(s4[pb], lb[pb], int(lb.sum()) + FN_LOG[c])
        aps_a.append(a)
        aps_b.append(b)
        print(f"  {c:10s} {a*100:6.1f}% {b*100:6.1f}% {(b-a)*100:+6.1f}pp")
    print(f"  {'mAP11':10s} {100*sum(aps_a)/6:6.1f}% {100*sum(aps_b)/6:6.1f}% {100*(sum(aps_b)-sum(aps_a))/6:+6.1f}pp")

    # ---------- 3) B 折叠树混淆矩阵 ----------
    print("\n===== 3) B 折叠树混淆矩阵（train1000 全量，GT 行 × Pred 列） =====")
    conf = Counter()
    for r in matched:
        conf[(NAMES[int(fnum(r, "gtType"))], tree_b(r))] += 1
    hdr = ["open", "short", "mousebite", "spur", "copper", "pinhole"]
    print("  GT\\Pred  " + " ".join(f"{h:>10s}" for h in hdr))
    for g in NAMES.values():
        print(f"  {g:8s} " + " ".join(f"{conf.get((g, p.upper()), 0):>10d}" for p in hdr))
    errs = sum(v for (g, p), v in conf.items() if g != p)
    print(f"  分型错误总数={errs}（线上 v1.2.0 同 dump=208）")

    # ---------- 4) BIN 权重（供移植） ----------
    print("\n===== 4) BIN(53) 权重（全量拟合） =====")
    names = ["bias"] + [f"{f}#{v}" for f in CONT for v in range(5)] + ["parts", "away", "severed", "nets", "splitN"]
    for nm, w in zip(names, wb):
        print(f"  {nm:16s} {w:+.4f}")
    print("  分位箱边界（0.2/0.4/0.6/0.8）：")
    for f in CONT:
        print(f"  {f:10s} {[round(e, 4) for e in edges_all[f]]}")


if __name__ == "__main__":
    main()
