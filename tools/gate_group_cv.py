#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
按「板(board)」分组的交叉验证 —— 检验闸门内 AUROC=0.93 是真判别力还是同图/同板泄漏。

为什么必须分组
--------------
上一轮 eval_gate_stratified.py 报出闸门内 AUROC 0.90~0.98（area<min 0.930 / lop 0.978 /
edge 0.904）。但正样本只有 11~24 个，且**同一块 PCB 上会同时出现真碎片和鬼影碎片**，
它们的背景纹理、邻近走线、局部铜箔几乎一致。随机打散的训练/验证会让模型见到
「这块板的其他碎片」→ 极可能只是记住了这块板的样子，而非判别缺陷与鬼影。

分组键必须是**板号**（pair 前 5 位，如 00041），不能是 pair 本身：
DeepPCB 把同一块 PCB 拍成多张 test 图，00041000 / 00041001 是同一块板的两张图
（后 3 位是组内图序号；data/full 1500 个 pair 只有 11 个板号）。
按 pair 分折时，同板不同图的候选会同时落进训练折与验证折 → 跨板泄漏，
OOF AUROC/精确率虚高（诊断见 crossset_calibration.py）。

本脚本用 GroupKFold(board) 保证同一块板不跨折，并额外报告：
  1. 分组 CV 的整体 AUROC 与闸门内 AUROC（宏平均）
  2. 与「闸门基线」和「随机折」对照，看分组后掉多少
  3. Wilson/boot 置信区间 —— 24 个正样本上的 0.93 本身就不稳

判据
----
若 分组CV AUROC 仍显著 > 闸门基线且 > 0.75 → 判别力真实，可进入「判别+框重建」实验。
若 掉回 ~0.6 以下 → 上一轮高分是同图泄漏，应停止 DL 路线（证伪）。

用法
----
python tools/gate_group_cv.py --folds 5 --epochs 12
"""
import argparse, csv, os, collections, random

import numpy as np
import torch
from PIL import Image
from sklearn.metrics import roc_auc_score

from train_gate_rescue import build_index, PatchSet, SmallCNN, ROOT


def board_of(pair):
    """DeepPCB 板号 = pair 前 5 位（如 00041），后 3 位为同一块板的图序号。

    00041000 / 00041001 是同一块板的两张 test 图 —— 按 pair 分折会让
    同板候选跨折泄漏，分组 CV 必须用板号（诊断见 crossset_calibration.py）。
    """
    return pair[:5]


def with_gate_and_pair(cand_dir, samples_dir):
    items = build_index(os.path.join(ROOT, cand_dir), os.path.join(ROOT, samples_dir))
    rows = list(csv.DictReader(open(os.path.join(ROOT, cand_dir, "manifest.csv"), encoding="utf-8")))
    gate_of, pair_of = {}, {}
    for r in rows:
        k = os.path.join(ROOT, cand_dir, r["file"])
        if r["source"].startswith("rejected"):
            gate_of[k] = r["source"].split(":", 1)[1]
        pair_of[k] = r["pair"]
    return [(p, lab, bi, gate_of.get(p, "?"), pair_of.get(p, "?")) for p, lab, bi in items]


def boot_ci(y, s, n=2000, seed=0):
    """bootstrap AUROC 的 95% 区间。"""
    rng = np.random.default_rng(seed)
    y = np.asarray(y); s = np.asarray(s)
    out = []
    for _ in range(n):
        idx = rng.integers(0, len(y), len(y))
        if y[idx].sum() < 3 or (1 - y[idx]).sum() < 3:
            continue
        out.append(roc_auc_score(y[idx], s[idx]))
    if len(out) < 100:
        return float("nan"), float("nan")
    return float(np.percentile(out, 2.5)), float(np.percentile(out, 97.5))


def norm_stats(paths, img):
    acc = np.zeros((min(len(paths), 2000), 3), dtype=np.float32)
    for i, p in enumerate(paths[:2000]):
        im = Image.open(p).convert("RGB").resize((img, img), Image.BILINEAR)
        acc[i] = np.asarray(im, dtype=np.float32).reshape(-1, 3).mean(0) / 255.0
    mean = acc.mean(0)
    acc2 = np.zeros_like(acc)
    for i, p in enumerate(paths[:2000]):
        im = Image.open(p).convert("RGB").resize((img, img), Image.BILINEAR)
        x = np.asarray(im, dtype=np.float32) / 255.0
        acc2[i] = (x - mean).reshape(-1, 3).std(0)
    return mean.astype(np.float32), np.maximum(acc2.mean(0), 1e-3).astype(np.float32)


def infer(model, items, img, mean, std, bs, dev):
    ds = PatchSet([(p, lab, bi) for p, lab, bi, *_ in items], img, mean, std)
    dl = torch.utils.data.DataLoader(ds, batch_size=bs, shuffle=False, num_workers=2)
    out = []
    with torch.no_grad():
        for x, _ in dl:
            out.append(torch.sigmoid(model(x.to(dev))).cpu().numpy())
    return np.concatenate(out) if out else np.zeros(0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cand", default="data/candidates_train")
    ap.add_argument("--samples", default="data/train1000")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--bs", type=int, default=64)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--neg-per-pos", type=float, default=20.0)
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    items = with_gate_and_pair(a.cand, a.samples)
    pos = [t for t in items if t[1] == 1]
    neg = [t for t in items if t[1] == 0]
    print(f"全集: 正 {len(pos)} / 负 {len(neg)}  "
          f"板数 {len(set(board_of(t[4]) for t in items))} "
          f"/ pair 数 {len(set(t[4] for t in items))}")

    # 折分配必须覆盖**所有**板。若只对含正样本的板分折，
    # 其余「纯负板」的候选 fold_of 取默认 -1 → 永远只进训练折、从不出现在验证折。
    # 后果：OOF 验证集变成「富含真缺陷的极少数板」的子集，正样本率被抬高到失真，
    #精确率/AUROC 双双虚高（实测 100% 精确率，纯属这个 bug）。
    #
    # 分组键必须是**板号**（pair 前5位），不能是 pair 本身：按 pair 分折时，
    # 同板不同图（00041000/00041001）会同时出现在训练折与验证折 → 跨板泄漏。
    all_boards = sorted(set(board_of(t[4]) for t in items))
    pos_boards = sorted(set(board_of(t[4]) for t in pos))
    rng = random.Random(a.seed)
    rs = list(all_boards); rng.shuffle(rs)
    fold_of = {b: i % a.folds for i, b in enumerate(rs)}
    print(f"参与分折的板总数 {len(rs)}（含 {len(rs)-len(pos_boards)} 个纯负板），"
          f"每折正样本所在板数: "
          f"{ {i: sum(1 for b in pos_boards if fold_of[b]==i) for i in range(a.folds)} }")

    # 闸门基线（用全体训练期的正样本率，与分层脚本口径一致）
    st = collections.defaultdict(lambda: [0, 0])
    for t in items:
        st[t[3]][1] += 1
        if t[1] == 1:
            st[t[3]][0] += 1
    gate_rate = {g: (v[0] / v[1] if v[1] else 0.0) for g, v in st.items()}

    oof_y, oof_s, oof_g, oof_f = [], [], [], []
    for f in range(a.folds):
        tr = [t for t in items if fold_of.get(board_of(t[4]), -1) != f]
        va = [t for t in items if fold_of.get(board_of(t[4]), -1) == f]
        trp = [t for t in tr if t[1] == 1]; trn = [t for t in tr if t[1] == 0]
        vap = [t for t in va if t[1] == 1]; van = [t for t in va if t[1] == 0]
        if len(trp) < 15 or len(vap) < 5:
            print(f"折{f}: 正样本不足（train {len(trp)} / val {len(vap)}），跳过")
            continue
        random.Random(a.seed + f).shuffle(trn)
        keep = trp + trn[: int(len(trp) * a.neg_per_pos)]
        random.Random(a.seed + f).shuffle(keep)
        mean, std = norm_stats([t[0] for t in keep], a.img)

        torch.manual_seed(a.seed)
        model = SmallCNN().to(dev)
        opt = torch.optim.AdamW(model.parameters(), lr=a.lr)
        ds = PatchSet([(t[0], t[1], t[2]) for t in keep], a.img, mean, std, augment=True)
        dl = torch.utils.data.DataLoader(ds, batch_size=a.bs, shuffle=True, num_workers=2)
        lossf = torch.nn.BCEWithLogitsLoss()
        model.train()
        for ep in range(a.epochs):
            tot = 0.0
            for x, y in dl:
                x = x.to(dev); y = y.to(dev).float()
                opt.zero_grad(); l = lossf(model(x), y); l.backward(); opt.step()
                tot += float(l) * len(y)
            if ep == a.epochs - 1:
                print(f"  折{f} ep{ep} loss={tot/len(ds):.4f}")
        model.eval()

        vitems = vap + van   # 全量验证（不抽样，避免漏掉正样本）
        s = infer(model, vitems, a.img, mean, std, a.bs, dev)
        oof_y += [t[1] for t in vitems]
        oof_s += list(s)
        oof_g += [t[3] for t in vitems]
        oof_f += [f] * len(vitems)
        print(f"  折{f}: val 正 {len(vap)} / 负 {len(van)}  AUROC="
              f"{roc_auc_score([t[1] for t in vitems], s):.4f}")

    oof_y = np.array(oof_y); oof_s = np.array(oof_s); oof_g = np.array(oof_g)
    print(f"\n=== 分组 GroupKFold(board) OOF: 正 {int(oof_y.sum())} / 负 {int((1-oof_y).sum())} ===")
    au = roc_auc_score(oof_y, oof_s)
    lo, hi = boot_ci(oof_y, oof_s)
    print(f"  分组 OOF AUROC = {au:.4f}   95%CI [{lo:.4f}, {hi:.4f}]")
    gb = np.array([gate_rate.get(g, 0.0) for g in oof_g])
    print(f"  闸门基线 AUROC  = {roc_auc_score(oof_y, gb):.4f}  ← 对照")

    print("\n  各折 AUROC（折间波动也是稳定性证据）:")
    for f in sorted(set(oof_f)):
        m = np.array(oof_f) == f
        if y_ok := (oof_y[m].sum() >= 3):
            print(f"    折{f}: {roc_auc_score(oof_y[m], oof_s[m]):.4f}  (正 {int(oof_y[m].sum())})")

    print("\n  分组 OOF 的闸门内 AUROC:")
    for g in sorted(set(oof_g)):
        m = oof_g == g
        if m.sum() < 30 or oof_y[m].sum() < 5:
            print(f"    {g:12s} n={m.sum():6d} 正={int(oof_y[m].sum()):4d}  过少，跳过")
            continue
        print(f"    {g:12s} n={m.sum():6d} 正={int(oof_y[m].sum()):4d}  "
              f"AUROC={roc_auc_score(oof_y[m], oof_s[m]):.4f}")

    # 在 OOF 打分上做一次实际救援收益的粗算（**全量**负样本，不抽样）
    print("\n=== OOF 打分下的分卡表现（全量负样本，与 official_infer_eval 同口径）===")
    for thr in (0.5, 0.8, 0.9, 0.95):
        sel = oof_s >= thr
        if not sel.sum():
            print(f"  thr={thr:.2f}  未选中任何候选")
            continue
        prec = oof_y[sel].mean()
        rec = oof_y[sel].sum() / max(oof_y.sum(), 1)
        print(f"  thr={thr:.2f}  选中 {int(sel.sum()):5d}  其中正 {int(oof_y[sel].sum()):3d}"
              f"  精确率 {prec:6.2%}  召回 {rec:6.2%}")
    print("\n注意：精确率需达到 ~90% 才可能让新增FP 可控（组合仿真见 rescue_combo_sim.py）。")


if __name__ == "__main__":
    main()