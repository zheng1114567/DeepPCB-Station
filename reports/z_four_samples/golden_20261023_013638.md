# DeepPCB 金标报告（bbox→区域近似）

- 时间：2026-10-23 01:36:38
- 引擎：Halcon · 配方 pcb_open_mousebite v1.2.1 hash=`26d991859176a03ee31cc7d5b728d646`
- 对数：41（≤100）· IoU=0.33
- **结论：通过：六类漏检≤20% 且过杀≤50%**

## 口径声明

- 金标为 bbox→区域近似（GT 以轴对齐框填充满矩形；命中=Pred 中心落在框内或 IoU≥阈值），不是像素级工业封样。
- Pred TypeCode 为长宽比启发式，非可靠分类器；金标匹配忽略 Pred 分型，主看命中/过杀。
- 首期 overlay 可有噪声候选框；不得把噪声框当过杀已控。本报表过杀来自 Pred↔GT 对照。
- 过杀按类列示时采用 **全局 FP 率**（未匹配任何 open/mousebite GT 的 Pred / 全部 Pred），因分型不可靠、不按 Pred TypeCode 归类刷数。
- **禁止**把论文 ≈89% mAP 写成己方成绩。

## 按类指标（门槛：漏检≤20%，过杀≤50%）

| 类 | GT数 | 命中 | 漏检 | 漏检率 | 漏检95%CI | 过杀率(全局FP) | 漏检过门 | 过杀过门 |
|----|------|------|------|--------|-----------|----------------|----------|----------|
| open | 49 | 49 | 0 | 0.0% | 0.0%–7.3% | 0.0% | 是 | 是 |
| mousebite | 68 | 67 | 1 | 1.5% | 0.3%–7.9% | 0.0% | 是 | 是 |
| short | 35 | 34 | 1 | 2.9% | 0.5%–14.5% | 0.0% | 是 | 是 |
| spur | 50 | 50 | 0 | 0.0% | 0.0%–7.1% | 0.0% | 是 | 是 |
| copper | 59 | 59 | 0 | 0.0% | 0.0%–6.1% | 0.0% | 是 | 是 |
| pinhole | 48 | 48 | 0 | 0.0% | 0.0%–7.4% | 0.0% | 是 | 是 |

- Pred OK/NG/FAULT = 0/41/0（计分对）
- Pred blobs = 307，未匹配 FP = 0，全局 FP 率 = 0.0%（95%CI 0.0%–1.2%）
- 板级：0/41 板含噪声框（0.0%，95%CI 0.0%–8.6%）

## 相对 v0.2.0 全局 FP 对比

| 配方 | Pred blobs | FP | 全局 FP 率 |
|------|------------|----|------------|
| v0.2.0 | 182 | 70 | 38.5% |
| candidate | 307 | 0 | 0.0% |

- ΔFP率 = -38.5% · 下降？ **是**
- 全对统计。

## Warnings

- 未计算、未报告 Pred 分型准确率（非可靠分类器）。

## 逐对摘要

- `00041000: preds=10 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041001: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/1`
- `00041002: preds=7 fp=0 open hit/miss=0/0 mousebite hit/miss=0/0`
- `00041003: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041004: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041005: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `00041006: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041007: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `00041008: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041009: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041010: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041011: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `00041012: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041013: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041014: preds=10 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `00041015: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041016: preds=8 fp=0 open hit/miss=3/0 mousebite hit/miss=1/0`
- `00041017: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041018: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041019: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041020: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=4/0`
- `00041021: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041022: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041023: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041024: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041025: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `00041026: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `00041027: preds=10 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `00041028: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041029: preds=3 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `00041030: preds=10 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041031: preds=10 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `00041032: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041033: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `00041034: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `00041035: preds=9 fp=0 open hit/miss=3/0 mousebite hit/miss=2/0`
- `00041036: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041037: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `00041038: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `00041039: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `00041040: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
