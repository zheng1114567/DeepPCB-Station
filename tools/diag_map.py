#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""mAP 结构诊断：用 train1000 dump 复算 v1.2.0 S4 置信度，模拟全局 AP11 曲线。
回答：90.2 的全局 AP 损失来自哪里——FP 排序分布 / 逐类池化 / 天花板。
"""
import math

PATH = "reports/features_train2/features_20261021_013052.tsv"
NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}

W = dict(B=-0.0165, lnArea=0.5795, lnMean=-0.0246, edge=-0.2802, fill=-0.1515,
         lnAspect=-0.5262, surround=0.2054, bite=0.0845, lnLocalW=0.6834,
         lnPeak=-0.0864, parts=-0.3357, split=-0.0043, away=0.3628,
         severed=-0.2894, nets=-0.8624, lnDepth=0.7073, splitN=-0.2063, lateral=0.3245)


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


def conf_s4(r0):
    r = {k: float(v) for k, v in r0.items() if k not in ("pair", "pred")}
    z = W["B"] + W["lnArea"] * ln(r["area"]) + W["lnMean"] * ln(r["mean"]) \
        + W["edge"] * r["edgeFrac"] + W["fill"] * r["fill"] + W["lnAspect"] * ln(r["aspect"]) \
        + W["surround"] * r["surround"] + W["bite"] * r["bite"] + W["lnLocalW"] * ln(r["localW"]) \
        + W["lnPeak"] * ln(r["peak"]) + W["parts"] * r["parts"] + W["split"] * r["split"] \
        + W["away"] * r["away"] + W["severed"] * r["severed"] + W["nets"] * r["nets"] \
        + W["lnDepth"] * ln(r["depth"]) + W["splitN"] * r["splitN"] + W["lateral"] * r["lateral"]
    return 1.0 / (1.0 + math.exp(-max(-30.0, min(30.0, z))))


def ap11(rec, prec):
    """VOC2007 11 点插值（rec/prec 按 conf 降序的累计点列）。"""
    if not rec:
        return 0.0
    aps = []
    for t in [i / 10.0 for i in range(11)]:
        best = 0.0
        for r_, p_ in zip(rec, prec):
            if r_ >= t - 1e-12:
                best = max(best, p_)
        aps.append(best)
    return sum(aps) / 11.0


def curve(scored):
    """scored: [(conf, isTP)] 按 conf 降序 → (rec, prec) 累计列。"""
    tp = fp = 0
    npos = sum(1 for _, y in scored if y)
    rec, prec = [], []
    for c, y in scored:
        if y:
            tp += 1
        else:
            fp += 1
        rec.append(tp / npos)
        prec.append(tp / (tp + fp))
    return rec, prec


def main():
    rows = load(PATH)
    # 一对一贪心近似：逐 pair 内按 conf 降序匹配（同图内）
    from collections import defaultdict
    bypair = defaultdict(list)
    for r in rows:
        bypair[r["pair"]].append(r)

    scored = []          # (conf, isTP_ignore)
    strict_scored = defaultdict(list)  # class -> [(conf, isTP_strict)]
    for pair, rs in bypair.items():
        rs2 = sorted(rs, key=lambda r: -conf_s4(r))
        used_gt = set()
        for r in rs2:
            gt = int(fnum(r, "gtType"))
            iou = fnum(r, "matchIou")
            hit = gt > 0 and iou >= 0.33 and gt not in used_gt
            if hit:
                used_gt.add(gt)
            scored.append((conf_s4(r), hit))
            pred = fv_tree(r)
            strict_scored[pred].append((conf_s4(r), hit and NAMES[gt] == pred))
    scored.sort(key=lambda t: -t[0])

    rec, prec = curve(scored)
    print(f"n_pred={len(scored)} TP={sum(1 for _,y in scored if y)} FP={sum(1 for _,y in scored if not y)}")
    print(f"全局 AP11 (S4 conf, train1000) = {ap11(rec, prec):.4f}")
    # oracle：按是否 TP 排序（完美排序天花板）
    orc = sorted(scored, key=lambda t: -t[1])
    r2, p2 = curve(orc)
    print(f"全局 AP11 (oracle 排序)      = {ap11(r2, p2):.4f}")
    # 面积排序对照（v1.0.0 之前的旧键）
    ar = sorted(rows, key=lambda r: -fnum(r, "area"))
    # 逐 pair 贪心太细，这里用 dump 的 matchIou/gtType 近似（同一 gt 只算一次）
    seen = set()
    sc2 = []
    for r in ar:
        gt = int(fnum(r, "gtType"))
        iou = fnum(r, "matchIou")
        key = (r["pair"], gt)
        hit = gt > 0 and iou >= 0.33 and key not in seen
        if hit:
            seen.add(key)
        sc2.append((fnum(r, "area"), hit))
    r3, p3 = curve(sc2)
    print(f"全局 AP11 (面积排序)         = {ap11(r3, p3):.4f}")

    # FP 的排序分位
    idx_fp = [i for i, (_, y) in enumerate(scored) if not y]
    idx_fp.sort()
    qs = [0.1, 0.25, 0.5, 0.75, 0.9]
    print("FP 排名分位（0=最前）:", {q: idx_fp[int(q * (len(idx_fp) - 1))] / len(scored) for q in qs})
    # 前 10%/20% 里的 FP 数
    for frac in (0.05, 0.1, 0.2, 0.5):
        k = int(frac * len(scored))
        nfp = sum(1 for _, y in scored[:k] if not y)
        print(f"  前 {frac:.0%} 排名内 FP={nfp}")

    # 逐类严格 AP11（S4 conf）
    print("\n逐类严格 AP11（train1000, S4 conf, 树 v1.2）:")
    aps = []
    for c in NAMES.values():
        s = sorted(strict_scored[c], key=lambda t: -t[0])
        if not s:
            continue
        rr, pp = curve(s)
        a = ap11(rr, pp)
        aps.append(a)
        print(f"  {c:9s} n={len(s):5d} TP={sum(1 for _,y in s if y):5d} AP11={a:.4f}")
    print(f"  均值 mAP11(strict) = {sum(aps)/len(aps):.4f}")


def fv_tree(r):
    """v1.2 坍缩树（与 C# 一致）。"""
    if fnum(r, "polarity") > 0:
        if fnum(r, "surround") >= 0.9725:
            return "PINHOLE"
        if fnum(r, "split") > 0.4747:
            return "OPEN"
        if fnum(r, "bite") > 0.6377:
            if ln(fnum(r, "localW")) <= 2.7058:
                return "MOUSEBITE" if fnum(r, "surround") <= 0.8174 else "PINHOLE"
            return "OPEN"
        if fnum(r, "split") <= 0.1655:
            return "MOUSEBITE"
        return "MOUSEBITE" if fnum(r, "lateral") <= 3.1548 else "OPEN"
    if fnum(r, "localW") <= 2.83:
        return "COPPER"
    if fnum(r, "parts") >= 2:
        return "SHORT" if fnum(r, "split") <= 0.1794 else "SPUR"
    return "SPUR"


if __name__ == "__main__":
    main()
