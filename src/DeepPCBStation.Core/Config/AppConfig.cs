using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepPCBStation.Core.Storage;

namespace DeepPCBStation.Core.Config;

public sealed class AppConfig
{
    public CameraConfig Camera { get; set; } = new();
    public DetectionConfig Detection { get; set; } = new();
    public CaptureQualityConfig CaptureQuality { get; set; } = new();
    public ReviewPolicyConfig ReviewPolicy { get; set; } = new();
    public TcpConfig Tcp { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public StatsConfig Stats { get; set; } = new();
    public List<RecipeConfig> Recipes { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return CreateDefault();

        var json = File.ReadAllText(path);
        // 配方唯一权威源是 recipes/ 目录（见 RecipeStore.LoadFromDirectory）。
        // appsettings 不再内嵌配方副本：历史四级回落（v0.2.0/v1.0.0/recipes/v1.2.1）
        // 曾让「以为在跑 A 实际在跑 B」，静默且不可诊断。目录缺失时由调用方抛
        // 「至少需要一条配方」，快速失败好过静默跑旧参数。
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions()) ?? CreateDefault();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions()), new UTF8Encoding(false));
    }

    /// <summary>
    /// 用户级配置路径：<c>%APPDATA%\DeepPCBStation\appsettings.json</c>。
    ///
    /// 运行期在 UI 上改的设置必须写这里，而不是程序目录：
    ///   1) 程序目录的 appsettings.json 由构建从源码树复制（PreserveNewest），
    ///      UI 保存的修改会在下一次构建被**静默覆盖**——现场改过的参数凭空消失；
    ///   2) 部署到 Program Files 这类只读目录时根本写不进去。
    /// 出厂默认仍随程序分发（程序目录 appsettings.json）；用户级文件存在时整体覆盖它。
    /// </summary>
    public static string UserConfigPath() => Path.Combine(UserConfigDir(), "appsettings.json");

    /// <summary>
    /// 用户级目录 <c>%APPDATA%\DeepPCBStation</c>。运行期产物（UI 设置、日志）一律落这里，
    /// 不落程序目录（构建会被覆盖、Program Files 下不可写）。
    /// </summary>
    public static string UserConfigDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(string.IsNullOrEmpty(appData) ? "." : appData, "DeepPCBStation");
    }

    /// <summary>
    /// 解析实际生效的配置：用户级（若存在）→ 程序目录出厂默认。
    /// 返回配置本体、其路径与来源，便于界面显示「当前生效的是哪一份」。
    /// </summary>
    public static (AppConfig Config, string Path, bool FromUserDir) LoadResolved(string appDirPath)
    {
        var userPath = UserConfigPath();
        if (File.Exists(userPath))
            return (Load(userPath), userPath, true);
        return (Load(appDirPath), appDirPath, false);
    }

    public static AppConfig CreateDefault() => new()
    {
        Camera = new CameraConfig
        {
            Mode = "PairedFilePlayback",
            Width = 640,
            Height = 640,
            PlaybackRootDir = "data/samples",
            PlaybackManifest = "Manifest.txt",
            PlaybackLoop = true
        },
        Detection = new DetectionConfig
        {
            Engine = "Halcon",
            DiffThreshold = 30,
            MinBlobArea = 40,
            MaxBlobArea = 8000,
            OpenRadius = 1
        },
        Tcp = new TcpConfig { Port = 9100, Enabled = true, TimeoutMs = 5000, TestInjectDelayMs = 0 },
        Storage = new StorageConfig
        {
            OutputRoot = "results",
            SaveImages = true,
            LogFormat = "both"
        },
        Ui = new UiConfig
        {
            AutoLockMinutes = 15,
            AlarmSound = false,
            AlarmSoundVolume = 60
        },
        Stats = new StatsConfig
        {
            TargetYieldPct = 95,
            AlertOnDppm = 0
        }
        // 注意：不在此内嵌配方。配方唯一权威源是 recipes/ 目录；
        // appsettings.json 只承载相机/检测引擎/TCP/存储/UI 等非算法配置。
    };

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed class CameraConfig
{
    /// <summary>PairedFilePlayback | Mock</summary>
    public string Mode { get; set; } = "PairedFilePlayback";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 640;
    public int SimulateExposureMs { get; set; } = 8;
    public double DefectProbability { get; set; } = 0.35;
    public int NoiseLevel { get; set; } = 8;

    public string PlaybackRootDir { get; set; } = "data/samples";
    public string? PlaybackManifest { get; set; }
    public bool PlaybackLoop { get; set; } = true;
}

public sealed class DetectionConfig
{
    /// <summary>Halcon（交付）| Stub（仅编译）</summary>
    public string Engine { get; set; } = "Halcon";
    /// <summary>差分阈值（AbsDiff 后 Threshold 下限）。</summary>
    public int DiffThreshold { get; set; } = 30;
    public int MinBlobArea { get; set; } = 40;
    public int MaxBlobArea { get; set; } = 8000;
    public int OpenRadius { get; set; } = 1;
}

/// <summary>
/// 采图有效性自检配置（工业 AOI 必备）：把「采图异常」从 NG 里摘出来，
/// 判为「采集无效 · 请重拍」，不计入过杀也不计入良率。
/// 判据全部是 test 相对 template 的相对量，换型/换批次不需重新标定。
/// </summary>
/// <remarks>默认 Enabled=false（现状行为，零风险）；阈值需在目标产线量测后标定。</remarks>
public sealed class CaptureQualityConfig
{
    /// <summary>总开关。false=不做自检（现状行为）。</summary>
    public bool Enabled { get; set; }

    /// <summary>锐度比下限：test/temp 低于此值判失焦。</summary>
    public double BlurRatioMin { get; set; } = 0.6;

    /// <summary>锐度绝对下限（无模板时用）；0=不启用绝对闸。</summary>
    public double BlurSharpnessMin { get; set; } = 1.0;

    /// <summary>高饱和判据灰度余量：≥255−margin 视为饱和。</summary>
    public int HiMargin { get; set; } = 5;

    /// <summary>近黑判据灰度上限。</summary>
    public int LoMargin { get; set; } = 5;

    /// <summary>
    /// 高饱和占比上限。PCB 图双峰（铜≈255/基材≈0），实测正常板 satHi 中位 67%、最大 98.65%，
    /// 故绝对上限默认关闭（1.01 不可达），曝光判据实际走相对增量 <see cref="SatExcessOverTemplate"/>。
    /// </summary>
    public double SatHiMax { get; set; } = 1.01;

    /// <summary>近黑占比上限。实测正常板最大 81.75%，同样默认关闭。</summary>
    public double SatLoMax { get; set; } = 1.01;

    /// <summary>饱和判据要求「比模板高出」的绝对量，避免批次本身亮/暗被误判。</summary>
    public double SatExcessOverTemplate { get; set; } = 0.05;

    /// <summary>
    /// 对比度比下限（曝光主判据）。PCB 图基材本就 255 饱和，过曝的真实表现是对比度塌陷，
    /// 故用 test/temp 标准差比判定，再按均值偏移方向区分过曝/欠曝。
    /// </summary>
    public double ContrastRatioMin { get; set; } = 0.7;

    /// <summary>判定过曝/欠曝所需的均值偏移量（灰度级）。</summary>
    public double MeanShiftForExposure { get; set; } = 10.0;

    /// <summary>灰度标准差下限，低于此值判空图。</summary>
    public double StdMin { get; set; } = 3.0;

    /// <summary>配准残差上限（残余异号像素比例）；超出判配准失败（疑似上错料号）。</summary>
    public double RegisterResidualMax { get; set; } = 0.20;

    /// <summary>配准残差搜索的降采样倍率。</summary>
    public int RegisterDownsample { get; set; } = 4;

    /// <summary>配准残差搜索的最大整数位移（像素）。</summary>
    public int RegisterMaxShift { get; set; } = 8;

    /// <summary>转为 <see cref="Imaging.CaptureQuality.Config"/>。</summary>
    public Imaging.CaptureQuality.Config ToGate() => new Imaging.CaptureQuality.Config
    {
        Enabled = Enabled,
        BlurRatioMin = BlurRatioMin,
        BlurSharpnessMin = BlurSharpnessMin,
        HiMargin = HiMargin,
        LoMargin = LoMargin,
        SatHiMax = SatHiMax,
        SatLoMax = SatLoMax,
        SatExcessOverTemplate = SatExcessOverTemplate,
        ContrastRatioMin = ContrastRatioMin,
        MeanShiftForExposure = MeanShiftForExposure,
        StdMin = StdMin,
        RegisterResidualMax = RegisterResidualMax,
        RegisterDownsample = RegisterDownsample,
        RegisterMaxShift = RegisterMaxShift
    };
}

/// <summary>
/// 复判（疑似）策略：低置信度缺陷不直接判 NG，转为「复判」态交人工确认。
/// 工业上 FCR（假点率）高的主要来源就是「把疑似直接当缺陷判 NG」。
/// </summary>
public sealed class ReviewPolicyConfig
{
    /// <summary>总开关。false=现状（有缺陷即 NG）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 复判带：判定置信度低于此值的缺陷，整板转「复判」而非 NG。
    /// 置信度为 PcbConfidence 的 logistic 输出（0~1）。
    /// </summary>
    public double ConfidenceBelow { get; set; } = 0.35;

    /// <summary>整板缺陷数超过此值时直接判 NG，不再转复判（缺陷太多没必要逐个人工看）。</summary>
    public int ForceNgAboveCount { get; set; } = 8;
}

public sealed class TcpConfig
{
    public int Port { get; set; } = 9100;
    public bool Enabled { get; set; }
    public int TimeoutMs { get; set; } = 5000;
    public int TestInjectDelayMs { get; set; } = 0;
}

public sealed class StorageConfig
{
    public string OutputRoot { get; set; } = "results";
    public bool SaveImages { get; set; } = true;
    public string LogFormat { get; set; } = "both";
    /// <summary>SQLite 数据库文件（相对仓库根）。</summary>
    public string DbPath { get; set; } = "results/station.db";
}

/// <summary>UI 行为配置：空闲锁屏与告警声音。</summary>
public sealed class UiConfig
{
    /// <summary>无操作多久后遮罩锁屏（分钟）；0=不锁屏。检测不停，仅锁 UI。</summary>
    public int AutoLockMinutes { get; set; } = 15;
    /// <summary>Critical 新告警时是否发声。默认关（符线体环境习惯）。</summary>
    public bool AlarmSound { get; set; }
    /// <summary>提示音量 0-100。</summary>
    public int AlarmSoundVolume { get; set; } = 60;
}

/// <summary>统计口径配置：目标良率与质量提醒线。</summary>
public sealed class StatsConfig
{
    /// <summary>目标直通率/良率（%），低于此值的日期标红。</summary>
    public double TargetYieldPct { get; set; } = 95;
    /// <summary>DPPM 提醒阀值；0=不提醒。</summary>
    public int AlertOnDppm { get; set; }
}

public sealed class RecipeConfig
{
    public string Id { get; set; } = "pcb_open_mousebite";
    public string Name { get; set; } = "PCB-开路鼠咬";
    public string Version { get; set; } = "0.2.0";
    public string ContentHash { get; set; } = "";
    public int DiffThreshold { get; set; } = 30;
    public int MinBlobArea { get; set; } = 40;
    /// <summary>open 类 Blob area 下界（可选；0=用 MinBlobArea）。</summary>
    public int MinBlobAreaOpen { get; set; }
    /// <summary>mousebite 类 Blob area 下界（可选；0=用 MinBlobArea）。</summary>
    public int MinBlobAreaMousebite { get; set; }
    /// <summary>
    /// 按类型面积闸（默认关 = 逐位等价现状）。关闭时全局预筛与分型后闸门都用
    /// <c>selectMin = min(MinBlobAreaOpen, MinBlobAreaMousebite)</c>，导致两类的面积下限
    /// 实际上永远是较小的那个（哪个类设得更小，另一个类的下限就被它顶掉）。
    /// 开启后：预筛仍用 selectMin 保证候选不被提前杀掉，分型完成后再按**该类自己的下限**
    /// 判定（OPEN/PINHOLE 用 MinBlobAreaOpen，MOUSEBITE 用 MinBlobAreaMousebite）。
    /// 注意：只开启本开关、不改两类下限时结果与关闭逐位相同（两个下限都回落到 MinBlobArea）；
    /// 真正回收漏检还需在拟合集上定出更低的分类下限并过四集零退化验收。
    /// </summary>
    public bool TypeAwareAreaGate { get; set; }
    /// <summary>
    /// <summary>
    /// 母团救援阈值（0=关闭）。开运算会把小面积浅咬缺陷切碎，只留边缘残渣（残渣面积可低至 6~21，
    /// 而框内差分像素达 17~67）。开运算前连通域（母团）面积 ≥ 本值时，母团内残渣按原 selectMin 之外的
    /// 小面积也放行。母团面积须同时 ≤ MaxBlobArea，防止成片大块误捞。
    /// </summary>
    public int MotherRescueArea { get; set; }
    /// <summary>
    /// 残渣生长闸（0=关）。母团救援放行的残渣先沿 |test-temp| 连通域长回缺陷足迹，
    /// 再要求生长后包围盒<strong>最大边 ≤ 此值</strong>才放行；超过则丢弃。
    /// 依据（tools/residue_grow_sim.py，train1000 的 area&lt;min + 开运算抹掉共 27 例）：
    /// 残渣原 IoU 中位仅 0.017（必漏检），生长后 maxDim≤50 的 21 例<strong>全部 IoU≥0.33（精确率 100%）</strong>，
    /// 而被丢弃的 6 例 maxDim 为 68~120（差分连通域洪泛失控）。
    /// 即「生长框最大边」是零代价判据：救回漏检的同时不引入任何 FP。需 MotherRescueArea>0 才生效。
    /// </summary>
    public int ResidueGrowMaxDim { get; set; }
    /// <summary>母团救援每板上限（0=用默认 24）。不设上限时大母团会捞出海量残渣导致超时。</summary>
    public int MotherRescueCap { get; set; }
    public int MaxBlobArea { get; set; } = 8000;
    public int OpenRadius { get; set; } = 1;
    /// <summary>模板铜区上界灰度（深色=铜）。用于铜 ROI 与针孔 surround 判别。</summary>
    public int CopperMaxGray { get; set; } = 100;
    /// <summary>surround 判别膨胀半径（像素）。</summary>
    public double SurroundRadius { get; set; } = 4.0;
    /// <summary>缺铜 blob 四周铜占比 ≥ 此值判为 pin-hole 并丢弃。</summary>
    public double SurroundCopperMin { get; set; } = 0.95;
    /// <summary>外接矩长宽比上限；超过判为边缘细条噪声（非缺陷）丢弃。</summary>
    public double MaxAspect { get; set; } = 6.0;
    /// <summary>多铜（负极性）开运算半径。0=与 OpenRadius 相同；-1=跳过开运算（负极性细短路会被 r=1 开运算抹掉）；&gt;0 用该值。</summary>
    public double OpenRadiusNeg { get; set; }
    /// <summary>缺铜 blob 四周铜只有一个连通分量（位于铜线末端）且铜占比低于此值 → 跨板线长差异噪声，丢弃。</summary>
    public double TipSurroundMax { get; set; } = 0.55;
    /// <summary>线端判定的局部铜线宽上限（0=关）。>0 时线端规则额外要求 localWidth &lt; 此值，避免把宽焊盘上的鼠咬当线端。</summary>
    public double TipMaxWidth { get; set; }
    /// <summary>blob 内切半径下限（0=关）。>0 时内切半径 &lt; 此值的细条（厚度≤1px 类噪声）被丢弃。</summary>
    public double MinThickness { get; set; }
    /// <summary>图像边界闸（0=关）。外接框触及边界 margin 内的 blob 丢弃（DeepPCB 分区拼接在图缘产生的错位条带；GT 缺陷极少贴边）。</summary>
    public int BorderMargin { get; set; }
    /// <summary>边界闸形状豁免（0=关）。&gt;0 时仅长宽比 ≥ 此值的贴边 blob 才丢弃——拼接伪影是细长条，矮胖的真缺陷（贴边的小鼠咬）豁免。</summary>
    public double BorderMinAspect { get; set; }
    /// <summary>六类模式：同时检出多铜（short/spur/copper）并保留针孔，承诺 open+short+mousebite+spur+copper+pinhole。</summary>
    public bool SixClass { get; set; }
    /// <summary>边缘可解释比例下限（0=关）。配准后 blob 像素被模板 ±Radius 邻域解释的比例 ≥ 此值则丢弃。</summary>
    public double EdgeExplainMin { get; set; }
    /// <summary>边缘解释的邻域半径（像素）。0 且 EdgeExplainMin&gt;0 时用 2。</summary>
    public int EdgeExplainRadius { get; set; }
    /// <summary>边缘闸深度豁免（0=关）。blob 咬进模板铜的最大深度 ≥ 此值时不做边缘解释丢弃——纯错位鬼影只在铜边 ±2px 内，咬进 3px 以上必是真缺陷。</summary>
    public double EdgeSkipDepth { get; set; }
    /// <summary>边缘剪枝（0=关/否）。对通过边缘解释闸的 blob 剪掉可解释像素，残余按连通分量重走闸门——分离「真缺陷+鬼影」连体 blob，收紧外接框。</summary>
    public bool EdgePrune { get; set; }
    /// <summary>巨块强制剪枝（0=关）。面积超过此值的 blob 不做边缘整杀（免开运算后噪声网络连成巨块，真缺陷会陪葬），强制走剪枝提取未解释真核。</summary>
    public double EdgeForcePruneArea { get; set; }
    /// <summary>边缘解释回退：同时对未配准原始模板解释并取最大比例。分块平移在曲线/斜线边缘会搬错方向，对原始模板解释可兜住该类错位。</summary>
    public bool EdgeExplainRawFallback { get; set; }
    /// <summary>
    /// 低对比差分阈值（0=关）。blob 内最大差分低于此值时改用 EdgeShallowMin 作为边缘解释下限：
    /// 刚过阈值的差分本来就接近配准噪声，真缺口/多铜的金属差异通常在 100 以上。
    /// </summary>
    public int EdgeShallowDiff { get; set; }
    /// <summary>低对比 blob 的边缘解释下限（0=用 EdgeExplainMin×0.7）。</summary>
    public double EdgeShallowMin { get; set; }
    /// <summary>多铜（负极性）边缘解释阈值（0=用 EdgeExplainMin）。负极性 FP 占比高且被解释比例远高于 TP，可用更激进阈值。</summary>
    public double EdgeExplainMinNeg { get; set; }
    /// <summary>多铜（负极性）边缘解释邻域半径（0=用 EdgeExplainRadius）。负极性错位鬼影紧贴铜边，半径 1 即可解释且不会误杀真短路。</summary>
    public int EdgeExplainRadiusNeg { get; set; }
    /// <summary>差分区域闭运算半径（0=关）。连接前 closing，合并同一缺陷被阈值/开运算切分的碎片。作用于缺铜（正极性）分支。</summary>
    public double ClosingRadius { get; set; }
    /// <summary>多铜（负极性）分支闭运算半径（0=关）。负极性上闭运算会把相邻真短路合并丢召回，默认关。</summary>
    public double ClosingRadiusNeg { get; set; }
    /// <summary>双极性配对半径（0=关）。缺铜候选与多铜候选外接框距离 ≤ 此值视为同一条错位铜边的正负鬼影，成对丢弃。在边缘解释过滤之后执行。</summary>
    public double PairPolarityRadius { get; set; }
    /// <summary>配对鬼影度下限（0=关）。&gt;0 时仅双方边缘解释度都 ≥ 此值才配对同杀——真缺陷解释度低，与鬼影相邻也不会被陪葬；低面积候选下防误杀。</summary>
    public double PairGhostFrac { get; set; }
    /// <summary>
    /// 拓扑救回面积下限（0=关）。不放宽全局 minBlobArea / minThickness。
    /// 负极性：开运算前，邻域同时碰到两块不同模板铜网的像素结成桥核，面积≥此值且长宽比过闸则当短路——
    /// 细桥可以小于 minArea、细于厚度闸，沿单侧铜边的鬼影碰不到第二块网。
    /// 正极性：缺铜区域若把模板铜网切断，放出咬进中心线的核；浅鬼影切不断，不因降面积闸涌入。
    /// </summary>
    public int TopoMinArea { get; set; }
    /// <summary>桥核触网的棋盘半径（0=用 6）。间隙约 2×半径 以内的短路，其中心像素仍能同时看见两侧铜网。</summary>
    public int TopoTouch { get; set; }
    /// <summary>模板灰度膨胀核尺寸（0=关）。>0 时对模板做 gray_dilation_rect，吸收 ±N/2 px 系统偏移。</summary>
    public int TemplateGrayDilation { get; set; }
    /// <summary>分块整数平移配准搜索半径（像素，0=关）。每块取二值 XOR 最小的平移来搬移模板，缺口本身不被削。</summary>
    public int RegisterShiftMax { get; set; }
    /// <summary>配准分块数（每边），1=整图单一平移。</summary>
    public int RegisterTiles { get; set; } = 1;
    /// <summary>最佳平移相对零平移的 XOR 降幅低于此比例时不采用（防缺陷带偏配准）。</summary>
    public double RegisterMinGain { get; set; } = 0.05;
    /// <summary>
    /// 亚像素平滑配准（需 RegisterShiftMax&gt;0）。开启后改用：灰度 SAD 整数搜索 + 抛物线亚像素细化
    /// + 3×3 中值平滑 + 双线性位移场重采样，消除分块整数平移在瓦片接缝处的台阶鬼影。
    /// </summary>
    public bool RegisterSubPixel { get; set; }
    /// <summary>相对线宽面积系数（0=关）。面积 &lt; coeff × localWidth 则丢弃（缺口≈沿线长，用线性而非 W²）。</summary>
    public double ScaleMinAreaCoeff { get; set; }
    /// <summary>相对线宽缺口深度下限（0=关）。maxDepth &lt; frac × localWidth 则丢弃（浅层边缘抖动）。</summary>
    public double ScaleMinDepthFrac { get; set; }
    /// <summary>估计局部线宽时 blob 膨胀半径，需能摸到铜线中心线。0=16。</summary>
    public double ScaleWidthRadius { get; set; }
    /// <summary>
    /// 局部最优平移复核（LOP）搜索半径（0=关）。
    /// 深度豁免跳过边缘闸的 blob 再走一次：存在同一个平移 d(|d|≤半径) 解释 ≥LopExplainMin 的像素 → 鬼影。
    /// 真缺陷在任何单一 d 下都有不可解释核心，故不误杀。
    /// </summary>
    public int LopRadius { get; set; }
    /// <summary>LOP 解释比例下限（0=用 0.98）。</summary>
    public double LopExplainMin { get; set; }
    /// <summary>LOP 深度豁免（0=关）。blob 咬进模板铜 ≥ 此值即视为真缺陷，不做 LOP 杀；也跳过 LOP 救援（深缺陷不吃边缘闸）。</summary>
    public double LopDepthSkip { get; set; }
    /// <summary>
    /// LOP 反极性证据下限（0=关）。平移鬼影在铜边另一侧必有反极性差分带，真缺口没有；
    /// 要求 LOP 解释度高且反极性证据 ≥ 此值才杀，避免把浅咬边当真鬼影。
    /// </summary>
    public double LopOppMin { get; set; }
    /// <summary>
    /// LOP 解释判定用的严格阈值系数（0=用 0.5）。解释定义为 |test − temp(x+d)| &lt; 系数×DiffThreshold；
    /// 用原阈值会把「刚过阈值的像素」随随便便就解释掉，LOP 就失去分辨力。
    /// </summary>
    public double LopThreshFrac { get; set; }
    /// <summary>
    /// LOP 反向救援（默认关）。边缘闸要杀时，若单一平移解释不了整块则救回。
    /// 实测：holdout 可救回 2 个漏检且 0 过杀；mixed 多放 27 个过杀只救回 4 个漏检，净负。
    /// 故默认关闭，仅在鬼影稀疏的场景下开启。
    /// </summary>
    public bool LopRescue { get; set; }
    /// <summary>
    /// 同极性重复检测合并半径（px，0=关）。bbox 间隙 ≤ 半径的候选只留面积最大的一块。
    /// 金标是一对一贪心，同一缺陷被拆成多块时除第一块外全算过杀，故这里做同极性去重。
    /// </summary>
    public int MergeRadius { get; set; }
    /// <summary>
    /// 铜网拓扑救援（默认关）。负极性边缘闸要杀时，若 blob 邻域出现 ≥2 个模板铜网编号，
    /// 说明它把两块独立铜网连了起来（短路的物理必要条件），不当鬼影杀。
    /// 贴边鬼影只贴同一块铜，网号恒为 1；救回后仍要过 LOP 二道闸。
    /// </summary>
    public bool EdgeNetRescue { get; set; }
    /// <summary>
    /// 卫星压制阈值（面积比，0=关）。>0 时只有小/大 ≤ 此比例的两块才合并，
    /// 避免把相邻两个尺寸相当的真缺陷当成重复吞掉；配合更大的 MergeRadius 使用。
    /// </summary>
    public double MergeScoreRatio { get; set; }
    /// <summary>
    /// 正极性 LOP 全量复核（默认关）。开启后连未进入深度豁免的缺铜 blob 也过 LOP 二道闸，
    /// 用来清理「不在铜边上、但能被单一平移整块解释」的线宽类鬼影。
    /// </summary>
    public bool LopReviewAll { get; set; }
    /// <summary>
    /// 正极性 LOP 复核下限（0=沿用 LopExplainMin）。与负极性解耦：
    /// 多铜鬼影侧必须严（低到 0.7 会误杀 13 个真 short），缺铜侧可以松。
    /// </summary>
        public double LopReviewMin { get; set; }
    /// <summary>
    /// 桥核免边缘解释（默认关）。桥核已判定为跨两块独立铜网，
    /// 单一铜网的平移鬼影不可能同时贴两网，故不再用 bFrac 二次否决（否则贴边 1px 细短路会被误杀）。
    /// </summary>
    public bool BridgeSkipEdge { get; set; }
    /// <summary>
    /// 跨网细条救援（默认关）。负极性 blob 若同时贴两块铜网（短路拓扑证据），
    /// 跳过 thin / aspect 闸：1px 细短路的 InnerCircle 半径恒为 0，否则会被静默杀掉。
    /// </summary>
    public bool BridgeThinRescue { get; set; }

    /// <summary>
    /// 缺铜 blob 的模板铜覆盖率下限（0=关）。open/mousebite/pinhole 都是铜被吃掉，
    /// blob 必须踩在模板铜上；整块落在模板衬底上的是反光/污渍，不是六类缺陷。
    /// </summary>
    public double PosCopperCoverMin { get; set; }

    /// <summary>
    /// 多铜 blob 远离模板铜救援（默认关）。错位鬼影的必要条件是模板该处有铜可错位；
    /// blob 离模板铜超过边缘解释半径时不可能由平移产生，按真 short/spur/copper 放行。
    /// </summary>
    public bool NegAwayCopperRescue { get; set; }

    /// <summary>
    /// 配对时保留跨网负块（默认关）。真短路跨两块独立铜网；平移鬼影只贴同一块网。
    /// 开启后「正块不缺铜网 + 负块跨网」的组合不再丢弃，专治提高 PairPolarityRadius 后 short/copper 漏检。
    /// </summary>
    public bool PairNetKeep { get; set; }

    /// <summary>
    /// 配对时保留大块负块（面积 ≥ 此值，0=关）。余铜（copper）/大短路是真缺陷，
    /// 面积远大于错位鬼影的半块，不能被配对逻辑连坐丢掉。
    /// </summary>
    public double PairKeepArea { get; set; }

    /// <summary>
    /// 远距卫星抑制半径（0=关）。同极性两块间距超过 MergeRadius 但在本半径内、
    /// 且小块面积 ≤ SatelliteRatio×大块时，判为同一缺陷的碎片/鬼影碎片，丢小块。
    /// 物理依据：金标框已命中时，旁边 20-30px 内的小得多的同极性块是重复检出，不是第二个缺陷。
    /// </summary>
    public double SatelliteRadius { get; set; }

    /// <summary>
    /// 远距卫星抑制的面积比上限（配合 SatelliteRadius，默认 0.3）。
    /// </summary>
    public double SatelliteRatio { get; set; }

    /// <summary>
    /// 深度核心收缩（0=关，建议 0.4-0.6）。候选框收到「深度 ≥ frac×峰值」的像素范围，
    /// 只改 X/Y/W/H（金标中心与 IoU 判定用），不动 Runs/Score（拓扑/配对仍用原物理范围）。
    /// 物理：噪声/标签/鬼影尾巴会把外接框中心拖出真缺陷，深度峰值区才是真缺陷位置。
    /// </summary>
    public double CoreBoxFrac { get; set; }

    /// <summary>
    /// 弱差区域生长阈值（0=关）。官方标注框给的是缺陷完整足迹，而阈值化后的差分 blob 只是高置信核心
    /// （实测 pred 中位面积 210px² vs GT 1540px²，99% pred 落在 GT 内，IoU 中位仅 0.14）。
    /// 开启后逐框在邻近窗口内吸收 |test-temp| ≥ 本阈值的连通像素（迭代至不动点，边长上限 3×）。
    /// 建议 6-12（低于主 diffThreshold）；只改 X/Y/W/H，不改候选数、不改分型、不改 Runs/Score。
    /// </summary>
    public int RegionGrowDiff { get; set; }

    /// <summary>
    /// 物理拓扑分型（默认关）。打开后六类标签不再只看长宽比/面积，而是用现有拓扑证据：
    /// 缺铜侧——能切断铜网的是 OPEN，否则全包围小块 PINHOLE、边缺口 MOUSEBITE；
    /// 多铜侧——跨两块独立铜网的是 SHORT，大块异铜 COPPER，单网突出物 SPUR。
    /// 不影响候选集合与命中判定（金标匹配不比分型），只改上报类型与官方严格分型 mAP。
    /// </summary>
    public bool TypeRefineTopology { get; set; }

    /// <summary>
    /// 拓扑分型下的余铜/毛刺分界（0=沿用默认 400）：贴在单条铜上的多铜 blob 面积超过本值判 COPPER，否则 SPUR。
    /// </summary>
    public double CopperBlobArea { get; set; }

    /// <summary>拓扑分型下的毛刺长宽比下限（0=沿用默认 2.5）：长宽比高于本值一律记 SPUR，不高且面积够大才记 COPPER。</summary>
    public double SpurAspectMax { get; set; }

    /// <summary>缺铜侧判开路的长宽比下限（0=沿用默认 2.2）：高于本值且没切断铜网时也记 OPEN（否则归 MOUSEBITE）。</summary>
    public double OpenAspectMin { get; set; }

    /// <summary>切断判定窗半径（0=沿用默认 12）：在 blob 外撑本半径取模板铜，去掉 blob 后连通数增加即判 OPEN。</summary>
    public double OpenSeverRadius { get; set; }

    /// <summary>区域生长向外探索窗口边宽（0=沿用默认 20px）：太小则长缺口长不满，太大则沿弱差长链泄漏。</summary>
    public int GrowMargin { get; set; }

    /// <summary>区域生长后最大边长与原边长之比（0=沿用默认 3）。</summary>
    public double GrowMaxScale { get; set; }

    /// <summary>生长后末端各向同性缩放（≤1=关）：绕中心把框乘本值，补 BFS 未长到的 GT 足迹边距；中心不变，旧口径不退化。</summary>
    public double GrowBoxScale { get; set; }

    /// <summary>末端各向异性伸长系数（≤1=关，即退化为各向同性 GrowBoxScale）：在 GrowBoxScale 已作用于短轴的基础上，对框的长轴（W、H 中较大者＝沿走线方向）再额外乘本值。专治细长缺陷（open/short 断裂）因各向同性保留错误宽高比而卡 IoU 0.2-0.33 的近失框。中心不变。</summary>
    public double GrowBoxElong { get; set; }

    /// <summary>末端加法固定像素外扩（0=关，逐像素等价现状）：在 GrowBoxScale 乘法之后，对框四边各额外补本值像素（W、H 各 +2×pad），绕中心。与乘法不同——加法给小框更大的相对增益、给大框更小的相对增益，用于检验 pred→GT 尺寸差更接近“恒定标注边距(加法)”还是“按比例(乘法)”，从而拟合尺寸分布尾部。中心不变，旧口径不退化。</summary>
    public double GrowBoxPad { get; set; }

    /// <summary>区域生长用 8-邻接（默认关＝4-邻接）：斜向窄桥在 4-邻接下断开，使缺口框长不满。</summary>
    public bool GrowDiagonal { get; set; }

    /// <summary>区域生长迭代轮数（0=沿用默认 3）：每轮以新框边为种子继续向外吸收弱差像素。</summary>
    public int GrowIters { get; set; }

    /// <summary>切断判定前给 blob 的膨胀半径（0=不膨胀）：阈值框只是缺口核心，实际吃铜更宽。</summary>
    public double OpenSeverDilate { get; set; }

    /// <summary>咬宽比分型阈值（0=关）：blob 垂直走线宽度 / 该处线宽 ≥ 本值判 OPEN（贯穿整条线宽=开路），否则 MOUSEBITE。</summary>
    public double OpenBiteFrac { get; set; }

    // ===== v1.0.0：候选特征驱动的分型 / 置信度（默认关＝逐像素等价，不改检出行为）=====

    /// <summary>
    /// 分型树（默认关）：用线宽归一化特征（bite/lateral/severed/nets/parts/surround）判六类，
    /// 取代 PosType/NegType 的绝对量纲启发式。<b>只打标签，不参与任何闸门</b>。
    /// </summary>
    public bool TypeTree { get; set; }

    /// <summary>
    /// 置信度标定（默认关）：输出 DefectItem.Confidence，供官方口径 mAP 排序。
    /// 不参与检出/抑制，只影响排序；关闭时 Confidence=0（评测器回落面积）。
    /// </summary>
    public bool FeatureConfidence { get; set; }

    // ===== v1.1.0：逐框足迹（FWHM）——替代全局 GrowBoxScale 的末端几何 =====

    /// <summary>
    /// 逐框足迹系数 τ（0=关，走 GrowBoxScale 全局缩放）：在现框 ±FootprintMargin 窗内取 |diff|
    /// 的行/列投影剖面，以 τ×峰值 为界取包含峰值的连续区间作为上报框（半高全宽足迹）。
    /// 与 GrowBoxScale 互斥——本值 &gt; 0 时末端缩放整段关。物理依据：模板差分对缺陷掩膜近似线性，
    /// |diff| 剖面即足迹与边缘响应的叠加，半高处对应标注的完整足迹；τ 是唯一新参数且量纲无关。
    /// </summary>
    public double FootprintTau { get; set; }

    /// <summary>足迹剖面搜索窗边宽（0=沿用默认 8px）：以 BFS 生长后的框为基准四向外扩本值。</summary>
    public int FootprintMargin { get; set; }

    // ===== v1.1.0：局部 z 通道（弱对比漏检的加性种子，默认关）=====

    /// <summary>
    /// 局部 z 分数种子阈值（0=关）：z=(|diff|−μ_w)/(σ_w+ZEps)，16×16 滑窗积分图 O(1)。
    /// 全局阈值 30 下的弱对比缺陷（峰值 12~30）在此通道补种子。<b>只加候选、不做减法</b>，
    /// 与全局阈值区域取并集后走完全相同的下游闸门（边缘解释/LOP/面积/极性配对）。
    /// </summary>
    public double SeedZ { get; set; }

    /// <summary>z 种子的绝对 |diff| 兜底（默认 12）：平坦区 σ≈0 时 z 会爆，必须同时满足绝对差。</summary>
    public int SeedZAbs { get; set; }

    /// <summary>z 分母平滑项（默认 2.0 灰度）：σ+eps，压平坦区噪声的 z 爆涨。</summary>
    public double ZEps { get; set; }

    /// <summary>z 统计滑窗边长（默认 16px）：积分图窗口，须 ≥4。</summary>
    public int ZWindow { get; set; }

    /// <summary>z 种子到模板铜的最大距离（0=不约束，默认 3px）：六类缺陷全部位于铜区，
    /// 衬底噪声（反射/污渍）被此约束滤掉；缺陷种子本身缺铜，故对铜区膨胀后取交。</summary>
    public int ZCopperRadius { get; set; }

    /// <summary>
    /// 梯度补偿亚像素配准（默认关）：不重采样图像，整数位移与现状逐位一致，仅把每块分数位移
    /// （SAD 抛物线细化）以 LK 一步补进差分：d = test − (tempA + Gx·fx + Gy·fy)。
    /// 区别于 RegisterSubPixel（双线性重采样，r1/r2 已证伪）：图像保持原始锐度，
    /// 只消掉铜边 ±2px 系统偏移。Gx/Gy 为对齐模板的单帧 Sobel。
    /// </summary>
    public bool SubpixelGradient { get; set; }
}
