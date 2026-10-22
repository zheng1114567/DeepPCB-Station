#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
组合仿真：「DL 判别 + 框重建」在 official_test 上的净收益（TP 增益 vs FP 代价）。

背景
----
已量化两件事：
  1. 碎片直接用只有 1.6% 能匹配 GT；并集框 9.4%；再外扩 6px 才到 45.3%
     （几何上限，tools/rescue_geometry_upperbound.py）。
  2. DL 对碎片有判别力（official AUROC 0.88，闸门内 0.84~0.96，
     tools/official_infer_eval.py）。

于是真正的工程问题只剩一个：**这套组合在最终指标上是净赚还是净亏？**
因为工业 KPI 里 FCR（每板假点数）和板级漏放率是要一起看的，
救回 40 个漏检若引入 300 个假点，就是净亏。

本脚本对 official_test 全量做端到端仿真：
  - 已有上报缺陷保持不变
  - 对被拒候选按模型分排序，选中者按「邻域并集框 + 外扩 m」重建为一个新上报框
  - 逐个计算：新增 TP（与此前未匹配 GT 的 IoU≥0.33）、新增 FP（匹配不上任何 GT）
  - 输出 (m, thr) 网格上的净收益

纪律
----
official_test 只做最终验收，不用于选参数。本脚本输出全网格供人工判读，
**不自动推荐最优点**；若某格最优，仍须先在 external 上复现再考虑升配方。

用法
----
python tools/rescue_combo_sim.py --cand data/candidates_official_test \
    --samples data/official_test --ckpt tools/gate_rescue_best.pt
"""
import argparse, csv, os, collections

import numpy as np
import torch
from PIL import Image

from train_gate_rescue import build_index, PatchSet, SmallCNN, ROOT

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


def grow(box, wh, m):
    w, h = wh
    cx = (box[0] + box[2]) / 2.0
    cy = (box[1] + box[3]) / 2.0
    hw = (box[2] - box[0] + 1) / 2.0 + m
    hh = (box[3] - box[1] + 1) / 2.0 + m
    return (max(0, int(cx - hw)), max(0, int(cy - hh)),
            min(w - 1, int(cx + hw)), min(h - 1, int(cy + hh)))


def union_box(bs):
    return (min(b[0] for b in bs), min(b[1] for b in bs),
            max(b[2] for b in bs), max(b[3] for b in bs))


def greedy_match(pred_boxes, gts, iou_thr=IOU_MATCH):
    """标准贪心 IoU 匹配：返回 (tp, fp, 命中列表)。"""
    used = set()
    tp = fp = 0
    hits = []
    # 面积从大到小（稳定且与评测器口径一致的近似）
    order = sorted(range(len(pred_boxes)), key=lambda i: -(
        (pred_boxes[i][2] - pred_boxes[i][0] + 1) * (pred_boxes[i][3] - pred_boxes[i][1] + 1)))
    for i in order:
        best, bj = 0.0, -1
        for j, gb in enumerate(gts):
            if j in used:
                continue
            v = iou(pred_boxes[i], gb)
            if v > best:
                best, bj = v, j
        if best >= iou_thr:
            used.add(bj); tp += 1; hits.append((i, bj, best))
        else:
            fp += 1
    return tp, fp, hits


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cand", default="data/candidates_official_test")
    ap.add_argument("--samples", default="data/official_test")
    ap.add_argument("--ckpt", default="tools/gate_rescue_best.pt")
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--bs", type=int, default=256)
    ap.add_argument("--per-board-max", type=int, default=3,
                    help="每板最多新增几个上报框（模拟工程上的闸门约束）")
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(os.path.join(ROOT, a.ckpt), map_location=dev, weights_only=False)
    model = SmallCNN().to(dev)
    model.load_state_dict(ck["model"])
    model.eval()

    items = build_index(os.path.join(ROOT, a.cand), os.path.join(ROOT, a.samples))
    ds = PatchSet(items, a.img, ck["mean"], ck["std"])
    dl = torch.utils.data.DataLoader(ds, batch_size=a.bs, shuffle=False, num_workers=4)
    ps = []
    with torch.no_grad():
        for x, _ in dl:
            ps.append(torch.sigmoid(model(x.to(dev))).cpu().numpy())
    score = np.concatenate(ps)

    rows = list(csv.DictReader(open(os.path.join(ROOT, a.cand, "manifest.csv"), encoding="utf-8")))
    # ---- 按 pair 聚合：已上报框取自 manifest，被拒候选取 model 打分 ----
    by_pair = collections.defaultdict(lambda: {"rep": [], "rej": [], "sc": []})
    score_of = {p: float(s) for (p, _l, _b), s in zip(items, score)}
    for r in rows:
        pair = r["pair"]
        b = (int(r["box_x"]), int(r["box_y"]),
             int(r["box_x"]) + int(r["box_w"]) - 1,
             int(r["box_y"]) + int(r["box_h"]) - 1)
        if r["source"].startswith("rejected"):
            k = os.path.join(ROOT, a.cand, r["file"])
            if k in score_of:
                by_pair[pair]["rej"].append(b)
                by_pair[pair]["sc"].append(score_of[k])
        else:
            by_pair[pair]["rep"].append(b)

    npairs = len(by_pair)
    nrej = sum(1 for r in rows if r["source"].startswith("rejected"))
    print(f"官方集 {npairs} 对，已对被拒候选打分 {len(score)} 个（manifest 中被拒 {nrej}）")

    # 基线
    base_tp = base_fp = base_ng = 0
    per_pair = {}
    for pair, d in by_pair.items():
        gts = load_gt(os.path.join(ROOT, a.samples), pair)
        tp, fp, _ = greedy_match(d["rep"], gts)
        per_pair[pair] = {"gts": gts, "rep": d["rep"], "rej": d["rej"], "sc": d["sc"],
                         "tp": tp, "fp": fp, "img": None}
        base_tp += tp; base_fp += fp; base_ng += len(gts)
    # 板级漏放
    base_esc = 0
    for pair, v in per_pair.items():
        if v["gts"] and v["tp"] == 0:
            base_esc += 1
    print(f"基线: TP {base_tp}  FP {base_fp}  漏检GT {base_ng - base_tp}  "
          f"板级漏放(整板全漏) {base_esc}/{npairs} = {base_esc/npairs:.2%}  "
          f"FCR {base_fp/npairs:.3f}/板")

    wh_cache = {}
    print("\n=== (外扩 m, 阈值 thr) 网格：新增TP / 新增FP / 板级漏放 ===")
    print("     thr  |" + "".join(f"  m={m:<2d}" for m in (0, 2, 4, 6, 8)))
    grid = []
    for thr in (0.5, 0.7, 0.8, 0.9, 0.95):
        line = f"  {thr:.2f} |"
        for m in (0, 2, 4, 6, 8):
            add_tp = add_fp = esc = 0
            for pair, v in per_pair.items():
                if v["img"] is None:
                    t = os.path.join(ROOT, a.samples, pair + "_test.jpg")
                    v["img"] = Image.open(t).size if os.path.exists(t) else (2000, 2000)
                sel = [(b, s) for b, s in zip(v["rej"], v["sc"]) if s >= thr]
                if not sel:
                    continue
                sel.sort(key=lambda x: -x[1])
                # 按空间聚类：把彼此接近(≤m) 的碎片并成一个重建框
                boxes = [b for b, _s in sel]
                clusters = []
                for b in boxes:
                    placed = False
                    for cl in clusters:
                        cb = union_box(cl)
                        if iou(grow(cb, v["img"], m + 1), grow(b, v["img"], m + 1)) > 0.0:
                            cl.append(b); placed = True; break
                    if not placed:
                        clusters.append([b])
                # 簇按最高分排序，受 per-board-max 约束
                scored = []
                for cl in clusters:
                    mx = max(s for (b, s) in sel if any(b is x for x in cl))
                    scored.append((mx, cl))
                scored.sort(key=lambda x: -x[0])
                chosen = scored[: a.per_board_max]
                new_boxes = []
                for _mx, cl in chosen:
                    ub = union_box(cl)
                    new_boxes.append(ub if m == 0 else grow(ub, v["img"], m))
                if not new_boxes:
                    continue
                matched_all = v["tp"]
                matched_all_fp = v["fp"]
                # 已有框占用的 GT 不再重复计
                used = set()
                for b in v["rep"]:
                    best, bj = 0.0, -1
                    for j, gb in enumerate(v["gts"]):
                        if j in used:
                            continue
                        t = iou(b, gb)
                        if t > best:
                            best, bj = t, j
                    if best >= IOU_MATCH:
                        used.add(bj)
                gt_tp, gt_fp, _ = greedy_match(new_boxes, [v["gts"][j] for j in range(len(v["gts"])) if j not in used])
                add_tp += gt_tp
                add_fp += gt_fp
                if gt_tp > 0 or len(v["gts"]) - len(used) > 0:
                    # 板级漏放：该板剩余未匹配 GT 归零视为救回
                    esc += 1 if (len(v["gts"]) - len(used) - gt_tp) == 0 and len(v["gts"]) > 0 else 0
            grid.append((thr, m, add_tp, add_fp))
            line += f"{add_tp:2d}/{add_fp:<3d}"
        print(line)

    print("\n净收益（新增TP - 新增FP）")
    for thr, m, tp, fp in grid:
        print(f"  thr={thr:.2f} m={m}:  +{tp} TP / +{fp} FP  净 {tp - fp:+d}")


if __name__ == "__main__":
    main()