# HalconPcbDefectDetector 架构拆分（2026-10-22）

状态：**阶段性完成，全部经逐位等价验证**。剩余部分见文末「未完成」。

## 一、为什么要拆

原 `HalconPcbDefectDetector.cs` 单文件 **2338 行**，其中 `DetectCore` 一个方法约 1750 行，
内含 24 个处理阶段与十余个局部函数。直接后果：

1. **改不动**：任何一处闸门调整都要在 1750 行里定位，且无法对单个阶段做单测。
2. **参数解析散落**：79 个 `>0 ? 用值 : 默认` 兜底表达式写在方法开头（已由 `PcbRecipeParams` 收敛）。
3. **闭包隐式耦合**：局部函数捕获 11~25 个上层局部变量，谁改了哪个变量会影响哪些阶段不可见。

## 二、拆分方式：partial class（保证零风险）

用 `partial class` 把**同一个类**拆到多个文件，调用点**一行不改**——
这保证了拆分是纯文件搬移，逐位等价由构造成立，而不是靠人工比对。

## 三、最终结构

| 文件 | 行数 | 职责 |
|------|------|------|
| `HalconPcbDefectDetector.cs` | **1642**（原 2338） | `Detect()` 入口 + `DetectCore()` 编排 + 各闸门局部函数（待继续拆） |
| `.Primitives.cs` | 259 | 图像/区域底层基元：Sobel、Compact、配准、鲁棒极值、几何量测、铜网标号（14 个 static 方法） |
| `.Rescue.cs` | 278 | 母团救援与残渣生长：`GrowResidueBox` / `RescueByMother` / `Reject` / `ToItem` |
| `.Register.cs` | 80 | **阶段 1 配准**：输入 `FrameBuffer` → 输出 `RegisterResult`（模板对齐到实测坐标系） |
| `.Grow.cs` | 209 | **阶段 6 区域生长** `GrowStage`：核心框沿弱差分连通域扩到缺陷完整足迹（含 FWHM 足迹与末端缩放） |

配套：`PcbRecipeParams.cs`（79 个配方参数的单一解析点，已接入，逐位等价）。

## 四、逐位等价证据（每步一次全量评测）

基线：`reports/sota_eval_20261021_232527`（v1.2.1，改动前）。

| 步骤 | 官方 test 500 对 · 非 KPI 字段差异 | 逐框 dump |
|---|---|---|
| ① `PcbRecipeParams` 接入（79 参数收敛） | **0 / 223** | 3 个 dump 逐字节相同 |
| ② 静态基元拆分（436 行 → Primitives + Rescue） | **0** | 逐字节相同 |
| ③ 配准阶段提取（`RegisterStage`） | **0** | 逐字节相同 |
| ④ 区域生长提取（`GrowStage`，186 行） | **0** | 逐字节相同 |

比对口径：`sota_eval.json` 扁平化后除时间戳/报告路径/工业 KPI 外的全部数值字段，
外加 `miss_dump.tsv` / `fp_dump.tsv` / `iou_near_dump.tsv` 三个逐框 dump 的字节级比对
（逐框 dump 比聚合指标更强——它覆盖每一个框的坐标、类型、置信度）。

## 五、搬移中发现的隐式耦合（记录，未改）

- `GrowStage` 原先闭包捕获 **12 个** 上层局部变量（`frame.Width/Height`、`growMargin`、
  `growMaxScale`、`growIters`、`growDiag`、`growBoxScale/Elong/Pad`、`fpTau`、`fpMargin`、`testPx`、`tempPx`），
  现已全部显式化为参数 + `PcbRecipeParams`。**这类隐式耦合正是「改不动」的根因**。
- 配准阶段原先是 5 个可变局部变量（`testPx/tempPx/rawTempPx/fracX/fracY`）加 20 行分支，
  现已收敛为「一进一出」的 `RegisterStage` + `RegisterResult`。

## 六、未完成（诚实记录）

`DetectCore` 仍有 **1642 行**，其中尚未搬移的是**强耦合的闸门阶段**：
`ProcessPosBlob`（~150 行，捕获约 25 个变量）、`ProcessNegBlob`、`TightenStretched`、
`PosType`/`NegType`、`LopFraction`、`ShrinkToCore`、`LocalSplit`、`CuTest`、`NetLabels`、
特征量测 `MeasureCandidate`（~137 行）。

**为什么没做**：这些函数捕获的是 `posCands`/`negCands`/`defects`/`rejected`/`copperRegion`/
`copperLabels`/`copperDilNeg`/`diffI`/`distToSubstrate` 等**跨阶段共享的可变状态**，
把它们变成方法必须先引入一个显式的 `PcbDetectContext` 上下文对象承载这些状态——
这是本次拆分的自然下一步，工作量大，且必须同样逐位等价验证。

建议下一轮：
1. 引入 `PcbDetectContext`（承载上述共享状态）；
2. 把 `ProcessPosBlob`/`ProcessNegBlob` 等改为上下文上的方法；
3. `MeasureCandidate` 独立为 `MeasureStage`（它已是「输入特征区域 → 输出特征行」的纯量测）。

## 七、纪律

- 每搬一步就 build + 跑一次官方 test 全量评测，确认逐位等价再继续。
- 不改任何算法逻辑、阈值、闸门顺序；不碰 `PcbTypeTree` / `PcbConfidence` / `RecipeStore` / 配方 JSON。
