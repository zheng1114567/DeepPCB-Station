# DL 分型分类器（离线验证 + ONNX 在线集成）

日期：2026-10-22　状态：**离线验证通过；ONNX 在线旁路已跑通，默认关闭**

> 本文只记录「DL 替代规则分型树」这一件事。检出、框几何、置信度排序、配方 hash 全部不动。

---

## 一、结论

在官方 test 500 对上，用 ResNet18 对已检出的候选做六分类，替换规则树输出的类别，
**严格分型 F 94.37% → 95.80%（+1.43pp）**，严格 mAP 全点 91.3% → 93.7%，
**检出口径逐位不变**（TP 3,033 / F 97.0% / mAP11 90.2%）。

| 指标 | 规则分型树（v1.2.1） | DL 分型 | Δ |
|---|---|---|---|
| 严格分型 TP | 2,952 | **2,997** | **+45** |
| 严格分型 F | 94.37% | **95.80%** | **+1.43pp** |
| 严格分型 mAP（全点） | 91.3% | **93.7%** | +2.4pp |
| 严格分型 mAP（11 点） | 88.2% | **89.2%** | +1.0pp |
| 检出 TP（忽略分型） | 3,033 | 3,033 | 0 |
| 检出 F | 97.0% | 97.0% | 0 |

证据：`reports/type_clf/dl/sota_eval.json`（离线覆盖版）、`reports/type_clf/onnx2/sota_eval.json`（**端到端 ONNX 版，逐位一致**）、基线 `reports/sota_eval_20261022_041809/`。

> 上表已由两种独立路径复现：① 离线 `--type-override`（替换已导出的 pred 类别）；
> ② 在线 `DPCB_DL_TYPE` 环境变量（检测器内部实时裁 patch + ONNX 推理）。
> 两者严格 TP 均为 2,997，证明在线 patch 生成与训练完全一致。

> 评测由 `PcbIcdarEvaluator` 原样执行（IoU≥0.33、同图、同类型、一对一贪心），
> 仅通过 `--type-override` 替换 pred 类别，**不是"预测对了几类"的模拟值**。
> 检出侧零变化，反证只动了分型。

---

## 二、为什么是分型，不是检出/复判

| 位置 | 已有结论 | 判断 |
|---|---|---|
| 检出（召回/框几何） | 三杠杆全部封顶（C1/D1/E1、工作点、GrowBox 系列） | 不做 |
| DL 救援（救回被拒残渣） | 已证伪：几何上限 45.3%、净收益全网格为负、跨板精确率崩 5 倍 | 不做 |
| 复判（已上报 TP vs FP） | 本轮实测：CNN AUROC 0.9031，17 特征逻辑回归 0.8975，单特征 B_mean 在 99.5% 召回点更好 | **无增量，不做** |
| **分型（六分类）** | 规则树 2,952/3,033 = 97.3%，已证特征池封顶 | **有空间，做** |

关键教训：`3,033 TP + 13,323 rejected` 上 AUROC 0.999 是假象——那个任务只是「真缺陷 vs 被闸门拒的弱信号」，
对复判没有意义。只有 `TP vs 已上报 FP` 才是真实场景，而那里传统特征已经吃满。

---

## 三、方法

| 项 | 设置 |
|---|---|
| 输入 | 候选 patch 三通道：R=实测 / G=模板 / B=\|diff\|×2（与 `PcbCandidateExporter` 一致） |
| 预处理 | letterbox 到 96×96（保持长宽比，居中，黑边填充） |
| 模型 | ResNet18（ImageNet 预训练），fc 改 6 类 |
| 训练 | AdamW lr=3e-4，wd=1e-4，bs=64，12 epoch，余弦退火；翻转增强 |
| 训练集 | `data/candidates_train` 的 defect/：6,646 个，**1,000 板** |
| 留出集 | `data/candidates_official_test` 的 defect/：3,033 个，**500 板** |
| 板级隔离 | 两组 pair **零重叠**（已核：1000 vs 500，交集 0） |
| 模型选择 | val（训练集内按板 80/20）准确率 98.96%，**official_test 未参与选模** |

六分类准确率（official_test 3,033 个）：**98.75%**
分类别：open 98.6% / short 99.1% / mousebite 98.4% / spur 97.5% / copper 99.4% / pin-hole 99.8%
（规则树在同样 3,033 个上是 97.3%）

---

## 四、边界与风险（写简历前必读）

1. **已实现在线旁路，但默认关闭**：`DlTypeClassifier` 在检测器内部实时裁 patch + ONNX 推理，
   由环境变量 `DPCB_DL_TYPE=<onnx 路径>` 启用；不设该变量时 `_dlType=null`，生产路径与 v1.2.1 逐位一致。
   代价约 **+22 秒 / 500 对**（3,116 次 CPU 推理，约 7ms/候选）。
2. **依赖 patch 参数一致**：`margin` 与 `diffGain` 必须与训练时相同，否则输入分布漂移。
3. **单数据源**：只在 DeepPCB（线性扫描 CCD、预对齐、8 位）上验证过，换板型/换相机需重训。
4. **形状先验**：六类框的宽高比差异明显，模型可能部分依赖形状而非纹理；换板型时需专项复核。
5. **可解释性下降**：规则树能给「铜被切断 / 连通块分裂」的依据，CNN 只能给类别与概率。
   若客户要求逐条归因，仍需保留规则树输出作为解释旁路。

---

## 五、复现

```powershell
# 1. 训练（缓存首次约 84s，之后每 epoch ~13s）
C:\Users\Administrator\miniconda3\python.exe -u tools\train_type_classifier.py --epochs 12

# 2. 生成 pred 类别覆盖表（3,116 条）
C:\Users\Administrator\miniconda3\python.exe -u tools\make_type_override.py

# 3. 用官方评测器跑真实口径（只换类别，不碰检出）
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- `
  --sota-eval --samples=data/official_test --reports=reports/type_clf/dl `
  --type-override=reports/type_clf/override_official.json
```

`--type-override` 为离线验证入口：不传时 `TypeOverride=null`，生产路径与既有基线逐位不变。

---

## 六、在线集成（ONNX）与踩坑

### 实现

| 项 | 内容 |
|---|---|
| 模型导出 | `tools/export_type_onnx.py` → `models/type_clf.onnx`（43.6 MB，opset 11） |
| 推理后端 | `Microsoft.ML.OnnxRuntime 1.16.3`（net48 兼容） |
| 接入类 | `Core/Inference/DlTypeClassifier.cs` |
| 挂载点 | `HalconPcbDefectDetector.Detect`：`DetectCore` 返回后 `_dlType?.Apply(frame, defects)` |
| 启用方式 | 环境变量 `DPCB_DL_TYPE=<绝对路径>`；不设即关闭（`_dlType=null`） |
| 作用范围 | 只赋值 `DefectItem.TypeCode`，框/检出/置信度/闸门全不动 |

数值一致性：`max|ONNX - PyTorch| = 7.7e-7`，argmax 完全一致。

### 踩坑：通道顺序错位导致严格 F 从 95.8% 崩到 51.3%

第一版在线实现直接按数组顺序取通道：

```
bgr[0]=|diff|  bgr[1]=模板  bgr[2]=实测   ← 导出数组的真实布局
```

但训练侧是 PIL 读 PNG 后转 RGB，通道语义为 **0=实测、1=模板、2=|diff|**。
按数组顺序喂给模型，等于把三个通道整体错位，严格 TP 从 2,997 掉到 1,604（F 51.3%）。

**教训**：跨语言迁移图像管线时，"保存时的数组布局"和"读取时的通道语义"必须分开核对。
最省事的验证方法：打印一张 patch 的三通道均值——实测≈172、模板≈157、|diff|≈17，
对不上就是错位。修正后端到端与离线覆盖逐位一致（2,997）。

### 性能

| 项 | 数值 |
|---|---|
| 单候选推理 | 约 7 ms（CPU，ResNet18 96×96，含 patch 合成与 letterbox） |
| 500 对总增量 | 约 +22 s（3,116 次推理） |
| 对单检 400ms 的影响 | +约 44 ms/对（含 6 个候选的板） |
