# v1.2.2 候选 B：残渣生长闸（ResidueGrowMaxDim）— 设计与实现

日期：2026-10-21
起因：用户指示攻「架构缺陷」——`selectMin = Math.Min(minOpen, minMb)` 无法真正按类分设面积门限
状态：**代码已实现并编译通过（默认关闭、零回归）；train1000 扫参与验收结果见`reports/residue_grow_sweep.log` 与本文末「验收结论」**

---

## 一、起点：原「架构缺陷」判断需要修正

上一轮（v1.2.2候选 A 的诊断）提出两条可修缺口：
1. LOP 复核闸设计假设在小面积浅咬上失效 → 已被 `lopDepthSkip` 实验证伪（过拟合）
2. **`selectMin` 架构缺陷**：`MinBlobAreaOpen`/`MinBlobAreaMousebite` 取`Math.Min` 做全局筛选，无法按类分设 → 本轮目标

**动手前先查代码，发现第2 条的实际约束比预想更硬**：`RescueByMother`（L2016）的历史注释已明确记录过一次失败实测：

> 救援确实在触发（100 对中 53 板、每板 1~12 个残渣），但漏检数恒定不变（300 对恒为 27）。
> 根因：area<min 的 36 个漏检，其 GT 框 25~34px 而开运算残渣仅 6~21px，
> 放行后面积仍远小于 GT，MatchOneToOne 匹配不上 —— **缺的是「形态」不是「面积」**。

即：**单纯按类放宽面积门限救不回这批缺陷**，因为残渣（面积 6~21）远小于 GT 框（面积中位 756），匹配谓词 `IoU≥0.33` 根本不成立。母团救援代码存在但默认关闭，原因是它单独用无效。

**这改变了方案**：要救这批，必须同时补上「把残渣长回缺陷足迹」这一步，让面积与形态都对上。这与母团救援是同一件事的两半，历史上从未一起跑过。

---

## 二、离线预判：先证明「生长 + 闸门」在原理上可行

**方法论**：本次改动触及检测主线（残渣放行逻辑），按项目纪律须先有票+ 反驳过审。故先做**纯离线仿真**（Python 复刻配准 + 开运算 + 生长），不碰 C# 就能判死活，避免白跑。

脚本 `tools/residue_grow_sim.py`，输入为 `PcbMissAuditor` 归因出的 train1000 的 `area<min` + `开运算抹掉` 共 27 例。

**仿真口径**（与现网逐位对齐）：
- 配准：复刻 `TileShiftRegistration.Align`（8×8 分块整数 XOR + 增益闸）
- 开运算：3×3 方形腐蚀后膨胀（r=1），逐位对应 `OpeningCircle`
- 生长：残渣包围盒为种子，沿 `|test − alignedTemp| ≥ 1` 的像素洪泛（8 邻接），margin 窗外扩，`growMaxScale` 硬上限 —— 与 `GrowBoxesToRegion`（regionGrowDiff=1, 8 邻接）同口径
- 判据：生长后包围盒 vs GT 的 IoU ≥ 0.33（官方口径阈值）

### 关键结果

| 量测项 | 值 |
|---|---|
| 残渣原IoU 中位 | **0.017**（几乎必漏检，与历史结论一致） |
| 生长后 IoU 中位 | 0.11 ~ 0.56（随margin 增大） |

生长确实大幅改善（0.017 → 0.11，6.5×，margin=12 时），说明「把残渣长回足迹」这个方向**原理上成立**。但margin=12 时只有 3/27 命中，不够。扫更大 margin：

| margin / cap / boxScale | IoU≥0.33 命中 | IoU 中位 |
|---|---|---|
| 12 / 6 / 1.5 | 3/27 | 0.107 |
| 24 / 10 / 2.0 | 4/27 | 0.179 |
| 32 / 12 / 2.5 | 11/27 | 0.265 |
| 40 / 16 / 3.0 | 20/27 | 0.368 |
| 48 / 20 / 3.5 | **22/27** | 0.483 |
| 64 / 24 / 4.0 | 21/27 | 0.558 |

**注意这里的陷阱**：命中随 margin 单调上升，看起来「margin 越大越好」。但这**很可能是过拟合信号**——放大框来赌覆盖，代价必然是FP 和对既有TP 的挤压。不能凭离线单指标选参。

### 找零代价判据（真正的收获）

逐例拆解发现，失败案例有清晰的几何特征：

| 类别 | n | 生长框/GT 面积比中位 | 生长框 maxDim |
|---|---|---|---|
| HIT（IoU≥0.33） | 21 | **0.68** | 16~48（全部 ≤48） |
| MISS（IoU<0.33） | 6 | **3.50** | 68、68、68、72、96、120 |

**分离判据 = 生长后包围盒的最大边**：

| 闸门阈值 | 保留 | 命中 | 精确率 | 召回 |
|---|---|---|---|---|
| maxDim ≤ 40 | 20/27 | 20 | **100%** | 74.1% |
| **maxDim ≤ 50** | **21/27** | **21** | **100%** | **77.8%** |
| maxDim ≤ 60 | 21/27 | 21 | 100% | 77.8% |
| maxDim ≤ 80 | 24/27 | 21 | 87.5% | 77.8% |

**maxDim ≤ 50 时精确率 100%（21/21）、零误放**，且召回 77.8%。失败的 6 例 maxDim 均 ≥68（差分连通域洪泛失控成大片，如 20×120、68×68）。

> 附带发现：另一个直觉指标「紧致度」（生长区域面积/包围盒面积）在此**失效**——所有案例 fill 都在 0.01~0.05（生长区域本身是稀疏差分像素），无论成败。**只有 maxDim 有分离度。**

**这正是「零代价判据」**：限制生长框尺寸，既救回漏检，又不引入任何 FP。所以 `ResidueGrowMaxDim` 必须带闸门实现，且离线不能只报「margin 越大越好」——那会是过拟合的假信号。

---

## 三、C# 实现

### 3.1 新增配方字段

`AppConfig.ResidueGrowMaxDim`（0=关），按项目「扩展配方字段四必改点」全部落地：

| 必改点 | 位置 | 内容 |
|---|---|---|
| 1. DTO 字段 | `RecipeStore.RecipeFileDto` | `public int? ResidueGrowMaxDim` |
| 2. 加载映射 | `LoadFromDirectory` | `ResidueGrowMaxDim = doc.ResidueGrowMaxDim ?? 0` |
| 3. 保存端 | `SaveTo` | `SetIf(root, "residueGrowMaxDim", config.ResidueGrowMaxDim, 0)` |
| 4. hash payload | `ComputeHashStatic` | 条件并入 `\|residueGrowMaxDim={0}`（仅 >0 时，保持未启用配方 hash 不变） |

`MotherRescueArea`/`MotherRescueCap` 同样补齐四必改点（此前 hash 里根本没有这两个字段 → 行为开关不进哈希，是既有缺陷，本次一并修掉）。

### 3.2 生长判定`GrowResidueBox`

`HalconPcbDefectDetector.GrowResidueBox`（新方法）：

- 从残渣包围盒出发洪泛（种子=整框，对齐 `GrowBoxesToRegion` 的边框种子口径）
- 沿 `|test − alignedTemp| ≥ 1` 连通（对齐 regionGrowDiff=1）
- 8 邻接（`growDiag`），margin 窗用配方 `growMargin`
- 硬上限 `min(原框长 × growMaxScale, growMaxDim)`；**洪泛中一旦越界立即返回 null（判失控）**，避免浪费算力
- 返回生长后包围盒；`maxDim > growMaxDim` 返回 null

**关键正确性决策**：洪泛用的模板是**配准后**的 `tempPx`（不是 `rawTempPx`）——残渣本身就产生在配准坐标系里，洪泛必须同坐标系，否则 1~2px 配准残差会让弱差像素洪泛断裂。这一处若用错，生长会静默失效。

**为什么不复用 `GrowBoxesToRegion`**：它是 `DefectItem` 的后处理，跑在**全部闸门之后**；而残渣必须在 `SelectShape` 阶段就决定放行与否，属更早阶段，无法复用。

### 3.3 残渣放行改造

`RescueByMother` 的残渣收集循环：

```
if (残渣面积 < selectMin)
    if (residueGrowMaxDim > 0)
        grownBox = GrowResidueBox(残渣, ...)
        if (grownBox == null) 丢弃            // 洪泛失控
        用 [残渣框外扩 margin] ∩ 母团 作为候选区域
        面积 > maxArea 则丢弃
        加入 extras（key 用新区域，使下游 typeMin 豁免生效）
    else
        原行为（纯放行残渣，历史已证伪的路径）
```

`residueGrowMaxDim=0` 时**逐位回退到历史行为**，故默认关闭零回归（已实测：hash 与 samples 金标 307/FP0 均不变）。

---

## 四、验收纪律

**train1000（可拟合集）扫参**（`reports/residue_grow_sweep.ps1`，6 组：mra×dim 组合）→ 选出候选 → **四集旧金标零退化验证** → **official_test 终验**。

**判据**（吸取 lds4 教训，官方集/留出集为准）：
- official test 严格 mAP11 / F 不降
- 四集旧金标 FP 不上升（尤其 external FP 38基线）
- 若仅 train1000 涨、留出集 FP 升 → **判过拟合，不升配方**

---

## 五、验收结论

见 `reports/residue_grow_sweep.log` 与 `reports/residue_grow_acceptance.md`。

---

## 六、代码改动清单

| 文件 | 改动 |
|---|---|
| `src/DeepPCBStation.Core/Config/AppConfig.cs` | 新增 `ResidueGrowMaxDim`；补 `MotherRescueArea`/`MotherRescueCap` 的语义注释 |
| `src/DeepPCBStation.Core/Recipe/RecipeStore.cs` | 四必改点 ×3 字段；**修正 hash payload 占位符错位**（见下） |
| `src/DeepPCBStation.Core/Detection/HalconPcbDefectDetector.cs` | 新增 `GrowResidueBox`；改造 `RescueByMother` 残渣放行 + 调用点 |
| `tools/residue_grow_sim.py` | 新增：离线预判仿真 |
| `reports/residue_grow_sweep.ps1` | 新增：train1000 扫参 |

### 附：顺带修掉的既有缺陷

`ComputeHashStatic` 的 hash payload 里，`MotherRescueArea`/`MotherRescueCap` 曾被插入到 `string.Format` 的**参数列表中间，但格式串无对应 `{n}` 占位符**（未提交的工作树改动引入）。后果：`maxArea` 之后的所有字段（open/copperMax/surroundR/surroundMin/maxAspect/tipSurr）取值**全部错位**，hash 语义失真 → 配方变更检测（RecipeGate）在这些字段上会误判。

已修为「条件并入 payload」（仅启用时并入，未启用配方 hash 不变）。这是**行为开关必须进哈希**这条纪律的落实——之前这两个开关根本没进 hash，改它们不会触发 Gate。
---

## 七、实跑发现的两个实现级 Bug（v1.2.2 修正）

仿真与设计都基于「残渣先长回缺陷足迹，再按包围盒最大边闸门」这一口径。C# 实跑暴露出
**实现与设计不一致**的两个 bug——若不修，闸门形同虚设，评测结论全部无效。

### Bug 1：生长框被算出来却直接丢弃（生长闸完全不生效）

原实现在 `GrowResidueBox` 辛苦算出生长框后，**根本没用它**，实际发射的候选是：

```
candRect = (残渣原框 ± growMargin) ∩ 母团
```

母团可以很大（实测母团面积中位 23，但尾部可达数百），于是**实际发射框的最大边完全不受
`residueGrowMaxDim` 约束**——闸门只管了一个被丢弃的框。实测 train1000 上开启后新增的
7 个 FP，其框最大边分别达 62、47、51、62、71px（`dim=50` 的闸根本拦不住）。

**修正**：改为真正按生长框发射 `candRect = 生长框 ∩ 母团`，让 `maxDim` 闸真正生效。

### Bug 2：`GenRectangle1` 参数顺序 (row, col) 被当成 (x, y)

`GrowResidueBox` 返回的是 `(x1=列, y1=行, x2, y2)`，而 HALCON `GenRectangle1` 的签名是
`(row1, col1, row2, col2)`。直接传 `(x1, y1, x2, y2)` 会把矩形**转置**，导致
`growRect ∩ 母团` 恒为空——所有被救候选全部消失（子集评测表现为「开启后与 baseline
逐位相同」）。

**修正**：交换行列顺序传参。

> 附带说明：原代码用 `SmallestRectangle1` 的 `rx1/ry1` 传参时行列顺序是对的，
> 所以 Bug 2 是修正 Bug 1 时新引入的——两个 bug 叠加的最终现象是「修复后候选全灭」，
> 由 `DPCB_RESCUE_DEBUG=1` 诊断日志（打印母团/残渣/生长框矩形与候选面积）定位。

### 诊断手段

在 `RescueByMother` 内加临时计数与矩形打印（环境变量 `DPCB_RESCUE_DEBUG=1` 开启），
一次性区分三种失败模式：候选未产生（`seen`）、生长失控被闸丢弃（`growNull`）、
矩形交集为空（`cand=0`）。这是定位上述问题的关键手段，验收完成后保留为开关式诊断。
