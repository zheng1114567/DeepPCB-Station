#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v1.2 分型分析：复刻线上 C# 树精度 + 混淆矩阵 + 混淆对特征可分性诊断。"""
import math
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


# ---- 线上 C# 树（PcbTypeTree.cs v1.0.0 简化三判据）----
def deployed(r):
    if fnum(r, "polarity") > 0:
        if fnum(r, "surround") >= 0.9725:
            return "PINHOLE"
        if fnum(r, "split") >= 0.55:
            return "OPEN"
        if fnum(r, "bite") >= 0.64:
            return "OPEN"
        return "MOUSEBITE"
    if fnum(r, "localW") <= 2.83:
        return "COPPER"
    if fnum(r, "parts") >= 2:
        return "SHORT"
    return "SPUR"


def main():
    rows = load(PATH)
    m = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"matched={len(m)}")

    conf = Counter()
    correct = 0
    for r in m:
        gt = NAMES[int(fnum(r, "gtType"))]
        pr = deployed(r)
        conf[(gt, pr)] += 1
        if gt == pr:
            correct += 1
    acc = correct / len(m)
    print(f"deployed-tree acc={acc:.4f} ({correct}/{len(m)})")

    # 分极性
    for pol, tag in ((1, "POS"), (-1, "NEG")):
        sub = [r for r in m if (fnum(r, "polarity") > 0) == (pol > 0)]
        c = sum(1 for r in sub if deployed(r) == NAMES[int(fnum(r, "gtType"))])
        print(f"  {tag} acc={c/len(sub):.4f} n={len(sub)}")

    print("\n===== deployed-tree confusion (gt -> pred: n) =====")
    for gt in NAMES.values():
        line = []
        for pr in NAMES.values():
            n = conf.get((gt, pr), 0)
            if n:
                line.append(f"{pr}:{n}" + ("*" if gt == pr else ""))
        print(f"  {gt:9s} " + "  ".join(line))

    # ---- 混淆对特征分布 ----
    print("\n===== open<->mousebite 混淆对特征分布 =====")
    feats = ["split", "bite", "lateral", "aspect", "fill", "lnArea", "surround", "localW", "edgeFrac", "severed", "parts", "splitN"]
    pairs = [("OPEN", "MOUSEBITE"), ("MOUSEBITE", "OPEN"), ("SHORT", "SPUR"), ("SPUR", "SHORT")]
    for gt, pr in pairs:
        sub = [r for r in m if NAMES[int(fnum(r, "gtType"))] == gt and deployed(r) == pr]
        if not sub:
            print(f"  {gt}->{pr}: 0")
            continue
        print(f"  {gt}->{pr}: n={len(sub)}")
        for k in feats:
            if k == "lnArea":
                vals = [math.log(max(1.0, fnum(r, "area"))) for r in sub]
            else:
                vals = [fnum(r, k) for r in sub]
            vals.sort()
            q = lambda p: vals[min(len(vals) - 1, int(p * len(vals)))]
            print(f"      {k:9s} p10={q(0.1):8.4f} p50={q(0.5):8.4f} p90={q(0.9):8.4f}")

    # ---- 正确样本的同特征分布作对照 ----
    print("\n===== 正确样本对照分布 =====")
    for cls in ("OPEN", "MOUSEBITE", "SHORT", "SPUR"):
        sub = [r for r in m if NAMES[int(fnum(r, "gtType"))] == cls and deployed(r) == cls]
        if not sub:
            continue
        print(f"  {cls} correct: n={len(sub)}")
        for k in ("split", "bite", "lateral", "aspect"):
            vals = sorted(fnum(r, k) for r in sub)
            q = lambda p: vals[min(len(vals) - 1, int(p * len(vals)))]
            print(f"      {k:9s} p10={q(0.1):8.4f} p50={q(0.5):8.4f} p90={q(0.9):8.4f}")


if __name__ == "__main__":
    main()
