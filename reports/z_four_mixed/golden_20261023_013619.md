# DeepPCB 金标报告（bbox→区域近似）

- 时间：2026-10-23 01:36:19
- 引擎：Halcon · 配方 pcb_open_mousebite v1.2.1 hash=`26d991859176a03ee31cc7d5b728d646`
- 对数：236（≤100）· IoU=0.33
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
| open | 315 | 314 | 1 | 0.3% | 0.1%–1.8% | 0.7% | 是 | 是 |
| mousebite | 300 | 291 | 9 | 3.0% | 1.6%–5.6% | 0.7% | 是 | 是 |
| short | 225 | 220 | 5 | 2.2% | 1.0%–5.1% | 0.7% | 是 | 是 |
| spur | 260 | 254 | 6 | 2.3% | 1.1%–4.9% | 0.7% | 是 | 是 |
| copper | 250 | 248 | 2 | 0.8% | 0.2%–2.9% | 0.7% | 是 | 是 |
| pinhole | 258 | 256 | 2 | 0.8% | 0.2%–2.8% | 0.7% | 是 | 是 |

- Pred OK/NG/FAULT = 0/236/0（计分对）
- Pred blobs = 1594，未匹配 FP = 11，全局 FP 率 = 0.7%（95%CI 0.4%–1.2%）
- 板级：10/236 板含噪声框（4.2%，95%CI 2.3%–7.6%）

## 相对 v0.2.0 全局 FP 对比

| 配方 | Pred blobs | FP | 全局 FP 率 |
|------|------------|----|------------|
| v0.2.0 | 182 | 70 | 38.5% |
| candidate | 1594 | 11 | 0.7% |

- ΔFP率 = -37.8% · 下降？ **是**
- 全对统计。

## Warnings

- 未计算、未报告 Pred 分型准确率（非可靠分类器）。

## 逐对摘要

- `12000001: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12000017: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12000038: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12000052: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12000068: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12000085: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12000102: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12000119: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12000136: preds=6 fp=0 open hit/miss=3/0 mousebite hit/miss=0/0`
- `12000155: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12000171: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12000187: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12000204: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12000593: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100000: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12100001: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100002: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100003: preds=6 fp=0 open hit/miss=2/1 mousebite hit/miss=1/0`
- `12100004: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100005: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100006: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100007: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100008: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100009: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100010: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100011: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100012: preds=6 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100013: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100014: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100015: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12100016: preds=8 fp=0 open hit/miss=3/0 mousebite hit/miss=1/0`
- `12100017: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/1`
- `12100018: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100019: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=3/0`
- `12100020: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100021: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100022: preds=7 fp=0 open hit/miss=3/0 mousebite hit/miss=2/0`
- `12100023: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100024: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100025: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100026: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100027: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100028: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12100029: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12100030: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12100031: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100032: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100033: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100034: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100035: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100036: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100037: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100038: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100039: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100040: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100041: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100042: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12100043: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100044: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12100045: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100046: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100047: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100048: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `12100049: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100050: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100051: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100052: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100053: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100054: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `12100055: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100056: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100057: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12100058: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100059: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100060: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12100061: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100062: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12100063: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100064: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100065: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100121: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12100122: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100123: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12100124: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12100125: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12100126: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100127: preds=3 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100128: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12100129: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300060: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12300061: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12300062: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300063: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300064: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300065: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300066: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300067: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300068: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300069: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `12300070: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300071: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300072: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300073: preds=7 fp=0 open hit/miss=3/0 mousebite hit/miss=0/0`
- `12300074: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300075: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300076: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300077: preds=3 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12300108: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300109: preds=9 fp=0 open hit/miss=3/0 mousebite hit/miss=3/0`
- `12300110: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300112: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300113: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300114: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12300115: preds=7 fp=1 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12300116: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300117: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300118: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300119: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300120: preds=9 fp=0 open hit/miss=3/0 mousebite hit/miss=2/0`
- `12300121: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300122: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300123: preds=7 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300124: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300125: preds=4 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300126: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300127: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=2/0`
- `12300128: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300129: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300130: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300131: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300132: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12300133: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300134: preds=5 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300135: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300136: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300137: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300138: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300139: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300140: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300141: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300142: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300144: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300145: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300146: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300147: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `12300148: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300149: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300150: preds=9 fp=0 open hit/miss=3/0 mousebite hit/miss=2/0`
- `12300151: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300152: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300153: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300154: preds=8 fp=1 open hit/miss=2/0 mousebite hit/miss=1/0`
- `12300155: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300156: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `12300216: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `12300217: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=1/1`
- `12300218: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=3/0`
- `12300219: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300220: preds=5 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300221: preds=7 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300222: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `12300223: preds=6 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `12300224: preds=5 fp=1 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000000: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000001: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000002: preds=10 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `13000003: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000004: preds=10 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `13000005: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000006: preds=7 fp=0 open hit/miss=0/0 mousebite hit/miss=3/0`
- `13000007: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000008: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=0/1`
- `13000009: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000010: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000011: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000012: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/1`
- `13000013: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000014: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000015: preds=7 fp=0 open hit/miss=0/0 mousebite hit/miss=2/0`
- `13000016: preds=4 fp=0 open hit/miss=0/0 mousebite hit/miss=1/1`
- `13000017: preds=11 fp=2 open hit/miss=2/0 mousebite hit/miss=1/1`
- `13000018: preds=9 fp=0 open hit/miss=0/0 mousebite hit/miss=4/0`
- `13000019: preds=13 fp=0 open hit/miss=3/0 mousebite hit/miss=2/0`
- `13000020: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=3/0`
- `13000021: preds=9 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000022: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000023: preds=11 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000024: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000025: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000026: preds=11 fp=0 open hit/miss=2/0 mousebite hit/miss=1/1`
- `13000027: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000028: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000029: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000030: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000031: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000033: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=3/0`
- `13000034: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/1`
- `13000035: preds=9 fp=1 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000036: preds=6 fp=0 open hit/miss=0/0 mousebite hit/miss=2/0`
- `13000037: preds=12 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000038: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000039: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000040: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000041: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000042: preds=4 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `13000043: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000044: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000045: preds=6 fp=0 open hit/miss=2/0 mousebite hit/miss=0/0`
- `13000046: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000047: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000048: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000049: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000050: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000051: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000052: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=4/0`
- `13000053: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000054: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `13000055: preds=5 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `13000056: preds=8 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000057: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000058: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=2/1`
- `13000059: preds=9 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000060: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=3/0`
- `13000061: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=2/0`
- `13000062: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000064: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000065: preds=7 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000066: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000067: preds=10 fp=0 open hit/miss=2/0 mousebite hit/miss=2/0`
- `13000068: preds=8 fp=0 open hit/miss=2/0 mousebite hit/miss=1/0`
- `13000069: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000070: preds=5 fp=0 open hit/miss=0/0 mousebite hit/miss=1/0`
- `13000071: preds=6 fp=0 open hit/miss=0/0 mousebite hit/miss=2/0`
- `13000072: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=0/0`
- `13000073: preds=6 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
- `13000074: preds=7 fp=0 open hit/miss=1/0 mousebite hit/miss=1/0`
