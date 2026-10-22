using System.IO;
using System.Text.Json;
using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Camera;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Handshake;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Core.Storage;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.Services;

namespace DeepPCBStation;

public sealed class StationServices : IDisposable
{
    public AppConfig Config { get; }
    public InspectionPipeline Pipeline { get; }
    public RecipeStore Recipes { get; }
    public ResultStore Store { get; }
    public StationDb Db { get; }
    public string RecipesDir { get; }
    public HandshakeServer? Handshake { get; }
    public AlarmService Alarms { get; }
    /// <summary>DB 写入健康度：写失败会亮 Critical 告警，并在运行页红条提示。</summary>
    public StorageHealth Health { get; }
    /// <summary>触发权仲裁：UI 与 PLC 两条通道的唯一互斥入口。</summary>
    public StationArbiter Arbiter { get; }
    public string EngineWarn { get; }
    public string? HandshakeError { get; }

    private StationServices(AppConfig config, ResultStore store, RecipeStore recipes,
        InspectionPipeline pipeline, StationDb db, string recipesDir,
        HandshakeServer? handshake, AlarmService alarms, StationArbiter arbiter,
        StorageHealth health, string engineWarn, string? handshakeError)
    {
        Config = config;
        Store = store;
        Recipes = recipes;
        Pipeline = pipeline;
        Db = db;
        RecipesDir = recipesDir;
        Handshake = handshake;
        Alarms = alarms;
        Arbiter = arbiter;
        Health = health;
        EngineWarn = engineWarn;
        HandshakeError = handshakeError;
    }

    public static StationServices Build()
    {
        // L7：用户级配置优先（%APPDATA%\DeepPCBStation\appsettings.json）。
        // 直接读程序目录的话，UI 保存的修改会被下一次构建从源码树复制的出厂默认覆盖。
        var appDirCfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var (config, cfgPath, fromUserDir) = AppConfig.LoadResolved(appDirCfgPath);
        System.Diagnostics.Debug.WriteLine($"[Cfg] 生效配置: {cfgPath}（用户级={fromUserDir}）");

        var root = RepoRoot.Current ?? RepoRoot.Find()
            ?? throw new DirectoryNotFoundException("仓库根无效：需要 data\\samples 与 recipes。");
        if (!string.Equals(Directory.GetCurrentDirectory(), root, StringComparison.OrdinalIgnoreCase))
            Directory.SetCurrentDirectory(root);
        RepoRoot.Current = root;

        var samples = Path.Combine(root, "data", "samples");
        if (!Directory.Exists(samples))
            throw new DirectoryNotFoundException("缺少回放目录: " + samples);
        config.Camera.PlaybackRootDir = samples;
        var man = Path.Combine(samples, "Manifest.txt");
        if (File.Exists(man))
            config.Camera.PlaybackManifest = "Manifest.txt";

        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(results);
        config.Storage.OutputRoot = results;

        var camera = CreateCamera(config.Camera);
        try
        {
            camera.Open();
        }
        catch (Exception ex)
        {
            camera.Dispose();
            throw new InvalidOperationException(
                "打开文件回放相机失败。PlaybackRootDir=" + config.Camera.PlaybackRootDir +
                " · cwd=" + Directory.GetCurrentDirectory() +
                " · 根因: " + ex.GetBaseException().Message, ex);
        }

        // 上位机是交付路径：Stub 一律不接受（配置里误写会抛异常快速失败，而不是静默跑假引擎）。
        // CI/冒烟要走 Stub 请设环境变量 DPCB_ALLOW_STUB_ENGINE=1。
        var detector = DetectorFactory.Create(config.Detection.Engine, out var warn);
        var store = new ResultStore(config.Storage);

        var recipesDir = Path.Combine(root, "recipes");
        var recipes = RecipeStore.LoadFromDirectory(recipesDir, config.Recipes);
        // 配方加载/校验诊断必须上屏：坏配方不再静默跳过（见 RecipeStore.Diagnostics）。
        // 走 EngineWarn 通道 → 运行页顶部黄条 + 启动日志，避免「参数写错但外观正常」。
        if (recipes.Diagnostics.Count > 0)
        {
            var diag = "配方诊断：" + string.Join("；", recipes.Diagnostics);
            warn = string.IsNullOrEmpty(warn) ? diag : warn + " · " + diag;
            System.Diagnostics.Debug.WriteLine(diag);
        }
        if (recipes.List().Count == 0)
            recipes = new RecipeStore(config.Recipes);
        var pipeline = new InspectionPipeline(camera, detector, store, recipes, config.Storage);
        pipeline.CaptureQuality = config.CaptureQuality;
        pipeline.ReviewPolicy = config.ReviewPolicy;
        pipeline.TestInjectDelayMs = config.Tcp.TestInjectDelayMs;

        var arbiter = new StationArbiter();

        var db = new StationDb(config.Storage.DbPath);
        var alarms = new AlarmService(db);
        var health = new StorageHealth(alarms);
        pipeline.Persisted += r =>
        {
            try
            {
                db.InsertInspection(new InspectionRow
                {
                    Id = r.InspectionId,
                    PartId = r.PartId,
                    Ts = r.Timestamp.ToString("O"),
                    Verdict = r.Verdict,
                    IsOk = r.IsOk,
                    FaultCode = r.FaultCode,
                    EngineMs = r.ElapsedMs,
                    DefectCount = r.DefectCount,
                    RecipeId = r.RecipeId,
                    RecipeVersion = r.RecipeVersion,
                    RecipeHash = r.RecipeHash,
                    Engine = r.EngineName,
                    CountsTowardYield = r.CountsTowardYield,
                    Operator = r.Operator,
                    Shift = r.Shift,
                    TriggerSource = r.TriggerSource,
                    StationId = r.StationId,
                    IsVoid = r.IsVoid,
                    VoidReason = r.VoidReason,
                    ParentId = r.ParentInspectionId,
                    FinalVerdict = r.FinalVerdict,
                    ReviewTs = r.ReviewTs,
                    ReviewUser = r.ReviewUser,
                    RawPath = r.RawImagePath,
                    OvlPath = r.OverlayImagePath,
                    SidecarPath = r.SidecarJsonPath
                }, r.Defects.Select(d => new DefectRow
                {
                    InspectionId = r.InspectionId,
                    TypeCode = d.TypeCode,
                    X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score
                }).ToList());
                health.Success();
            }
            catch (Exception ex)
            {
                // 静默 catch 等于丢账：转成 Critical 告警 + 运行页红条
                health.Fail(ex);
            }

            if (r.IsFault)
                alarms.Raise(AlarmSeverity.Critical, "检测", r.FaultCode ?? "FAULT",
                    $"检测故障 {r.FaultCode} · partId {r.PartId ?? "—"} · {r.InspectionId}");
        };

        // 配方快照落库（id, hash 主键，INSERT OR REPLACE 幂等）：生产路径此前只在
        // --db-selftest 里写，换型后无法复盘当次检测用的配方全文。启动即落一次，
        // 之后每次切换/换型确认由 RecipeApplied 事件续写。
        void SaveRecipeSnapshot(RecipeConfig r)
        {
            db.SaveRecipeSnapshot(new RecipeSnapshotRow
            {
                Id = r.Id,
                Version = r.Version,
                Hash = pipeline.CurrentRecipeHash,
                Note = null,
                Json = JsonSerializer.Serialize(r),
                UpdatedAt = DateTime.Now.ToString("O")
            });
            health.Success();
        }
        pipeline.RecipeApplied += r =>
        {
            try { SaveRecipeSnapshot(r); }
            catch (Exception ex) { health.Fail(ex); }
        };
        try { SaveRecipeSnapshot(recipes.Current); }
        catch (Exception ex) { health.Fail(ex); }

        if (!string.IsNullOrWhiteSpace(warn))
            alarms.Raise(AlarmSeverity.Warning, "引擎", "ENGINE_DEGRADED", warn);

        HandshakeServer? handshake = null;
        string? handshakeError = null;
        if (config.Tcp.Enabled)
        {
            try
            {
                handshake = new HandshakeServer(
                    config.Tcp.Port,
                    config.Tcp.TimeoutMs,
                    runInspection: (ticket, ct) => pipeline.RunOnceAsync(ticket, ct),
                    recipeInfo: () => (pipeline.CurrentRecipeId, pipeline.CurrentRecipeVersion, pipeline.CurrentRecipeHash),
                    persistResult: null,
                    persistFault: (partId, code, ms, inspId, ticket) =>
                        pipeline.PersistHandshakeFault(partId, code, ms, inspId, ticket),
                    isChangeoverConfirmed: () => recipes.IsChangeoverConfirmed,
                    arbiter: arbiter,
                    currentOperator: () => App.CurrentUser?.Name,
                    stationId: pipeline.StationId);
                handshake.Start();
            }
            catch (Exception ex)
            {
                handshakeError = "TCP 启动失败（窗体仍可用）: " + ex.GetBaseException().Message;
            }
        }

        if (!string.IsNullOrEmpty(handshakeError))
            alarms.Raise(AlarmSeverity.Critical, "TCP", "LISTEN_FAILED", handshakeError!);

        return new StationServices(config, store, recipes, pipeline, db, recipesDir, handshake, alarms, arbiter, health, warn, handshakeError);
    }

    private static ICamera CreateCamera(CameraConfig cfg)
    {
        if (string.Equals(cfg.Mode, "Mock", StringComparison.OrdinalIgnoreCase))
            return new MockCamera(cfg);
        return new PairedFilePlaybackCamera(cfg);
    }

    public void Dispose()
    {
        // sync-over-async 死锁隐患：若由 UI 线程调用，原写法在捕获的 SynchronizationContext
        // 上阻塞等待；派发到线程池执行异步释放，规避死锁（P2-⑧）。
        if (Handshake != null)
        {
            Task.Run(async () => await Handshake.DisposeAsync().ConfigureAwait(false))
                .GetAwaiter().GetResult();
        }
        Pipeline.Dispose();
    }
}
