using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// DeepPCB 交付引擎：模板匹配 → 仿射对齐 → 差分 → 形态学 → Blob。
/// 仅承诺 open + mousebite 场景；不引入磁瓦 DynThreshold/minGrad。
/// Fault：未编译 SDK → NO_HALCON；真 license 异常 → LICENSE；其它 → ENGINE。
/// </summary>
public sealed partial class HalconPcbDefectDetector : IDefectDetector
{
#if USE_HALCON
    /// <summary>
    /// 开启 Halcon 内部算子并行（threshold / 形态学 / 距离变换等）。
    /// Halcon 保证多线程下结果逐位一致，只影响速度；不支持时静默忽略。
    /// </summary>
    static HalconPcbDefectDetector()
    {
        try { HOperatorSet.SetSystem("parallelize_operators", "true"); }
        catch { /* 旧版本或受限 license：保持默认 */ }
    }
#endif

    private RecipeConfig _recipe = new();

    public string EngineName => "Halcon";

    public void ApplyRecipe(RecipeConfig recipe) => _recipe = recipe;

    /// <summary>诊断开关：收集被过滤候选（漏检归因用）；产线保持关闭。</summary>
    public bool CollectRejected { get; set; }

    /// <summary>诊断开关：收集通过闸门的候选特征行（分型树/置信度标定用）；产线保持 null。</summary>
    public List<DefectFeatureRow>? FeatureSink { get; set; }

    /// <summary>DL 分型旁路（DPCB_DL_TYPE 环境变量指定 ONNX 时启用）；只改类别，默认 null。</summary>
    private readonly DeepPCBStation.Core.Inference.DlTypeClassifier? _dlType =
        DeepPCBStation.Core.Inference.DlTypeClassifier.TryCreateFromEnvironment();

    public DetectionOutcome Detect(FrameBuffer frame)
    {
        var sw = Stopwatch.StartNew();
#if !USE_HALCON
        sw.Stop();
        return new DetectionOutcome
        {
            IsOk = false,
            Defects = Array.Empty<DefectItem>(),
            EngineMs = sw.Elapsed.TotalMilliseconds,
            FaultCode = "NO_HALCON"
        };
#else
        try
        {
            if (!frame.HasTemplate)
            {
                sw.Stop();
                return new DetectionOutcome
                {
                    IsOk = false,
                    Defects = Array.Empty<DefectItem>(),
                    EngineMs = sw.Elapsed.TotalMilliseconds,
                    FaultCode = "NO_TEMPLATE"
                };
            }

            var rejected = CollectRejected ? new List<RejectedBlob>() : null;
            var defects = DetectCore(frame, rejected);
            // DL 分型旁路：只覆盖 TypeCode，框/检出/置信度一律不动（未配置环境变量时为 null）
            _dlType?.Apply(frame, defects);
            sw.Stop();
            return new DetectionOutcome
            {
                IsOk = defects.Count == 0,
                Defects = defects,
                EngineMs = sw.Elapsed.TotalMilliseconds,
                Rejected = rejected
            };
        }
        catch (HOperatorException ex) when (IsLicenseError(ex))
        {
            sw.Stop();
            return new DetectionOutcome
            {
                IsOk = false,
                Defects = Array.Empty<DefectItem>(),
                EngineMs = sw.Elapsed.TotalMilliseconds,
                FaultCode = "LICENSE"
            };
        }
        catch (Exception)
        {
            sw.Stop();
            return new DetectionOutcome
            {
                IsOk = false,
                Defects = Array.Empty<DefectItem>(),
                EngineMs = sw.Elapsed.TotalMilliseconds,
                FaultCode = "ENGINE"
            };
        }
#endif
    }

#if USE_HALCON
    private List<DefectItem> DetectCore(FrameBuffer frame, List<RejectedBlob>? rejected)
    {
        // ---- 阶段 1：配准（见 HalconPcbDefectDetector.Register.cs）----
        var tStage = StageTimer.Mark();
        var reg = RegisterStage(frame);
        StageTimer.Add("1_register", tStage);
        tStage = StageTimer.Mark();
        var testPx = reg.TestPx;
        var tempPx = reg.TempPx;
        var rawTempPx = reg.RawTempPx;
        double[]? fracX = reg.FracX;
        double[]? fracY = reg.FracY;

        var testHandle = GCHandle.Alloc(testPx, GCHandleType.Pinned);
        var tempHandle = GCHandle.Alloc(tempPx, GCHandleType.Pinned);
        HObject? hoTest = null;
        HObject? hoTemp = null;
        HObject? hoTempAligned = null;
        HObject? diffI = null;
        HObject? copperRegion = null;
        HTuple? modelId = null;
        try
        {
            HOperatorSet.GenImage1(out hoTest, "byte", frame.Width, frame.Height, testHandle.AddrOfPinnedObject());
            HOperatorSet.GenImage1(out hoTemp, "byte", frame.Width, frame.Height, tempHandle.AddrOfPinnedObject());

            // DeepPCB 官方预对齐（test = temp + 缺陷，同坐标）；恒等即可，避免 shape-model 亚像素误配准引入边缘噪声
            // 本轮 B 闸不修配准；铜 ROI 在模板坐标系，恒等即等价仿射到 test。
            HOperatorSet.CopyImage(hoTemp, out hoTempAligned);
            modelId = null;

            var tSetup = StageTimer.Mark();
            // ---- 配方参数解析：全部收敛到 PcbRecipeParams（单一真源）----
            // 原先此处散落 79 个「>0 ? 用值 : 默认」兜底表达式，既无法单测、也无法被新阶段复用，
            // 且 Program.cs 之类调用方的默认值会与此处打架。现统一由 PcbRecipeParams 解析，
            // 这里只保留同名别名（下游引用一行不改），因此本改动是逐位等价的纯结构搬迁。
            var P = new PcbRecipeParams(_recipe);
            var thresh = P.thresh;
            var minArea = P.minArea;
            var minOpen = P.minOpen;
            var minMb = P.minMb;
            var selectMin = P.selectMin;
            var typeAwareArea = P.typeAwareArea;
            var motherRescueArea = P.motherRescueArea;
            var rescueCap = P.rescueCap;
            var residueGrowMaxDim = P.residueGrowMaxDim;
            var maxArea = P.maxArea;
            var openR = P.openR;
            var openRNeg = P.openRNeg;
            var copperMax = P.copperMax;
            var surroundR = P.surroundR;
            var surroundMin = P.surroundMin;
            var maxAspect = P.maxAspect;
            var tipSurroundMax = P.tipSurroundMax;
            var tipMaxWidth = P.tipMaxWidth;
            var minThickness = P.minThickness;
            var borderMargin = P.borderMargin;
            var borderMinAspect = P.borderMinAspect;
            var sixClass = P.sixClass;
            var edgeMin = P.edgeMin;
            var edgeRad = P.edgeRad;
            var edgeRawFallback = P.edgeRawFallback;
            var edgePrune = P.edgePrune;
            var edgeForcePruneArea = P.edgeForcePruneArea;
            var edgeSkipDepth = P.edgeSkipDepth;
            var edgeMinNeg = P.edgeMinNeg;
            var edgeShallowDiff = P.edgeShallowDiff;
            var edgeShallowMin = P.edgeShallowMin;
            var edgeRadNeg = P.edgeRadNeg;
            var lopR = P.lopR;
            var lopMin = P.lopMin;
            var lopDepthSkip = P.lopDepthSkip;
            var lopOppMin = P.lopOppMin;
            var lopTF = P.lopTF;
            var lopRescue = P.lopRescue;
            var lopThresh = P.lopThresh;
            var closingR = P.closingR;
            var closingRNeg = P.closingRNeg;
            var pairR = P.pairR;
            var pairGhostFrac = P.pairGhostFrac;
            var pairNetKeep = P.pairNetKeep;
            var pairKeepArea = P.pairKeepArea;
            var mergeR = P.mergeR;
            var mergeRatio = P.mergeRatio;
            var satR = P.satR;
            var satRatio = P.satRatio;
            var coreBoxFrac = P.coreBoxFrac;
            var typeRefine = P.typeRefine;
            var copperBlobMin = P.copperBlobMin;
            var spurAspectMax = P.spurAspectMax;
            var openAspectMin = P.openAspectMin;
            var severWin = P.severWin;
            var growMargin = P.growMargin;
            var growMaxScale = P.growMaxScale;
            var growBoxScale = P.growBoxScale;
            var growBoxElong = P.growBoxElong;
            var growBoxPad = P.growBoxPad;
            var growDiag = P.growDiag;
            var growIters = P.growIters;
            var fpTau = P.fpTau;
            var fpMargin = P.fpMargin;
            var severDilate = P.severDilate;
            var openBiteFrac = P.openBiteFrac;
            var lopReviewAll = P.lopReviewAll;
            var lopReviewMin = P.lopReviewMin;
            var netRescue = P.netRescue;
            var bridgeSkipEdge = P.bridgeSkipEdge;
            var bridgeThin = P.bridgeThin;
            var posCopperMin = P.posCopperMin;
            var negAwayCu = P.negAwayCu;
            HObject? copperDilNeg = null;
            // 模板铜网标号（惰性，一次标号多处复用：桥核判定 / 凸起判定 / 边缘闸拓扑救援）。
            int[]? copperLabels = null;
            int[] NetLabels() => copperLabels ??= CopperLabels(copperRegion, frame.Width, frame.Height);
            var topoMin = P.topoMin;
            var topoTouch = P.topoTouch;
            var gd = P.gd;
            var scaleArea = P.scaleArea;
            var scaleDepth = P.scaleDepth;
            var scaleOn = P.scaleOn;
            var scaleWidthR = P.scaleWidthR;

            // 有符号差分（int2 保号）：只取「缺铜」极性（test 亮于模板）。
            // open/mousebite 属缺铜；short/spur/copper 属多铜（test 暗于模板）被排除；
            // pin-hole（针孔）虽缺铜，但被模板铜全包围，后续按 surround 判别剔除。
            // ★ 极性 SubImage / aspect / surround 逻辑本轮不动。
            HObject hoTestI, hoTempI;
            HOperatorSet.ConvertImageType(hoTest, out hoTestI, "int2");
            HOperatorSet.ConvertImageType(hoTempAligned, out hoTempI, "int2");
            if (gd > 0)
            {
                // 灰度膨胀模板铜区（MAX 滤波收缩铜区），吸收 ±(gd/2)px 系统偏移。
                HObject hoTempDil;
                HOperatorSet.GrayDilationRect(hoTempI, out hoTempDil, gd, gd);
                hoTempI.Dispose();
                hoTempI = hoTempDil;
            }
            // fracX/fracY 成对产生（配准要么给两者、要么都不给），但类型都是 double[]?；
            // 只判 fracX 的话编译器会警告 fracY 可能为空——顺带把"只给一半"的情形也挡掉。
            if (fracX != null && fracY != null)
            {
                // E1 梯度补偿差分（Lucas-Kanade 一步）：d = test − (tempA + Gx·fx + Gy·fy)。
                // 图像不重采样（保持原始锐度），只把每块分数位移补进差分式，消掉铜边 ±2px 系统偏移。
                // Gx/Gy = 对齐模板的单帧 Sobel；gd>0 时模板膨胀不影响 tempPx，此处按未膨胀模板算（记为近似）。
                var n = frame.Width * frame.Height;
                var gradX = new short[n];
                var gradY = new short[n];
                Sobel3x3(tempPx, gradX, gradY, frame.Width, frame.Height);
                var comp = new short[n];
                for (var i = 0; i < n; i++)
                {
                    var v = testPx[i] - tempPx[i] - (gradX[i] * fracX[i] + gradY[i] * fracY[i]);
                    comp[i] = v > 32767 ? (short)32767 : v < -32768 ? (short)-32768 : (short)v;
                }
                var compHandle = GCHandle.Alloc(comp, GCHandleType.Pinned);
                try
                {
                    HOperatorSet.GenImage1(out diffI, "int2", frame.Width, frame.Height,
                        compHandle.AddrOfPinnedObject());
                }
                finally { compHandle.Free(); }
            }
            else
            {
                HOperatorSet.SubImage(hoTestI, hoTempI, out diffI, 1, 0);
            }
            StageTimer.Add(">2a_prep_diff", tSetup);
            var tRoi = StageTimer.Mark();
            HOperatorSet.Threshold(hoTempAligned, out copperRegion, 0, copperMax);
            StageTimer.Add(">2c_copper_roi", tRoi);
            // ---- v1.1.0 局部 z 通道：弱对比缺陷的加性种子（SeedZ=0 关，逐像素等价现状）----
            // 全局阈值 30 漏掉的峰值 12~30 弱缺陷在此补种子；只加不减，闸门全走既有链路。
            // 三兜底值（12/2.0/16）已收编到 PcbRecipeParams（单一真源），此处只读解析结果。
            HObject? zPos = null;
            HObject? zNeg = null;
            var seedZ = P.seedZ;
            if (seedZ > 0)
            {
                BuildZChannels(frame.Width, frame.Height, seedZ,
                    P.seedZAbs, P.zEps, P.zWindow,
                    P.zCopperRadius, copperRegion, out zPos, out zNeg);
            }
            HObject? distToSubstrate = null;
            if (scaleOn || (tipSurroundMax > 0 && tipMaxWidth > 0) || (edgeMin > 0 && edgeSkipDepth > 0) || topoMin > 0)
            {
                // 铜区内每点到衬底的棋盘距离：中心线≈半线宽；blob 上的最大值=缺口咬进铜线的深度
                HTuple imgW, imgH;
                HOperatorSet.GetImageSize(hoTempAligned, out imgW, out imgH);
                HOperatorSet.DistanceTransform(copperRegion, out distToSubstrate, "chessboard", "true", imgW, imgH);
            }
            hoTempI.Dispose();
            hoTestI.Dispose();

            HObject region, opened, connected, selected;
            StageTimer.Add("2_diff_roi", tStage);
            tStage = StageTimer.Mark();
            HOperatorSet.Threshold(diffI, out region, thresh, 32000);
            if (zPos != null)
            {
                // z 通道只加种子：与全局阈值区域取并集后走完全相同的下游闸门，既有命中不受影响
                // （除非加进来的种子与既有候选合并改形，属预期内小概率，由四集旧金标把关）。
                HOperatorSet.Union2(region, zPos, out var regionU);
                region.Dispose();
                zPos.Dispose();
                zPos = null;
                region = regionU;
            }
            HObject? closedPos = null;
            var posSource = region;
            if (closingR > 0)
            {
                // 闭运算合并同缺陷碎片（阈值/开运算切分）；再走开运算去细条
                HOperatorSet.ClosingCircle(region, out closedPos, closingR);
                posSource = closedPos;
            }
            HOperatorSet.OpeningCircle(posSource, out opened, openR);
            HOperatorSet.Connection(opened, out connected);
            // 母团救援（默认关闭，MotherRescueArea>0 生效）：
            // 开运算会把小面积浅咬缺陷的主体切掉，只留边缘残渣（实测残渣面积 6~21
            // 而框内差分像素 17~67）。只看残渣面积会把这类真缺陷误杀（归因area<min 组）。
            // 但直接下调MinBlobArea 等于放行全场边缘残渣（噪声洪水）。
            // 正确判据：残渣虽小，但若它在**开运算前**所属的连通域（母团）足够大，
            // 说明差分信号本来是成片的，只是被开运算切碎 —— 据此回收。
            // 被母团救援放行的残渣键（"x,y,w,h"），下游 typeMin 二次闸门据此豁免，
            // 否则残渣会在ProcessPosBlob 里被同一阈值再杀一次，救援等于没做。
            var rescuedKeys = new HashSet<string>();
            HOperatorSet.SelectShape(connected, out selected, "area", "and", selectMin, maxArea);
            if (motherRescueArea > 0)
            {
                var motherSrc = closedPos ?? region;
                HObject motherConnected;
                HOperatorSet.Connection(motherSrc, out motherConnected);
                // 残渣生长闸（v1.2.2候选，0=关）：残渣先长回缺陷足迹，再按包围盒最大边闸门。
                // 纯仿真（tools/residue_grow_sim.py）显示 maxDim≤50 时精确率 100%、召回 77.8%，
                // 故生长必须带闸——无闸时 margin 越大越好但引入 FP（与 lds4 同样会被留出集否决）。
                // 模板用**配准后**的 tempPx（残渣在配准坐标系里，洪泛必须同坐标系，
                // 否则 1~2px 残差会让弱差像素洪泛断裂）。
                RescueByMother(ref selected, opened, motherConnected, selectMin, maxArea,
                    motherRescueArea, rescuedKeys, rescueCap,
                    residueGrowMaxDim > 0 ? residueGrowMaxDim : 0,
                    tempPx, testPx, frame.Width, frame.Height, growMargin, growMaxScale, growDiag);
                motherConnected.Dispose();
            }

            var defects = new List<DefectItem>();
            var posCands = new List<DefectItem>();
            var negCands = new List<DefectItem>();
            if (rejected != null)
            {
                HTuple nAll;
                HOperatorSet.CountObj(connected, out nAll);
                for (int i = 1; i <= nAll.I; i++)
                {
                    HObject c;
                    HOperatorSet.SelectObj(connected, out c, i);
                    HTuple a, r, cc;
                    HOperatorSet.AreaCenter(c, out a, out r, out cc);
                    if (a.D < selectMin || a.D > maxArea)
                    {
                        var (item, asp) = ToItem(c, "?", a.D);
                        rejected.Add(new RejectedBlob
                        {
                            Reason = a.D < selectMin ? RejectedBlob.Reasons.AreaTooSmall : RejectedBlob.Reasons.AreaTooLarge,
                            Blob = item, Area = a.D, Aspect = asp
                        });
                    }
                    c.Dispose();
                }
            }
            HTuple count;
            HOperatorSet.CountObj(selected, out count);
            for (int i = 1; i <= count.I; i++)
            {
                HObject one;
                HOperatorSet.SelectObj(selected, out one, i);
                try
                {
                    var frac = 0.0;
                    if (edgeMin > 0)
                    {
                        var (a0, asp0) = BlobAreaAspect(one);
                        // 纯错位鬼影只在铜边 ±2px 内；blob 咬进模板铜 ≥edgeSkipDepth 必是真缺陷，免检
                        var edgeDepth = edgeSkipDepth > 0 && distToSubstrate != null
                            ? RobustMaxGray(one, distToSubstrate, 0)
                            : -1.0;
                        var deepDefect = edgeSkipDepth > 0 && edgeDepth >= edgeSkipDepth;
                        var (probe0, _) = ToItem(one, "?", a0);
                        if (!deepDefect)
                        {
                            frac = EdgeExplain.Fraction(
                                probe0, testPx, tempPx, frame.Width, frame.Height, thresh, edgeRad, extraCopper: false);
                            if (edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx))
                                frac = Math.Max(frac, EdgeExplain.Fraction(
                                    probe0, testPx, rawTempPx, frame.Width, frame.Height, thresh, edgeRad, extraCopper: false));
                        }
                        if (lopR > 0 && (deepDefect || lopReviewAll) &&
                            (lopDepthSkip <= 0 || edgeDepth < lopDepthSkip))
                        {
                            // 深缺陷深度豁免跳过了边缘闸，但「线宽变化/局部错位」同样能咬进铜 ≥3px；
                            // 用更严的 LOP（同一平移解释整块）复核，真缺陷保留不可解释核心。
                            // ★ 不能写回 frac：frac 下游还要给边缘闸/残核剪枝用，污染会误杀真缺口。
                            var (lf, ldx, ldy) = LopFraction(probe0);
                            var opp = lopOppMin > 0
                                ? PolarityPair.Ratio(probe0, testPx, tempPx, frame.Width, frame.Height,
                                    thresh, Math.Max(3, (int)Math.Ceiling(pairR) + 1), oppositePositive: false)
                                : -1;
                            if (lf >= lopReviewMin && (lopOppMin <= 0 || opp >= lopOppMin))
                            {
                                Reject(rejected, one, RejectedBlob.Reasons.Lop, a0, asp0,
                                    maxDepth: edgeDepth, lopFrac: lf, lopDx: ldx, lopDy: ldy, oppRatio: opp);
                                continue;
                            }
                        }
                        var forcePrune = edgePrune && edgeForcePruneArea > 0 && a0 > edgeForcePruneArea;
                        // 低对比 blob：刚过阈值的差分接近配准噪声，但真缺口/多铜的金属差异在 100+。
                        var edgeMinEff = edgeMin;
                        if (edgeShallowDiff > 0)
                        {
                            var mdShallow = RobustMaxGray(one, diffI, 0);
                            if (mdShallow < edgeShallowDiff) edgeMinEff = edgeShallowMin;
                        }
                        // 解释度够高也先拆残核：真缺陷粘在鬼影上时，整块会被边缘闸杀掉。
                        // 残核走正常闸（不放宽 thin/aspect）。拆不出够大的核才整杀。
                        var edgeKill = frac >= edgeMinEff && !forcePrune;
                        if (edgeKill && lopR > 0 && lopRescue)
                        {
                            // LOP 反向用法：边缘闸只说明「差分贴在铜边上」（真鬼影和真缺口都贴边），
                            // 单一平移能解释整块才是鬼影。解释不了 → 救回（短/毛刺常被邻域极值闸误杀）。
                            // 注意：该分支在 mixed 上会放出较多鬼影，独占开关 LopRescue。
                            var (lr, _, _) = LopFraction(probe0);
                            if (lr < lopMin) edgeKill = false;
                        }
                        if (edgePrune)
                        {
                            byte[]? altTemp = edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx) ? rawTempPx : null;
                            var res = EdgeExplain.Residual(
                                probe0, testPx, tempPx, altTemp, frame.Width, frame.Height, thresh, edgeRad, extraCopper: false);
                            // 残核闸用全局 minArea，不用 selectMin：分型下限降到 11 时，
                            // 否则会有更多 blob 改走剪枝路径，残核又被后续闸杀掉，反而把真缺陷弄丢。
                            if (res != null && res.Value.total - res.Value.explained >= minArea)
                            {
                                HOperatorSet.GenRegionRuns(out var resRegion, res.Value.rows, res.Value.begins, res.Value.ends);
                                HObject? resClosed = null;
                                var resSource = resRegion;
                                if (closingR > 0)
                                {
                                    HOperatorSet.ClosingCircle(resRegion, out resClosed, closingR);
                                    resSource = resClosed;
                                }
                                HOperatorSet.Connection(resSource, out var resConn);
                                resRegion.Dispose();
                                resClosed?.Dispose();
                                HOperatorSet.CountObj(resConn, out var nRes);
                                for (var j = 1; j <= nRes.I; j++)
                                {
                                    HOperatorSet.SelectObj(resConn, out var comp, j);
                                    try { ProcessPosBlob(comp, !edgeKill, frac); }
                                    finally { comp.Dispose(); }
                                }
                                resConn.Dispose();
                                continue;
                            }
                        }
                        if (edgeKill)
                        {
                            Reject(rejected, one, RejectedBlob.Reasons.Edge, a0, asp0, -1, -1, -1, edgeDepth);
                            continue;
                        }
                    }
                    ProcessPosBlob(one, false, frac);
                }
                finally
                {
                    one.Dispose();
                }
            }

            HObject? negRaw = null;
            if (sixClass)
            {
                StageTimer.Add("3_pos_blob_gate", tStage);
                tStage = StageTimer.Mark();
                // 负差（多铜）：short / spur / copper
                HObject negRegion, negConnected, negSelected;
                HObject? negOpened = null;
                HOperatorSet.Threshold(diffI, out negRegion, -32000, -thresh);
                if (zNeg != null)
                {
                    HOperatorSet.Union2(negRegion, zNeg, out var negU);
                    negRegion.Dispose();
                    zNeg.Dispose();
                    zNeg = null;
                    negRegion = negU;
                }
                HObject? closedNeg = null;
                var negSource = negRegion;
                if (closingRNeg > 0)
                {
                    HOperatorSet.ClosingCircle(negRegion, out closedNeg, closingRNeg);
                    negSource = closedNeg;
                }
                var negOpenR = openRNeg < 0 ? -1.0 : (openRNeg == 0 ? openR : openRNeg);
                HObject negConnSource;
                if (negOpenR < 0)
                {
                    negConnSource = negSource;
                }
                else
                {
                    HOperatorSet.OpeningCircle(negSource, out negOpened, negOpenR);
                    negConnSource = negOpened;
                }
                HOperatorSet.Connection(negConnSource, out negConnected);
                HOperatorSet.SelectShape(negConnected, out negSelected, "area", "and", selectMin, maxArea);

                HTuple negCount;
                HOperatorSet.CountObj(negSelected, out negCount);
                for (int i = 1; i <= negCount.I; i++)
                {
                    HObject one;
                    HOperatorSet.SelectObj(negSelected, out one, i);
                    try
                    {
                        var frac = 0.0;
                        if (edgeMinNeg > 0)
                        {
                            var (a0, asp0) = BlobAreaAspect(one);
                            var (probe0, _) = ToItem(one, "?", a0);
                            frac = EdgeExplain.Fraction(
                                probe0, testPx, tempPx, frame.Width, frame.Height, thresh, edgeRadNeg, extraCopper: true);
                            if (edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx))
                                frac = Math.Max(frac, EdgeExplain.Fraction(
                                    probe0, testPx, rawTempPx, frame.Width, frame.Height, thresh, edgeRadNeg, extraCopper: true));
                            var forcePruneN = edgePrune && edgeForcePruneArea > 0 && a0 > edgeForcePruneArea;
                            var edgeMinEffN = edgeMinNeg;
                            if (edgeShallowDiff > 0)
                            {
                                var mdShallowN = RobustMaxGray(one, diffI, 0);
                                if (mdShallowN < edgeShallowDiff) edgeMinEffN = edgeShallowMin;
                            }
                            var edgeKillN = frac >= edgeMinEffN && !forcePruneN;
                            // 远离模板铜的多铜 blob 不可能由平移产生（模板那里没有铜可错位）→ 真值，免闸。
                            // 与 BridgeSkipEdge 的区别就在这条距离约束：不限距离会让缝里的鬼影全部漏进来。
                            if (edgeKillN && negAwayCu)
                            {
                                if (copperDilNeg == null)
                                    HOperatorSet.DilationCircle(copperRegion, out copperDilNeg, edgeRadNeg);
                                HObject awayOv;
                                HOperatorSet.Intersection(one, copperDilNeg, out awayOv);
                                HTuple awayA;
                                HOperatorSet.AreaCenter(awayOv, out awayA, out _, out _);
                                awayOv.Dispose();
                                if (awayA.D < 1) edgeKillN = false;
                            }
                            if (edgeKillN && lopR > 0 && lopRescue)
                            {
                                // 同正极性：单一平移解释不了整块 → 真短/毛刺，救回。
                                var (lrN, _, _) = LopFraction(probe0);
                                if (lrN < lopMin) edgeKillN = false;
                            }
                            if (edgeKillN && netRescue)
                            {
                                // 短路的物理必要条件：把两块原本独立的铜网连起来。
                                // 沿边鬼影只贴着同一块铜（网号恒为 1）；跨网 → 真桥，边缘闸不杀。
                                // 救回后仍要过下面的 LOP 二道闸，纯平移错的鬼影照样被拦。
                                if (UnionNets(NetLabels(), frame.Width, frame.Height, one, 2) >= 2)
                                    edgeKillN = false;
                            }
                            if (edgePrune)
                            {
                                byte[]? altTemp = edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx) ? rawTempPx : null;
                                var res = EdgeExplain.Residual(
                                    probe0, testPx, tempPx, altTemp, frame.Width, frame.Height, thresh, edgeRadNeg, extraCopper: true);
                                if (res != null && res.Value.total - res.Value.explained >= minArea)
                                {
                                    HOperatorSet.GenRegionRuns(out var resRegion, res.Value.rows, res.Value.begins, res.Value.ends);
                                    HObject? resClosedN = null;
                                    var resSourceN = resRegion;
                                    if (closingR > 0)
                                    {
                                        HOperatorSet.ClosingCircle(resRegion, out resClosedN, closingR);
                                        resSourceN = resClosedN;
                                    }
                                    HOperatorSet.Connection(resSourceN, out var resConn);
                                    resRegion.Dispose();
                                    resClosedN?.Dispose();
                                    HOperatorSet.CountObj(resConn, out var nRes);
                                    for (var j = 1; j <= nRes.I; j++)
                                    {
                                        HOperatorSet.SelectObj(resConn, out var comp, j);
                                        try { ProcessNegBlob(comp, !edgeKillN, frac); }
                                        finally { comp.Dispose(); }
                                    }
                                    resConn.Dispose();
                                    continue;
                                }
                            }
                            if (edgeKillN)
                            {
                                Reject(rejected, one, RejectedBlob.Reasons.Edge, a0, asp0);
                                continue;
                            }
                        }
                        if (lopR > 0)
                        {
                            // 多铜鬼影（错位/线宽变粗）同样可用单一平移解释；与 edgeMinNeg 串联做二道闸。
                            var (aL, aspL) = BlobAreaAspect(one);
                            var (probeL, _) = ToItem(one, "?", aL);
                            var (lf, ldx, ldy) = LopFraction(probeL);
                            var oppL = lopOppMin > 0
                                ? PolarityPair.Ratio(probeL, testPx, tempPx, frame.Width, frame.Height,
                                    thresh, Math.Max(3, (int)Math.Ceiling(pairR) + 1), oppositePositive: true)
                                : -1;
                            var awayHit = negAwayCu &&
                                CopperGate.AwayFromCopper(one, copperRegion, edgeRadNeg, ref copperDilNeg);
                            if (lf >= lopMin && (lopOppMin <= 0 || oppL >= lopOppMin) && !awayHit)
                            {
                                Reject(rejected, one, RejectedBlob.Reasons.Lop, aL, aspL,
                                    lopFrac: lf, lopDx: ldx, lopDy: ldy, oppRatio: oppL);
                                continue;
                            }
                        }
                        ProcessNegBlob(one, false, frac);
                    }
                    finally
                    {
                        one.Dispose();
                    }
                }

                negSelected.Dispose();
                negConnected.Dispose();
                negOpened?.Dispose();
                negRaw = negRegion;
                closedNeg?.Dispose();
            }

            if (topoMin > 0)
                AppendTopoRescue(region, negRaw, topoMin, topoTouch);

            // 局部函数：单个 blob（或剪枝残余分量）的形状/位置闸门。边缘解释闸在调用方处理。
            // fromPrune：剪枝分量已被证明含「未解释=真实差异」像素，但其核心天然细/长
            // （短路核=桥中心线、缺口核=深层行），按整 blob 校准的 thin/aspect 闸会误杀，跳过。
            void ProcessPosBlob(HObject blob, bool fromPrune, double frac)
            {
                HTuple row1, col1, row2, col2, area, row, col;
                HOperatorSet.SmallestRectangle1(blob, out row1, out col1, out row2, out col2);
                HOperatorSet.AreaCenter(blob, out area, out row, out col);
                var w = Math.Max(1, col2.I - col1.I + 1);
                var h = Math.Max(1, row2.I - row1.I + 1);
                var aspect = w >= h ? (double)w / h : (double)h / w;

                // 面积闸（剪枝分量需要；原 blob 已过 SelectShape，重复检查无害）
                if (area.D < selectMin || area.D > maxArea)
                {
                    Reject(rejected, blob,
                        area.D < selectMin ? RejectedBlob.Reasons.AreaTooSmall : RejectedBlob.Reasons.AreaTooLarge,
                        area.D, aspect);
                    return;
                }

                // 衬底闸：缺铜缺陷必须在模板铜上吃掉铜；整块落在模板衬底上的是反光/污渍。
                // 审计依据：external 10/43 个 FP 是「MeanDiff<0 且模板缺口深度=0」的衬底暗斑。
                // 只卡覆盖率（不卡深度），贴着铜边的真缺口不会误杀。
                if (posCopperMin > 0 &&
                    CopperGate.CopperCover(blob, copperRegion, area.D) < posCopperMin)
                {
                    Reject(rejected, blob, RejectedBlob.Reasons.Substrate, area.D, aspect);
                    return;
                }

                // 图像边界闸：分区拼接错位集中在图缘（见 QUALITY_DEBT），GT 缺陷极少贴边。
                // 形状豁免：拼接伪影是细长条；矮胖贴边 blob 可能是真缺陷（BorderMinAspect=0 时全杀）。
                if (borderMargin > 0 &&
                    (col1.I <= borderMargin || row1.I <= borderMargin ||
                     col2.I >= frame.Width - 1 - borderMargin || row2.I >= frame.Height - 1 - borderMargin) &&
                    (borderMinAspect <= 0 || aspect >= borderMinAspect))
                {
                    Reject(rejected, blob, RejectedBlob.Reasons.Border, area.D, aspect);
                    return;
                }

                // 边缘细条噪声：外接矩过度细长 → 非缺陷，丢弃。
                // 剪枝分量核心天然拉长，上限放宽一倍但仍设闸（拦弧形鬼影尖端）。
                // 切得断铜网的长开路同样放到 2×：长鬼影切不断，不会跟着进来。
                if (aspect > (fromPrune ? maxAspect * 2 : maxAspect))
                {
                    var longCut = !fromPrune && aspect <= maxAspect * 2
                        && distToSubstrate != null && SeversCopper(blob);
                    if (!longCut)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.Aspect, area.D, aspect);
                        return;
                    }
                }

                // 针孔剔除：缺铜 blob 若被模板铜全包围 → pin-hole（非承诺类），丢弃。
                HObject dilated, ring, ringCopper, ringConn;
                HOperatorSet.DilationCircle(blob, out dilated, surroundR);
                HOperatorSet.Difference(dilated, blob, out ring);
                HOperatorSet.Intersection(ring, copperRegion, out ringCopper);
                HOperatorSet.Connection(ringCopper, out ringConn);
                HTuple ringArea, ringCopperArea, r0, c0, ncomp;
                HOperatorSet.AreaCenter(ring, out ringArea, out r0, out c0);
                HOperatorSet.AreaCenter(ringCopper, out ringCopperArea, out r0, out c0);
                HOperatorSet.CountObj(ringConn, out ncomp);
                var surround = ringArea.D > 0 ? ringCopperArea.D / ringArea.D : 0.0;
                ringConn.Dispose();
                ringCopper.Dispose();
                ring.Dispose();
                dilated.Dispose();
                if (surround >= surroundMin)
                {
                    // 六类模式保留针孔；二类模式丢弃（pin-hole 非承诺）。
                    if (!sixClass)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.PinHole, area.D, aspect, surround, ncomp.I);
                        return;
                    }
                }

                // 细条：内切半径过小（厚度≤1px 类）→ 非缺陷，丢弃。
                // 圆孔豁免：被模板铜全包围（pin-hole 形态）且近圆的小孔是真针孔，不按细条杀（二值化抖动条纹是拉长的，仍会被杀）。
                // 剪枝分量核心天然细（缺口核=深层行），下限降到 1.2 但仍设闸。
                var thinFloor = fromPrune ? Math.Min(minThickness, 1.2) : minThickness;
                if (thinFloor > 0 && !(surround >= surroundMin && aspect < 2.5))
                {
                    HTuple iRow, iCol, iRad;
                    HOperatorSet.InnerCircle(blob, out iRow, out iCol, out iRad);
                    if (iRad.D < thinFloor)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.Thin, area.D, aspect, surround, ncomp.I);
                        return;
                    }
                }

                // 线端噪声：四周铜只有一个连通分量（位于铜线末端）且铜占比低 → 跨板线长差异，非缺陷。
                // 附加线宽判定：只在「细铜线端」才算线端；宽焊盘/铜块上（localWidth 大）是鼠咬等缺陷，不当线端。
                if (tipSurroundMax > 0 && ncomp.I <= 1 && surround < tipSurroundMax)
                {
                    var onThinTrace = tipMaxWidth <= 0;
                    var tipW = -1.0;
                    if (tipMaxWidth > 0 && distToSubstrate != null)
                    {
                        (_, tipW) = ScaleFeatures(blob, copperRegion, distToSubstrate, scaleWidthR);
                        if (tipW < 1.0) tipW = 1.0;
                        onThinTrace = tipW < tipMaxWidth;
                    }
                    if (onThinTrace)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.Tip, area.D, aspect, surround, ncomp.I, tipW);
                        return;
                    }
                }

                var localW = -1.0;
                var maxDepth = -1.0;
                if (scaleOn && distToSubstrate != null)
                {
                    (maxDepth, localW) = ScaleFeatures(blob, copperRegion, distToSubstrate, scaleWidthR);
                    if (localW < 1.0) localW = 1.0;
                    if (scaleDepth > 0 && maxDepth < scaleDepth * localW)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.ScaleDepth, area.D, aspect, surround, ncomp.I, localW, maxDepth);
                        return;
                    }
                    // 缺口面积随线宽近似线性（咬口长度≈线宽量级），不是 W²
                    if (scaleArea > 0 && area.D < scaleArea * localW)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.ScaleArea, area.D, aspect, surround, ncomp.I, localW, maxDepth);
                        return;
                    }
                }

                // 分型标签：仅用于叠图着色与官方严格分型 mAP（金标匹配不比分型，不影响旧口径命中）。
                var type = PosType(blob, aspect, surround);
                // 分型仅叠图着色用（不可靠），面积下限不能再看启发式分型：
                // 真 mousebite 常被长宽比判成 OPEN，然后被 open 类更大的下限误杀。
                // TypeAwareAreaGate（默认关）开启后改为「先分型、再按该类自己的下限」：
                // 预筛已在 SelectShape 用 selectMin 放过候选，此处只做分类复核，
                // 因此关闭时仍是 selectMin（逐位等价）。
                var typeMin = selectMin;
                if (typeAwareArea)
                    typeMin = type == "MOUSEBITE" ? minMb : minOpen;
                if (area.D < typeMin && !rescuedKeys.Contains(BlobKey(blob)))
                {
                    Reject(rejected, blob, RejectedBlob.Reasons.TypeMinArea, area.D, aspect, surround, ncomp.I);
                    return;
                }

                var posItem = ToItem(blob, type, area.D).item;
                posItem.EdgeFrac = frac;
                posItem = ShrinkToCore(posItem, coreBoxFrac, positive: true);
                posCands.Add(posItem);
            }

            void ProcessNegBlob(HObject blob, bool fromPrune, double frac)
            {
                var (a, asp) = BlobAreaAspect(blob);
                if (a < selectMin || a > maxArea) return;
                // 跨网桥接：细短路的物理证据（单一铜网的平移鬼影不可能同时贴两块网）。
                // 1px 细短路的 InnerCircle 半径恒为 0，会被 thin 闸静默杀掉，故跨网时跳过 thin/aspect。
                var bridged = bridgeThin && UnionNets(NetLabels(), frame.Width, frame.Height, blob, 2) >= 2;
                if (borderMargin > 0)
                {
                    HOperatorSet.SmallestRectangle1(blob, out var r1, out var c1, out var r2, out var c2);
                    if (c1.I <= borderMargin || r1.I <= borderMargin ||
                        c2.I >= frame.Width - 1 - borderMargin || r2.I >= frame.Height - 1 - borderMargin)
                    {
                        Reject(rejected, blob, RejectedBlob.Reasons.Border, a, asp);
                        return;
                    }
                }
                if (asp > (fromPrune ? maxAspect * 2 : maxAspect) && !bridged) return;
                var thinFloorN = fromPrune ? Math.Min(minThickness, 1.2) : minThickness;
                if (thinFloorN > 0 && !bridged)
                {
                    HTuple iRow, iCol, iRad;
                    HOperatorSet.InnerCircle(blob, out iRow, out iCol, out iRad);
                    if (iRad.D < thinFloorN) return;
                }
                // 多铜类型：见 NegType（面积启发式 or 拓扑证据）
                var typeN = NegType(blob, a, asp);
                var negItem = ToItem(blob, typeN, a).item;
                negItem.EdgeFrac = frac;
                negItem = ShrinkToCore(negItem, coreBoxFrac, positive: false);
                negCands.Add(negItem);
            }

            // 拉长框收核：鬼影尾巴把外接框中心拖出 GT 时，换成未解释残核的框。不新增候选。
            TightenStretched(posCands, edgeRad, extraCopper: false);
            TightenStretched(negCands, edgeRadNeg, extraCopper: true);

            // 反极性证据（仅诊断口径）：错位鬼影对侧必有反极性带，真缺口没有。
            // 落地在候选上，FP/漏检审计可直接读，用于标定 LopOppMin。
            if (lopR > 0)
            {
                var oppRad = Math.Max(3, (int)Math.Ceiling(pairR) + 1);
                for (var i = 0; i < posCands.Count; i++)
                    posCands[i].OppRatio = PolarityPair.Ratio(
                        posCands[i], testPx, tempPx, frame.Width, frame.Height, thresh, oppRad, oppositePositive: false);
                for (var j = 0; j < negCands.Count; j++)
                    negCands[j].OppRatio = PolarityPair.Ratio(
                        negCands[j], testPx, tempPx, frame.Width, frame.Height, thresh, oppRad, oppositePositive: true);
            }

            // 同极性重复检测合并：Closing 断开时同一缺陷会输出多块，金标一对一贪心只认先到的一块，
            // 其余全算过杀。external 审计显示 161/207 个 FP 属此类，其中 158 个还在已命中的 GT 框内。
            // 按 bbox 间隙聚类，每组只留面积最大的一块；跨极性交给下面的配对逻辑。
            if (mergeR > 0)
            {
                DedupSamePolarity(posCands);
                DedupSamePolarity(negCands);
            }

            void DedupSamePolarity(List<DefectItem> list)
            {
                if (list.Count < 2) return;
                var dead = new bool[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    if (dead[i]) continue;
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        if (dead[j]) continue;
                        var gap = BboxGap(list[i], list[j]);
                        var si = list[i].Score;
                        var sj = list[j].Score;
                        var big = Math.Max(si, sj);
                        var small = Math.Min(si, sj);
                        // 卫星压制：只有「小很多」的伴生块才算同一缺陷的碎片；
                        // 尺寸相当的两块是相邻的两个真缺陷，合并会连带丢掉金标框。
                        if (mergeR > 0 && gap <= mergeR)
                        {
                            if (mergeRatio > 0 && big > 0 && small / big > mergeRatio) continue;
                        }
                        // 远距卫星抑制：mergeR 外、satR 内的同极性小块（面积 ≤ satRatio×大块）是同一缺陷
                        // 被 Closing 撕开的碎片；金标一对一贪心只认先到的一块，其余全算过杀。
                        else if (!(satR > 0 && gap <= satR && big > 0 && small / big <= satRatio))
                        {
                            continue;
                        }
                        if (list[j].Score > list[i].Score) { dead[i] = true; break; }
                        dead[j] = true;
                    }
                }
                for (var k = list.Count - 1; k >= 0; k--)
                    if (dead[k]) list.RemoveAt(k);
            }

            // 双极性配对：错位铜边一侧缺铜、另一侧多铜，正负鬼影相距≤pairR 成对丢弃。
            // 在边缘解释过滤之后执行：可解释鬼影已死，真实缺陷身边无幸存对手。
            if (pairR > 0)
            {
                var dropPos = new bool[posCands.Count];
                var dropNeg = new bool[negCands.Count];
                var posSevers = new bool?[posCands.Count];
                var negBridged = new bool?[negCands.Count];
                for (var i = 0; i < posCands.Count; i++)
                for (var j = 0; j < negCands.Count; j++)
                {
                    if (dropNeg[j] && dropPos[i]) continue;
                    if (BboxGap(posCands[i], negCands[j]) > pairR) continue;
                    // 大块多铜是真缺陷（余铜/大短路），不与缺铜鬼影连坐。
                    if (pairKeepArea > 0 && negCands[j].Score >= pairKeepArea) continue;
                    // 跨两块独立铜网的负块是真桥：单块网的平移鬼影贴不出这种拓扑。
                    if (pairNetKeep)
                    {
                        if (negBridged[j] == null)
                        {
                            var bridgedJ = false;
                            if (negCands[j].HasRuns)
                            {
                                HOperatorSet.GenRegionRuns(out var nreg,
                                    new HTuple(negCands[j].RunRows!), new HTuple(negCands[j].RunColBegin!), new HTuple(negCands[j].RunColEnd!));
                                try { bridgedJ = UnionNets(NetLabels(), frame.Width, frame.Height, nreg, 2) >= 2; }
                                finally { nreg.Dispose(); }
                            }
                            negBridged[j] = bridgedJ;
                        }
                        if (negBridged[j] == true) continue;
                    }
                    if (pairGhostFrac > 0 &&
                        (posCands[i].EdgeFrac < pairGhostFrac || negCands[j].EdgeFrac < pairGhostFrac))
                        continue;
                    // 切得断铜网的缺铜不是鬼影配对的一侧，留下；对面的多铜鬼影照杀。
                    if (posSevers[i] == null) posSevers[i] = CandidateSevers(posCands[i]);
                    if (posSevers[i] != true) dropPos[i] = true;
                    dropNeg[j] = true;
                }
                for (var i = 0; i < posCands.Count; i++)
                {
                    if (dropPos[i] && rejected != null)
                        rejected.Add(new RejectedBlob { Reason = RejectedBlob.Reasons.EdgePair, Blob = posCands[i] });
                    else if (!dropPos[i]) defects.Add(posCands[i]);
                }
                for (var j = 0; j < negCands.Count; j++)
                {
                    if (dropNeg[j] && rejected != null)
                        rejected.Add(new RejectedBlob { Reason = RejectedBlob.Reasons.EdgePair, Blob = negCands[j] });
                    else if (!dropNeg[j]) defects.Add(negCands[j]);
                }
            }
            else
            {
                defects.AddRange(posCands);
                defects.AddRange(negCands);
            }

            // 拓扑救回：只认「连上两块铜网」的桥核与「切断铜网」的缺口核。
            // 全局面积闸/厚度闸不动，避免小噪声改道涌入。
            void AppendTopoRescue(HObject posRaw, HObject? negRawRegion, int minCore, int touch)
            {
                if (negRawRegion != null)
                {
                    var labels = NetLabels();
                    HOperatorSet.Connection(negRawRegion, out var negConn);
                    // 父块可以是「真短路 + 整条鬼影」的巨连体（超过 maxArea）。只抽出桥核，不把巨块本身放行。
                    HOperatorSet.SelectShape(negConn, out var negSel, "area", "and", minCore, 1000000);
                    negConn.Dispose();
                    HOperatorSet.CountObj(negSel, out var nNeg);
                    for (var i = 1; i <= nNeg.I; i++)
                    {
                        HOperatorSet.SelectObj(negSel, out var blob, i);
                        try { EmitBridgeCores(blob, labels, minCore, touch); }
                        finally { blob.Dispose(); }
                    }
                    negSel.Dispose();

                    HOperatorSet.GenRectangle1(out var domain, 0, 0, frame.Height - 1, frame.Width - 1);
                    HOperatorSet.Difference(domain, copperRegion, out var substrate);
                    domain.Dispose();
                    HOperatorSet.DistanceTransform(substrate, out var distToCopper, "chessboard", "true", frame.Width, frame.Height);
                    substrate.Dispose();
                    try { EmitSpurTips(negRawRegion, labels, distToCopper); }
                    finally { distToCopper.Dispose(); }
                }

                if (distToSubstrate != null)
                {
                    HOperatorSet.Connection(posRaw, out var posConn);
                    HOperatorSet.SelectShape(posConn, out var posSel, "area", "and", minCore, maxArea);
                    posConn.Dispose();
                    HOperatorSet.CountObj(posSel, out var nPos);
                    for (var i = 1; i <= nPos.I; i++)
                    {
                        HOperatorSet.SelectObj(posSel, out var blob, i);
                        try { EmitCutCore(blob, minCore); }
                        finally { blob.Dispose(); }
                    }
                    posSel.Dispose();
                }
            }

            void EmitBridgeCores(HObject blob, int[] labels, int minCore, int touch)
            {
                HOperatorSet.GetRegionRuns(blob, out var runRows, out var runBeg, out var runEnd);
                var ys = new List<int>();
                var xs = new List<int>();
                var w = frame.Width;
                var h = frame.Height;
                for (var r = 0; r < runRows.Length; r++)
                {
                    var y = runRows[r].I;
                    if ((uint)y >= (uint)h) continue;
                    var x1 = runBeg[r].I;
                    var x2 = runEnd[r].I;
                    for (var x = x1; x <= x2; x++)
                    {
                        if ((uint)x >= (uint)w) continue;
                        if (DistinctCopper(labels, w, h, y, x, touch) >= 2)
                        {
                            ys.Add(y);
                            xs.Add(x);
                        }
                    }
                }
                if (ys.Count < minCore) return;
                HOperatorSet.GenRegionPoints(out var cores, new HTuple(ys.ToArray()), new HTuple(xs.ToArray()));
                HOperatorSet.Connection(cores, out var parts);
                cores.Dispose();
                HOperatorSet.CountObj(parts, out var nParts);
                for (var j = 1; j <= nParts.I; j++)
                {
                    HOperatorSet.SelectObj(parts, out var part, j);
                    try
                    {
                        var (a, asp) = BlobAreaAspect(part);
                        if (a < minCore || a > maxArea || asp > Math.Min(maxAspect, 4.0)) continue;
                        if (HitsBorder(part)) continue;
                        var typeN = NegType(part, a, asp);
                        var item = ToItem(part, typeN, a).item;
                        // 窄缝里的错位鬼影能被模板邻域解释；跨过较宽间隙的真短路中心解释不了。
                        var bFrac = EdgeExplain.Fraction(
                            item, testPx, tempPx, frame.Width, frame.Height, thresh, edgeRadNeg, extraCopper: true);
                        if (edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx))
                            bFrac = Math.Max(bFrac, EdgeExplain.Fraction(
                                item, testPx, rawTempPx, frame.Width, frame.Height, thresh, edgeRadNeg, extraCopper: true));
                        // 桥核已过「跨两块独立铜网」的拓扑判定，这比边缘解释更硬：
                        // 单一铜网的平移鬼影不可能同时贴到两个网。BridgeSkipEdge 时不再用边缘解释二次否决，
                        // 否则贴边 1px 细短路（其邻域差分几乎全由铜边构成）会被 bFrac 误杀。
                        if (!bridgeSkipEdge && bFrac >= edgeMinNeg) continue;
                        var hit = -1;
                        for (var k = 0; k < negCands.Count; k++)
                            if (BboxGap(negCands[k], item) <= 0) { hit = k; break; }
                        if (hit >= 0)
                        {
                            // 已有框被鬼影拉长、中心漂出 GT：用更小的桥核换掉，避免再占一个框变成过杀。
                            var old = negCands[hit];
                            var oldAsp = old.H <= 0 ? 99 : (old.W >= old.H ? (double)old.W / old.H : (double)old.H / old.W);
                            if (old.Score > item.Score * 2 && oldAsp >= 3)
                                negCands[hit] = item;
                            continue;
                        }
                        negCands.Add(item);
                    }
                    finally { part.Dispose(); }
                }
                parts.Dispose();
            }

            void EmitSpurTips(HObject negRawRegion, int[] labels, HObject distToCopper)
            {
                // 凸起：只碰一块铜网，并且有离铜边 ≥3px 的尖端（配准残差只到 2px）。沿边鬼影没有这种尖端。
                HOperatorSet.Connection(negRawRegion, out var conn);
                HOperatorSet.SelectShape(conn, out var sel, "area", "and", 12, 200);
                conn.Dispose();
                HOperatorSet.Threshold(distToCopper, out var far, 3, 9999);
                HOperatorSet.CountObj(sel, out var n);
                for (var i = 1; i <= n.I; i++)
                {
                    HOperatorSet.SelectObj(sel, out var blob, i);
                    try
                    {
                        if (UnionNets(labels, frame.Width, frame.Height, blob, 2) != 1) continue;
                        HOperatorSet.Intersection(blob, far, out var tipRegion);
                        HOperatorSet.Connection(tipRegion, out var tips);
                        tipRegion.Dispose();
                        HOperatorSet.CountObj(tips, out var nt);
                        for (var j = 1; j <= nt.I; j++)
                        {
                            HOperatorSet.SelectObj(tips, out var tip, j);
                            try
                            {
                                var (a, asp) = BlobAreaAspect(tip);
                                if (a < 12 || a > 60 || asp > 2.5) continue;
                                if (HitsBorder(tip)) continue;
                                var item = ToItem(tip, "SPUR", a).item;
                                var hit = -1;
                                for (var k = 0; k < negCands.Count; k++)
                                    if (BboxGap(negCands[k], item) <= 0) { hit = k; break; }
                                if (hit >= 0)
                                {
                                    var old = negCands[hit];
                                    var oldAsp = old.H <= 0 ? 99.0 : (old.W >= old.H ? (double)old.W / old.H : (double)old.H / old.W);
                                    if (old.Score > item.Score * 2 && oldAsp >= 3)
                                        negCands[hit] = item;
                                    continue;
                                }
                                negCands.Add(item);
                            }
                            finally { tip.Dispose(); }
                        }
                        tips.Dispose();
                    }
                    finally { blob.Dispose(); }
                }
                far.Dispose();
                sel.Dispose();
            }

            void EmitCutCore(HObject blob, int minCore)
            {
                var (a0, asp0) = BlobAreaAspect(blob);
                if (a0 < minCore || a0 > maxArea || asp0 > maxAspect * 2) return;
                if (distToSubstrate == null) return;
                var depth = RobustMaxGray(blob, distToSubstrate, 0);
                // 纯贴边鬼影深度通常 ≤1，而且切不断铜网。两条同时成立才救。
                if (depth < 2) return;
                if (OverlapsBlob(posCands, blob)) return;
                if (!SeversCopper(blob)) return;

                HOperatorSet.Threshold(distToSubstrate, out var deep, 2, 99999);
                HOperatorSet.Intersection(blob, deep, out var core);
                deep.Dispose();
                try
                {
                    HTuple coreArea, cr, cc;
                    HOperatorSet.AreaCenter(core, out coreArea, out cr, out cc);
                    if (coreArea.Length == 0 || coreArea.D < 4 || coreArea.D > maxArea) return;
                    var (_, asp) = BlobAreaAspect(core);
                    if (asp > maxAspect * 2) return;
                    if (HitsBorder(core)) return;
                    var item = ToItem(core, "OPEN", coreArea.D).item;
                    if (OverlapsAny(posCands, item)) return;
                    posCands.Add(item);
                }
                finally
                {
                    core.Dispose();
                }
            }

            (double frac, int dx, int dy) LopFraction(DefectItem probe)
            {
                if (lopR <= 0 || !probe.HasRuns) return (0, 0, 0);
                var best = LocalShiftMatch.Best(
                    probe, testPx, tempPx, frame.Width, frame.Height, lopThresh, lopR);
                if (edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx))
                {
                    var alt = LocalShiftMatch.Best(
                        probe, testPx, rawTempPx, frame.Width, frame.Height, lopThresh, lopR);
                    if (alt.frac > best.frac) best = alt;
                }
                return best;
            }

            // ---- v1.1.0 局部 z 通道（弱对比加性种子）----
            // z = (|diff| − μ_w) / (σ_w + eps)，zWin×zWin 滑窗，积分图 O(1)/像素。
            // 两道硬约束压平坦区噪声爆 z：绝对差 ≥ zAbs；种子须落在模板铜（膨胀 zCuR）上
            // ——六类缺陷全部位于铜区（缺铜缺陷的种子贴着铜边），衬底反射/污渍进不来。
            // 差分口径与 diffI 一致（gd=0；TemplateGrayDilation>0 时 z 通道按未膨胀模板算，记为近似）。
            void BuildZChannels(int w, int h, double seedZ, int zAbs, double zEps, int zWin, int zCuR,
                HObject copper, out HObject? zPos, out HObject? zNeg)
            {
                zPos = null; zNeg = null;
                var sw = w + 1;
                var sat1 = new long[sw * (h + 1)];
                var sat2 = new long[sw * (h + 1)];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var d = testPx[y * w + x] - tempPx[y * w + x];
                    var ad = Math.Abs((double)d);
                    var i = (y + 1) * sw + (x + 1);
                    sat1[i] = sat1[y * sw + (x + 1)] + sat1[(y + 1) * sw + x] - sat1[y * sw + x] + (long)ad;
                    var ad2 = (long)(ad * ad);
                    sat2[i] = sat2[y * sw + (x + 1)] + sat2[(y + 1) * sw + x] - sat2[y * sw + x] + ad2;
                }

                var r = zWin / 2;
                var n = w * h;
                var maskPos = new byte[n];
                var maskNeg = new byte[n];
                var anyPos = false;
                var anyNeg = false;
                for (var y = 0; y < h; y++)
                {
                    var y1 = Math.Max(0, y - r); var y2 = Math.Min(h - 1, y + r);
                    var rowA = (y2 + 1) * sw; var rowB = y1 * sw;
                    for (var x = 0; x < w; x++)
                    {
                        var x1 = Math.Max(0, x - r); var x2 = Math.Min(w - 1, x + r);
                        var cnt = (long)(x2 - x1 + 1) * (y2 - y1 + 1);
                        var s1 = sat1[rowA + (x2 + 1)] - sat1[rowB + (x2 + 1)]
                               - sat1[rowA + x1] + sat1[rowB + x1];
                        var s2 = sat2[rowA + (x2 + 1)] - sat2[rowB + (x2 + 1)]
                               - sat2[rowA + x1] + sat2[rowB + x1];
                        var mu = (double)s1 / cnt;
                        var varr = (double)s2 / cnt - mu * mu;
                        if (varr < 0) varr = 0;
                        var d = testPx[y * w + x] - tempPx[y * w + x];
                        var ad = Math.Abs((double)d);
                        if (ad < zAbs) continue;
                        if ((ad - mu) / (Math.Sqrt(varr) + zEps) < seedZ) continue;
                        var idx = y * w + x;
                        if (d > 0) { maskPos[idx] = 1; anyPos = true; }
                        else { maskNeg[idx] = 1; anyNeg = true; }
                    }
                }

                if (anyPos) zPos = MaskToZRegion(maskPos, w, h, zCuR, copper);
                if (anyNeg) zNeg = MaskToZRegion(maskNeg, w, h, zCuR, copper);
            }

            HObject MaskToZRegion(byte[] mask, int w, int h, int zCuR, HObject copper)
            {
                var mh = GCHandle.Alloc(mask, GCHandleType.Pinned);
                HObject img, reg;
                try
                {
                    HOperatorSet.GenImage1(out img, "byte", w, h, mh.AddrOfPinnedObject());
                    HOperatorSet.Threshold(img, out reg, 1, 1);
                }
                finally { mh.Free(); }
                img.Dispose();
                if (zCuR > 0)
                {
                    HOperatorSet.DilationCircle(copper, out var cuDil, zCuR);
                    HOperatorSet.Intersection(reg, cuDil, out var regT);
                    reg.Dispose();
                    cuDil.Dispose();
                    return regT;
                }
                return reg;
            }

            // 深度核心收缩：把候选框收到「深度 ≥ frac×峰值」的像素范围。只改 X/Y/W/H，
            // 不动 Runs/Score——金标判定看中心与 IoU，拓扑/配对仍用原物理范围。
            // 物理依据：噪声、标签渲染差、鬼影尾巴会把外接框中心拖出真缺陷；深度峰值区才是真缺陷位置。
            // 框→弱差连通区域生长：官方标注是缺陷完整足迹，阈值化 blob 只是高置信核心。
            // 从现框出发，在「框向外扩展 20px」窗内 BFS 吸收 |diff|≥g 的 4-邻接连通像素；
            // 新边长 ≤ 原 3×（防沿弱差长链泄漏）。只改 X/Y/W/H，不改候选数/分型/Runs/Score。
            
            DefectItem ShrinkToCore(DefectItem d, double frac, bool positive)
            {
                if (frac <= 0 || !d.HasRuns || d.W <= 0 || d.H <= 0) return d;
                var w = frame.Width;
                var rows = d.RunRows!;
                var begs = d.RunColBegin!;
                var ends = d.RunColEnd!;
                var peak = 0;
                for (var r = 0; r < rows.Length; r++)
                {
                    var rowBase = rows[r] * w;
                    for (var x = begs[r]; x <= ends[r]; x++)
                    {
                        var ad = positive ? testPx[rowBase + x] - tempPx[rowBase + x]
                                          : tempPx[rowBase + x] - testPx[rowBase + x];
                        if (ad > peak) peak = ad;
                    }
                }
                if (peak <= 0) return d;
                var cut = frac * peak;
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = -1;
                var maxY = -1;
                var core = 0;
                for (var r = 0; r < rows.Length; r++)
                {
                    var y = rows[r];
                    var rowBase = y * w;
                    for (var x = begs[r]; x <= ends[r]; x++)
                    {
                        var ad = positive ? testPx[rowBase + x] - tempPx[rowBase + x]
                                          : tempPx[rowBase + x] - testPx[rowBase + x];
                        if (ad < cut) continue;
                        core++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (core < 6 || maxX < minX || maxY < minY) return d;
                var nw = maxX - minX + 1;
                var nh = maxY - minY + 1;
                // 只在真正缩小且核心仍成块时替换，避免逐帧抖动。
                if (nw * nh >= d.W * d.H) return d;
                return new DefectItem
                {
                    TypeCode = d.TypeCode,
                    X = minX,
                    Y = minY,
                    W = nw,
                    H = nh,
                    Score = d.Score,
                    RunRows = d.RunRows,
                    RunColBegin = d.RunColBegin,
                    RunColEnd = d.RunColEnd,
                    EdgeFrac = d.EdgeFrac,
                    OppRatio = d.OppRatio
                };
            }

            void TightenStretched(List<DefectItem> cands, int rad, bool extraCopper)
            {
                if (rad <= 0) return;
                byte[]? alt = edgeRawFallback && !ReferenceEquals(rawTempPx, tempPx) ? rawTempPx : null;
                for (var i = 0; i < cands.Count; i++)
                {
                    var d = cands[i];
                    if (!d.HasRuns || d.W <= 0 || d.H <= 0) continue;
                    var asp = d.W >= d.H ? (double)d.W / d.H : (double)d.H / d.W;
                    if (asp < 3) continue;
                    var res = EdgeExplain.Residual(
                        d, testPx, tempPx, alt, frame.Width, frame.Height, thresh, rad, extraCopper);
                    if (res == null) continue;
                    var unexplained = res.Value.total - res.Value.explained;
                    if (unexplained < 4 || unexplained * 2 > d.Score) continue;
                    HOperatorSet.GenRegionRuns(out var reg, new HTuple(res.Value.rows), new HTuple(res.Value.begins), new HTuple(res.Value.ends));
                    HOperatorSet.Connection(reg, out var parts);
                    reg.Dispose();
                    HOperatorSet.CountObj(parts, out var n);
                    DefectItem? best = null;
                    var bestArea = 0.0;
                    for (var j = 1; j <= n.I; j++)
                    {
                        HOperatorSet.SelectObj(parts, out var part, j);
                        try
                        {
                            var (a, aAsp) = BlobAreaAspect(part);
                            if (a < 4 || a * 2 > d.Score || aAsp > maxAspect || aAsp >= asp) continue;
                            if (a <= bestArea) continue;
                            bestArea = a;
                            var item = ToItem(part, d.TypeCode, a).item;
                            item.EdgeFrac = d.EdgeFrac;
                            best = item;
                        }
                        finally { part.Dispose(); }
                    }
                    parts.Dispose();
                    if (best != null) cands[i] = best;
                }
            }

            bool OverlapsBlob(List<DefectItem> cs, HObject b)
            {
                HOperatorSet.SmallestRectangle1(b, out var r1, out var c1, out var r2, out var c2);
                var probe = new DefectItem
                {
                    X = c1.I, Y = r1.I,
                    W = Math.Max(1, c2.I - c1.I + 1),
                    H = Math.Max(1, r2.I - r1.I + 1)
                };
                return OverlapsAny(cs, probe);
            }

            // 缺铜侧分型：能切断铜网的是开路 OPEN；被模板铜全包围的是针孔 PINHOLE；否则边缺口 MOUSEBITE。
            string PosType(HObject blob, double aspect, double surround)
            {
                if (!sixClass) return aspect >= openAspectMin ? "OPEN" : "MOUSEBITE";
                if (typeRefine && distToSubstrate != null && SeversCopper(blob)) return "OPEN";
                if (surround >= surroundMin) return "PINHOLE";
                // 咬宽比：blob 垂直走线方向的宽度 / 该处线宽。贯穿整条线宽＝开路，只咬一角＝鼠咬。
                if (typeRefine && openBiteFrac > 0 && distToSubstrate != null)
                {
                    HOperatorSet.SmallestRectangle1(blob, out var r1, out var c1, out var r2, out var c2);
                    var lateral = Math.Min(c2.I - c1.I + 1, r2.I - r1.I + 1);
                    var (_, localW) = ScaleFeatures(blob, copperRegion, distToSubstrate, scaleWidthR);
                    if (localW > 0 && (double)lateral / localW >= openBiteFrac) return "OPEN";
                }
                return aspect >= openAspectMin ? "OPEN" : "MOUSEBITE";
            }

            // 多铜侧分型：按「邻域内剩下几块铜」三分类——0 块＝不挂线的独立异铜 COPPER；
            // ≥2 块＝跨间隙桥接 SHORT；1 块＝贴在单条铜上的突出物 SPUR（大钝块归余铜）。
            // 不用全局铜网编号（同一走线在远端已连通，会被归成同一个编号而漏掉真短路），改用局部连通数。
            string NegType(HObject blob, double a, double asp)
            {
                if (!sixClass || !typeRefine) return a >= 400 ? "COPPER" : "SHORT";
                var parts = LocalCopperParts(blob, 6.0);
                if (parts >= 2) return "SHORT";
                if (parts == 0) return "COPPER";
                // 贴单条铜的突出物：细/长＝毛刺 SPUR；粗钝且大＝整块余铜 COPPER。
                if (a >= copperBlobMin && asp <= spurAspectMax) return "COPPER";
                return "SPUR";
            }

            // 局部桥接证据：在 blob 外撑 rad 窗内取模板铜、去掉 blob，看分裂成几块。
            int LocalCopperParts(HObject blob, double rad)
            {
                HOperatorSet.DilationCircle(blob, out var win, rad);
                HOperatorSet.Intersection(win, copperRegion, out var local);
                HOperatorSet.Difference(local, blob, out var cut);
                win.Dispose(); local.Dispose();
                HOperatorSet.Connection(cut, out var parts);
                cut.Dispose();
                HOperatorSet.CountObj(parts, out var n);
                parts.Dispose();
                return n.I;
            }

            bool CandidateSevers(DefectItem d)
            {
                if (!d.HasRuns || distToSubstrate == null) return false;
                HOperatorSet.GenRegionRuns(out var reg, new HTuple(d.RunRows!), new HTuple(d.RunColBegin!), new HTuple(d.RunColEnd!));
                try { return SeversCopper(reg); }
                finally { reg.Dispose(); }
            }

            bool HitsBorder(HObject blob)
            {
                if (borderMargin <= 0) return false;
                HOperatorSet.SmallestRectangle1(blob, out var r1, out var c1, out var r2, out var c2);
                return c1.I <= borderMargin || r1.I <= borderMargin ||
                       c2.I >= frame.Width - 1 - borderMargin || r2.I >= frame.Height - 1 - borderMargin;
            }

            bool SeversCopper(HObject blob)
            {
                // 局部窗：blob 膨胀内的模板铜，去掉 blob 后连通数增加才算切断。窗足够大，避免贴边裁断。
                // 窗太小→走线宽时两侧铜在窗内已分叉（误判）；窗太大→鼠咬缺口也算切断（串类）。
                HOperatorSet.DilationCircle(blob, out var win, severWin);
                HOperatorSet.Intersection(win, copperRegion, out var local);
                win.Dispose();
                HOperatorSet.Connection(local, out var before);
                HOperatorSet.CountObj(before, out var nBefore);
                before.Dispose();
                // 阈值 blob 只是缺口核心，实际吃铜更宽：剪前适度膨胀才能真断开。
                HObject cutSrc = blob;
                if (severDilate > 0)
                {
                    HOperatorSet.DilationCircle(blob, out var wide, severDilate);
                    cutSrc = wide;
                }
                HOperatorSet.Difference(local, cutSrc, out var cut);
                if (!ReferenceEquals(cutSrc, blob)) cutSrc.Dispose();
                local.Dispose();
                HOperatorSet.Connection(cut, out var after);
                cut.Dispose();
                HOperatorSet.CountObj(after, out var nAfter);
                after.Dispose();
                return nAfter.I > nBefore.I && nBefore.I >= 1;
            }

            // 测试图铜区（惰性）：open/mousebite 的物理判据要看「实测图里铜断没断」。
            HObject? copperTestRegion = null;
            HObject CuTest()
            {
                if (copperTestRegion == null)
                    HOperatorSet.Threshold(hoTest, out copperTestRegion, 0, copperMax);
                return copperTestRegion;
            }

            // 候选特征：全部按线宽归一化，供分型树 / 置信度使用（不做任何判定）。
            DefectFeatureRow MeasureCandidate(HObject reg, DefectItem d)
            {
                var (area, asp) = BlobAreaAspect(reg);
                var bw = Math.Max(1, d.W);
                var bh = Math.Max(1, d.H);
                var row = new DefectFeatureRow
                {
                    Pair = frame.SourcePath == null ? "" : System.IO.Path.GetFileNameWithoutExtension(frame.SourcePath),
                    PredType = d.TypeCode,
                    X = d.X, Y = d.Y, W = d.W, H = d.H,
                    Area = area, Aspect = asp, Fill = area / (bw * bh),
                    EdgeFrac = d.EdgeFrac, OppRatio = d.OppRatio
                };

                // 极性 + 峰值/均值：直接扫 runs，避开有符号 int2 图上的取极值语义歧义。
                long sum = 0;
                var n = 0;
                var peakPos = 0;
                var peakNeg = 0;
                for (var r = 0; r < d.RunRows!.Length; r++)
                {
                    var y = d.RunRows[r];
                    if ((uint)y >= (uint)frame.Height) continue;
                    var rowBase = y * frame.Width;
                    for (var x = d.RunColBegin![r]; x <= d.RunColEnd![r]; x++)
                    {
                        if ((uint)x >= (uint)frame.Width) continue;
                        var dv = testPx[rowBase + x] - tempPx[rowBase + x];
                        if (dv > peakPos) peakPos = dv;
                        if (-dv > peakNeg) peakNeg = -dv;
                        sum += Math.Abs(dv);
                        n++;
                    }
                }
                row.Polarity = peakPos >= peakNeg ? 1 : -1;
                row.PeakDiff = Math.Max(peakPos, peakNeg);
                row.MeanDiff = n > 0 ? (double)sum / n : 0;

                // 线宽 / 咬深（铜距场）：缺铜侧与多铜侧都可算，线宽是共用基准。
                if (distToSubstrate != null)
                {
                    var (depth, lw) = ScaleFeatures(reg, copperRegion, distToSubstrate, scaleWidthR);
                    row.MaxDepth = depth;
                    row.LocalWidth = lw;
                    row.Bite = lw > 0 ? depth / lw : 0;
                    row.Lateral = lw > 0 ? Math.Min(bw, bh) / lw : 0;
                }
                else
                {
                    row.LocalWidth = Math.Max(1.0, Math.Min(bw, bh));
                    row.Bite = 0;
                    row.Lateral = 1;
                }

                // 拓扑证据。
                row.Severed = distToSubstrate != null && SeversCopper(reg) ? 1 : 0;
                row.Nets = UnionNets(NetLabels(), frame.Width, frame.Height, reg, 2);
                row.Parts = LocalCopperParts(reg, 6.0);

                // 包围环铜占比（针孔判据）。
                HObject ringDil, ring, ringCu, ringConn;
                HOperatorSet.DilationCircle(reg, out ringDil, surroundR);
                HOperatorSet.Difference(ringDil, reg, out ring);
                HOperatorSet.Intersection(ring, copperRegion, out ringCu);
                HOperatorSet.Connection(ringCu, out ringConn);
                HTuple ringArea, ringCuArea;
                HOperatorSet.AreaCenter(ring, out ringArea, out _, out _);
                HOperatorSet.AreaCenter(ringCu, out ringCuArea, out _, out _);
                row.Surround = ringArea.D > 0 ? ringCuArea.D / ringArea.D : 0.0;
                ringConn.Dispose();
                ringCu.Dispose();
                ring.Dispose();
                ringDil.Dispose();

                row.AwayFromCopper = CopperGate.AwayFromCopper(reg, copperRegion, edgeRadNeg, ref copperDilNeg) ? 1 : 0;

                // 切断度：缺陷邻域内铜的分块结构。开路=走线被切断→两块都显著；鼠咬→仍连通。
                // Split/SplitN=2×线宽窗（置信度 S4 的 split/splitN 特征来源，权重已验证）；
                // SplitB=1.5×线宽窗——分型树的 split 判据（v1.2.1 起）：小窗减少邻域无关铜干扰，
                //   train1000 实测把 open↔mousebite 错分 99→65、总分型错误 209→175，5 折全赢，
                //   且树阈值无需重调（网格搜索确认 0.4747/0.1655/0.1794 对 SplitB 分布仍最优）。
                // SplitT/SplitC=已证伪实验（见 reports/fit_v13.log：T 灾难性、C 与 Split 逐位相同），
                //   仅 dump 时计算留档，产线零开销。
                (double, int) LocalSplit(HObject copper, HObject reg0, double winR, double areaCut, bool subtractBlob)
                {
                    HOperatorSet.DilationCircle(reg0, out var win, winR);
                    HOperatorSet.Intersection(copper, win, out var local);
                    win.Dispose();
                    HObject src = local;
                    if (subtractBlob)
                    {
                        HOperatorSet.Difference(local, reg0, out var cut0);
                        local.Dispose();
                        src = cut0;
                    }
                    HOperatorSet.Connection(src, out var parts);
                    src.Dispose();
                    HTuple nParts;
                    HOperatorSet.CountObj(parts, out nParts);
                    var ar = new List<double>(nParts.I);
                    for (var k = 1; k <= nParts.I; k++)
                    {
                        HOperatorSet.SelectObj(parts, out var one, k);
                        try
                        {
                            HTuple pa;
                            HOperatorSet.AreaCenter(one, out pa, out _, out _);
                            if (pa.D >= areaCut) ar.Add(pa.D);
                        }
                        finally { one.Dispose(); }
                    }
                    parts.Dispose();
                    ar.Sort();
                    ar.Reverse();
                    return (ar.Count >= 2 && ar[0] > 0 ? ar[1] / ar[0] : 0.0, ar.Count);
                }

                var splitWinR = Math.Max(10.0, 2.0 * row.LocalWidth);
                (row.Split, row.SplitN) = LocalSplit(CuTest(), reg, splitWinR, 15, false);
                row.SplitB = LocalSplit(CuTest(), reg, Math.Max(8.0, 1.5 * row.LocalWidth), 15, false).Item1;
                if (FeatureSink != null)
                {
                    row.SplitC = LocalSplit(CuTest(), reg, splitWinR, 6, false).Item1;
                    row.SplitT = LocalSplit(copperRegion, reg, splitWinR, 15, true).Item1;
                }
                return row;
            }

            StageTimer.Add("4_neg_blob_gate", tStage);
            tStage = StageTimer.Mark();
            // 先把框长到缺陷足迹，特征仍按核心 runs 量（核心才是高置信证据），
            // 但输出/匹配用的 X/Y/W/H 是最终足迹，与评测器看到的一致。
            defects = GrowStage(defects, _recipe.RegionGrowDiff, testPx, tempPx, frame.Width, frame.Height, P);
            StageTimer.Add("5_grow", tStage);
            tStage = StageTimer.Mark();

            // ---- v1.0.0 候选特征：分型重判 + 置信度标定（只改标签与排序键，不改检出）----
            if ((FeatureSink != null || _recipe.TypeTree || _recipe.FeatureConfidence) && defects.Count > 0)
            {
                for (var i = 0; i < defects.Count; i++)
                {
                    var d = defects[i];
                    if (!d.HasRuns) continue;
                    HOperatorSet.GenRegionRuns(out var featReg,
                        new HTuple(d.RunRows!), new HTuple(d.RunColBegin!), new HTuple(d.RunColEnd!));
                    try
                    {
                        var row = MeasureCandidate(featReg, d);
                        var type = _recipe.TypeTree ? PcbTypeTree.Classify(row) : d.TypeCode;
                        var conf = _recipe.FeatureConfidence ? PcbConfidence.Score(row) : d.Confidence;
                        if (FeatureSink != null)
                        {
                            row.Pair = frame.SourcePath ?? "";
                            row.Confidence = conf; // v1.2.1 补：dump conf 列回填（此前恒为零，Classify/Score 均为纯函数，先算后写不影响产出）
                            FeatureSink.Add(row);
                        }
                        if (type != d.TypeCode || Math.Abs(conf - d.Confidence) > 1e-9)
                        {
                            defects[i] = new DefectItem
                            {
                                TypeCode = type,
                                X = d.X, Y = d.Y, W = d.W, H = d.H,
                                Score = d.Score,
                                RunRows = d.RunRows, RunColBegin = d.RunColBegin, RunColEnd = d.RunColEnd,
                                EdgeFrac = d.EdgeFrac, OppRatio = d.OppRatio, Confidence = conf
                            };
                        }
                    }
                    finally { featReg.Dispose(); }
                }
            }

            StageTimer.Add("6_classify_conf", tStage);
            selected.Dispose();
            connected.Dispose();
            opened.Dispose();
            region.Dispose();
            negRaw?.Dispose();
            zPos?.Dispose();   // sixClass=false 时负极性 z 区域不会被并走，在此收尾
            zNeg?.Dispose();
            closedPos?.Dispose();
            distToSubstrate?.Dispose();
            copperTestRegion?.Dispose();
            copperRegion.Dispose();
            diffI.Dispose();
            return defects;
        }
        finally
        {
            if (modelId != null && modelId.Length > 0)
            {
                try { HOperatorSet.ClearShapeModel(modelId); } catch { /* ignore */ }
            }
            hoTempAligned?.Dispose();
            hoTemp?.Dispose();
            hoTest?.Dispose();
            if (tempHandle.IsAllocated) tempHandle.Free();
            if (testHandle.IsAllocated) testHandle.Free();
        }
    }

#endif
}
