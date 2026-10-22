#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
闸门分层评估：排除「模型只学会了识别是哪个闸门杀的」这一幻觉。

背景：整体 AUROC 高，可能来自正负样本在**不同闸门间**的分布差异
（lop 闸正占比 5.23%，edge 闸仅 0.52%），而不是同一闸门内的真判别力。
若模型只是把 lop 闸的候选排前面，那是"识别闸门"而非"识别真缺陷"，无用。

本脚本给出三个对照：
  1. 整体 AUROC（复现）
  2. **闸门基线**：仅用「该闸门的训练期正样本率」打分，看能到多少（trivial 上界）
  3. **闸门内 AUROC**：在每一闸内部单独算，这才是真判别力
"""
import argparse, csv, os, collections
import numpy as np, torch
from PIL import Image
from sklearn.metrics import roc_auc_score
from train_gate_rescue import build_index, PatchSet, SmallCNN, ROOT, IOU_MATCH


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--train-cand", default="data/candidates_train")
    ap.add_argument("--train-samples", default="data/train1000")
    ap.add_argument("--val-cand", default="data/candidates_external")
    ap.add_argument("--val-samples", default="data/external")
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--bs", type=int, default=64)
    ap.add_argument("--ckpt", default="tools/gate_rescue_best.pt")
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(os.path.join(ROOT, a.ckpt), map_location=dev, weights_only=False)
    model = SmallCNN().to(dev)
    model.load_state_dict(ck["model"])
    model.eval()

    def index_of(items):
        """给每条样本补上闸门名。"""
        out = []
        for p, lab, bi in items:
            # manifest 的 source 在 build_index 里丢了，这里从路径反查不可靠，改为重读
            out.append([p, lab, bi, ""])
        return out

    # 重读 manifest 拿 source（build_index 未保留）
    def with_gate(cand_dir, samples_dir):
        items = build_index(os.path.join(ROOT, cand_dir), os.path.join(ROOT, samples_dir))
        rows = list(csv.DictReader(open(os.path.join(ROOT, cand_dir, "manifest.csv"), encoding="utf-8")))
        gate_of = {}
        for r in rows:
            if r["source"].startswith("rejected"):
                gate_of[os.path.join(ROOT, cand_dir, r["file"])] = r["source"].split(":", 1)[1]
        return [(p, lab, bi, gate_of.get(p, "?")) for p, lab, bi in items]

    tr = with_gate(a.train_cand, a.train_samples)
    va = with_gate(a.val_cand, a.val_samples)

    # ---------- 2. 闸门基线：训练期每闸正样本率 ----------
    st = collections.defaultdict(lambda: [0, 0])
    for _, lab, _, g in tr:
        st[g][1] += 1
        if lab == 1:
            st[g][0] += 1
    gate_rate = {g: (v[0] / v[1] if v[1] else 0.0) for g, v in st.items()}
    print("【训练期各闸门正样本率】（将作为 trivial 打分基准）")
    for g, r in sorted(gate_rate.items(), key=lambda kv: -kv[1]):
        print(f"  {g:12s} 正 {st[g][0]:5d}/{st[g][1]:6d} = {r:7.2%}")

    # ---------- 推理 ----------
    ds = PatchSet([(p, lab, bi) for p, lab, bi, _g in va], a.img, ck["mean"], ck["std"])
    dl = torch.utils.data.DataLoader(ds, batch_size=a.bs, shuffle=False, num_workers=2)
    ps = []
    with torch.no_grad():
        for x, _ in dl:
            ps.append(torch.sigmoid(model(x.to(dev))).cpu().numpy())
    pv = np.concatenate(ps)
    yv = np.array([t[1] for t in va], dtype=int)
    gv = np.array([t[3] for t in va])

    print(f"\n【留出集 external】正 {int(yv.sum())} / 负 {int((1-yv).sum())}")
    print(f"  模型整体 AUROC        = {roc_auc_score(yv, pv):.4f}")
    gate_base = np.array([gate_rate.get(g, 0.0) for g in gv])
    try:
        print(f"  闸门基线 AUROC(trivial)= {roc_auc_score(yv, gate_base):.4f}  ← 模型必须显著高于它")
    except ValueError:
        print("  闸门基线 AUROC = n/a")

    print("\n【闸门内 AUROC】（同一闸门内部，才是真判别力）")
    tot_p = tot_n = 0
    for g in sorted(set(gv)):
        m = gv == g
        if m.sum() < 30 or yv[m].sum() < 5:
            print(f"  {g:12s} n={m.sum():6d} 正={int(yv[m].sum()):4d}  样本过少，跳过")
            continue
        try:
            au = roc_auc_score(yv[m], pv[m])
        except ValueError:
            au = float("nan")
        print(f"  {g:12s} n={m.sum():6d} 正={int(yv[m].sum()):4d}  AUROC={au:.4f}")
        tot_p += int(yv[m].sum()); tot_n += int((m & (yv == 0)).sum())

    # ---------- 决策 ----------
    print("\n=== 结论口径 ===")
    print("若闸门内 AUROC 仍显著 > 0.5，说明真判别力存在，值得继续；")
    print("若闸门内 AUROC 掉回 ~0.5，则整体高分只是「识别闸门」的幻觉，应停止。")


if __name__ == "__main__":
    main()
