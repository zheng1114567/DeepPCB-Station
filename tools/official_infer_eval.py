#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
官方 test 集完整推理评测（最终验收，只读，绝不用于选阈值）。

纪律
----
data/official_test 是最终验收集。本脚本**不接受任何阈值参数用于决策**，
只报告现状数字：整体/闸门内 AUROC、以及「若在 external 上定的阈值直接搬过来」
的精确率/召回率。阈值选择一律在 external 上做。

用法
----
python tools/official_infer_eval.py --ckpt tools/gate_rescue_best.pt --thr 0.9
"""
import argparse, csv, os, collections

import numpy as np
import torch
from sklearn.metrics import roc_auc_score

from train_gate_rescue import build_index, PatchSet, SmallCNN, ROOT


def with_gate_and_pair(cand_dir, samples_dir):
    items = build_index(os.path.join(ROOT, cand_dir), os.path.join(ROOT, samples_dir))
    rows = list(csv.DictReader(open(os.path.join(ROOT, cand_dir, "manifest.csv"), encoding="utf-8")))
    gate_of = {}
    for r in rows:
        if r["source"].startswith("rejected"):
            gate_of[os.path.join(ROOT, cand_dir, r["file"])] = r["source"].split(":", 1)[1]
    return [(p, lab, bi, gate_of.get(p, "?")) for p, lab, bi in items]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cand", default="data/candidates_official_test")
    ap.add_argument("--samples", default="data/official_test")
    ap.add_argument("--ckpt", default="tools/gate_rescue_best.pt")
    ap.add_argument("--thr", type=float, default=0.9,
                    help="阈值须来自 external 评测，此处仅照搬，不在官方集上选")
    ap.add_argument("--img", type=int, default=64)
    ap.add_argument("--bs", type=int, default=256)
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ck = torch.load(os.path.join(ROOT, a.ckpt), map_location=dev, weights_only=False)
    model = SmallCNN().to(dev)
    model.load_state_dict(ck["model"])
    model.eval()

    items = with_gate_and_pair(a.cand, a.samples)
    y = np.array([t[1] for t in items])
    g = np.array([t[3] for t in items])
    ds = PatchSet([(t[0], t[1], t[2]) for t in items], a.img, ck["mean"], ck["std"])
    dl = torch.utils.data.DataLoader(ds, batch_size=a.bs, shuffle=False, num_workers=4)
    ps = []
    with torch.no_grad():
        for x, _ in dl:
            ps.append(torch.sigmoid(model(x.to(dev))).cpu().numpy())
    s = np.concatenate(ps)

    print(f"【官方 test 500 对】被闸门拒的候选 正 {int(y.sum())} / 负 {int((1-y).sum())}")
    print(f"  整体 AUROC = {roc_auc_score(y, s):.4f}")

    st = collections.defaultdict(lambda: [0, 0])
    for lab, gg in zip(y, g):
        st[gg][1] += 1
        if lab:
            st[gg][0] += 1
    gb = np.array([(st[gg][0] / st[gg][1] if st[gg][1] else 0.0) for gg in g])
    print(f"  闸门基线 AUROC = {roc_auc_score(y, gb):.4f}")

    print("  闸门内 AUROC:")
    for gg in sorted(set(g)):
        m = g == gg
        if m.sum() < 30 or y[m].sum() < 5:
            print(f"    {gg:12s} n={m.sum():6d} 正={int(y[m].sum()):4d}  过少")
            continue
        print(f"    {gg:12s} n={m.sum():6d} 正={int(y[m].sum()):4d}  "
              f"AUROC={roc_auc_score(y[m], s[m]):.4f}")

    print(f"\n  照搬 external 阈值 {a.thr}:")
    sel = s >= a.thr
    print(f"    选中 {int(sel.sum()):5d} / {len(s)}  其中真碎片 {int(y[sel].sum()):4d}"
          f"  精确率 {y[sel].mean() if sel.sum() else 0:.3%}"
          f"  召回 {y[sel].sum()/max(y.sum(),1):.2%}")


if __name__ == "__main__":
    main()