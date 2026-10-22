#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fit_v16：分型树候选横评（train1000 全量 + 5 折稳定性）。

候选：
  A. deployed v1.2.0（split 原列）
  B. deployed 结构 + split→splitB（阈值不变）——最小干预
  C. deployed 结构 + split→splitB + 重调阈值（0.2972/0.0370/0.0278，取自 fit_v13 重拟合）
  D. B 重拟合深度5折叠树（splitB 判据，修复 localW 对数 bug）
  E. deployed 结构 + split→splitB + 网格微调两个 POS 阈值（全量优选后汇报，供 CV 复核）

每候选输出：全量错误数、逐类错误、混淆对（open<->mb / mb<->ph / short<->spur / spur<->copper）、
5 折各自测试折上的错误数（固定树，非重拟合——衡量部署树在子集上的稳定性）。
"""
import math
import random
import sys
import glob
import os
from collections import Counter

import numpy as np

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fit_v14 import load, fnum

# v1.2.0 部署树常量
OPEN_SPLIT, MB_SPLIT, SHORT_SPLIT = 0.4747, 0.1655, 0.1794
OPEN_BITE, THIN_LN_W, DB_SURROUND = 0.6377, 2.7058, 0.8174
PINHOLE_SURROUND, LATERAL_MB, CU_LOCAL_W = 0.9725, 3.1548, 2.83
# 重拟合 B 树阈值
B_OPEN_SPLIT, B_MB_SPLIT, B_SHORT_SPLIT = 0.2972, 0.0370, 0.0278
B_OPEN_BITE, B_LNAREA, B_LATERAL_O, B_LATERAL_M = 0.6485, 4.9558, 1.8849, 2.1909


def ln(v):
    return math.log(max(1.0, v))


def tree_deployed(r, split_src="split", open_sp=None, mb_sp=None, short_sp=None):
    """部署树结构；split 来源与三个 split 阈值可参数化。"""
    sp = fnum(r, split_src)
    open_sp = OPEN_SPLIT if open_sp is None else open_sp
    mb_sp = MB_SPLIT if mb_sp is None else mb_sp
    short_sp = SHORT_SPLIT if short_sp is None else short_sp
    if fnum(r, "surround") > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if sp > open_sp:
            return "OPEN"
        if fnum(r, "bite") > OPEN_BITE:
            if ln(fnum(r, "localW")) <= THIN_LN_W:
                return "MOUSEBITE" if fnum(r, "surround") <= DB_SURROUND else "PINHOLE"
            return "OPEN"
        if sp <= mb_sp:
            return "MOUSEBITE"
        return "MOUSEBITE" if fnum(r, "lateral") <= LATERAL_MB else "OPEN"
    if fnum(r, "localW") <= CU_LOCAL_W:
        return "COPPER"
    if fnum(r, "parts") <= 1.5:
        return "SPUR"
    if sp <= short_sp:
        return "SHORT"
    return "SPUR"


def tree_b_refit(r):
    """fit_v13 重拟合 B 树（深度5折叠版）。"""
    if fnum(r, "surround") > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        sp = fnum(r, "splitB")
        if sp > B_OPEN_SPLIT:
            if sp > 0.5664:
                return "OPEN"
            if fnum(r, "lateral") <= B_LATERAL_O:
                return "MOUSEBITE"
            return "OPEN"
        if fnum(r, "bite") > B_OPEN_BITE:
            if ln(fnum(r, "area")) > B_LNAREA:
                return "OPEN"
            return "MOUSEBITE"
        return "MOUSEBITE"
    if ln(fnum(r, "localW")) <= 1.0397:
        return "COPPER"
    if fnum(r, "parts") <= 1.5:
        return "SPUR"
    if fnum(r, "splitB") <= B_SHORT_SPLIT:
        return "SHORT"
    return "SPUR"


def evaluate(name, fn, rows, folds_idx):
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    conf = Counter()
    for r in matched:
        conf[(NAMES[int(fnum(r, "gtType"))], fn(r))] += 1
    errs = sum(v for (g, p), v in conf.items() if g != p)
    omb = conf.get(("OPEN", "MOUSEBITE"), 0) + conf.get(("MOUSEBITE", "OPEN"), 0)
    mph = conf.get(("MOUSEBITE", "PINHOLE"), 0) + conf.get(("PINHOLE", "MOUSEBITE"), 0)
    ss = conf.get(("SHORT", "SPUR"), 0) + conf.get(("SPUR", "SHORT"), 0)
    sc = conf.get(("SPUR", "COPPER"), 0) + conf.get(("COPPER", "SPUR"), 0)
    # 5 折稳定性（固定树在各测试折上的错误数）
    fold_errs = []
    for te in folds_idx:
        e = sum(1 for i in te if (g := NAMES.get(int(fnum(rows[i], "gtType")), "")) != "" and g != fn(rows[i]))
        fold_errs.append(e)
    print(f"  {name:34s} err={errs:4d} acc={1-errs/len(matched):.4f} | open<->mb={omb:3d} mb<->ph={mph:3d} short<->spur={ss:3d} spur<->cu={sc:3d} | folds={fold_errs}")
    return errs, conf


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("reports/features_train3/features_*.tsv"))[-1]
    rows = load(path)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={os.path.basename(path)} matched={len(matched)}")

    idx = list(range(len(rows)))
    random.Random(7).shuffle(idx)
    folds_idx = [[i for i in idx if i in set(idx[k::5])] for k in range(5)]

    print("\n===== 候选横评 =====")
    evaluate("A deployed(split)", lambda r: tree_deployed(r), rows, folds_idx)
    evaluate("B deployed+splitB(阈值不变)", lambda r: tree_deployed(r, "splitB"), rows, folds_idx)
    evaluate("C deployed+splitB(重调阈值)", lambda r: tree_deployed(r, "splitB", B_OPEN_SPLIT, B_MB_SPLIT, B_SHORT_SPLIT), rows, folds_idx)
    evaluate("D B重拟合折叠树", tree_b_refit, rows, folds_idx)

    # E. 网格微调（全量；随后用折稳定性复核）
    print("\n===== E. POS 双阈值网格（splitB，全量错误数） =====")
    best = None
    for osp in [0.22, 0.26, 0.2972, 0.34, 0.38, 0.42, 0.4747]:
        for msp in [0.0, 0.02, 0.037, 0.06, 0.10, 0.1655]:
            fn = lambda r, o=osp, m=msp: tree_deployed(r, "splitB", o, m)
            e = sum(1 for r in matched if NAMES[int(fnum(r, "gtType"))] != fn(r))
            if best is None or e < best[0]:
                best = (e, osp, msp)
    print(f"  最优 open_sp={best[1]} mb_sp={best[2]} err={best[0]}")
    # 用最优阈值复评折稳定性
    evaluate(f"E deployed+splitB(网格 {best[1]}/{best[2]})",
             lambda r: tree_deployed(r, "splitB", best[1], best[2]), rows, folds_idx)

    # 中键阈值下的 NEG short 阈值网格
    print("\n===== E2. NEG short 阈值网格（splitB，全量错误数） =====")
    for ssp in [0.0, 0.01, 0.0278, 0.05, 0.08, 0.12, 0.1794]:
        fn = lambda r, s=ssp: tree_deployed(r, "splitB", None, None, s)
        e = sum(1 for r in matched if NAMES[int(fnum(r, "gtType"))] != fn(r))
        print(f"  short_sp={ssp:.4f} err={e}")

    # 获胜候选的混淆矩阵
    print("\n===== 获胜候选混淆矩阵 =====")
    win_fn = lambda r: tree_deployed(r, "splitB", best[1], best[2])
    conf = Counter()
    for r in matched:
        conf[(NAMES[int(fnum(r, "gtType"))], win_fn(r))] += 1
    hdr = ["open", "short", "mousebite", "spur", "copper", "pinhole"]
    print("  GT\\Pred  " + " ".join(f"{h:>10s}" for h in hdr))
    for g in NAMES.values():
        print(f"  {g:8s} " + " ".join(f"{conf.get((g, p.upper()), 0):>10d}" for p in hdr))


if __name__ == "__main__":
    main()
