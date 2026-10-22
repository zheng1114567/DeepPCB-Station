# DeepPCB-Station · PCB AOI 工位（MVP）

> 仓库：<https://github.com/zheng1114567/DeepPCB-Station>（主支路 `main`）

**路径**：`E:\demo\DeepPCB-Station`（与 `E:\demo\VisionStationHost` **并列**，零交叉）

C# / WPF + Halcon 传统算子：DeepPCB 成对图回放 → **模板匹配 → 分块配准 → 差分 → Blob → 边缘解释剪枝 → 拓扑分型** → SoftPLC TRIG 握手 → inspectionId 证据包。另含**走线亚像素量测**（`edges_sub_pix` 线宽/节距 + 像素当量反标定，与检测主线解耦）。

> **与磁瓦仓零交叉**：不共享检测代码、配方、数据路径、金标集。磁瓦 `VisionStationHost` 只读封板。

## 一句话

PCB AOI **模板差分 + 工位壳**；六类（open / mousebite / short / spur / copper / pinhole）。v1.0.0 起分型由「线宽归一化特征 + 实测铜切断度」判定（训练集 78.5%→96.9%），v1.2.1 起 split 判据换源小窗；官方口径可单独计分。

**检测主链路不上 DL**（分块配准 → 差分 → Blob → 剪枝 → 分型，全为可解释的传统算子）；**分型层已接 ONNX 旁路**（`DPCB_DL_TYPE=<onnx>`，默认 null = 生产零变化），实测官方 500 对严格分型 F **94.37% → 95.80%**、定位结果逐位不变。定位是"规则层"、分类是"DL 层"——这正是工业主流的串联混合，详见「架构定位」一节。

## 默认配方（现行 v1.2.1）

| 项 | 值 |
|----|-----|
| Id / Version | `pcb_open_mousebite` **v1.2.1** |
| ContentHash | **`26d991859176a03ee31cc7d5b728d646`** |
| 加载路径 | `recipes/pcb_open_mousebite_v0.json` |
| 要点 | 分块平移配准（8 块）+ 边缘解释剪枝 + 极性配对 + 拓扑救援 + 区域生长足迹（×1.5） + **分型树 / 置信度标定** |
| 定性 | v1.2.1 = v1.2.0 + 分型树 split 判据来源 2×线宽窗→1.5×线宽窗（SplitB，阈值不变、零新拟合）；**只改标签与排序键，检出零变化** |

详见 `docs/QUALITY_DEBT.md`。

## 官方 test.txt 划分（500 对）· v1.2.1（现行）

DeepPCB 官方 `test.txt` 全部 500 对（GT 3140 / Pred 3116），IoU≥0.33、同图、一对一贪心。
证据：`reports/v121_official.log`、`reports/v121_train1000.log`、`reports/v121_golden_*.log`。

| 指标 | v1.0.0 | v1.2.0 | **v1.2.1** | 论文 DL 模型（VGG16+GPP） |
|---|---|---|---|---|
| 严格分型 F-score | 92.5% | 93.6% | **94.4%** | 98.2% |
| 严格分型 mAP(11) | 84.0% | 86.3% | **88.2%** | 98.6% |
| 严格分型 TP | 2894 | 2927 | **2952** | — |
| 检出（忽略分型）F | 97.0% | 97.0% | 97.0% | — |
| 检出 mAP(11) | 89.7% | 90.2% | 90.2% | — |

**据实（含 2026-10-22 对标事实更正）**：98.6% mAP / 98.2% F 是论文的**深度神经网络**模型（VGG16-tiny + Group Pyramid Pooling）成绩——数据集 GitHub README 原文为 "our model based on deep neural network"，**并非同范式的传统模板差分法**；传统 image processing 基线约 89.3%（第三方汇总）。故本项目的正确对标是「传统 97.0% vs DL 98.2%，差 1.2pp」，而非「同范式差 1.2pp」。分型这层已接近本范式天花板（v1.2.1 后边际特征/全特征池均证伪）；mousebite AP11 77.0%→86.1% 是 v1.2.1 最大单项增益。距 DL 的残余差距为**结构性**：49 个零重叠漏检（信号在噪声底以下）+ 检出召回 96.8% 封顶 11 点插值（排序神谕亦仅 90.9%），追平需换范式（DL，超出项目约束）。**不得**把 94.4% 或 88.2% 说成反超 98.2%/98.6%。

对照口径见 `docs/QUALITY_DEBT.md` 的 v1.0.0 章节。

## 金标结论（旧口径，数字冻结）

### v0.3.7 · 通过

证据：`reports/agent_v037_{samples,mixed,holdout}/golden_latest.md`。

| 集 | 对数 | 全局 FP | 最大漏检率 |
|----|------|---------|-----------|
| samples | 41 | **1.6%**（过门≤50%） | 5.7%（short，过门≤20%） |
| mixed | 237 | **1.7%** | 3.6%（short） |
| holdout | 99 | **1.1%** | 2.8%（short） |
| 聚合（三集并集） | 377 | **1.5%**（95%CI 1.1–2.1%） | 3.5%（short） |
| external（未调参） | 480 | **6.9%**（95%CI 6.0–7.8%） | 8.7%（short） |
| 全集（DeepPCB 11 组） | 1500 | **3.0%**（95%CI 2.6–3.3%） | 5.0%（short） |

**结论：通过**（bbox 近似；**分型不计成绩**）。单检约 413ms。板级：内测 8.2%、external 16.7%、全集 10.0% 的板含至少一个噪声框。**对外泛化口径以 external 480 对为准**（框对率约 93.1%）。已知弱段：group20085 的 20085240–20085324 鼠咬漏检 25.2%（单段未过门）。证据：`reports/agent_v037_{all,external480,g*}/golden_latest.md`。

### v0.3.6 · 历史

证据：`reports/agent_v036_{samples,mixed,holdout}/golden_latest.md`。holdout FP 2.6%，open 1.7% / mousebite 3.4%。

### v0.2.0 · 历史（补票）

证据：`reports/golden_latest.md`（41 对）。open 漏检 0.0% / mousebite 7.4%；全局 FP 38.5%。

### v0.1.0 · 历史债（保留）

证据：`reports/golden_20261018_191325.md` · hash `43e8b00dc40dfe88a0bebfece96bd7a1`  
open 漏检 2.0% / mousebite 7.4% 过门；**过杀 84.6% 未过** → 当时质量债停拧。不得抹掉。

## 对外口径（硬）

- 可写工位壳 + 模板差分；写金标须注明子集规模与 bbox 近似。
- **禁止**把论文传统 image processing ≈89% mAP **写成己方成绩**。
- **禁止**吹噪声框或把 Pred 长宽比分型写成分类成绩。
- 建议引用：Tang et al., *Online PCB defect detector on a new PCB defect dataset*, arXiv:1902.06197。

## 数据与许可

| 项 | 约定 |
|----|------|
| 数据集 | [DeepPCB](https://github.com/tangsanli5201/DeepPCB)：`*_temp.jpg` / `*_test.jpg`，640×640 |
| 本仓小样 | `data/samples/`（manifest；**大数据不进 git**） |
| 许可 | **非商用**可用；商用须联系作者 |
| 承诺类 | 仅 **open (type=1)** + **mousebite (type=3)** |
| Out | short / spur / copper / pin-hole；DL 替换检测主链路（旁路分型不算）；磁瓦交叉 |

## 金标口径（bbox 近似）

- **Pred**：Halcon region → 二值 mask；**GT**：bbox 填充满矩形（非像素级工业封样）
- 命中：中心落框或 IoU≥0.33；**忽略 Pred 分型**（长宽比启发式 = **非可靠分类器**）
- overlay 可有噪声候选框 → 不得单独当过杀已控
- 匹配失败：模板恒等拷贝（预对齐兜底）

## 架构（壳复用边界）

| 复用 | 不复用 |
|---|---|
| ICamera / IDefectDetector / Recipe / SoftPLC / ResultStore / WPF | 磁瓦检测器、DynThreshold、minGrad、磁瓦数据/金标 |

检测插件：`HalconPcbDefectDetector`（v1.2.1 六类）+ `PcbTypeTree` / `PcbConfidence`（只改标签与排序）。

### 可观测性（日志与异常）

Core 不直接写 `Console` / `Debug`，统一走 `Core/Diagnostics/StationLog`：

- 宿主装 sink 才落盘；**未装 sink 时全部是空操作**（行为与无日志时逐位一致）。
- CLI 装 stderr 端点（stdout 留给评测报告，混进去报告就不可解析）；
  上位机装「调试输出 + `%APPDATA%\DeepPCBStation\logs\station-yyyyMMdd.log`」滚动文件
  ——`Debug.WriteLine` 在 Release 下会被整段裁掉，只有文件日志能在现场事后回看。
- `StationLog` 自己吞掉全部写日志异常（含 sink 内部异常、磁盘满、目录不可建）：
  **日志失败不允许把业务打挂**。
- 告警路径一律"按类型判定 + 留痕"，不用 `ex.Message.Contains(...)` 做分支：
  `CameraNoImageException` 取代了原来的 `NO_IMAGE` 子串过滤。

## 架构定位：规则层判定 + DL 复判/分型（工业混合路径）

**"不上 DL"不是"落后"，而是按缺陷类型分工后的选择。** 工业现场的通行做法不是
"传统 vs DL 二选一"，而是**串联混合**：规则层做确定性判定（尺寸/有无/极性/高对比缺陷），
DL 承担复判与分类（降假点 FCR、分型、严重度分级），人只处理最难的少数。
分工判据是可描述的：

| 缺陷特征 | 谁更合适 | 理由 |
|---|---|---|
| 高对比、规则可描述、要求可追溯/可解释 | **传统** | 可达万件/分钟、近零误废，判据能写进工艺文件 |
| 外观类（划痕/脏污/异色/纹理异常）、形态多变、背景复杂 | **DL** | 传统 60–80% vs DL 95–99% |

PCB 裸板的开路/鼠咬/短路属前者：**规则可描述 + 需可追溯**，故主链路用传统算子站得住。
本项目把 DL 放在它真正有增量的位置——**分类层**，而不是替换检测器：

| 层级 | 实现 | 状态 |
|---|---|---|
| 定位/检出 | 分块配准 + 差分 + Blob + 剪枝（纯传统） | 主力，官方 500 对检出口径 F 97.0% |
| 分型 | 规则树 `PcbTypeTree`（默认） / ResNet18 六分类 ONNX 旁路 | 旁路已落地且实测：严格分型 F 94.37% → 95.80%（定位逐位不变） |
| 复判 | 人工终判（`final_verdict` + 伪点统计） | 已落地：FCR 0.17/板，伪点单独计数用于降 FCR |

已实证的两条边界（详见 `docs/DL-分型分类器离线验证.md`）：

- **DL 复判（TP vs FP）在本数据上无增量**：CNN AUROC 0.9031、17 特征 LR 0.8975，
  都不如单特征 `B_mean` 在 99.5% 召回点上的表现——低置信候选本就是最难样本。
- **DL 救援被闸门误杀的碎片不成立**：判别力按板 GroupKFold OOF AUROC 0.9451，
  但同阈值精确率 train OOF 74.4% → official 仅 14.8%，跨板泛化站不住（详见
  `reports/dl_rescue_falsification.md`）。

因此"补 DL"的正确优先级是**给 REVIEW 队列加复判分流器**（转人工、不担判 NG 责任），
而不是换掉检测器。这一点与代码现状一致：ONNX 旁路只改标签，定位链路一条未动。

## 关联项目（作品集）

两个项目分工互补，不重复造对方的长处：

| 项目 | 定位 | 覆盖能力 |
|---|---|---|
| **DeepPCB-Station**（本仓） | 2D 裸板缺陷检测工位（离线回放） | 传统视觉管线、工位壳、配方、复判、评测纪律、DL 分型旁路 |
| **机械臂视觉引导抓取** | 6 轴机械臂 + 3D 引导（**真机**） | D435i 手眼标定、YOLO 识别抓取、PLC 联调部署、999 点标定 |

说明文档：`docs/PORTFOLIO-机械臂视觉项目说明.md`

> 按岗位选主讲：上位机 / AOI 岗主讲本仓，算法 / 机器人岗主讲机械臂项目。

## 仓库与 remote

| 项 | 现状 |
|----|------|
| 本机 | `E:\demo\DeepPCB-Station`（独立 git） |
| remote | `origin` → `https://github.com/zheng1114567/DeepPCB-Station`（主支路 `main`） |

禁止把本仓文件提交进磁瓦仓。

## 上位机界面

自绘标题栏 + 菜单栏 + 工具栏（单次/连续/停止/复位/结果目录/复判/导出 + 配方 + 确认换型）+ 状态灯条（相机/光源/PLC/触发权/存储/换型/运行）+ 设备栏 + 三栏主体 + 底部四页签（运行消息/最近结果/统计/报警）+ 状态栏（工位/操作员/班次/配方/节拍/握手/时钟）。

| 项 | 位置 |
|---|---|
| 视觉基准（改版先改样张） | `docs/ui-mockup-上位机.html` |
| 实际渲染快照 | `docs/ui-actual-mainwindow.png`（运行页）+ `docs/ui-pages/`（历史/统计/配方/告警/设置） |
| 闸门与验收 | `docs/SCOPE-UI一页范围.md` + `scripts/check-ui-gate.ps1` |
| 版式核对工具 | 设环境变量 `DPCB_DEV_UI_SNAPSHOT=<png 路径>` 启动 **Debug** 版 exe，跳过登录渲染主窗并截图退出。该分支整块包在 `#if DEBUG` 内——因为它会跳过登录直接起 Admin 会话，Release（交付）二进制里不存在这段代码，不依赖"别让人设这个变量" |

## 演示命令

**请用仓库根「启动主窗.bat」**，或先 `cd` 到 `E:\demo\DeepPCB-Station` 再启 exe。  
程序会从 BaseDirectory 向上找含 `data\samples` + `recipes` 的根并 `SetCurrentDirectory`；失败弹中文 MessageBox（含 InnerException 全文）。

> **构建前提：需要 MVTec HALCON SDK（本仓实测 22.11）**。检测引擎 `HalconPcbDefectDetector`
> 真依赖 `HalconDotNet`（不是可选插件）：需安装 HALCON 并设 `HALCONROOT`，使其
> `$(HALCONROOT)\bin\dotnet45\halcondotnet.dll` 存在。缺 SDK 时构建会**快速失败**并提示缺什么
> （不会退化成静默回落 Stub —— 那样会跑出一组假框，看起来像有结果）。
> `--engine=Stub` 仅供编译/冒烟，不能替代 SDK。

```powershell
cd E:\demo\DeepPCB-Station
dotnet build DeepPCBStation.sln -c Release

# 推荐（双击亦可）
.\启动主窗.bat

# 等价：仓库根启动
.\src\DeepPCBStation\bin\Release\net48-windows\DeepPCBStation.exe

# 单检 / 握手 / 金标复现（勿改参冒充新成绩）
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --engine=Halcon
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --handshake-selftest
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --recipe-gate-selftest
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --db-selftest
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --concurrency-selftest
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --golden-eval --engine=Halcon --max-pairs=100

# 官方 test 500 对（ICDAR 口径；默认用标定置信度，--conf=area 可回到面积排序）
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --sota-eval --samples=data/official_test

# 候选特征 dump（分型/置信度重拟合用；只导训练集，禁止导 official_test 后调参）
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --feature-dump --samples=data/full --exclude=data/official_test_ids.txt

# 走线亚像素量测（Halcon 量测工具箱落地；与检测主线解耦，默认不参与判定）
#   --calib-um=<该走线真实宽度μm> 用一根已知宽度的走线反推像素当量，再把节距换算成 μm
#   --um=<μm/px> 已知当量时直接给；--w-tol/--p-tol 公差（±μm，无当量时按 px）
dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release -- --measure `
  --image=reports/_smoke/trace.png --roi=190,100,450,500 --calib-um=15 --p-tol=3

# 单元测试（Core 纯函数 + 四态判定行为 + 量测精度；量测精度用例需要 SDK，缺 SDK 时标记 Skipped 而非消失）
dotnet test DeepPCBStation.sln -c Release
```

量测精度（合成已知真值走线图，`LineMetrologyTests` 锁住合格线）：线宽误差 ≤0.008 px（6–160 px 全程）、
节距误差 0.0000 px、行间 MAD 0.0000、单次 6–15 ms。**这是合成图上的算法精度，不是产线指标**。

TCP 默认 `:9100`。先「确认换型」再开检。

## Fault 语义

| 码 | 含义 |
|----|------|
| `NO_HALCON` / `LICENSE` / `NO_TEMPLATE` | SDK / 证 / 缺模板 |
| `RECIPE_NOT_CONFIRMED` | 换型未确认 |
| `NO_OPERATOR` / `BAD_PART_ID` / `INTERNAL` | 无登录会话 / partId 非法 / 内部异常 |
| `TIMEOUT` / `DISCONNECT` / `BUSY` | 握手侧：超时、断连、触发权被占 |

超时与断连后的迟到结果仍落盘，但走 `results/VOID/{day}/`、写 `is_void=1` + `parent_id`，**不计良率**。

## In / Out

**In**：成对回放、v0.4.0 Halcon 差分管线（六类）、SoftPLC、配方 hash + 乐观锁、inspectionId、六类金标（bbox 近似）；工业级工位壳：登录取证 + 6 页（运行/历史/统计/配方/告警/用户/设置）+ 角色门控 + 15 分钟遮罩锁屏；触发权仲裁（UI/PLC 互斥，另一方秒拒）；每条记录带 operator/shift/trigger_source/station_id；复判闭环（伪点改判 + 审计）；统计含 FPY/伪点率/DPPM/作废；**走线亚像素量测**（`edges_sub_pix` 线宽/节距、像素当量反标定、公差判定、批量重复性统计、JSON 落盘）。

**Out**：无票再拧、其它缺陷类死磕、DL、磁瓦交叉、简历冒用文献 mAP。
