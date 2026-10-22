using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed partial class ParamVm : ObservableObject
{
    private readonly RecipeConfig _cfg;
    private readonly PropertyInfo _prop;
    private readonly bool _isBool;

    public ParamVm(RecipeConfig cfg, string propName, string label)
    {
        _cfg = cfg;
        _prop = typeof(RecipeConfig).GetProperty(propName)
            ?? throw new ArgumentException("unknown prop " + propName);
        _isBool = _prop.PropertyType == typeof(bool);
        Label = label;
        PropName = propName;
        if (_isBool)
            _valueBool = (bool)_prop.GetValue(_cfg)!;
        else
            _valueText = Format(_prop.GetValue(_cfg));
    }

    public string PropName { get; }
    public string Label { get; }
    public bool IsBool => _isBool;

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private bool _valueBool;

    partial void OnValueTextChanged(string value)
    {
        if (_isBool) return;
        try
        {
            object v = _prop.PropertyType == typeof(int)
                ? int.Parse(value, CultureInfo.InvariantCulture)
                : double.Parse(value, CultureInfo.InvariantCulture);
            _prop.SetValue(_cfg, v);
        }
        // 只吞「解析失败」这一类：输入中间态（如 "-"、""、"1."）不回写，失焦时再纠偏。
        // 原来是无条件 catch，属性 setter 抛的其它异常（类型转换、安全异常）也会被一并吃掉。
        catch (Exception ex) when (ex is FormatException || ex is OverflowException
                                  || ex is ArgumentException || ex is TargetInvocationException)
        {
            // 输入中间态（如 "-"）不回写，失焦时再纠偏
            StationLog.Debug(nameof(RecipeViewModel),
                $"配方参数 {_prop.Name} 输入中间态未回写: {ex.Message}");
        }
    }

    partial void OnValueBoolChanged(bool value)
    {
        if (_isBool) _prop.SetValue(_cfg, value);
    }

    public void RefreshFromSource()
    {
        if (_isBool)
            ValueBool = (bool)_prop.GetValue(_cfg)!;
        else
            ValueText = Format(_prop.GetValue(_cfg));
    }

    private static string Format(object? v) => v switch
    {
        double d => d.ToString("0.0###", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b.ToString(),
        _ => v?.ToString() ?? ""
    };
}

public sealed class ParamGroupVm
{
    public string Title { get; set; } = "";
    public ObservableCollection<ParamVm> Items { get; } = new();
}

public sealed partial class RecipeViewModel : ObservableObject, IDisposable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;
    private readonly RecipeStore _recipes;
    private RecipeConfig? _editing;
    /// <summary>编辑开始时的 hash：保存时作为乐观锁凭据。</summary>
    private string _baseHash = "";
    private int _disposed;

    public RecipeViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        _recipes = station.Recipes;
        Recipes = new ObservableCollection<RecipeListItemVm>();
        Groups = new ObservableCollection<ParamGroupVm>();

        CanEdit = App.CurrentUser?.IsEngineer ?? false;
        WarnText = CanEdit
            ? "修改或切换配方后，需重新确认换型。"
            : "当前角色无编辑权限（需工程师及以上）。";

        _recipes.StoreChanged += OnStoreChanged;
        RefreshLists();
    }

    // 具名方法才能退订（匿名 lambda 会泄漏，且窗口关闭后仍会回调）
    private void OnStoreChanged() => Application.Current?.Dispatcher.BeginInvoke(RefreshLists);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _recipes.StoreChanged -= OnStoreChanged;
    }

    public ObservableCollection<RecipeListItemVm> Recipes { get; }
    public ObservableCollection<ParamGroupVm> Groups { get; }
    public bool CanEdit { get; }

    public bool CanSetCurrent => HasEditing && SelectedRecipe?.IsCurrent != true;

    partial void OnHasEditingChanged(bool value) => OnPropertyChanged(nameof(CanSetCurrent));

    [ObservableProperty] private RecipeListItemVm? _selectedRecipe;
    [ObservableProperty] private string _editingHeaderText = "未选择配方";
    [ObservableProperty] private string _hashText = "";
    [ObservableProperty] private string _hashDisplay = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _warnText = "";
    [ObservableProperty] private bool _hasEditing;
    [ObservableProperty] private bool _isDirty;

    partial void OnSelectedRecipeChanged(RecipeListItemVm? value)
    {
        OnPropertyChanged(nameof(CanSetCurrent));

        // 未保存修改会被丢弃
        if (IsDirty && _editing != null)
        {
            var ask = _dialogs.Confirm("当前配方有未保存的修改，切换将丢弃。继续？", "配方管理");
            if (!ask)
            {
                var back = Recipes.FirstOrDefault(x => x.Id == _editing.Id);
                if (back != null) SelectedRecipe = back;
                return;
            }
        }
        LoadEditing(value);
    }

    private void LoadEditing(RecipeListItemVm? item)
    {
        if (item == null) return;
        try
        {
            var src = _recipes.Get(item.Id);
            _editing = RecipeStore.Clone(src);
            _baseHash = string.IsNullOrEmpty(src.ContentHash) ? RecipeStore.ComputeHashStatic(src) : src.ContentHash;
            NoteText = ReadNote(item.Id) ?? "";
            BuildGroups(_editing);
            RefreshHeader();
            HasEditing = true;
            IsDirty = false;
        }
        catch (Exception ex)
        {
            _dialogs.Warn("加载配方失败: " + ex.Message, "配方管理");
        }
    }

    private string? ReadNote(string id)
    {
        try
        {
            var path = Path.Combine(_station.RecipesDir, id + ".json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            // 备注框优先显示 note；老配方把生产沿革写在 note2，回退读取避免显示为空
            if (doc.RootElement.TryGetProperty("note", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                return n.GetString();
            if (doc.RootElement.TryGetProperty("note2", out var n2) && n2.ValueKind == System.Text.Json.JsonValueKind.String)
                return n2.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void BuildGroups(RecipeConfig cfg)
    {
        Groups.Clear();
        void G(string title, params (string prop, string label)[] items)
        {
            var g = new ParamGroupVm { Title = title };
            foreach (var (p, l) in items)
            {
                var vm = new ParamVm(cfg, p, l);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ParamVm.ValueText) or nameof(ParamVm.ValueBool))
                        IsDirty = true;
                };
                g.Items.Add(vm);
            }
            Groups.Add(g);
        }

        G("差分与形态学",
            ("DiffThreshold", "差分阈值 DiffThreshold"),
            ("OpenRadius", "开运算半径 OpenRadius"),
            ("OpenRadiusNeg", "负极性开运算 OpenRadiusNeg（0=同正极性/-1=跳过）"),
            ("ClosingRadius", "闭运算半径 ClosingRadius"),
            ("ClosingRadiusNeg", "负极性闭运算 ClosingRadiusNeg"),
            ("TemplateGrayDilation", "模板灰度膨胀 TemplateGrayDilation"),
            ("PairPolarityRadius", "双极性配对半径 PairPolarityRadius"),
            ("PairGhostFrac", "配对鬼影占比 PairGhostFrac（0=关）"),
            ("BorderMargin", "边界闸 BorderMargin（0=关）"),
            ("BorderMinAspect", "边界细条长宽比 BorderMinAspect（0=关）"),
            ("TopoMinArea", "拓扑面积 TopoMinArea（0=关）"),
            ("TopoTouch", "拓扑触铜 TopoTouch（0=关）"));

        G("Blob 过滤",
            ("MinBlobArea", "Blob 面积下界 MinBlobArea"),
            ("MinBlobAreaOpen", "open 类下界 MinBlobAreaOpen（0=用 MinBlobArea）"),
            ("MinBlobAreaMousebite", "mousebite 类下界 MinBlobAreaMousebite（0=用 MinBlobArea）"),
            ("MotherRescueArea", "母团救援阈值 MotherRescueArea（0=关闭；开运算前连通域面积≥此值时放行其小残渣）"),
            ("MotherRescueCap", "母团救援每板上�� MotherRescueCap（0=默认 24）"),
            ("MaxBlobArea", "Blob 面积上界 MaxBlobArea"),
            ("MaxAspect", "长宽比上限 MaxAspect"),
            ("MinThickness", "内切半径下界 MinThickness（0=关）"),
            ("CopperMaxGray", "铜区灰度上界 CopperMaxGray"),
            ("SurroundRadius", "surround 判别半径 SurroundRadius"),
            ("SurroundCopperMin", "surround 铜占比 SurroundCopperMin"),
            ("TipSurroundMax", "线端铜占比上限 TipSurroundMax"),
            ("TipMaxWidth", "线端宽度上限 TipMaxWidth（0=关）"));

        G("配准与相对尺度",
            ("RegisterShiftMax", "平移配准半径 RegisterShiftMax（0=关）"),
            ("RegisterTiles", "配准分块数 RegisterTiles"),
            ("RegisterMinGain", "配准最小增益 RegisterMinGain"),
            ("ScaleMinAreaCoeff", "相对线宽面积系数 ScaleMinAreaCoeff（0=关）"),
            ("ScaleMinDepthFrac", "相对线宽深度系数 ScaleMinDepthFrac（0=关）"),
            ("ScaleWidthRadius", "线宽估计半径 ScaleWidthRadius（0=16）"));

        G("边缘解释栈",
            ("SixClass", "六类模式 SixClass"),
            ("EdgeExplainMin", "边缘解释下限 EdgeExplainMin（0=关）"),
            ("EdgeExplainRadius", "边缘解释半径 EdgeExplainRadius"),
            ("EdgeSkipDepth", "边缘深度豁免 EdgeSkipDepth（0=关）"),
            ("EdgePrune", "边缘剪枝 EdgePrune"),
            ("EdgeForcePruneArea", "巨块强制剪枝面积 EdgeForcePruneArea（0=关）"),
            ("EdgeExplainRawFallback", "原始模板回退解释 EdgeExplainRawFallback"),
            ("EdgeExplainMinNeg", "负极性解释下限 EdgeExplainMinNeg（0=用正极性）"),
            ("EdgeExplainRadiusNeg", "负极性解释半径 EdgeExplainRadiusNeg"));
    }

    private void RefreshHeader()
    {
        if (_editing == null) return;
        EditingHeaderText = $"{_editing.Id} · {_editing.Name} · v{_editing.Version}";
        HashText = RecipeStore.ComputeHashStatic(_editing);
        HashDisplay = "hash=" + HashText;
    }

    private void RefreshLists()
    {
        var currentId = _recipes.Current.Id;
        Recipes.Clear();
        foreach (var r in _recipes.List())
            Recipes.Add(new RecipeListItemVm
            {
                Id = r.Id,
                Name = r.Name,
                Version = r.Version,
                Hash = string.IsNullOrEmpty(r.ContentHash) ? RecipeStore.ComputeHashStatic(r) : r.ContentHash,
                IsCurrent = string.Equals(r.Id, currentId, StringComparison.OrdinalIgnoreCase)
            });
        var sel = SelectedRecipe;
        if (sel != null && Recipes.FirstOrDefault(x => x.Id == sel.Id) is { } again)
            SelectedRecipe = again;
        else if (SelectedRecipe == null)
            SelectedRecipe = Recipes.FirstOrDefault(x => x.IsCurrent) ?? Recipes.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectCurrent()
    {
        if (SelectedRecipe == null) return;
        if (RejectIfInspecting()) return;
        try
        {
            _recipes.Select(SelectedRecipe.Id, SelectedRecipe.Hash);
            _station.Pipeline.SelectRecipe(SelectedRecipe.Id);
            Audit("RECIPE_SELECT", $"{SelectedRecipe.Id}@{SelectedRecipe.Version}");
            _dialogs.Info($"已切换当前配方 {SelectedRecipe.Id}，需重新确认换型。", "配方管理");
        }
        catch (RecipeConcurrencyException)
        {
            ShowConflict(SelectedRecipe.Id);
        }
        catch (Exception ex)
        {
            _dialogs.Warn("切换失败: " + ex.Message, "配方管理");
        }
    }

    /// <summary>检测中改配方会让当前片用两份参数跑，直接拒绝。</summary>
    private bool RejectIfInspecting()
    {
        if (!_station.Pipeline.IsBusy) return false;
        _dialogs.Warn("检测进行中，禁止保存或切换配方。请等本片结束。", "配方管理");
        return true;
    }

    private void ShowConflict(string id)
    {
        _dialogs.Warn($"配方 {id} 已被他人修改，请刷新后重试。\n（已重新加载列表，未保存的修改仍在编辑区）",
            "配方管理");
        RefreshLists();
        var again = Recipes.FirstOrDefault(x => x.Id == id);
        if (again != null) SelectedRecipe = again;
    }

    [RelayCommand]
    private void SaveNewVersion()
    {
        if (_editing == null || !CanEdit) return;
        if (RejectIfInspecting()) return;
        try
        {
            _editing.Version = BumpVersion(_editing.Version);
            _recipes.Save(_station.RecipesDir, _editing,
                string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim(), _baseHash);
            _baseHash = _editing.ContentHash;
            Audit("RECIPE_SAVE", $"{_editing.Id}@{_editing.Version} hash={_editing.ContentHash}");
            RefreshLists();
            IsDirty = false;
            _dialogs.Info($"已保存 {_editing.Id} v{_editing.Version}\nhash={_editing.ContentHash}\n换型确认已失效，请到运行监控页重新确认。",
                "配方管理");
        }
        catch (RecipeConcurrencyException)
        {
            ShowConflict(_editing.Id);
        }
        catch (Exception ex)
        {
            _dialogs.Warn("保存失败: " + ex.Message, "配方管理");
        }
    }

    [RelayCommand]
    private void SaveAsNew()
    {
        if (_editing == null || !CanEdit) return;
        if (RejectIfInspecting()) return;
        var dlg = new PromptWindow("另存为新配方", "新配方 Id（英文/数字/下划线）", _editing.Id + "_v2");
        if (dlg.ShowDialog() != true) return;
        var newId = dlg.InputValue?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newId)
            || !newId.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            _dialogs.Warn("非法 Id：仅允许英文/数字/下划线。", "配方管理");
            return;
        }
        if (_recipes.List().Any(r => string.Equals(r.Id, newId, StringComparison.OrdinalIgnoreCase))
            || File.Exists(Path.Combine(_station.RecipesDir, newId + ".json")))
        {
            _dialogs.Warn($"Id 已存在：{newId}", "配方管理");
            return;
        }
        try
        {
            var clone = RecipeStore.Clone(_editing);
            clone.Id = newId;
            clone.Version = "0.1.0";
            _recipes.Save(_station.RecipesDir, clone, string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim());
            Audit("RECIPE_SAVE", $"as-new {clone.Id}@{clone.Version}");
            RefreshLists();
            SelectedRecipe = Recipes.FirstOrDefault(x => x.Id == newId);
            _dialogs.Info($"已另存 {clone.Id} v{clone.Version}（未切换当前配方）。", "配方管理");
        }
        catch (Exception ex)
        {
            _dialogs.Warn("另存失败: " + ex.Message, "配方管理");
        }
    }

    private static string BumpVersion(string v)
    {
        var parts = v.Split('.');
        if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
            return $"{parts[0]}.{parts[1]}.{patch + 1}";
        if (parts.Length == 2 && int.TryParse(parts[1], out var minor))
            return $"{parts[0]}.{minor + 1}.0";
        return v + ".1";
    }

    private void Audit(string action, string detail)
    {
        try
        {
            _station.Db.AddAudit(App.CurrentUser?.Name ?? "-", action, detail);
        }
        catch { /* 审计失败不阻塞 */ }
    }
}

public sealed class RecipeListItemVm
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool IsCurrent { get; set; }
    public string Display => (IsCurrent ? "● " : "  ") + $"{Name}  {Id}  v{Version}";
}
