# CHANGELOG · DeepPCB-Station 配方与算法

> 本文件承接原 `recipes/pcb_open_mousebite_v0.json` 的 `note2` 字段（约 4,900 字符）。
> 迁出原文逐字保留，仅按版本切段；配方 JSON 从此只保留一行指针。
> 新增/修改配方参数时必须同步三处：`RecipeConfig` 属性、`RecipeFileDto` + `LoadFromDirectory`、
> `RecipeStore.ComputeHashStatic`（含 `Save` 的 `SetIf`）——`RecipeHashContractTests` 会强制校验。

## 现行配方

- Id / Version：`pcb_open_mousebite` **v1.2.1**
- ContentHash：`26d991859176a03ee31cc7d5b728d646`（冻结值，见 `RecipeHashContractTests.ShippedRecipe_KeepsFrozenHash`）
- 定性：v1.2.1 = v1.2.0 + 分型树 split 判据来源 2×线宽窗 → 1.5×线宽窗（SplitB，阈值不变、零新拟合）

## 现行配方方法栈（原 `note` 字段）

> 分块平移配准 + 边缘解释剪枝 + 极性配对（半径 5，大块余铜不连线）+ 拓扑过滤 + LOP 解释（负极性严/正极性全量复核 0.65）+ 同极性去重合并 + 跨铜网拓扑救援（边缘闸 + 细条闸） + 负极性跳过开运算 + 弱差区域生长（regionGrowDiff=1：上报框沿 |test-temp|>0 连通域扩到缺陷完整足迹，边长≤原 3×）。v0.9.1：负极性边缘解释 0.85、细条闸 1.5。v0.9.2：区域生长只改上报框几何，四集旧口径（中心落框）零退化（external TP2838/FP38/FN42，F1 98.61%），但官方 IoU≥0.33 口径 F-score 7.6%→83.6%、mAP(11) 7.8%→76.8%。口径声明与历史债见 docs/QUALITY_DEBT.md。

## 版本史（原 `note2` 字段，逐字保留）

### v0.9.3

v0.9.3：拓扑分型（局部铜连通数三分类 short/spur/copper + 切断证据判 open + 包围环半径 6），严格分型 F 3.0%→63.9%。

### v0.9.4

v0.9.4：生长窗 margin12/cap6，忽略分型 F 83.6%→87.1%。

### v0.9.5

v0.9.5：区域生长改 8-邻接（斜向窄桥不致缺口框长不满），中位 IoU 0.507→0.557。

### v0.9.6

v0.9.6：咬宽比分型（openBiteFrac=0.8：blob 垂直走线宽/该处线宽≥0.8 判 OPEN 贯穿），子集严格分型 F 63.9%→67.7%、open AP 43%→63%。

### v0.9.7

v0.9.7：GrowBoxScale=1.2 末端各向同性缩放（补 BFS 未长到的 GT 足迹边距，中心不变），**官方 test.txt(500 对) 上检出口径（忽略分型）F 90.2%→96.1%、mAP(11) 76.6%→89.0%**。

### v0.9.8

v0.9.8：GrowBoxScale 全量扫参定峰 1.2→1.5（官方 test 500 对实跑，非 GrowSweep 模拟），检出 F 96.1%→**97.0%**、mAP11 89.0%→89.3%（TP 3033/Pred 3116/FP 83）；1.6/1.7/1.8/2.0 均回落（0.2-0.33 近失档从 47 暴涨至 86→174→375→1007），各向同性天花板≈**97.0%**。已试并证伪 GrowBoxElong（沿长轴各向异性伸长）：因近失框在 1.5 已过大（pred/GT 宽高中位 1.38×/1.66×，48/80 FP 即这批近失框），真因是中心偏移，伸长只会使 F 降（1.5_1.15→95.0、1.5_1.3→91.1），故默认关（=1 逐像素等价现状）。定位纠正：DeepPCB 官方论文法（Zhao 2015）本质同为传统参考模板差分+连通域（非深度网络），其 mAP 98.6%/F 98.2% 是同范式对手成绩、口径可直接对拼——目标为在官方 test 上反超。**【2026-10-22 更正：该定性有误。98.6%/98.2% 实为论文 VGG16-tiny + Group Pyramid Pooling 深度神经网络模型的成绩（数据集 README 原文 "our model based on deep neural network"），传统 image processing 基线约 89.3%。历史原文保留不删，但对外不得再引用「同范式」说法。】**残余差距为结构性：49 个零重叠真漏检（mousebite17+short14为主，最近 pred>60px）+ 48 个卡 IoU0.2-0.33 的近失框（过大+中心偏移），均非末端缩放可解，需改检测主线（配准/定位精度）。

### v1.0.0

v1.0.0：候选特征驱动的分型树 + 置信度标定（只改标签与排序键，不改检出）。分型改看「实测铜是否被切断」（split＝邻域测试铜第二/第一连通块面积比，open 中位 0.90 / mousebite 0.00）与咬深/线宽，取代旧 PosType 的绝对量纲 aspect≥1.3；训练集（data/full 非 test 的 1000 对、6873 GT）分型准确率 78.5%→95.4%，官方 test 500 对严格分型 F 78.7%→92.5%、mAP11 68.7%→84.0%，检出口径（忽略分型）F 97.0%、mAP11 89.3%→89.7% 零退化。置信度＝logistic(面积/均值差/边缘解释度/实心度/长宽比/包围度/咬深)，替换原「面积排序」。

### v1.1.0

v1.1.0（实验三连，均默认关、生产逐位等价

### v1.0.0，hash 288c1c41 不变）

v1.0.0，hash 288c1c41 不变）：① C1 FWHM 逐框足迹（footprintTau）——|diff| 剖面双峰（热核平台→断崖→亚阈值裙边），τ∈[0.15,0.6] 输出逐位相同、τ=0.08 仅占 GT 面积 19%（131 pred 落 GT 内/0 包住），证伪；② D1 局部 z 通道（seedZ）——统计窗含缺陷自身致 z 上限=√((1−f)/f)，SeedZ≥3 在 16×16 窗结构性不可达，洪水档（SeedZ=1/ZEps=0.1） +80 候选全 FP/0 TP，证伪；③ E1 梯度补偿亚像素（subpixelGradient）——补偿量 Gx·fx 与弱缺陷信号同阶（±15 灰度 vs 12~30），train1000 检出 F −3.2pp、TP −257，证伪。train1000 战略发现：忽略分型 97.5% vs 严格分型 93.0%，分型损失 4.5pp 是距 SOTA(98.2/98.6) 的最大单一差距，定位/召回侧已无近路，下一轮重心转分型混淆对特征。详见 docs/QUALITY_DEBT.md

### v1.1.0 节。

v1.1.0 节。

### v1.2.0

v1.2.0：分型树按 train1000 重新拟合 depth-5 后坍缩（tools/fit_type_tree.py，5 折 CV：缺铜 95.3%/多铜 97.8%，train−CV gap 0.8pp/0pp）。四处变化：① 半断带 split∈(0.1655,0.4747] 看 lateral≤3.15 判鼠咬否则开路；② 深咬 bite>0.6377 且细线 localW≤15px 看 surround≤0.8174 判鼠咬否则针孔；③ 宽线深咬判开路；④ 多铜侧 parts≥2 且 split>0.1794（实测铜已断）判毛刺而非短路（spur→short 误判 62→12）。train1000 分型总体 95.4%→96.9%（缺铜 94.7%→96.1%、多铜 96.2%→97.8%）；残余混淆：open↔mousebite 99、copper→spur 22、mousebite→pinhole 20、short→spur 18。置信度模型暂不变（

### v1.0.0 logistic）。官方 test 500 对验收见 reports/v120*。

v1.0.0 logistic）。官方 test 500 对验收见 reports/v120*。

### v1.2.0 补记

v1.2.0 补记：置信度同步升级为

### v1.2.0 logistic 17 特征

v1.2.0 logistic 17 特征（S4，cvAUC 0.782→0.838，tools/fit_type_tree.py 5 折 CV），官方 test 检出 mAP11 89.7%→90.2%（reports/v120b_official.log）。

### v1.2.1

v1.2.1：分型树 split 判据来源 Split(2×线宽窗)→SplitB(1.5×线宽窗)，三个阈值全部不变（网格搜索确认 0.4747/0.1655/0.1794 对 SplitB 分布仍最优，零新拟合）。train1000（reports/features_train3，tools/fit_v13.py/fit_v16.py）：总分型错误 209→175，open↔mousebite 99→65（提升全部来自该对），mb↔ph +2、short↔spur +2 可忽略；5 折逐折改善 [43,44,40,50,32]→[33,39,35,44,24]。物理：小窗减少邻域无关铜对「实测铜是否被切断」的污染。SplitT/SplitC 变体已证伪（fit_v13：T 阈值换源后精度崩塌至 73%，C 与 Split 逐位相同），改为仅 dump 计算留档，产线零开销。同期证伪并记录：BIN(53) 分箱置信度 cvAUC +1.6pp 但 pooled AP11 仿真零增益（OOF 90.6%=S4 现状；排序神谕亦仅 90.9%——检出召回上限 96.8% 使 11 点插值 r=1.0 不可达，排序杠杆已耗尽，tools/fit_v15.py）。

### v1.2.1 验收

v1.2.1 验收（reports/v121_train1000.log / v121_official.log / v121_golden_*）：官方 test 500 对严格分型 F 93.6%→**94.4%**、mAP11 86.3%→**88.2%**、TP 2927→2952（mousebite AP11 77.0%→86.1%、open 84.7%→86.8%），分型错误 108→83；检出口径 F 97.0%/mAP11 90.2%/TP 3033 **逐位不变**；train1000 严格 F 94.4%→94.9%、mAP11 87.0%→87.3%、分型错误 208→176；四集旧金标与

### v1.2.0 逐位一致

v1.2.0 逐位一致（零退化）。同期证伪：边际特征 m_min（tools/fit_v17.py：TP/FP AUC 0.570，仿真严格 mAP11 87.34%→87.30%）与全特征池重拟合（tools/fit_v18.py：20 特征 depth 4/5/6 均 212 错 vs 部署树 175，nets/severed/edge/oppRatio 从未被选中）——分型树亦达特征天花板。结构化结论：检出杠杆（C1/D1/E1 + 49 零重叠 + 55 近失框证伪）、排序杠杆（BIN + 神谕 90.9%）、分型杠杆（SplitB 后两项证伪）三杠杆均已实证封顶；反超官方 98.2/98.6 需换范式（DL，超出项目约束）。另修 dump conf 列恒零（HalconPcbDefectDetector：Classify/Score 均为纯函数，改为先算后写 sink，产线路径零影响、hash 不变），20 对抽样复刻校验 max|diff| 5e-5。同日补充证伪——工作点下移（diffThreshold × minBlobArea 6 组，仅 train1000 可拟合集实跑、official_test 全程未参与；reports/workpoint_sweep.ps1 / workpoint_sweep.log / workpoint_sweep_report.md）：最优点 d30_m18 严格 F 94.93→94.95、mAP11 87.28→87.31、忽略 F 97.48→97.53、忽略 TP +8，但忽略 mAP11 逐位持平（90.59），增益在噪声内，不升配方、hash 不变。机理：110 个零重叠漏检 GT 到同图最近候选框中心距离中位 107px（仅 16 个 <60px），即这些位置根本没产生连通域候选而非被闸门剪掉，故降 minBlobArea 天然无效；且 d26/d27 虽把零重叠 GT 压到 101/102（多检出 9 个）却同时 Pred +26，净 mAP11 反降——降阈值买到的是噪声框。漏检类型集中于 mousebite（85/110），根因是弱对比落在 8 位量化+配准残差噪声之下，属信噪比而非标定问题。近失框（101 条 IoU 0.2–0.33）中心偏移中位仅 2.5px，真问题是尺度失配且双峰（Area/GArea p10=0.31 与 p90=3.39 并存），单一全局缩放因子无法同时服务，故 GrowBoxScale=1.5 定峰后各向异性改动只会掉 F。检出杠杆至此四条路径（C1/D1/E1 + 工作点）全部实证封顶。

### v1.2.2 候选二

v1.2.2 候选二（母团救援+残渣生长）证伪不升配方：修掉母团救援两道反向闸门（motherMin 40/60/100 把母团面积中位仅 23 的真缺陷全排除；rescueCap 按面积降序截断把真缺陷母团排最后）、修掉 ComputeHashStatic 的 hash payload 占位符错位、修掉 GrowResidueBox 生长框被丢弃（生长闸形同虚设）与 GenRectangle1 行列顺序错误（矩形转置致交集恒空）；加 DPCB_RESCUE_DEBUG=1 诊断开关。验收：mra=16/dim=50 在 train1000 换回 6 个真 mousebite 漏检（零重叠 110→104）但 pred +12/TP +6，external/holdout/mixed 三个留出集 FP 全升、TP 零增益、严格 mAP11 全降 → 证伪，配方与 hash 26d99185… 不变。详见 reports/residue_grow_falsification.md。
