#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
跨集校准崩塌检查：同一模型、同一 thr，三个集上的精确率差异。

观察到的矛盾
------------
- train1000 分组 CV 的 OOF：thr=0.90 精确率100%（32/32）
- official_test：thr=0.90 精确率仅 14.8%（45/304）

若 OOF 的 100% 是真的，则 official 上不该崩塌 6倍。这必须查清，
否则「AUROC 0.91 是判别力」这个结论就不能用。

可能解释：
  (a) train1000 与 official_test 的**正样本定义难度不同**
      —— train 的 GT 是 bbox 口径、且未匹配 GT 可能更大更好认；
  (b) 分布外泛化失败（板型/成像链路不同）；
  (c) OOF 仍有残余泄漏（同一板的候选被分到不同折？——GroupKFold 已排除 pair，但
      若同一块板有多个 pair 目录（如 00041000 / 00041001 是同一板的两张图），
      分组键用 pair 就不够，必须用板号）。

(c) 最可能：DeepPCB 每个原始 PCB 被切成多张 test 图，pair = 00041000/00041001
    来自同一块板。用 pair 分组仍会跨板泄漏。必须改用「板号 = pair 前5位」再分组。
    （pair 名本身就是 8 位，取前 8 位是恒等映射 —— 板数永远等于 pair 数，
      泄漏检查会显示「无泄漏」而实际失效；data/full 1500 pair 只有 11 个板号。）

本脚本输出三集在统一 thr 下的精确率/召回率，并检查 board 级分组泄漏。
"""
import argparse, csv, os, collections

import numpy as np
import torch

from train_gate_rescue import build_index, PatchSet, SmallCNN, ROOT


def board_of(pair):
    """DeepPCB 板号 = pair 前 5 位（如 00041）；后 3 位为同一块板的图序号。

    注意：pair 名本身就是 8 位，取 pair[:8] 是恒等映射（板数==pair 数），
    「板数 << pair数」的泄漏检查会永远显示无泄漏而实际失效。必须取前 5 位。
    """
    return pair[:5]


def with_gate_pair(cand, samples):
    items = build_index(os.path.join(ROOT, cand), os.path.join(ROOT, samples))
    rows = list(csv.DictReader(open(os.path.join(ROOT, cand, "manifest.csv"), encoding="utf-8")))
    g, p = {}, {}
    for r in rows:
        k = os.path.join(ROOT, cand, r["file"])
        if r["source"].startswith("rejected"):
            g[k] = r["source"].split(":", 1)[1]
        p[k] = r["pair"]
    return [(x, l, b, g.get(x, "?"), p.get(x, "?")) for x, l, b in items]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", default="tools/gate_rescue_best.pt")
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--bs", type=int, default=256)
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(os.path.join(ROOT, a.ckpt), map_location=dev, weights_only=False)
    model = SmallCNN().to(dev)
    model.load_state_dict(ck["model"]); model.eval()

    sets = [("train1000", "data/candidates_train", "data/train1000"),
            ("external", "data/candidates_external", "data/external"),
            ("official_test", "data/candidates_official_test", "data/official_test")]

    print("=== 统一阈值下的精确率/召回率 ===")
    print("  集              正/负        thr=0.90精确率  召回")
    for name, cand, samp in sets:
        items = with_gate_pair(cand, samp)
        ds = PatchSet([(t[0], t[1], t[2]) for t in items], a.img, ck["mean"], ck["std"])
        dl = torch.utils.data.DataLoader(ds, batch_size=a.bs, shuffle=False, num_workers=4)
        ps = []
        with torch.no_grad():
            for x, _ in dl:
                ps.append(torch.sigmoid(model(x.to(dev))).cpu().numpy())
        s = np.concatenate(ps)
        y = np.array([t[1] for t in items])
        sel = s >= 0.90
        prec = y[sel].mean() if sel.sum() else 0.0
        rec = y[sel].sum() / max(y.sum(), 1)
        print(f"  {name:14s} {int(y.sum()):4d}/{int((1-y).sum()):6d}   "
              f"{prec:9.1%}  {rec:6.2%}")

    print("\n=== 分组键泄漏检查 ===")
    for name, cand, samp in sets:
        items = with_gate_pair(cand, samp)
        pairs = set(t[4] for t in items)
        boards = set(board_of(t[4]) for t in items)
        print(f"  {name:14s} pair数={len(pairs):4d}  板数={len(boards):4d}  "
              f"（若板数 << pair数，说明 pair 分组仍跨板泄漏）")


if __name__ == "__main__":
    main()