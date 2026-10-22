using System.Diagnostics;
using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Camera;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Core.Storage;

namespace DeepPCBStation.Core.Pipeline;

/// <summary>
/// 采图 → IDefectDetector → 叠图 → ResultStore。单闸门。
/// 换型未确认 → FAULT:RECIPE_NOT_CONFIRMED。
/// </summary>
public sealed class InspectionPipeline : IDisposable
{
    private readonly ICamera _camera;
    private readonly IDefectDetector _detector;
    private readonly ResultStore _store;
    private readonly IRecipeStore _recipes;
    private readonly StorageConfig _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _testInjectDelayMs;

    /// <summary>
    /// 采图有效性自检配置。null=不做自检（保持现状行为）。
    /// 开启后，失焦/过曝/空图/上错料号的板判「INVALID·请重拍」，不计入过杀与良率。
    /// </summary>
    public CaptureQualityConfig? CaptureQuality { get; set; }

    /// <summary>
    /// 复判策略。null=有缺陷即 NG（保持现状行为）。
    /// 开启后，低置信度缺陷整板转 REVIEW 交人工确认。
    /// </summary>
    public ReviewPolicyConfig? ReviewPolicy { get; set; }

    /// <summary>最近一次采图自检的量测值（诊断/审计用；未自检为 null）。</summary>
    public Imaging.CaptureQuality.Report? LastCaptureReport { get; private set; }

    public string CurrentRecipeName => _recipes.Current.Name;
    public string CurrentRecipeId => _recipes.Current.Id;
    public string CurrentRecipeVersion => _recipes.Current.Version;
    public string CurrentRecipeHash =>
        string.IsNullOrEmpty(_recipes.Current.ContentHash)
            ? RecipeStore.ComputeHashStatic(_recipes.Current)
            : _recipes.Current.ContentHash;
    public bool IsChangeoverConfirmed => _recipes.IsChangeoverConfirmed;
    public string EngineName => _detector.EngineName;
    public IRecipeStore Recipes => _recipes;
    public ResultStore Store => _store;

    /// <summary>检测闸门被占用（正在采图/检测/落盘）。用于 UI 禁用入口与配方写入闸门。</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>工位标识，随检测记录落库（审计追溯“哪台机”）。</summary>
    public string StationId { get; set; } = Environment.MachineName;

    /// <summary>测试钩子：人为延迟（ms），用于握手超时用例。生产保持 0。</summary>
    public int TestInjectDelayMs
    {
        get => _testInjectDelayMs;
        set => _testInjectDelayMs = Math.Max(0, value);
    }

    public event Action<InspectionResult, OverlayFrame>? FrameReady;

    /// <summary>持久化完成后触发（含 Fault 摘要），供数据库双写等旁路使用。不改变检测语义。</summary>
    public event Action<InspectionResult>? Persisted;

    /// <summary>配方落地（切换配方 / 换型确认）后触发。宿主用它把配方全文快照落库
    /// （recipe_snapshots 表），保证事后可复盘当次检测到底用的哪一版参数。</summary>
    public event Action<RecipeConfig>? RecipeApplied;

    public InspectionPipeline(
        ICamera camera,
        IDefectDetector detector,
        ResultStore store,
        IRecipeStore recipes,
        StorageConfig storage)
    {
        _camera = camera;
        _detector = detector;
        _store = store;
        _recipes = recipes;
        _storage = storage;
        _detector.ApplyRecipe(_recipes.Current);
    }

    public void ConfirmChangeover()
    {
        _recipes.ConfirmChangeover();
        _detector.ApplyRecipe(_recipes.Current);
        RecipeApplied?.Invoke(_recipes.Current);
    }

    public void SelectRecipe(string idOrName)
    {
        try { _recipes.Select(idOrName); }
        catch (KeyNotFoundException)
        {
            if (_recipes is RecipeStore rs)
                rs.SelectByName(idOrName);
            else
                throw;
        }
        _detector.ApplyRecipe(_recipes.Current);
        RecipeApplied?.Invoke(_recipes.Current);
    }

    /// <summary>兼容重载：无归属的内部/自测调用（AUTO 触发、无操作员）。</summary>
    public Task<InspectionResult> RunOnceAsync(
        string? partId = null,
        CancellationToken ct = default,
        bool persist = true,
        bool requireChangeover = true)
        => RunOnceAsync(new InspectionTicket
        {
            PartId = partId,
            Trigger = TriggerSource.AutoLoop,
            Shift = InspectionTicket.ShiftOf(DateTime.Now),
            Persist = persist,
            RequireChangeover = requireChangeover
        }, ct);

    public async Task<InspectionResult> RunOnceAsync(InspectionTicket ticket, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunOnceCore(ticket), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private InspectionResult RunOnceCore(InspectionTicket ticket)
    {
        var persist = ticket.Persist;
        var requireChangeover = ticket.RequireChangeover;
        var partId = ticket.PartId;
        var sw = Stopwatch.StartNew();
        var inspectionId = NewInspectionId();
        var recipe = _recipes.Current;
        var hash = string.IsNullOrEmpty(recipe.ContentHash)
            ? RecipeStore.ComputeHashStatic(recipe)
            : recipe.ContentHash;

        if (requireChangeover && !_recipes.IsChangeoverConfirmed)
        {
            sw.Stop();
            var fault = MakeResult(ticket, inspectionId, partId, sw, isOk: false,
                defects: Array.Empty<DefectItem>(), faultCode: "RECIPE_NOT_CONFIRMED", hash);
            if (persist) { _store.AppendFaultSummary(fault); Persisted?.Invoke(fault); }
            return fault;
        }

        if (_testInjectDelayMs > 0)
            Thread.Sleep(_testInjectDelayMs);

        FrameBuffer? frame = null;
        try
        {
            frame = _camera.Grab();
        }
        // 按**类型**判定无图，不再匹配消息里是否含 "NO_IMAGE"：
        // 文案/包装一变子串过滤就静默失效，无图会被误当成不可恢复的引擎异常。
        // CameraNoImageException 本身是 InvalidOperationException，两个分支合并为一处处理。
        catch (Exception ex) when (ex is CameraNoImageException || ex is InvalidOperationException)
        {
            sw.Stop();
            StationLog.Warn(nameof(InspectionPipeline),
                $"采图失败，判 NO_IMAGE（{ex.GetType().Name}）: {ex.Message}");
            var faultNoImage = MakeResult(ticket, inspectionId, partId, sw, isOk: false,
                defects: Array.Empty<DefectItem>(), faultCode: CameraNoImageException.FaultCode, hash);
            if (persist) { _store.AppendFaultSummary(faultNoImage); Persisted?.Invoke(faultNoImage); }
            return faultNoImage;
        }

        using (frame)
        {
            // ---------- 采图有效性自检（检测之前）----------
            // 采图失效的板不该进检测：无论检出什么都不可信，判 NG 即过杀。
            var cap = CheckCapture(frame);
            if (cap != null && !cap.IsValid)
            {
                sw.Stop();
                // FaultCode 一并置位：握手侧据此回 FAULT 而非 NG
                // ——采集无效的板绝不能报成 NG，否则等于把一块没看清的好板判废。
                var invalid = MakeResult(ticket, inspectionId, partId, sw, isOk: false,
                    defects: Array.Empty<DefectItem>(), faultCode: $"CAPTURE_INVALID:{cap.Code}", hash);
                invalid.Kind = VerdictKind.Invalid;
                invalid.InvalidCode = cap.Code;
                invalid.VerdictNote = cap.Reason;
                var previewInv = OverlayRenderer.RenderBgr(frame, Array.Empty<DefectItem>());
                if (persist)
                {
                    _store.Save(invalid, frame, previewInv, frame.Width, frame.Height);
                    Persisted?.Invoke(invalid);
                }
                FrameReady?.Invoke(invalid, new OverlayFrame(previewInv, frame.Width, frame.Height));
                return invalid;
            }

            var outcome = _detector.Detect(frame);
            sw.Stop();

            if (outcome.IsFault)
            {
                var fault = MakeResult(ticket, inspectionId, partId, sw, isOk: false,
                    defects: Array.Empty<DefectItem>(), faultCode: outcome.FaultCode, hash);
                fault.Kind = VerdictKind.Fault;
                var preview = OverlayRenderer.RenderBgr(frame, Array.Empty<DefectItem>());
                if (persist)
                {
                    _store.Save(fault, frame, preview, frame.Width, frame.Height);
                    Persisted?.Invoke(fault);
                }
                FrameReady?.Invoke(fault, new OverlayFrame(preview, frame.Width, frame.Height));
                return fault;
            }

            var overlayBgr = OverlayRenderer.RenderBgr(frame, outcome.Defects);
            var result = MakeResult(ticket, inspectionId, partId, sw, outcome.IsOk, outcome.Defects,
                faultCode: null, hash);
            result.Kind = outcome.IsOk ? VerdictKind.Ok : VerdictKind.Ng;

            // ---------- 复判策略：低置信度疑似转人工 ----------
            ApplyReviewPolicy(result, outcome.Defects);

            if (persist)
            {
                _store.Save(result, frame, overlayBgr, frame.Width, frame.Height);
                Persisted?.Invoke(result);
            }

            FrameReady?.Invoke(result, new OverlayFrame(overlayBgr, frame.Width, frame.Height));
            return result;
        }
    }

    /// <summary>
    /// 采图有效性自检。未配置或开关关闭时返回 null（保持现状：不自检）。
    /// </summary>
    private Imaging.CaptureQuality.Report? CheckCapture(FrameBuffer frame)
    {
        var cfg = CaptureQuality;
        if (cfg == null || !cfg.Enabled) { LastCaptureReport = null; return null; }

        var report = Imaging.CaptureQuality.Assess(
            frame.Pixels, frame.TemplatePixels, frame.Width, frame.Height,
            cfg.ToGate(), stride: frame.Stride);
        LastCaptureReport = report;
        return report;
    }

    /// <summary>
    /// 复判策略：有缺陷但置信度不足 → 整板转 REVIEW（交人工复判），不直接判 NG。
    /// 目的：把「疑似」与「确认缺陷」分开，使 FCR（假点率）可度量、可控。
    /// 未配置或开关关闭时不动（保持现状：有缺陷即 NG）。
    /// </summary>
    private void ApplyReviewPolicy(InspectionResult result, IReadOnlyList<DefectItem> defects)
    {
        var cfg = ReviewPolicy;
        if (cfg == null || !cfg.Enabled) return;
        if (result.Kind != VerdictKind.Ng || defects.Count == 0) return;

        // 缺陷过多时直接判 NG：逐个人工确认没有意义，也不该被「疑似」稀释。
        if (cfg.ForceNgAboveCount > 0 && defects.Count > cfg.ForceNgAboveCount) return;

        double worst = 1.0;
        foreach (var d in defects)
        {
            var c = d.Confidence > 0 ? d.Confidence : d.Score;
            if (c < worst) worst = c;
        }

        if (worst >= cfg.ConfidenceBelow) return;

        result.Kind = VerdictKind.Review;
        result.ReviewTriggerConfidence = worst;
        result.VerdictNote =
            $"检出 {defects.Count} 处，最低置信度 {worst:F2} 低于复判线 {cfg.ConfidenceBelow:F2}，转人工复判。";
    }

    private InspectionResult MakeResult(
        InspectionTicket ticket, string inspectionId, string? partId, Stopwatch sw, bool isOk,
        IReadOnlyList<DefectItem> defects, string? faultCode, string hash)
    {
        var recipe = _recipes.Current;
        var result = new InspectionResult
        {
            Timestamp = DateTime.Now,
            InspectionId = inspectionId,
            PartId = partId,
            IsOk = isOk,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            DefectCount = defects.Count,
            Defects = defects.ToList(),
            RecipeName = recipe.Name,
            RecipeId = recipe.Id,
            RecipeVersion = recipe.Version,
            RecipeHash = hash,
            FaultCode = faultCode,
            EngineName = _detector.EngineName,
            Operator = ticket.Operator,
            Shift = ticket.Shift,
            TriggerSource = ticket.Source,
            StationId = string.IsNullOrEmpty(ticket.StationId) ? StationId : ticket.StationId,
            ParentInspectionId = ticket.ParentInspectionId
        };
        // 超时/断连后才跑完：留证据但不计良率（落 VOID 目录）
        if (ticket.IsVoid)
            result.MarkVoid(ticket.VoidReason ?? "TIMEOUT");
        // 任何带 FaultCode 的结果统一归 Fault 态。
        // 历史遗留：换型未确认 / NO_IMAGE 等分支只置了 FaultCode 而未置 Kind，
        // 导致上层按 Kind 分流时（None）落到既有 Ok/Ng 判断之外，语义不一致。
        // 显式置过 Kind 的分支（如采图无效的 Invalid）不受影响。
        if (!string.IsNullOrEmpty(faultCode) && result.Kind == VerdictKind.None)
            result.Kind = VerdictKind.Fault;
        return result;
    }

    public static string NewInspectionId() =>
        DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>握手侧丢弃结果时落 Fault（不进良率）。返回落库用的 inspectionId，便于迟到结果回填 parent_id。</summary>
    public string PersistHandshakeFault(string? partId, string faultCode, double elapsedMs,
        string? inspectionId = null, InspectionTicket? ticket = null)
    {
        var recipe = _recipes.Current;
        var hash = string.IsNullOrEmpty(recipe.ContentHash)
            ? RecipeStore.ComputeHashStatic(recipe)
            : recipe.ContentHash;
        var id = inspectionId ?? NewInspectionId();
        var r = new InspectionResult
        {
            Timestamp = DateTime.Now,
            InspectionId = id,
            PartId = partId,
            IsOk = false,
            ElapsedMs = elapsedMs,
            DefectCount = 0,
            RecipeName = recipe.Name,
            RecipeId = recipe.Id,
            RecipeVersion = recipe.Version,
            RecipeHash = hash,
            FaultCode = faultCode,
            EngineName = _detector.EngineName,
            Operator = ticket?.Operator,
            Shift = ticket?.Shift ?? InspectionTicket.ShiftOf(DateTime.Now),
            TriggerSource = ticket?.Source ?? "AUTO",
            StationId = string.IsNullOrEmpty(ticket?.StationId) ? StationId : ticket!.StationId
        };
        _store.AppendFaultSummary(r);
        Persisted?.Invoke(r);
        return id;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _camera.Dispose();
    }
}

public sealed class OverlayFrame
{
    public byte[] BgrPixels { get; }
    public int Width { get; }
    public int Height { get; }

    public OverlayFrame(byte[] bgrPixels, int width, int height)
    {
        BgrPixels = bgrPixels;
        Width = width;
        Height = height;
    }
}
