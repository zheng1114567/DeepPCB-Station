using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Storage;

namespace DeepPCBStation.Core.Recipe;

/// <summary>乐观锁冲突：内存中的配方已在别处被保存，本次写入被拒。</summary>
public sealed class RecipeConcurrencyException : InvalidOperationException
{
    public RecipeConcurrencyException(string message) : base(message) { }
}

/// <summary>配方读写、ContentHash、换型确认门闩。</summary>
public sealed class RecipeStore : IRecipeStore
{
    private readonly List<RecipeConfig> _recipes = new();
    private RecipeConfig _current;
    private bool _confirmed;
    private DateTimeOffset? _confirmedAt;
    private readonly object _gate = new();

    /// <summary>列表或当前配方变化（Save/Select/Invalidate）后触发，供 UI 侧刷新。</summary>
    public event Action? StoreChanged;

    private void RaiseChanged() => StoreChanged?.Invoke();

    /// <summary>
    /// 加载/校验诊断（坏文件、参数越界）。为空表示配方目录干净。
    /// 由 <see cref="LoadFromDirectory"/> 填充；UI 与 CLI 应把它显示出来，
    /// 而不是像旧实现那样 <c>catch { /* skip */ }</c> 静默丢文件。
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    private readonly List<string> _diagnostics = new();

    public RecipeStore(IEnumerable<RecipeConfig> recipes, string? initialId = null)
        : this(recipes, initialId, null)
    {
    }

    public RecipeStore(IEnumerable<RecipeConfig> recipes, string? initialId, IEnumerable<string>? diagnostics)
    {
        if (diagnostics != null) _diagnostics.AddRange(diagnostics);
        foreach (var r in recipes)
        {
            r.ContentHash = ComputeHash(r);
            _recipes.Add(r);
        }
        if (_recipes.Count == 0)
            throw new InvalidOperationException("至少需要一条配方。");
        _current = initialId != null
            ? (_recipes.FirstOrDefault(x => x.Id == initialId) ?? _recipes[0])
            : _recipes[0];
        _confirmed = false;
    }

    public IReadOnlyList<RecipeConfig> List()
    {
        lock (_gate) return _recipes.ToList();
    }

    public RecipeConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsChangeoverConfirmed
    {
        get { lock (_gate) return _confirmed; }
    }

    public DateTimeOffset? ConfirmedAt
    {
        get { lock (_gate) return _confirmedAt; }
    }

    public RecipeConfig Get(string id)
    {
        lock (_gate)
        {
            return _recipes.FirstOrDefault(r => r.Id == id)
                   ?? throw new KeyNotFoundException($"配方不存在: {id}");
        }
    }

    public void Select(string id) => Select(id, null);

    /// <summary>带乐观锁的切换：expectedHash 不匹配则拒绝（配方已被他人保存）。</summary>
    public void Select(string id, string? expectedHash)
    {
        lock (_gate)
        {
            var r = _recipes.FirstOrDefault(x => x.Id == id)
                    ?? throw new KeyNotFoundException($"配方不存在: {id}");
            // 乐观锁：调用方拿着的是旧快照（列表面板可陈旧），直接切会拉回已废弃的参数
            if (expectedHash != null && !HashMatches(r, expectedHash))
                throw new RecipeConcurrencyException($"配方 {id} 已被他人修改，请刷新后重试。");
            _current = r;
            _confirmed = false;
            _confirmedAt = null;
        }
        RaiseChanged();
    }

    private static bool HashMatches(RecipeConfig r, string expected)
    {
        var live = string.IsNullOrEmpty(r.ContentHash) ? ComputeHashStatic(r) : r.ContentHash;
        return string.Equals(live, expected, StringComparison.Ordinal);
    }

    public void SelectByName(string name)
    {
        lock (_gate)
        {
            var r = _recipes.FirstOrDefault(x => x.Name == name)
                    ?? throw new KeyNotFoundException($"配方不存在: {name}");
            _current = r;
            _confirmed = false;
            _confirmedAt = null;
        }
        RaiseChanged();
    }

    public void ConfirmChangeover()
    {
        lock (_gate)
        {
            _current.ContentHash = ComputeHash(_current);
            _confirmed = true;
            _confirmedAt = DateTimeOffset.Now;
        }
        // 确认也广播：顶栏/其他订阅者能同步换型状态
        RaiseChanged();
    }

    public void InvalidateConfirmation()
    {
        lock (_gate)
        {
            _confirmed = false;
            _confirmedAt = null;
        }
        RaiseChanged();
    }

    public void UpdateCurrentParams(Action<RecipeConfig> mutator)
    {
        lock (_gate)
        {
            mutator(_current);
            _current.ContentHash = ComputeHash(_current);
            _confirmed = false;
            _confirmedAt = null;
        }
        RaiseChanged();
    }

    /// <summary>
    /// 保存配方到 recipes/{id}.json。逐键更新 JsonNode：保留未知字段（status 等）与键顺序；
    /// 与 LoadFromDirectory 的「未配置=默认值」规则对称——等于未配置默认值的键不写出，
    /// 保证对手维护 JSON 语义无损、diff 最小。保存后更新内存副本；若保存的是当前配方则
    /// 重算 hash 并使换型确认失效。UI 需另行确认换型。
    /// </summary>
    public void Save(string recipesDir, RecipeConfig config, string? note = null, string? expectedHash = null)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new InvalidOperationException("配方 Id 不能为空。");
        Directory.CreateDirectory(recipesDir);
        var path = Path.Combine(recipesDir, config.Id + ".json");

        // 乐观锁：写入前先确认内存里的版本仍是编辑开始时那一版，否则用旧参数覆盖别人的修改
        if (expectedHash != null)
        {
            lock (_gate)
            {
                var live = _recipes.FirstOrDefault(r => string.Equals(r.Id, config.Id, StringComparison.OrdinalIgnoreCase));
                if (live != null && !HashMatches(live, expectedHash))
                    throw new RecipeConcurrencyException($"配方 {config.Id} 已被他人修改，请刷新后重试。");
            }
        }

        var root = new JsonObject();
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch { root = new JsonObject(); }
        }

        root["id"] = config.Id;
        root["name"] = config.Name;
        root["version"] = config.Version;
        if (note != null)
            root["note"] = note;
        root["diffThreshold"] = config.DiffThreshold;
        root["minBlobArea"] = config.MinBlobArea;
        root["maxBlobArea"] = config.MaxBlobArea;
        root["openRadius"] = config.OpenRadius;
        root["copperMaxGray"] = config.CopperMaxGray;
        root["surroundRadius"] = config.SurroundRadius;
        root["surroundCopperMin"] = config.SurroundCopperMin;
        root["maxAspect"] = config.MaxAspect;

        SetIf(root, "minBlobAreaOpen", config.MinBlobAreaOpen, 0);
        SetIf(root, "minBlobAreaMousebite", config.MinBlobAreaMousebite, 0);
        SetIf(root, "motherRescueArea", config.MotherRescueArea, 0);
        SetIf(root, "motherRescueCap", config.MotherRescueCap, 0);
        SetIf(root, "residueGrowMaxDim", config.ResidueGrowMaxDim, 0);
        SetIf(root, "openRadiusNeg", config.OpenRadiusNeg, 0);
        SetIf(root, "tipSurroundMax", config.TipSurroundMax, 0.55);
        SetIf(root, "templateGrayDilation", config.TemplateGrayDilation, 0);
        SetIf(root, "registerShiftMax", config.RegisterShiftMax, 0);
        SetIf(root, "registerTiles", config.RegisterTiles, 1);
        SetIf(root, "registerMinGain", config.RegisterMinGain, 0.05);
        SetIf(root, "registerSubPixel", config.RegisterSubPixel, false);
        SetIf(root, "lopRadius", config.LopRadius, 0);
        SetIf(root, "lopExplainMin", config.LopExplainMin, 0);
        SetIf(root, "lopDepthSkip", config.LopDepthSkip, 0);
        SetIf(root, "lopOppMin", config.LopOppMin, 0);
        SetIf(root, "lopThreshFrac", config.LopThreshFrac, 0);
        SetIf(root, "lopRescue", config.LopRescue, false);
        SetIf(root, "mergeRadius", config.MergeRadius, 0);
        SetIf(root, "edgeNetRescue", config.EdgeNetRescue, false);
        SetIf(root, "mergeScoreRatio", config.MergeScoreRatio, 0);
        SetIf(root, "lopReviewAll", config.LopReviewAll, false);
        SetIf(root, "lopReviewMin", config.LopReviewMin, 0);
        SetIf(root, "bridgeSkipEdge", config.BridgeSkipEdge, false);
        SetIf(root, "bridgeThinRescue", config.BridgeThinRescue, false);
        SetIf(root, "posCopperCoverMin", config.PosCopperCoverMin, 0);
        SetIf(root, "negAwayCopperRescue", config.NegAwayCopperRescue, false);
        SetIf(root, "pairNetKeep", config.PairNetKeep, false);
        SetIf(root, "pairKeepArea", config.PairKeepArea, 0);
        SetIf(root, "satelliteRadius", config.SatelliteRadius, 0);
        SetIf(root, "satelliteRatio", config.SatelliteRatio, 0);
        SetIf(root, "coreBoxFrac", config.CoreBoxFrac, 0);
        SetIf(root, "regionGrowDiff", config.RegionGrowDiff, 0);
        SetIf(root, "typeRefineTopology", config.TypeRefineTopology, false);
        SetIf(root, "copperBlobArea", config.CopperBlobArea, 0.0);
        SetIf(root, "spurAspectMax", config.SpurAspectMax, 0.0);
        SetIf(root, "openAspectMin", config.OpenAspectMin, 0.0);
        SetIf(root, "openSeverRadius", config.OpenSeverRadius, 0.0);
        SetIf(root, "growMargin", config.GrowMargin, 0);
        SetIf(root, "growMaxScale", config.GrowMaxScale, 0.0);
        SetIf(root, "growBoxScale", config.GrowBoxScale, 0.0);
        SetIf(root, "growBoxElong", config.GrowBoxElong, 0.0);
        SetIf(root, "growBoxPad", config.GrowBoxPad, 0.0);
        SetIf(root, "growDiagonal", config.GrowDiagonal, false);
        SetIf(root, "growIters", config.GrowIters, 0);
        SetIf(root, "openSeverDilate", config.OpenSeverDilate, 0.0);
        SetIf(root, "openBiteFrac", config.OpenBiteFrac, 0.0);
        SetIf(root, "typeTree", config.TypeTree, false);
        SetIf(root, "featureConfidence", config.FeatureConfidence, false);
        SetIf(root, "footprintTau", config.FootprintTau, 0.0);
        SetIf(root, "footprintMargin", config.FootprintMargin, 0);
        SetIf(root, "seedZ", config.SeedZ, 0.0);
        SetIf(root, "seedZAbs", config.SeedZAbs, 0);
        SetIf(root, "zEps", config.ZEps, 0.0);
        SetIf(root, "zWindow", config.ZWindow, 0);
        SetIf(root, "zCopperRadius", config.ZCopperRadius, 0);
        SetIf(root, "subpixelGradient", config.SubpixelGradient, false);
        SetIf(root, "scaleMinAreaCoeff", config.ScaleMinAreaCoeff, 0);
        SetIf(root, "scaleMinDepthFrac", config.ScaleMinDepthFrac, 0);
        SetIf(root, "scaleWidthRadius", config.ScaleWidthRadius, 0);
        SetIf(root, "tipMaxWidth", config.TipMaxWidth, 0);
        SetIf(root, "minThickness", config.MinThickness, 0);
        SetIf(root, "borderMargin", config.BorderMargin, 0);
        SetIf(root, "borderMinAspect", config.BorderMinAspect, 0.0);
        SetIf(root, "sixClass", config.SixClass, false);
        SetIf(root, "edgeExplainMin", config.EdgeExplainMin, 0);
        SetIf(root, "edgeExplainRadius", config.EdgeExplainRadius, 0);
        SetIf(root, "edgeSkipDepth", config.EdgeSkipDepth, 0);
        SetIf(root, "edgeExplainRawFallback", config.EdgeExplainRawFallback, false);
        SetIf(root, "edgeShallowDiff", config.EdgeShallowDiff, 0);
        SetIf(root, "edgeShallowMin", config.EdgeShallowMin, 0);
        SetIf(root, "edgePrune", config.EdgePrune, false);
        SetIf(root, "edgeForcePruneArea", config.EdgeForcePruneArea, 0.0);
        SetIf(root, "edgeExplainMinNeg", config.EdgeExplainMinNeg, 0);
        SetIf(root, "edgeExplainRadiusNeg", config.EdgeExplainRadiusNeg, 0);
        SetIf(root, "closingRadius", config.ClosingRadius, 0);
        SetIf(root, "closingRadiusNeg", config.ClosingRadiusNeg, 0);
        SetIf(root, "pairPolarityRadius", config.PairPolarityRadius, 0);
        SetIf(root, "pairGhostFrac", config.PairGhostFrac, 0.0);
        SetIf(root, "topoMinArea", config.TopoMinArea, 0);
        SetIf(root, "topoTouch", config.TopoTouch, 0);
        SetIf(root, "typeAwareAreaGate", config.TypeAwareAreaGate, false);

        AtomicFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        lock (_gate)
        {
            var hash = ComputeHash(config);
            config.ContentHash = hash;
            var idx = _recipes.FindIndex(r => string.Equals(r.Id, config.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                // 保留原对象引用以避免外部持有旧引用失效；逐字段覆盖
                var existing = _recipes[idx];
                CopyInto(config, existing);
                existing.ContentHash = ComputeHash(existing);
                config = existing;
            }
            else
            {
                _recipes.Add(config);
            }
            if (string.Equals(_current.Id, config.Id, StringComparison.OrdinalIgnoreCase))
            {
                _current = config;
                _confirmed = false;
                _confirmedAt = null;
            }
        }
        RaiseChanged();
    }

    private static void SetIf(JsonObject o, string key, double v, double unset)
    {
        if (Math.Abs(v - unset) > 1e-9)
            o[key] = v;
        else
            o.Remove(key);
    }

    private static void SetIf(JsonObject o, string key, int v, int unset)
    {
        if (v != unset)
            o[key] = v;
        else
            o.Remove(key);
    }

    private static void SetIf(JsonObject o, string key, bool v, bool unset)
    {
        if (v != unset)
            o[key] = v;
        else
            o.Remove(key);
    }

    private static void CopyInto(RecipeConfig src, RecipeConfig dst)
    {
        foreach (var p in typeof(RecipeConfig).GetProperties())
        {
            if (p.CanRead && p.CanWrite)
                p.SetValue(dst, p.GetValue(src));
        }
    }

    /// <summary>深拷贝（JSON 往返），供编辑器在副本上改参数。</summary>
    public static RecipeConfig Clone(RecipeConfig r) =>
        JsonSerializer.Deserialize<RecipeConfig>(JsonSerializer.Serialize(r)) ?? new RecipeConfig();

    public string ComputeHash(RecipeConfig r) => ComputeHashStatic(r);

    public static string ComputeHashStatic(RecipeConfig r)
    {
        var payload = string.Format(
            CultureInfo.InvariantCulture,
            "id={0}|ver={1}|diff={2}|minArea={3}|minOpen={4}|minMb={5}|maxArea={6}|open={7}|copperMax={8}|surroundR={9}|surroundMin={10}|maxAspect={11}|tipSurr={12}",
            r.Id, r.Version, r.DiffThreshold, r.MinBlobArea, r.MinBlobAreaOpen, r.MinBlobAreaMousebite,
            r.MaxBlobArea, r.OpenRadius, r.CopperMaxGray,
            r.SurroundRadius, r.SurroundCopperMin, r.MaxAspect, r.TipSurroundMax);
        // 以下仅启用时并入，保持未启用配方的 hash 不变
        if (r.MotherRescueArea > 0)
            payload += string.Format(CultureInfo.InvariantCulture,
                "|motherRescue={0}|motherRescueCap={1}", r.MotherRescueArea, r.MotherRescueCap);
        if (r.ResidueGrowMaxDim > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|residueGrowMaxDim={0}", r.ResidueGrowMaxDim);
        if (r.TemplateGrayDilation > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|gdil={0}", r.TemplateGrayDilation);
        if (r.RegisterShiftMax > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|reg={0}|regTiles={1}|regGain={2}",
                r.RegisterShiftMax, r.RegisterTiles, r.RegisterMinGain);
        if (r.RegisterSubPixel)
            payload += "|regSub=1";
        if (r.LopRadius > 0)
        {
            payload += string.Format(CultureInfo.InvariantCulture, "|lop={0}|lopMin={1}|lopDepth={2}|lopOpp={3}|lopTF={4}",
                r.LopRadius, r.LopExplainMin, r.LopDepthSkip, r.LopOppMin, r.LopThreshFrac);
            if (r.LopRescue) payload += "|lopRescue=1";
        }
        if (r.MergeRadius > 0) payload += "|merge=" + r.MergeRadius;
        if (r.EdgeNetRescue) payload += "|netRescue";
        if (r.MergeScoreRatio > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|mergeRatio={0}", r.MergeScoreRatio);
        if (r.LopReviewAll) payload += "|lopReviewAll";
        if (r.LopReviewMin > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|lopReviewMin={0}", r.LopReviewMin);
        if (r.BridgeSkipEdge) payload += "|bridgeSkipEdge";
        if (r.BridgeThinRescue) payload += "|bridgeThin";
        if (r.PosCopperCoverMin > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|pcc={0}", r.PosCopperCoverMin);
        if (r.NegAwayCopperRescue) payload += "|awayCu";
        if (r.PairNetKeep) payload += "|pairNetKeep";
        if (r.PairKeepArea > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|pka={0}", r.PairKeepArea);
        if (r.SatelliteRadius > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|sat={0}/{1}", r.SatelliteRadius, r.SatelliteRatio);
        if (r.CoreBoxFrac > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|core={0}", r.CoreBoxFrac);
        if (r.RegionGrowDiff > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|grow={0}", r.RegionGrowDiff);
        if (r.TypeRefineTopology)
        {
            payload += "|typeTopo=1";
            if (r.CopperBlobArea > 0)
                payload += string.Format(CultureInfo.InvariantCulture, "|cba={0}", r.CopperBlobArea);
            if (r.SpurAspectMax > 0)
                payload += string.Format(CultureInfo.InvariantCulture, "|sam={0}", r.SpurAspectMax);
            if (r.OpenAspectMin > 0)
                payload += string.Format(CultureInfo.InvariantCulture, "|oam={0}", r.OpenAspectMin);
            if (r.OpenSeverRadius > 0)
                payload += string.Format(CultureInfo.InvariantCulture, "|osr={0}", r.OpenSeverRadius);
        }
        // 以下**不再**嵌套在 TypeRefineTopology 里：区域生长 / 分型树 / 置信度 / 足迹 / z 通道 /
        // 梯度补偿都与「物理拓扑分型」是否开启无关，各自有独立的生效条件。
        // 原实现把它们缩进在 TypeRefineTopology 块内，导致 typeRefine=0 的配方改这些参数时
        // hash 不变（换型确认门闩失效）。typeRefine=1 的配方（现行 v1.2.1）载荷与顺序逐位不变。
        if (r.GrowMargin > 0 || r.GrowMaxScale > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|gm={0}|gs={1}", r.GrowMargin, r.GrowMaxScale);
        if (r.GrowBoxScale > 1.001)
            payload += string.Format(CultureInfo.InvariantCulture, "|gbs={0}", r.GrowBoxScale);
        if (r.GrowBoxElong > 1.001)
            payload += string.Format(CultureInfo.InvariantCulture, "|gbe={0}", r.GrowBoxElong);
        if (r.GrowBoxPad > 0.001)
            payload += string.Format(CultureInfo.InvariantCulture, "|gbp={0}", r.GrowBoxPad);
        if (r.GrowDiagonal)
            payload += "|gd=1";
        if (r.GrowIters > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|gi={0}", r.GrowIters);
        if (r.OpenSeverDilate > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|osd={0}", r.OpenSeverDilate);
        if (r.OpenBiteFrac > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|obf={0}", r.OpenBiteFrac);
        if (r.TypeTree) payload += "|typeTree=1";
        if (r.FeatureConfidence) payload += "|conf=1";
        if (r.FootprintTau > 0.001)
            payload += string.Format(CultureInfo.InvariantCulture, "|fpT={0}|fpM={1}", r.FootprintTau, r.FootprintMargin);
        if (r.SeedZ > 0.001)
            payload += string.Format(CultureInfo.InvariantCulture, "|z={0}|zA={1}|zE={2}|zW={3}|zCu={4}",
                r.SeedZ, r.SeedZAbs, r.ZEps, r.ZWindow, r.ZCopperRadius);
        if (r.SubpixelGradient) payload += "|subG=1";
        if (r.ScaleMinAreaCoeff > 0 || r.ScaleMinDepthFrac > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|scA={0}|scD={1}|scR={2}",
                r.ScaleMinAreaCoeff, r.ScaleMinDepthFrac, r.ScaleWidthRadius);
        if (r.TipMaxWidth > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|tipW={0}", r.TipMaxWidth);
        if (r.MinThickness > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|minT={0}", r.MinThickness);
        if (r.EdgeSkipDepth > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|edgeSkipD={0}", r.EdgeSkipDepth);
        if (r.OpenRadiusNeg != 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|openRN={0}", r.OpenRadiusNeg);
        if (r.BorderMargin > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|border={0}", r.BorderMargin);
        if (r.BorderMinAspect > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|borderA={0}", r.BorderMinAspect);
        if (r.SixClass)
            payload += "|six=1";
        if (r.EdgeExplainMin > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|edge={0}|edgeR={1}", r.EdgeExplainMin, r.EdgeExplainRadius);
        if (r.EdgeExplainMinNeg > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|edgeN={0}|edgeNR={1}", r.EdgeExplainMinNeg, r.EdgeExplainRadiusNeg);
        if (r.EdgeShallowDiff > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|edgeSD={0}|edgeSM={1}", r.EdgeShallowDiff, r.EdgeShallowMin);
        if (r.EdgeExplainRawFallback)
            payload += "|edgeRaw=1";
        if (r.EdgePrune)
            payload += "|edgePrune=1";
        if (r.EdgeForcePruneArea > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|edgeForce={0}", r.EdgeForcePruneArea);
        if (r.ClosingRadius > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|close={0}", r.ClosingRadius);
        if (r.ClosingRadiusNeg > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|closeN={0}", r.ClosingRadiusNeg);
        if (r.PairPolarityRadius > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|pair={0}", r.PairPolarityRadius);
        if (r.PairGhostFrac > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|pairG={0}", r.PairGhostFrac);
        if (r.TopoMinArea > 0)
            payload += string.Format(CultureInfo.InvariantCulture, "|topo={0}|topoR={1}", r.TopoMinArea, r.TopoTouch);
        // 仅启用时并入（保持未启用配方的 hash 不变）
        if (r.TypeAwareAreaGate) payload += "|typeAwareArea=1";
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            var hex = sb.ToString();
            return hex.Length <= 32 ? hex : hex.Substring(0, 32);
        }
    }

    /// <summary>
    /// 参数合法性校验（只检查「明显写错」的量纲/区间，不检查工艺合理性——后者只能靠四集验收）。
    /// 返回问题列表，带 <c>ERROR</c>/<c>WARN</c> 前缀；空列表表示通过。
    /// </summary>
    public static IReadOnlyList<string> Validate(RecipeConfig r)
    {
        var issues = new List<string>();
        void Error(string m) => issues.Add("ERROR " + m);
        void Warn(string m) => issues.Add("WARN " + m);
        bool In01(double v) => v > 0 && v <= 1.0;

        if (r.DiffThreshold < 1 || r.DiffThreshold > 255)
            Error($"diffThreshold={r.DiffThreshold} 越界（应在 1~255；0 等于关掉差分）。");
        if (r.MinBlobArea < 1)
            Error($"minBlobArea={r.MinBlobArea} 必须 ≥1。");
        if (r.MaxBlobArea > 0 && r.MaxBlobArea < r.MinBlobArea)
            Error($"maxBlobArea={r.MaxBlobArea} < minBlobArea={r.MinBlobArea}，将杀光所有候选。");
        if (r.MinBlobAreaOpen < 0 || r.MinBlobAreaMousebite < 0)
            Error("minBlobAreaOpen/minBlobAreaMousebite 不能为负（0=沿用 minBlobArea）。");
        if (r.MinBlobAreaOpen > 0 && r.MaxBlobArea > 0 && r.MinBlobAreaOpen > r.MaxBlobArea)
            Error($"minBlobAreaOpen={r.MinBlobAreaOpen} 超过 maxBlobArea={r.MaxBlobArea}。");
        if (r.MinBlobAreaMousebite > 0 && r.MaxBlobArea > 0 && r.MinBlobAreaMousebite > r.MaxBlobArea)
            Error($"minBlobAreaMousebite={r.MinBlobAreaMousebite} 超过 maxBlobArea={r.MaxBlobArea}。");
        if (r.OpenRadius < 0)
            Error($"openRadius={r.OpenRadius} 不能为负。");
        if (r.OpenRadiusNeg < -1)
            Error($"openRadiusNeg={r.OpenRadiusNeg} 越界（-1=跳过，0=同 openRadius，>0=指定半径）。");
        if (r.CopperMaxGray < 0 || r.CopperMaxGray > 255)
            Error($"copperMaxGray={r.CopperMaxGray} 越界（应在 0~255）。");
        if (r.SurroundCopperMin > 0 && !In01(r.SurroundCopperMin))
            Error($"surroundCopperMin={r.SurroundCopperMin} 应为 (0,1]。");
        if (r.MaxAspect > 0 && r.MaxAspect < 1)
            Error($"maxAspect={r.MaxAspect} 应 ≥1。");
        if (r.EdgeExplainMin > 0 && !In01(r.EdgeExplainMin))
            Error($"edgeExplainMin={r.EdgeExplainMin} 应为 (0,1]。");
        if (r.EdgeExplainMinNeg > 0 && !In01(r.EdgeExplainMinNeg))
            Error($"edgeExplainMinNeg={r.EdgeExplainMinNeg} 应为 (0,1]。");
        if (r.LopRadius < 0)
            Error($"lopRadius={r.LopRadius} 不能为负。");
        if (r.LopExplainMin > 0 && !In01(r.LopExplainMin))
            Error($"lopExplainMin={r.LopExplainMin} 应为 (0,1]。");
        if (r.LopThreshFrac > 0 && !In01(r.LopThreshFrac))
            Error($"lopThreshFrac={r.LopThreshFrac} 应为 (0,1]。");
        if (r.PairGhostFrac > 0 && !In01(r.PairGhostFrac))
            Error($"pairGhostFrac={r.PairGhostFrac} 应为 (0,1]。");
        if (r.RegisterShiftMax > 0 && r.RegisterTiles < 1)
            Error($"registerTiles={r.RegisterTiles} 必须 ≥1（1=整图单一平移）。");
        if (r.FootprintTau > 0 && !In01(r.FootprintTau))
            Error($"footprintTau={r.FootprintTau} 应为 (0,1]（半高全宽系数）。");
        if (r.GrowMaxScale > 0 && r.GrowMaxScale < 1)
            Error($"growMaxScale={r.GrowMaxScale} 应 ≥1（生长上限倍率）。");
        if (r.GrowBoxScale > 1.001 && r.FootprintTau > 0.001)
            Warn("footprintTau 与 growBoxScale 互斥（footprintTau>0 时末端缩放整段关），当前同时设置，growBoxScale 不会生效。");
        if (r.TypeTree && !r.SixClass)
            Warn("typeTree 需要 sixClass；未开 sixClass 时按两类分支走。");
        return issues;
    }

    public static RecipeStore LoadFromDirectory(string recipesDir, IEnumerable<RecipeConfig>? fallback = null)
    {
        var list = new List<RecipeConfig>();
        var diagnostics = new List<string>();
        var fileCount = 0;
        if (Directory.Exists(recipesDir))
        {
            foreach (var file in Directory.EnumerateFiles(recipesDir, "*.json").OrderBy(f => f))
            {
                fileCount++;
                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonSerializer.Deserialize<RecipeFileDto>(json, JsonOpts());
                    if (doc == null)
                    {
                        diagnostics.Add($"{Path.GetFileName(file)}: 反序列化结果为空，已跳过。");
                        continue;
                    }
                    var cfg = new RecipeConfig
                    {
                        Id = doc.Id ?? Path.GetFileNameWithoutExtension(file),
                        Name = doc.Name ?? doc.Id ?? Path.GetFileNameWithoutExtension(file),
                        Version = doc.Version ?? "0.1.0",
                        DiffThreshold = doc.DiffThreshold ?? 30,
                        MinBlobArea = doc.MinBlobArea ?? 40,
                        MinBlobAreaOpen = doc.MinBlobAreaOpen ?? 0,
                        MinBlobAreaMousebite = doc.MinBlobAreaMousebite ?? 0,
                        MotherRescueArea = doc.MotherRescueArea ?? 0,
                        MotherRescueCap = doc.MotherRescueCap ?? 0,
                        ResidueGrowMaxDim = doc.ResidueGrowMaxDim ?? 0,
                        MaxBlobArea = doc.MaxBlobArea ?? 8000,
                        OpenRadius = doc.OpenRadius ?? 1,
                        OpenRadiusNeg = doc.OpenRadiusNeg ?? 0,
                        CopperMaxGray = doc.CopperMaxGray ?? 100,
                        SurroundRadius = doc.SurroundRadius ?? 4.0,
                        SurroundCopperMin = doc.SurroundCopperMin ?? 0.95,
                        MaxAspect = doc.MaxAspect ?? 6.0,
                        TipSurroundMax = doc.TipSurroundMax ?? 0.55,
                        TemplateGrayDilation = doc.TemplateGrayDilation ?? 0,
                        RegisterShiftMax = doc.RegisterShiftMax ?? 0,
                        RegisterTiles = doc.RegisterTiles ?? 1,
                        RegisterMinGain = doc.RegisterMinGain ?? 0.05,
                        RegisterSubPixel = doc.RegisterSubPixel ?? false,
                        LopRadius = doc.LopRadius ?? 0,
                        LopExplainMin = doc.LopExplainMin ?? 0,
                        LopDepthSkip = doc.LopDepthSkip ?? 0,
                        LopOppMin = doc.LopOppMin ?? 0,
                        LopThreshFrac = doc.LopThreshFrac ?? 0,
                        LopRescue = doc.LopRescue ?? false,
                        MergeRadius = doc.MergeRadius ?? 0,
                        EdgeNetRescue = doc.EdgeNetRescue ?? false,
                        MergeScoreRatio = doc.MergeScoreRatio ?? 0,
                        LopReviewAll = doc.LopReviewAll ?? false,
                        LopReviewMin = doc.LopReviewMin ?? 0,
                        BridgeSkipEdge = doc.BridgeSkipEdge ?? false,
                        BridgeThinRescue = doc.BridgeThinRescue ?? false,
                        PosCopperCoverMin = doc.PosCopperCoverMin ?? 0,
                        NegAwayCopperRescue = doc.NegAwayCopperRescue ?? false,
                        PairNetKeep = doc.PairNetKeep ?? false,
                        PairKeepArea = doc.PairKeepArea ?? 0,
                        SatelliteRadius = doc.SatelliteRadius ?? 0,
                        SatelliteRatio = doc.SatelliteRatio ?? 0,
                        CoreBoxFrac = doc.CoreBoxFrac ?? 0,
                        RegionGrowDiff = doc.RegionGrowDiff ?? 0,
                        TypeRefineTopology = doc.TypeRefineTopology ?? false,
                        CopperBlobArea = doc.CopperBlobArea ?? 0,
                        SpurAspectMax = doc.SpurAspectMax ?? 0,
                        OpenAspectMin = doc.OpenAspectMin ?? 0,
                        OpenSeverRadius = doc.OpenSeverRadius ?? 0,
                        GrowMargin = doc.GrowMargin ?? 0,
                        GrowMaxScale = doc.GrowMaxScale ?? 0,
                        GrowBoxScale = doc.GrowBoxScale ?? 0,
                        GrowBoxElong = doc.GrowBoxElong ?? 0,
                        GrowBoxPad = doc.GrowBoxPad ?? 0,
                        GrowDiagonal = doc.GrowDiagonal ?? false,
                        GrowIters = doc.GrowIters ?? 0,
                        OpenSeverDilate = doc.OpenSeverDilate ?? 0,
                        OpenBiteFrac = doc.OpenBiteFrac ?? 0,
                        TypeTree = doc.TypeTree ?? false,
                        FeatureConfidence = doc.FeatureConfidence ?? false,
                        FootprintTau = doc.FootprintTau ?? 0,
                        FootprintMargin = doc.FootprintMargin ?? 0,
                        SeedZ = doc.SeedZ ?? 0,
                        SeedZAbs = doc.SeedZAbs ?? 0,
                        ZEps = doc.ZEps ?? 0,
                        ZWindow = doc.ZWindow ?? 0,
                        ZCopperRadius = doc.ZCopperRadius ?? 0,
                        SubpixelGradient = doc.SubpixelGradient ?? false,
                        ScaleMinAreaCoeff = doc.ScaleMinAreaCoeff ?? 0,
                        ScaleMinDepthFrac = doc.ScaleMinDepthFrac ?? 0,
                        ScaleWidthRadius = doc.ScaleWidthRadius ?? 0,
                        TipMaxWidth = doc.TipMaxWidth ?? 0,
                        MinThickness = doc.MinThickness ?? 0,
                        BorderMargin = doc.BorderMargin ?? 0,
                        BorderMinAspect = doc.BorderMinAspect ?? 0,
                        SixClass = doc.SixClass ?? false,
                        EdgeExplainMin = doc.EdgeExplainMin ?? 0,
                        EdgeExplainRadius = doc.EdgeExplainRadius ?? 0,
                        EdgeSkipDepth = doc.EdgeSkipDepth ?? 0,
                        EdgeExplainRawFallback = doc.EdgeExplainRawFallback ?? false,
                        EdgeShallowDiff = doc.EdgeShallowDiff ?? 0,
                        EdgeShallowMin = doc.EdgeShallowMin ?? 0,
                        EdgePrune = doc.EdgePrune ?? false,
                        EdgeForcePruneArea = doc.EdgeForcePruneArea ?? 0,
                        EdgeExplainMinNeg = doc.EdgeExplainMinNeg ?? 0,
                        EdgeExplainRadiusNeg = doc.EdgeExplainRadiusNeg ?? 0,
                        ClosingRadius = doc.ClosingRadius ?? 0,
                        ClosingRadiusNeg = doc.ClosingRadiusNeg ?? 0,
                        PairPolarityRadius = doc.PairPolarityRadius ?? 0,
                        PairGhostFrac = doc.PairGhostFrac ?? 0,
                        TopoMinArea = doc.TopoMinArea ?? 0,
                        TopoTouch = doc.TopoTouch ?? 0,
                        TypeAwareAreaGate = doc.TypeAwareAreaGate ?? false,
                    };
                    var issues = Validate(cfg);
                    foreach (var issue in issues)
                        diagnostics.Add($"{Path.GetFileName(file)}: {issue}");
                    list.Add(cfg);
                }
                catch (Exception ex)
                {
                    // 不再静默跳过：坏配方必须出现在 Diagnostics 里，且全部失败时直接抛出。
                    diagnostics.Add($"{Path.GetFileName(file)}: 解析失败 — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (fileCount > 0 && list.Count == 0)
            throw new InvalidDataException(
                $"配方目录 {recipesDir} 内 {fileCount} 个 JSON 全部加载失败：" +
                string.Join(" | ", diagnostics));
        if (list.Count == 0 && fallback != null)
            list.AddRange(fallback);
        return new RecipeStore(list, null, diagnostics);
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class RecipeFileDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public int? DiffThreshold { get; set; }
        public int? MinBlobArea { get; set; }
        public int? MinBlobAreaOpen { get; set; }
        public int? MinBlobAreaMousebite { get; set; }
        public int? MotherRescueArea { get; set; }
        public int? MotherRescueCap { get; set; }
        public int? ResidueGrowMaxDim { get; set; }
        public int? MaxBlobArea { get; set; }
        public int? OpenRadius { get; set; }
        public double? OpenRadiusNeg { get; set; }
        public int? CopperMaxGray { get; set; }
        public double? SurroundRadius { get; set; }
        public double? SurroundCopperMin { get; set; }
        public double? MaxAspect { get; set; }
        public double? TipSurroundMax { get; set; }
        public int? TemplateGrayDilation { get; set; }
        public int? RegisterShiftMax { get; set; }
        public int? RegisterTiles { get; set; }
        public double? RegisterMinGain { get; set; }
        public bool? RegisterSubPixel { get; set; }
        public int? LopRadius { get; set; }
        public double? LopExplainMin { get; set; }
        public double? LopDepthSkip { get; set; }
        public double? LopOppMin { get; set; }
        public double? LopThreshFrac { get; set; }
        public bool? LopRescue { get; set; }
        public int? MergeRadius { get; set; }
        public bool? EdgeNetRescue { get; set; }
        public double? MergeScoreRatio { get; set; }
        public bool? LopReviewAll { get; set; }
        public double? LopReviewMin { get; set; }
        public bool? BridgeSkipEdge { get; set; }
        public bool? BridgeThinRescue { get; set; }
        public double? PosCopperCoverMin { get; set; }
        public bool? NegAwayCopperRescue { get; set; }
        public bool? PairNetKeep { get; set; }
        public double? PairKeepArea { get; set; }
        public double? SatelliteRadius { get; set; }
        public double? SatelliteRatio { get; set; }
        public double? CoreBoxFrac { get; set; }
        public int? RegionGrowDiff { get; set; }
        public bool? TypeRefineTopology { get; set; }
        public double? CopperBlobArea { get; set; }
        public double? SpurAspectMax { get; set; }
        public double? OpenAspectMin { get; set; }
        public double? OpenSeverRadius { get; set; }
        public int? GrowMargin { get; set; }
        public double? GrowMaxScale { get; set; }
        public double? GrowBoxScale { get; set; }
        public double? GrowBoxElong { get; set; }
        public double? GrowBoxPad { get; set; }
        public bool? GrowDiagonal { get; set; }
        public int? GrowIters { get; set; }
        public double? OpenSeverDilate { get; set; }
        public double? OpenBiteFrac { get; set; }
        public bool? TypeTree { get; set; }
        public bool? FeatureConfidence { get; set; }
        public double? FootprintTau { get; set; }
        public int? FootprintMargin { get; set; }
        public double? SeedZ { get; set; }
        public int? SeedZAbs { get; set; }
        public double? ZEps { get; set; }
        public int? ZWindow { get; set; }
        public int? ZCopperRadius { get; set; }
        public bool? SubpixelGradient { get; set; }
        public double? ScaleMinAreaCoeff { get; set; }
        public double? ScaleMinDepthFrac { get; set; }
        public double? ScaleWidthRadius { get; set; }
        public double? TipMaxWidth { get; set; }
        public double? MinThickness { get; set; }
        public int? BorderMargin { get; set; }
        public double? BorderMinAspect { get; set; }
        public bool? SixClass { get; set; }
        public double? EdgeExplainMin { get; set; }
        public int? EdgeExplainRadius { get; set; }
        public double? EdgeSkipDepth { get; set; }
        public bool? EdgeExplainRawFallback { get; set; }
        public int? EdgeShallowDiff { get; set; }
        public double? EdgeShallowMin { get; set; }
        public bool? EdgePrune { get; set; }
        public double? EdgeForcePruneArea { get; set; }
        public double? EdgeExplainMinNeg { get; set; }
        public int? EdgeExplainRadiusNeg { get; set; }
        public double? ClosingRadius { get; set; }
        public double? ClosingRadiusNeg { get; set; }
        public double? PairPolarityRadius { get; set; }
        public double? PairGhostFrac { get; set; }
        public int? TopoMinArea { get; set; }
        public int? TopoTouch { get; set; }
        public bool? TypeAwareAreaGate { get; set; }
    }
}

public interface IRecipeStore
{
    IReadOnlyList<RecipeConfig> List();
    RecipeConfig Current { get; }
    RecipeConfig Get(string id);
    string ComputeHash(RecipeConfig r);
    bool IsChangeoverConfirmed { get; }
    void Select(string id);
    void ConfirmChangeover();
    void InvalidateConfirmation();
}
