#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
实验：被闸门杀掉的候选里，能否用 DL 区分「真缺陷碎片」与「纯鬼影」？

背景
----
docs/QUALITY_DEBT.md 已实证：检出/排序/分型三杠杆封顶，144 个漏检里
lop 闸 51 + area<min 36 = 60%，且全部 RawDiffPx>0（有信号，是被闸门误杀）。
瓶颈定性为「判别性不足：鬼影与浅咬在灰度统计上高度重叠」。

本实验直接检验这个定性：
  正样本 = 被闸门杀掉、但与「未被任何上报缺陷匹配的 GT」有重叠的候选（真碎片被误杀）
  负样本 = 被闸门杀掉、且与任何未匹配 GT 零重叠的候选（纯鬼影）

若 AUROC ≈ 0.5 → 判别性确为 0，DL 也无解，应停止投入（证伪）。
若 AUROC 明显 > 0.5 → 才有必要继续做「判别 + 区域生长」的组合实验。

纪律
----
- 训练只用 train1000（data/full 中不含 official_test 的部分）。
- 决策看 external（从未参与调参的留出集）。
- official_test 只作最终验收，不参与任何阈值选择。

用法
----
python tools/train_gate_rescue.py --epochs 15 --img 64 --neg-per-pos 20
"""
import argparse
import csv
import os
import random
import collections

import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader
from PIL import Image
from sklearn.metrics import roc_auc_score, average_precision_score

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
IOU_MATCH = 0.33   # 与评测口径一致：缺陷被算作"已检出"的门槛


# ---------------- 数据 ----------------

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
        if len(v) < 5:
            continue
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


def build_index(cand_dir, samples_dir):
    """返回 [(png_path, label, best_gt_iou), ...]，只看被闸门杀掉的候选。"""
    rows = list(csv.DictReader(open(os.path.join(cand_dir, "manifest.csv"), encoding="utf-8")))
    by = collections.defaultdict(list)
    for r in rows:
        by[r["pair"]].append(r)

    items = []
    for pair, rs in by.items():
        gts = load_gt(samples_dir, pair)
        reported = []
        for r in rs:
            if r["source"].startswith("rejected"):
                continue
            reported.append((
                int(r["box_x"]), int(r["box_y"]),
                int(r["box_x"]) + int(r["box_w"]) - 1,
                int(r["box_y"]) + int(r["box_h"]) - 1))
        unmatched = []
        for g in gts:
            gb = (g[0], g[1], g[2], g[3])
            best = max([iou(b, gb) for b in reported], default=0.0)
            if best < IOU_MATCH:
                unmatched.append(gb)

        for r in rs:
            if not r["source"].startswith("rejected"):
                continue
            b = (int(r["box_x"]), int(r["box_y"]),
                 int(r["box_x"]) + int(r["box_w"]) - 1,
                 int(r["box_y"]) + int(r["box_h"]) - 1)
            best = max([iou(b, gb) for gb in unmatched], default=0.0)
            items.append((os.path.join(cand_dir, r["file"]), 1 if best > 0 else 0, best))
    return items


class PatchSet(Dataset):
    def __init__(self, items, img, mean, std, augment=False):
        self.items = items
        self.img = img
        self.mean = mean
        self.std = std
        self.augment = augment

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        path, label, _ = self.items[i]
        im = Image.open(path).convert("RGB").resize((self.img, self.img), Image.BILINEAR)
        x = np.asarray(im, dtype=np.float32) / 255.0
        if self.augment:
            if random.random() < 0.5:
                x = x[:, ::-1, :].copy()
            if random.random() < 0.5:
                x = x[::-1, :, :].copy()
            k = random.randint(0, 3)
            x = np.rot90(x, k, axes=(0, 1)).copy()
        x = (x - self.mean) / self.std
        x = torch.from_numpy(np.ascontiguousarray(x.transpose(2, 0, 1)))
        return x, float(label)


# ---------------- 模型：轻量 CNN（合成三通道，不强依赖 ImageNet 预训练）----------------

class SmallCNN(nn.Module):
    def __init__(self):
        super().__init__()
        def blk(cin, cout):
            return nn.Sequential(
                nn.Conv2d(cin, cout, 3, padding=1, bias=False),
                nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
                nn.Conv2d(cout, cout, 3, padding=1, bias=False),
                nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
                nn.MaxPool2d(2))
        self.f = nn.Sequential(blk(3, 32), blk(32, 64), blk(64, 128), blk(128, 256))
        self.head = nn.Sequential(nn.AdaptiveAvgPool2d(1), nn.Flatten(),
                                  nn.Linear(256, 64), nn.ReLU(inplace=True), nn.Linear(64, 1))

    def forward(self, x):
        return self.head(self.f(x)).squeeze(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--train-cand", default="data/candidates_train")
    ap.add_argument("--train-samples", default="data/train1000")
    ap.add_argument("--val-cand", default="data/candidates_external")
    ap.add_argument("--val-samples", default="data/external")
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--epochs", type=int, default=15)
    ap.add_argument("--bs", type=int, default=64)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--neg-per-pos", type=float, default=20.0,
                    help="负样本按正样本的多少倍抽样（极端不平衡，需降采样）")
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    random.seed(a.seed); np.random.seed(a.seed); torch.manual_seed(a.seed)
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device={dev}")

    tr = build_index(os.path.join(ROOT, a.train_cand), os.path.join(ROOT, a.train_samples))
    va = build_index(os.path.join(ROOT, a.val_cand), os.path.join(ROOT, a.val_samples))
    trp = [t for t in tr if t[1] == 1]; trn = [t for t in tr if t[1] == 0]
    vap = [t for t in va if t[1] == 1]; van = [t for t in va if t[1] == 0]
    print(f"train: 正 {len(trp)} / 负 {len(trn)}")
    print(f"val  : 正 {len(vap)} / 负 {len(van)}")
    if len(trp) < 20 or len(vap) < 10:
        print("!! 正样本过少，结论不可靠，停止。")
        return

    random.shuffle(trn)
    keep = trp + trn[: int(len(trp) * a.neg_per_pos)]
    random.shuffle(keep)

    # 归一化统计量（训练集抽样算）
    mean = np.zeros(3, dtype=np.float32); std = np.ones(3, dtype=np.float32)
    sample = [t for t in keep[:2000]]
    acc = np.zeros((len(sample), 3), dtype=np.float32)
    for i, (p, _, _) in enumerate(sample):
        im = Image.open(p).convert("RGB").resize((a.img, a.img), Image.BILINEAR)
        acc[i] = np.asarray(im, dtype=np.float32).reshape(-1, 3).mean(0) / 255.0
    mean = acc.mean(0)
    sacc = np.zeros((len(sample), 3), dtype=np.float32)
    for i, (p, _, _) in enumerate(sample):
        im = Image.open(p).convert("RGB").resize((a.img, a.img), Image.BILINEAR)
        v = np.asarray(im, dtype=np.float32).reshape(-1, 3) / 255.0
        sacc[i] = ((v - mean) ** 2).mean(0)
    std = np.sqrt(sacc.mean(0)) + 1e-6
    print(f"mean={mean.round(4).tolist()} std={std.round(4).tolist()}")

    ds = PatchSet(keep, a.img, mean, std, augment=True)
    dsv = PatchSet(va, a.img, mean, std)
    dl = DataLoader(ds, batch_size=a.bs, shuffle=True, num_workers=2, drop_last=True)
    dlv = DataLoader(dsv, batch_size=a.bs, shuffle=False, num_workers=2)

    model = SmallCNN().to(dev)
    pos_w = torch.tensor([len([t for t in keep if t[1] == 0]) / max(1, len([t for t in keep if t[1] == 1]))],
                         device=dev)
    crit = nn.BCEWithLogitsLoss(pos_weight=pos_w)
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-4)

    best = -1
    for ep in range(1, a.epochs + 1):
        model.train(); tot = 0.0
        for x, y in dl:
            x, y = x.to(dev), y.to(dev)
            opt.zero_grad()
            loss = crit(model(x), y)
            loss.backward(); opt.step()
            tot += loss.item() * len(y)
        model.eval(); ys, ps = [], []
        with torch.no_grad():
            for x, y in dlv:
                p = torch.sigmoid(model(x.to(dev))).cpu().numpy()
                ys.append(y.numpy()); ps.append(p)
        yv = np.concatenate(ys); pv = np.concatenate(ps)
        try:
            auc = roc_auc_score(yv, pv); aps = average_precision_score(yv, pv)
        except ValueError:
            auc, aps = float("nan"), float("nan")
        print(f"ep{ep:02d} loss={tot/len(ds):.4f}  val AUROC={auc:.4f}  PR-AUC={aps:.4f}")
        if auc == auc and auc > best:
            best = auc
            torch.save({"model": model.state_dict(), "mean": mean, "std": std},
                       os.path.join(ROOT, "tools", "gate_rescue_best.pt"))

    # 最终工作点分析
    model.eval(); ys, ps = [], []
    with torch.no_grad():
        for x, y in dlv:
            ps.append(torch.sigmoid(model(x.to(dev))).cpu().numpy()); ys.append(y.numpy())
    yv = np.concatenate(ys); pv = np.concatenate(ps)
    print("\n=== 留出集（external，从未参与调参）工作点分析 ===")
    print(f"正样本 {int(yv.sum())} / 负样本 {int((1-yv).sum())}")
    order = np.argsort(-pv)
    ysort = yv[order]
    for r in (0.2, 0.5, 0.8):
        need = max(1, int(np.ceil(r * yv.sum())))
        tp_at = int(ysort[:need].sum())
        print(f"  取置信度最高的 {need} 个（目标召回 {r:.0%}）：命中真碎片 {tp_at}，"
              f"其中混鬼影 {need - tp_at}，精确率 {tp_at/need:.1%}")
    print(f"\n判定：AUROC={best:.4f}。"
          f"{'接近 0.5 → 判别性确为 0，DL 亦无解，应停止投入（证伪）。' if best < 0.6 else '高于 0.5，值得继续做「判别+生长」组合实验。'}")


if __name__ == "__main__":
    main()
