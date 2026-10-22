using DeepPCBStation.Core.Config;

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 检测配方解析结果：把 <see cref="RecipeConfig"/> 摊平成「已解析好的标量」，
/// 供各检测阶段直接读取，避免在每个阶段里重复写「&gt;0 ? 用值 : 默认」的兜底表达式。
///
/// 字段名沿用原 <c>HalconPcbDefectDetector.DetectCore</c> 中的局部变量名，
/// 便于与历史实现逐行对照（仅搬家 + 命名，不改任何取值逻辑）。
/// </summary>
public sealed class PcbRecipeParams
{
    // ---- 差分 / 面积 / 形态学 ----
    public int thresh;
    public double minArea;
    public double minOpen;
    public double minMb;
    public double selectMin;
    /// <summary>按类型面积闸（默认关）。开启后分型完成时按类各自下限复核，见 <see cref="RecipeConfig.TypeAwareAreaGate"/>。</summary>
    public bool typeAwareArea;
    public double maxArea;
    public int openR;
    public double openRNeg;
    public double closingR;
    public double closingRNeg;

    // ---- 母团救援 ----
    public int motherRescueArea;
    public int rescueCap;
    public int residueGrowMaxDim;

    // ---- 铜区 / 针孔 ----
    public int copperMax;
    public double surroundR;
    public double surroundMin;

    // ---- 形状闸 ----
    public double maxAspect;
    public double tipSurroundMax;
    public double tipMaxWidth;
    public double minThickness;
    public int borderMargin;
    public double borderMinAspect;
    public bool sixClass;

    // ---- 边缘解释闸 ----
    public double edgeMin;
    public int edgeRad;
    public bool edgeRawFallback;
    public bool edgePrune;
    public double edgeForcePruneArea;
    public double edgeSkipDepth;
    public double edgeMinNeg;
    public int edgeShallowDiff;
    public double edgeShallowMin;
    public int edgeRadNeg;

    // ---- LOP（局部最优平移复核） ----
    public int lopR;
    public double lopMin;
    public double lopDepthSkip;
    public double lopOppMin;
    public double lopTF;
    public bool lopRescue;
    public int lopThresh;
    public bool lopReviewAll;
    public double lopReviewMin;

    // ---- 双极性配对 / 同极性去重 ----
    public double pairR;
    public double pairGhostFrac;
    public bool pairNetKeep;
    public double pairKeepArea;
    public int mergeR;
    public double mergeRatio;
    public double satR;
    public double satRatio;

    // ---- 分型 / 拓扑 ----
    public double coreBoxFrac;
    public bool typeRefine;
    public double copperBlobMin;
    public double spurAspectMax;
    public double openAspectMin;
    public double severWin;
    public double severDilate;
    public double openBiteFrac;
    public int topoMin;
    public int topoTouch;

    // ---- 区域生长 / 足迹 ----
    public int growMargin;
    public double growMaxScale;
    public double growBoxScale;
    public double growBoxElong;
    public double growBoxPad;
    public bool growDiag;
    public int growIters;
    public double fpTau;
    public int fpMargin;

    // ---- 局部 z 通道（弱对比加性种子；SeedZ=0 关） ----
    public double seedZ;
    public int seedZAbs;
    public double zEps;
    public int zWindow;
    public int zCopperRadius;

    // ---- 配准 / 梯度补偿 / 灰度膨胀 ----
    public int gd;
    public double scaleArea;
    public double scaleDepth;
    public bool scaleOn;
    public double scaleWidthR;

    // ---- 其它闸门 ----
    public double posCopperMin;
    public bool negAwayCu;
    public bool netRescue;
    public bool bridgeSkipEdge;
    public bool bridgeThin;

    /// <summary>按原 <c>DetectCore</c> 中的求值顺序构造（依赖关系：minOpen/minMb→selectMin、edgeMin→edgeMinNeg/edgeShallowMin、thresh+lopTF→lopThresh…）。</summary>
    public PcbRecipeParams(RecipeConfig r)
    {
        thresh = r.DiffThreshold > 0 ? r.DiffThreshold : 30;
        minArea = Math.Max(1.0, r.MinBlobArea);
        minOpen = r.MinBlobAreaOpen > 0 ? (double)r.MinBlobAreaOpen : minArea;
        minMb = r.MinBlobAreaMousebite > 0 ? (double)r.MinBlobAreaMousebite : minArea;
        selectMin = Math.Min(minOpen, minMb);
        typeAwareArea = r.TypeAwareAreaGate;
        motherRescueArea = r.MotherRescueArea;
        // 每板最多救援几个残渣：不设上限时大母团会捞出海量残渣，实测 50 时整板跑不完。
        rescueCap = r.MotherRescueCap > 0 ? r.MotherRescueCap : 24;
        residueGrowMaxDim = r.ResidueGrowMaxDim;
        // 与 RecipeFileDto 的 `?? 8000`、RecipeConfig 属性初始值 8000 对齐（三处同一值）
        maxArea = r.MaxBlobArea > 0 ? (double)r.MaxBlobArea : 8000.0;
        openR = Math.Max(1, r.OpenRadius);
        openRNeg = r.OpenRadiusNeg;
        copperMax = r.CopperMaxGray > 0 ? r.CopperMaxGray : 100;
        surroundR = r.SurroundRadius > 0 ? r.SurroundRadius : 4.0;
        surroundMin = r.SurroundCopperMin > 0 ? r.SurroundCopperMin : 0.95;
        maxAspect = r.MaxAspect > 0 ? r.MaxAspect : 6.0;
        // ≤0：关闭线端过滤（0 不再回落到 0.55，便于留出集关掉误杀）
        tipSurroundMax = r.TipSurroundMax;
        tipMaxWidth = r.TipMaxWidth;
        minThickness = r.MinThickness;
        borderMargin = r.BorderMargin;
        borderMinAspect = r.BorderMinAspect;
        sixClass = r.SixClass;
        edgeMin = r.EdgeExplainMin;
        edgeRad = r.EdgeExplainRadius > 0 ? r.EdgeExplainRadius : 2;
        edgeRawFallback = r.EdgeExplainRawFallback;
        edgePrune = r.EdgePrune;
        edgeForcePruneArea = r.EdgeForcePruneArea;
        edgeSkipDepth = r.EdgeSkipDepth;
        edgeMinNeg = r.EdgeExplainMinNeg > 0 ? r.EdgeExplainMinNeg : edgeMin;
        edgeShallowDiff = r.EdgeShallowDiff;
        edgeShallowMin = r.EdgeShallowMin > 0 ? r.EdgeShallowMin : edgeMin * 0.7;
        edgeRadNeg = r.EdgeExplainRadiusNeg > 0 ? r.EdgeExplainRadiusNeg : edgeRad;
        lopR = r.LopRadius;
        lopMin = r.LopExplainMin > 0 ? r.LopExplainMin : 0.98;
        lopDepthSkip = r.LopDepthSkip;
        lopOppMin = r.LopOppMin;
        lopTF = r.LopThreshFrac > 0 ? r.LopThreshFrac : 0.5;
        lopRescue = r.LopRescue;
        lopThresh = Math.Max(4, (int)Math.Round(thresh * lopTF));
        closingR = r.ClosingRadius;
        closingRNeg = r.ClosingRadiusNeg;
        pairR = r.PairPolarityRadius;
        pairGhostFrac = r.PairGhostFrac;
        pairNetKeep = r.PairNetKeep;
        pairKeepArea = r.PairKeepArea;
        mergeR = r.MergeRadius;
        mergeRatio = r.MergeScoreRatio;
        satR = r.SatelliteRadius;
        satRatio = r.SatelliteRatio > 0 ? r.SatelliteRatio : 0.3;
        coreBoxFrac = r.CoreBoxFrac;
        typeRefine = r.TypeRefineTopology;
        copperBlobMin = r.CopperBlobArea > 0 ? r.CopperBlobArea : 400;
        spurAspectMax = r.SpurAspectMax > 0 ? r.SpurAspectMax : 2.5;
        openAspectMin = r.OpenAspectMin > 0 ? r.OpenAspectMin : 2.2;
        severWin = r.OpenSeverRadius > 0 ? r.OpenSeverRadius : 12.0;
        growMargin = r.GrowMargin > 0 ? r.GrowMargin : 20;
        growMaxScale = r.GrowMaxScale > 0 ? r.GrowMaxScale : 3.0;
        growBoxScale = r.GrowBoxScale > 1.001 ? r.GrowBoxScale : 0.0;
        growBoxElong = r.GrowBoxElong > 1.001 ? r.GrowBoxElong : 0.0;
        growBoxPad = r.GrowBoxPad > 0.001 ? r.GrowBoxPad : 0.0;
        growDiag = r.GrowDiagonal;
        growIters = r.GrowIters > 0 ? r.GrowIters : 3;
        fpTau = r.FootprintTau;
        fpMargin = r.FootprintMargin > 0 ? r.FootprintMargin : 8;
        // z 通道三兜底值（12 / 2.0 / 16）从 HalconPcbDefectDetector 内联收编到此（H8：单一真源）。
        // 表达式与原内联逐位一致：仅搬家，不改取值逻辑（SeedZ=0 时整段不执行，逐像素等价）。
        seedZ = r.SeedZ;
        seedZAbs = r.SeedZAbs > 0 ? r.SeedZAbs : 12;
        zEps = r.ZEps > 0 ? r.ZEps : 2.0;
        zWindow = r.ZWindow >= 4 ? r.ZWindow : 16;
        zCopperRadius = r.ZCopperRadius;
        severDilate = r.OpenSeverDilate;
        openBiteFrac = r.OpenBiteFrac;
        lopReviewAll = r.LopReviewAll;
        lopReviewMin = r.LopReviewMin > 0 ? r.LopReviewMin : lopMin;
        netRescue = r.EdgeNetRescue;
        bridgeSkipEdge = r.BridgeSkipEdge;
        bridgeThin = r.BridgeThinRescue;
        posCopperMin = r.PosCopperCoverMin;
        negAwayCu = r.NegAwayCopperRescue;
        topoMin = r.TopoMinArea;
        topoTouch = r.TopoTouch > 0 ? r.TopoTouch : 6;
        gd = r.TemplateGrayDilation > 0 ? r.TemplateGrayDilation : 0;
        scaleArea = r.ScaleMinAreaCoeff;
        scaleDepth = r.ScaleMinDepthFrac;
        scaleOn = scaleArea > 0 || scaleDepth > 0;
        scaleWidthR = r.ScaleWidthRadius > 0 ? r.ScaleWidthRadius : 8.0;
    }
}
