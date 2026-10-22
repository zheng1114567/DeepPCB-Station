#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fit_v14：置信度（排序）杠杆验证 + 严格 mAP11 天花板分解。

输入：v1.2 版 dump（含 splitT/splitC/splitB、oppRatio、conf、matchIou 列）。
A. 复刻校验：Python 复刻 S4 分数 vs dump conf 列；复刻 v1.2.0 树 vs train1000 混淆矩阵。
B. 天花板分解（严格 mAP11，逐类）：现状 / 排序神谕 / 分型神谕 / 双神谕。
   FN 取 reports/v120_train1000.log 实测（open84 short59 mb186 spur53 copper31 pinhole22）。
C. 置信度 bake-off（5 折 CV AUC，标签=gtType>0）：
   S4 基线 / split 四变体 / +oppRatio / +交互项 / +类条件交互 / 分箱 GAM。
D. 类内 AUC（严格 mAP 真正关心的排序）：优胜模型逐类。
E. 未命中池（FP）画像。
"""
import math
import random
import sys
import glob
import os
from collections import Counter

import numpy as np

NAMES = {1: "OPEN", 2: "SHORT", 3: "MOUSEBITE", 4: "SPUR", 5: "COPPER", 6: "PINHOLE"}

FN_LOG = {"OPEN": 84, "SHORT": 59, "MOUSEBITE": 186, "SPUR": 53, "COPPER": 31, "PINHOLE": 22}
AP11_LOG = {"OPEN": 87.8, "SHORT": 89.8, "MOUSEBITE": 77.1, "SPUR": 86.6, "COPPER": 90.5, "PINHOLE": 90.1}
CONF_LOG = {
    "OPEN":      {"OPEN": 1199, "MOUSEBITE": 50, "SPUR": 1, "PINHOLE": 5},
    "SHORT":     {"SHORT": 969, "SPUR": 18, "COPPER": 13},
    "MOUSEBITE": {"OPEN": 49, "MOUSEBITE": 1193, "SPUR": 1, "PINHOLE": 20},
    "SPUR":      {"SHORT": 12, "SPUR": 1089, "COPPER": 2},
    "COPPER":    {"SPUR": 22, "COPPER": 979},
    "PINHOLE":   {"OPEN": 2, "MOUSEBITE": 13, "PINHOLE": 1009},
}

S4W = {
    "b": -0.0165, "lnArea": 0.5795, "lnMean": -0.0246, "edge": -0.2802, "fill": -0.1515,
    "lnAspect": -0.5262, "surround": 0.2054, "bite": 0.0845, "lnLocalW": 0.6834,
    "lnPeak": -0.0864, "parts": -0.3357, "split": -0.0043, "away": 0.3628,
    "severed": -0.2894, "nets": -0.8624, "lnDepth": 0.7073, "splitN": -0.2063,
    "lateral": 0.3245,
}

OPEN_SPLIT, MB_SPLIT, SHORT_SPLIT = 0.4747, 0.1655, 0.1794
OPEN_BITE, THIN_LN_W, DB_SURROUND = 0.6377, 2.7058, 0.8174
PINHOLE_SURROUND, LATERAL_MB, CU_LOCAL_W = 0.9725, 3.1548, 2.83


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


def clamp01(v):
    return 0.0 if v < 0 else (1.0 if v > 1 else v)


def s4_feats(r, split_src="split"):
    return {
        "lnArea": math.log(max(1.0, fnum(r, "area"))),
        "lnMean": math.log(max(1.0, fnum(r, "mean"))),
        "edge": clamp01(fnum(r, "edgeFrac")),
        "fill": clamp01(fnum(r, "fill")),
        "lnAspect": math.log(max(1.0, fnum(r, "aspect"))),
        "surround": clamp01(fnum(r, "surround")),
        "bite": fnum(r, "bite"),
        "lnLocalW": math.log(max(1.0, fnum(r, "localW"))),
        "lnPeak": math.log(max(1.0, fnum(r, "peak"))),
        "parts": fnum(r, "parts"),
        "split": fnum(r, split_src),
        "away": fnum(r, "away"),
        "severed": fnum(r, "severed"),
        "nets": fnum(r, "nets"),
        "lnDepth": math.log(max(1.0, fnum(r, "depth"))),
        "splitN": fnum(r, "splitN"),
        "lateral": fnum(r, "lateral"),
    }


def s4_score(r):
    f = s4_feats(r)
    z = S4W["b"] + sum(S4W[k] * f[k] for k in f)
    return 1.0 / (1.0 + math.exp(-max(-30.0, min(30.0, z))))


def deployed_tree(r):
    sp = fnum(r, "split")
    if fnum(r, "surround") > PINHOLE_SURROUND:
        return "PINHOLE"
    if fnum(r, "polarity") > 0:
        if sp > OPEN_SPLIT:
            return "OPEN"
        if fnum(r, "bite") > OPEN_BITE:
            if math.log(max(1.0, fnum(r, "localW"))) <= THIN_LN_W:
                return "MOUSEBITE" if fnum(r, "surround") <= DB_SURROUND else "PINHOLE"
            return "OPEN"
        if sp <= MB_SPLIT:
            return "MOUSEBITE"
        return "MOUSEBITE" if fnum(r, "lateral") <= LATERAL_MB else "OPEN"
    if fnum(r, "localW") <= CU_LOCAL_W:
        return "COPPER"
    if fnum(r, "parts") <= 1.5:
        return "SPUR"
    if sp <= SHORT_SPLIT:
        return "SHORT"
    return "SPUR"


# ---------- numpy logistic (IRLS + ridge，截距不正则) ----------
def fit_logistic(X, y, l2=1e-3, iters=30):
    n, d = X.shape
    w = np.zeros(d)
    R = l2 * n * np.eye(d)
    R[0, 0] = 0.0
    for _ in range(iters):
        z = np.clip(X @ w, -30, 30)
        p = 1.0 / (1.0 + np.exp(-z))
        g = X.T @ (p - y) + R @ w
        W = np.clip(p * (1 - p), 1e-6, None)
        H = X.T @ (X * W[:, None]) + R
        try:
            step = np.linalg.solve(H, g)
        except np.linalg.LinAlgError:
            break
        w_new = w - step
        if np.max(np.abs(w_new - w)) < 1e-9:
            w = w_new
            break
        w = w_new
    return w


def auc(y, s):
    y = np.asarray(y, dtype=float)
    s = np.asarray(s, dtype=float)
    order = np.argsort(s, kind="stable")
    ys = y[order]
    ss = s[order]
    ranks = np.empty(len(ss), dtype=float)
    i = 0
    while i < len(ss):
        j = i
        while j + 1 < len(ss) and ss[j + 1] == ss[i]:
            j += 1
        ranks[i:j + 1] = (i + j) / 2.0 + 1.0
        i = j + 1
    npos = ys.sum()
    nneg = len(ys) - npos
    if npos == 0 or nneg == 0:
        return float("nan")
    return (ranks[ys == 1].sum() - npos * (npos + 1) / 2.0) / (npos * nneg)


def ap11(scores, labels, total_gt):
    scores = np.asarray(scores, dtype=float)
    labels = np.asarray(labels, dtype=float)
    order = np.argsort(-scores, kind="stable")
    lab = labels[order]
    tp = np.cumsum(lab)
    fp = np.cumsum(1.0 - lab)
    prec = tp / np.maximum(tp + fp, 1e-9)
    rec = tp / max(total_gt, 1)
    ap = 0.0
    for r in np.linspace(0.0, 1.0, 11):
        m = rec >= r - 1e-12
        ap += prec[m].max() if m.any() else 0.0
    return ap / 11.0


# ---------- 特征矩阵 ----------
CONT = ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite",
        "lnLocalW", "lnPeak", "split", "lnDepth", "lateral"]
S4_KEYS = ["lnArea", "lnMean", "edge", "fill", "lnAspect", "surround", "bite",
           "lnLocalW", "lnPeak", "parts", "split", "away", "severed", "nets",
           "lnDepth", "splitN", "lateral"]
CLS_INTERACT = ["lnLocalW", "nets", "surround", "lnArea", "edge"]
CLS_DUMMIES = ["c_open", "c_short", "c_mousebite", "c_spur", "c_copper"]


def base_feats(r, split_src="split"):
    f = s4_feats(r, split_src)
    f["oppRatio"] = max(0.0, fnum(r, "oppRatio"))
    f["splitT"] = fnum(r, "splitT")
    f["splitC"] = fnum(r, "splitC")
    f["splitB"] = fnum(r, "splitB")
    f["lnArea*lnLocalW"] = f["lnArea"] * f["lnLocalW"]
    f["bite*lnLocalW"] = f["bite"] * f["lnLocalW"]
    f["surround*bite"] = f["surround"] * f["bite"]
    f["nets*edge"] = f["nets"] * f["edge"]
    f["lnArea^2"] = f["lnArea"] ** 2
    f["bite^2"] = f["bite"] ** 2
    return f


def design_matrix(bf, keys, preds):
    """bf: base_feats 列表；keys: 特征名（c_ 开头为类哑变量/交互）。"""
    cols = [np.ones(len(bf))]
    for k in keys:
        if k.startswith("c_"):
            continue
        cols.append(np.array([x[k] for x in bf]))
    for c in ("open", "short", "mousebite", "spur", "copper"):
        dummy = np.array([1.0 if p == c.upper() else 0.0 for p in preds])
        if f"c_{c}" in keys:
            cols.append(dummy)
        for fk in CLS_INTERACT:
            if f"c_{c}:{fk}" in keys:
                cols.append(dummy * np.array([x[fk] for x in bf]))
    return np.stack(cols, axis=1)


def design_binned(bf, edges):
    cols = [np.ones(len(bf))]
    for f in CONT:
        b = np.digitize([x[f] for x in bf], edges[f])
        for v in range(5):
            cols.append((b == v).astype(float))
    for f in ("parts", "away", "severed", "nets", "splitN"):
        cols.append(np.array([x[f] for x in bf]))
    return np.stack(cols, axis=1)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("reports/features_train3/features_*.tsv"))[-1]
    rows = load(path)
    n = len(rows)
    matched = [r for r in rows if int(fnum(r, "gtType")) > 0]
    print(f"file={os.path.basename(path)}")
    print(f"rows={n} matched={len(matched)} unmatched={n-len(matched)}")

    # ---------- A. 复刻校验 ----------
    print("\n===== A1. S4 分数复刻 vs dump conf 列 =====")
    diffs = [abs(s4_score(r) - fnum(r, "conf")) for r in rows[:2000]]
    print(f"  max|diff|={max(diffs):.6f} mean={sum(diffs)/len(diffs):.6f} (n=2000)")

    print("\n===== A2. v1.2.0 树复刻 vs train1000 混淆矩阵 =====")
    got = Counter()
    for r in matched:
        got[(NAMES[int(fnum(r, "gtType"))], deployed_tree(r))] += 1
    tot_err = 0
    for gt, row in CONF_LOG.items():
        cells = []
        for pr, cnt in row.items():
            g = got.get((gt, pr), 0)
            tot_err += abs(g - cnt)
            cells.append(f"{gt}->{pr}: log={cnt} got={g}")
        print("  " + " | ".join(cells))
    print(f"  总偏差单元格={tot_err}（0=逐位一致）")

    # 全量预测（树复刻，含未命中行）
    pred = np.array([deployed_tree(r) for r in rows])
    gt = np.array([NAMES.get(int(fnum(r, "gtType")), "") for r in rows])
    s4_all = np.array([s4_score(r) for r in rows])
    y = np.array([1.0 if int(fnum(r, "gtType")) > 0 else 0.0 for r in rows])

    # ---------- B. 天花板分解 ----------
    print("\n===== B. 严格 mAP11 天花板分解（FN 取 train1000 实测日志） =====")
    print(f"  {'类':10s} {'现状':>7s} {'日志':>7s} {'排序神谕':>8s} {'分型神谕':>8s} {'双神谕':>8s} {'GT':>5s} {'TP':>5s} {'FN':>4s} {'FP':>4s}")
    aps = {"cur": [], "rank": [], "cls": [], "both": []}
    for c in NAMES.values():
        in_pool = (pred == c) | (gt == c)          # 现状池=pred==c；神谕池额外含 matched&gt==c
        cur_pool = pred == c
        lab_cur = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred)])
        lab_cur = lab_cur[cur_pool]
        sc_cur = s4_all[cur_pool]
        tp_c = int(lab_cur.sum())
        fp_c = int((1 - lab_cur).sum())
        fn_c = FN_LOG[c]
        total_gt = tp_c + fn_c
        cur = ap11(sc_cur, lab_cur, total_gt)
        order = np.argsort(-lab_cur, kind="stable")
        orank = ap11(lab_cur[order], lab_cur[order], total_gt)
        # 分型神谕池：pred==c 的行 + matched 且 gt==c 的行（去重）
        lab_o = np.array([1.0 if g == c else 0.0 for g, p in zip(gt, pred) if p == c or g == c])
        sc_o = np.array([s for s, g, p in zip(s4_all, gt, pred) if p == c or g == c])
        ocls = ap11(sc_o, lab_o, total_gt)
        order2 = np.argsort(-lab_o, kind="stable")
        oboth = ap11(lab_o[order2], lab_o[order2], total_gt)
        aps["cur"].append(cur)
        aps["rank"].append(orank)
        aps["cls"].append(ocls)
        aps["both"].append(oboth)
        print(f"  {c:10s} {cur*100:6.1f}% {AP11_LOG[c]:6.1f}% {orank*100:7.1f}% {ocls*100:7.1f}% {oboth*100:7.1f}% {total_gt:5d} {tp_c:5d} {fn_c:4d} {fp_c:4d}")
    print(f"  {'mAP11':10s} {100*sum(aps['cur'])/6:6.1f}% {87.0:6.1f}% {100*sum(aps['rank'])/6:7.1f}% {100*sum(aps['cls'])/6:7.1f}% {100*sum(aps['both'])/6:7.1f}%")

    # ---------- C. bake-off ----------
    print("\n===== C. 置信度 bake-off（5 折 CV AUC，标签=gtType>0） =====")
    idx = list(range(n))
    random.Random(7).shuffle(idx)
    folds = [set(idx[k::5]) for k in range(5)]

    fold_aucs = []
    for te in folds:
        te_i = [i for i in idx if i in te]
        fold_aucs.append(auc(y[te_i], s4_all[te_i]))
    print(f"  {'S4线上(固定权重)':22s} cvAUC={sum(fold_aucs)/5:.4f} folds={[round(a,3) for a in fold_aucs]}")

    def cv_auc(keys):
        accs = []
        for te in folds:
            tr_i = [i for i in idx if i not in te]
            te_i = [i for i in idx if i in te]
            if keys == "BIN":
                bf_tr = [base_feats(rows[i]) for i in tr_i]
                edges = {f: np.quantile([x[f] for x in bf_tr], [0.2, 0.4, 0.6, 0.8]).tolist() for f in CONT}
                Xtr = design_binned(bf_tr, edges)
                Xte = design_binned([base_feats(rows[i]) for i in te_i], edges)
            else:
                Xtr = design_matrix([base_feats(rows[i]) for i in tr_i], keys, [pred[i] for i in tr_i])
                Xte = design_matrix([base_feats(rows[i]) for i in te_i], keys, [pred[i] for i in te_i])
            w = fit_logistic(Xtr, y[tr_i])
            accs.append(auc(y[te_i], Xte @ w))
        return sum(accs) / 5, accs

    cls_set = S4_KEYS + CLS_DUMMIES + [f"c_{c}:{f}" for c in ("open", "short", "mousebite", "spur", "copper") for f in CLS_INTERACT]
    SETS = {
        "S4(17)": S4_KEYS,
        "S4_T": [k if k != "split" else "splitT" for k in S4_KEYS],
        "S4_C": [k if k != "split" else "splitC" for k in S4_KEYS],
        "S4_B": [k if k != "split" else "splitB" for k in S4_KEYS],
        "S4+opp": S4_KEYS + ["oppRatio"],
        "S4+inter": S4_KEYS + ["lnArea*lnLocalW", "bite*lnLocalW", "surround*bite", "nets*edge", "lnArea^2", "bite^2"],
        "S4+inter+opp": S4_KEYS + ["oppRatio", "lnArea*lnLocalW", "bite*lnLocalW", "surround*bite", "nets*edge", "lnArea^2", "bite^2"],
        "S4+cls(42)": cls_set,
    }
    bf_all = [base_feats(r) for r in rows]
    for name, keys in SETS.items():
        m, accs = cv_auc(keys)
        X = design_matrix(bf_all, keys, list(pred))
        w = fit_logistic(X, y)
        print(f"  {name:22s} cvAUC={m:.4f} folds={[round(a,3) for a in accs]} trainAUC={auc(y, X@w):.4f}")
    m, accs = cv_auc("BIN")
    edges_all = {f: np.quantile([x[f] for x in bf_all], [0.2, 0.4, 0.6, 0.8]).tolist() for f in CONT}
    Xb = design_binned(bf_all, edges_all)
    wb = fit_logistic(Xb, y)
    print(f"  {'BIN(53)':22s} cvAUC={m:.4f} folds={[round(a,3) for a in accs]} trainAUC={auc(y, Xb@wb):.4f}")

    # ---------- D. 类内 AUC ----------
    print("\n===== D. 类内 AUC（括号=线上 S4 固定权重） =====")
    for name in ("S4(17)", "S4+inter+opp", "S4+cls(42)"):
        keys = SETS[name]
        X = design_matrix(bf_all, keys, list(pred))
        w = fit_logistic(X, y)
        sc = X @ w
        cells = []
        for c in NAMES.values():
            pool = pred == c
            lb = np.array([1.0 if (g == c and p == c) else 0.0 for g, p in zip(gt, pred)])
            cells.append(f"{c[:4]}:{auc(lb[pool], sc[pool]):.3f}({auc(lb[pool], s4_all[pool]):.3f})")
        print(f"  {name:14s} " + " ".join(cells))

    # ---------- E. FP 池画像 ----------
    print("\n===== E. 未命中池（FP）画像 vs 命中池 =====")
    fp_rows = [r for r in rows if int(fnum(r, "gtType")) == 0]
    for k in ("oppRatio", "edgeFrac", "nets", "away", "surround", "split", "splitT", "splitC", "splitB"):
        fv = [fnum(r, k) for r in fp_rows]
        mv = [fnum(r, k) for r in matched]
        print(f"  {k:8s} FP mean={sum(fv)/len(fv):.3f} med={sorted(fv)[len(fv)//2]:.3f} | TP mean={sum(mv)/len(mv):.3f} med={sorted(mv)[len(mv)//2]:.3f}")
    order = np.argsort(-s4_all)
    rank_of_fp = [i for i in order if y[i] == 0]
    top10 = sum(1 for r in rank_of_fp if r < n * 0.1)
    top20 = sum(1 for r in rank_of_fp if r < n * 0.2)
    print(f"  FP 落在 S4 排序前10%: {top10}/{len(rank_of_fp)}，前20%: {top20}/{len(rank_of_fp)}")


if __name__ == "__main__":
    main()
