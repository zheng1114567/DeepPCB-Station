#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
几何可救性分析：在做「DL 判别 + 框重建」之前，先算清重建的**理论上限**。

问题
----
既往实测：18,189 个被闸门杀掉的候选中，与漏检 GT 有重叠的仅 133 个，
且 IoU≥0.33 的为 0。结论「碎片框直接当最终框无效」。

但组合方案是「DL 判别 → 把判为真的碎片**重建/生长**回可匹配 GT 的框」。
这一步能救回多少，此前**从未量化**。若上限≈0，整条组合路线应立刻停止，
不必再训练任何模型。

本脚本对每个漏检 GT 问：
  1. 该 GT 附近有多少个被拒候选？（素材够不够生长）
  2. 把这些碎片按若干策略合并/扩张后，与 GT 的 IoU 最高能到多少？
     策略：
       - union      : 所有碎片包围盒的并集外接框
       - hull       : 碎片质心凸包的外接框
       - flood      : 允许在 |diff| 缺席时用固定边长外扩（模拟生长上限）
  3. 按 IoU≥0.33 / ≥0.5 分别统计能救回的 GT 比例

用法
----
python tools/rescue_geometry_upperbound.py --cand data/candidates_official_test \
    --samples data/official_test
"""
import argparse, csv, os, collections

import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
IOU_MATCH = 0.33


def load_gt(samples_dir, pair):
    p = os.path.join(samples_dir, pair + ".txt")
    if not os.path.exists(p):
        return []
    out = []
    for ln in open(p, encoding="utf-8"):
        ln = ln.strip()
        if not ln:
            continue
        v = ln.replace(",", " ").split()
        if len(v) >= 5:
            out.append((int(v[0]), int(v[1]), int(v[2]), int(v[3]), int(v[4])))
    return out


def iou(a, b):
    ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
    ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
    iw, ih = max(0, ix2 - ix1 + 1), max(0, iy2 - iy1 + 1)
    inter = iw * ih
    if inter <= 0:
        return 0.0
    aa = (a[2] - a[0] + 1) * (a[3] - a[1] + 1)
    bb = (b[2] - b[0] + 1) * (b[3] - b[1] + 1)
    return inter / (aa + bb - inter)


def union_box(bs):
    return (min(b[0] for b in bs), min(b[1] for b in bs),
            max(b[2] for b in bs), max(b[3] for b in bs))


def grow(box, img_wh, m):
    w, h = img_wh
    cx = (box[0] + box[2]) / 2.0
    cy = (box[1] + box[3]) / 2.0
    hw = (box[2] - box[0] + 1) / 2.0 + m
    hh = (box[3] - box[1] + 1) / 2.0 + m
    return (max(0, int(cx - hw)), max(0, int(cy - hh)),
            min(w - 1, int(cx + hw)), min(h - 1, int(cy + hh)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cand", default="data/candidates_official_test")
    ap.add_argument("--samples", default="data/official_test")
    ap.add_argument("--expand", type=int, default=6,
                    help="额外模拟的固定外扩像素（越大越乐观，等于给免费生长预算）")
    a = ap.parse_args()

    rows = list(csv.DictReader(open(os.path.join(ROOT, a.cand, "manifest.csv"), encoding="utf-8")))
    by_pair = collections.defaultdict(list)
    for r in rows:
        by_pair[r["pair"]].append(r)

    tot_gt = 0          # 全部 GT
    miss_gt = 0         # 当前未检出的 GT
    miss_with_cand = 0  # 附近有被拒候选的漏检 GT
    hist_direct = collections.Counter()
    hist_union = collections.Counter()
    hist_grow = collections.Counter()
    nfrag_hist = collections.Counter()
    exact_union_ratio = []

    for pair, rs in by_pair.items():
        gts = load_gt(os.path.join(ROOT, a.samples), pair)
        if not gts:
            continue
        img_wh = (int(rs[0]["patch_w"]) * 0 + 0, 0)  # 占位，后面用 diff 无关的近似
        # 直接读 test 图尺寸
        tpath = None
        for cand in (os.path.join(ROOT, a.samples, pair + "_test.jpg"),
                     os.path.join(ROOT, a.samples, pair + ".jpg"),
                     os.path.join(ROOT, a.samples, pair + "_test.png")):
            if os.path.exists(cand):
                tpath = cand
                break
        img_wh = Image.open(tpath).size if tpath else (2000, 2000)

        reported = [(int(r["box_x"]), int(r["box_y"]),
                     int(r["box_x"]) + int(r["box_w"]) - 1,
                     int(r["box_y"]) + int(r["box_h"]) - 1)
                    for r in rs if not r["source"].startswith("rejected")]
        rejected = [(int(r["box_x"]), int(r["box_y"]),
                     int(r["box_x"]) + int(r["box_w"]) - 1,
                     int(r["box_y"]) + int(r["box_h"]) - 1)
                    for r in rs if r["source"].startswith("rejected")]

        for g in gts:
            gb = (g[0], g[1], g[2], g[3])
            tot_gt += 1
            if max([iou(b, gb) for b in reported], default=0.0) >= IOU_MATCH:
                continue
            miss_gt += 1

            # 该 GT 附近的碎片（允许一定外扩，因为生长会跨过这段距离）
            near = [b for b in rejected
                    if iou(grow(b, img_wh, a.expand), gb) > 0.0]
            if not near:
                continue
            miss_with_cand += 1
            nfrag_hist[min(len(near), 20)] += 1

            # 策略1：直接用碎片（单个或并集）——现状
            best_direct = max(iou(b, gb) for b in near)
            hist_direct[">=0.33" if best_direct >= IOU_MATCH else "<0.33"] += 1

            # 策略2：并集外接框
            ub = union_box(near)
            iu = iou(ub, gb)
            hist_union[">=0.33" if iu >= IOU_MATCH else "<0.33"] += 1
            ga = (gb[2]-gb[0]+1)*(gb[3]-gb[1]+1)
            exact_union_ratio.append(((ub[2]-ub[0]+1)*(ub[3]-ub[1]+1)) / max(ga, 1))

            # 策略3：并集框再外扩 expand 像素（模拟区域生长能覆盖到多大）
            gbox = grow(ub, img_wh, a.expand)
            ig = iou(gbox, gb)
            hist_grow[">=0.33" if ig >= IOU_MATCH else "<0.33"] += 1

    print(f"样本对 {len(by_pair)}   GT 总数 {tot_gt}   当前漏检 {miss_gt}")
    print(f"漏检 GT 中，附近({a.expand}px 内)有被拒候选的: {miss_with_cand} "
          f"({miss_with_cand/max(miss_gt,1):.1%} 的漏检)")

    def rep(name, h):
        n = sum(h.values())
        ok = h[">=0.33"]
        print(f"  {name:22s} 达标(IoU>=0.33) {ok:4d}/{n:4d} = {ok/max(n,1):6.1%}")

    print("\n【重建策略能救回的漏检 GT 比例】")
    rep("碎片直接用(现状)", hist_direct)
    rep("并集外接框", hist_union)
    rep(f"并集框+外扩{a.expand}px", hist_grow)

    if exact_union_ratio:
        r = np.array(exact_union_ratio)
        print(f"\n并集框面积 / GT 面积: 中位 {np.median(r):.2f}  "
              f"p25 {np.percentile(r,25):.2f}  p75 {np.percentile(r,75):.2f}")
        print("  （≈1.0 理想；远大于 1 说明并集会把大量正常铜箔圈进来，IoU 被稀释）")

    print("\n每个漏检 GT 附近的碎片数分布(截断20):",
          dict(sorted(nfrag_hist.items())))


if __name__ == "__main__":
    main()