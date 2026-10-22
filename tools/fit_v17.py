#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fit_v17：分型边际特征对置信度（排序）的增益——严格 mAP 的残余杠杆。

机制：严格口径下，错分的 TP 会变成预测类的 FP。mousebite 类内 AUC 仅 0.44
（S4 把 mb 的 FP 排在 TP 前面），类内排序神谕 +4.7pp——剩余最大单项。
候选越贴近分型树的翻转边界，越可能是错分 → 把「到各决策闸门的归一化距离」
作为置信度特征（树跑完再打分，类型/边际均可得，部署可行）。

评估：5 折 CV 的类内 AUC + 仿真严格 mAP11（FN 取 train1000 实测日志，
树用 v1.2.1 splitB 版）。与 S4 现状对比。
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
AP11_LOG = {"OPEN": 87.8, "SHORT": 89.8, "MOUSEBITE": 77.1, "SPUR": 86.6, "COPPER": 90.5, "PINHOLE": 90.1}

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fit_v14 import (load, fnum, s4_score, base_feats, fit_logistic, auc, ap11,
                     design_matrix, CONT, S4_KEYS)

# v1.2.1 部署树（splitB 判据）
OPEN_SPLIT, MB_SPLIT, SHORT_SPLIT = 0.4747, 0.1655, 0.1794
OPEN_BITE, THIN_LN_W, DB_SURROUND = 0.6377, 2.7058, 0.8174
PINHOLE_SURROUND, LATERAL_MB, CU_LOCAL_W = 0.9725, 3.1548, 2.83


def tree_121(r):
    if fnum(r, "surround") > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if fnum(r, "splitB") > OPEN_SPLIT:
            return "OPEN"
        if fnum(r, "bite") > OPEN_BITE:
            if math.log(max(1.0, fnum(r, "localW"))) <= THIN_LN_W:
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


def margins(r):
    """到 v1.2.1 树各闸门的归一化距离（尺度取各特征的经验标准差）。
    返回 dict：m_open / m_mb / m_bite / m_lat / m_ph / m_db / m_parts / m_short / m_min。"""
    sp = fnum(r, "splitB")
    bite = fnum(r, "bite")
    lat = fnum(r, "lateral")
    sur = fnum(r, "surround")
    parts = fnum(r, "parts")
    m = {
        "m_open": abs(sp - OPEN_SPLIT) / 0.15,
        "m_mb": abs(sp - MB_SPLIT) / 0.15,
        "m_bite": abs(bite - OPEN_BITE) / 0.05,
        "m_lat": abs(lat - LATERAL_MB) / 1.0,
        "m_ph": abs(sur - PINHOLE_SURROUND) / 0.02,
        "m_db": abs(sur - DB_SURROUND) / 0.05,
        "m_parts": abs(parts - 1.5) / 0.5,
        "m_short": abs(sp - SHORT_SPLIT) / 0.15,
    }
    pos = fnum(r, "polarity") > 0
    rel = ["m_open", "m_mb", "m_bite", "m_lat", "m_ph", "m_db"] if pos else ["m_parts", "m_short"]
    m["m_min"] = min(m[k] for k in rel)
    return m


MARGIN_KEYS = ["m_open", "m_mb", "m_bite", "m_lat", "m_ph", "m_db", "m_parts", "m_short", "m_min"]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("reports/features_train3/features_*.tsv"))[-1]
    rows = load(path)
    n = len(rows)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={os.path.basename(path)} rows={n} matched={len(matched)}")

    y = np.array([1.0 if int(fnum(r, "gtType")) > 0 else 0.0 for r in rows])
    s4 = np.array([s4_score(r) for r in rows])
    gt = np.array([NAMES.get(int(fnum(r, "gtType")), "") for r in rows])
    pred = np.array([tree_121(r) for r in rows])
    bf_all = [base_feats(r) for r in rows]
    mg_all = [margins(r) for r in rows]

    # 设计矩阵：S4 + 边际特征
    def design(keys, mgs):
        cols = [np.ones(len(bf_all))]
        for k in keys:
            if k in MARGIN_KEYS:
                cols.append(np.array([m[k] for m in mgs]))
            else:
                cols.append(np.array([x[k] for x in bf_all]))
        return np.stack(cols, axis=1)

    idx = list(range(n))
    random.Random(7).shuffle(idx)
    folds = [set(idx[k::5]) for k in range(5)]

    print("\n===== A. 边际特征的区分度（TP vs FP，全量） =====")
    for k in MARGIN_KEYS:
        v = np.array([m[k] for m in mg_all])
        print(f"  {k:8s} TP mean={v[y==1].mean():.3f} FP mean={v[y==0].mean():.3f} | AUC={auc(y, -v):.3f}")

    print("\n===== B. 5 折 CV：类内 AUC（严格 mAP 的排序面） =====")

    def sub(keys, idxs):
        cols = [np.ones(len(idxs))]
        for k in keys:
            if k in MARGIN_KEYS:
                cols.append(np.array([mg_all[i][k] for i in idxs]))
            else:
                cols.append(np.array([bf_all[i][k] for i in idxs]))
        return np.stack(cols, axis=1)

    SETS = {
        "S4(17)": S4_KEYS,
        "S4+m_min": S4_KEYS + ["m_min"],
        "S4+m_all": S4_KEYS + MARGIN_KEYS,
    }
    for name, keys in SETS.items():
        oof = np.zeros(n)
        for te in folds:
            tr_i = [i for i in idx if i not in te]
            te_i = [i for i in idx if i in te]
            w = fit_logistic(sub(keys, tr_i), y[tr_i])
            oof[te_i] = sub(keys, te_i) @ w
        cells = []
        for c in NAMES.values():
            pool = pred == c
            lb = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred)])
            a_new = auc(lb[pool], oof[pool])
            a_old = auc(lb[pool], s4[pool])
            cells.append(f"{c[:4]}:{a_new:.3f}({a_old:.3f})")
        print(f"  {name:10s} pooledOOF_AUC={auc(y, oof):.4f} | " + " ".join(cells))

    print("\n===== C. 仿真严格 mAP11（v1.2.1 树 + OOF 置信度 vs S4） =====")
    oof_s4min, oof_s4all = np.zeros(n), np.zeros(n)
    for te in folds:
        tr_i = [i for i in idx if i not in te]
        te_i = [i for i in idx if i in te]
        for keys, store in ((SETS["S4+m_min"], oof_s4min), (SETS["S4+m_all"], oof_s4all)):
            w = fit_logistic(sub(keys, tr_i), y[tr_i])
            store[te_i] = sub(keys, te_i) @ w

    def strict_map11(scores, tag):
        aps = []
        for c in NAMES.values():
            pool = pred == c
            lb = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred)])
            tp_c = int(lb[pool].sum())
            aps.append(ap11(scores[pool], lb[pool], tp_c + FN_LOG[c]))
        print(f"  {tag:12s} mAP11={100*sum(aps)/6:.2f}%  逐类=" + " ".join(f"{c[:4]}:{a*100:.1f}" for c, a in zip(NAMES.values(), aps)))
        return sum(aps) / 6

    strict_map11(s4, "S4")
    strict_map11(oof_s4min, "S4+m_min")
    strict_map11(oof_s4all, "S4+m_all")


if __name__ == "__main__":
    main()
