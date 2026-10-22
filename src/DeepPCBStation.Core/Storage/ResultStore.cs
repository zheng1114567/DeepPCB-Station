using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Pipeline;

namespace DeepPCBStation.Core.Storage;

/// <summary>
/// inspectionId 证据包落盘（TDD §4.6）：原图/叠图/JSON 侧车 + index.jsonl。
/// Fault/Timeout 带 FaultCode；CountsTowardYield=false 不进良率。
/// </summary>
public sealed class ResultStore
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly StorageConfig _cfg;
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _indexPath;
    private readonly string _csvPath;

    public ResultStore(StorageConfig cfg, string? rootOverride = null)
    {
        _cfg = cfg;
        _root = Path.GetFullPath(rootOverride ?? cfg.OutputRoot);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "OK"));
        Directory.CreateDirectory(Path.Combine(_root, "NG"));
        Directory.CreateDirectory(Path.Combine(_root, "FAULT"));
        // VOID：超时/断连后才跑完的结果——保留证据但不计入良率
        Directory.CreateDirectory(Path.Combine(_root, "VOID"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        _indexPath = Path.Combine(_root, "index.jsonl");
        _csvPath = Path.Combine(_root, "logs", "inspection.csv");
        EnsureCsvHeader();
    }

    public string OutputRoot => _root;

    /// <summary>CSV 表头（带版本语义）：列不一致时旧文件改名留档，避免新旧列错位。当前为 v2。</summary>
    private const string CsvHeader =
        "timestamp,inspection_id,part_id,verdict,elapsed_ms,defect_count,recipe_id,recipe_version,recipe_hash,engine,fault,raw_path,ovl_path,yield_count,operator,shift,trigger,void,void_reason\n";

    private void EnsureCsvHeader()
    {
        if (File.Exists(_csvPath))
        {
            try
            {
                var firstLine = File.ReadLines(_csvPath).FirstOrDefault();
                if (firstLine == CsvHeader.TrimEnd('\n')) return;
                var bak = _csvPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(_csvPath, bak);
            }
            catch (IOException ex)
            {
                // 文件被占用时本进程不重建，下次启动再试。
                // 原来完全静默：表头不兼容（新旧列错位）这种**数据正确性**问题会一直被掩盖，
                // 直到有人对帐时才发现 CSV 列串了。这里留痕。
                Diagnostics.StationLog.Warn(nameof(ResultStore),
                    $"inspection.csv 表头校验失败，本次不重建（下次启动重试）: {ex.Message}");
                return;
            }
        }
        File.WriteAllText(_csvPath, CsvHeader, Utf8NoBom);
    }

    /// <summary>
    /// 保存完整证据包。okFolder: OK|NG|FAULT。
    /// </summary>
    public InspectionResult Save(
        InspectionResult result,
        FrameBuffer? rawGray = null,
        byte[]? overlayBgr = null,
        int ovlW = 0,
        int ovlH = 0)
    {
        lock (_gate)
        {
            var day = result.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            // 四级目录：VOID 优先于 FAULT/OK/NG，作废结果单独归档以保留现场证据
            var folderName = result.IsVoid ? "VOID" : result.IsFault ? "FAULT" : (result.IsOk ? "OK" : "NG");
            var dayDir = Path.Combine(_root, folderName, day);
            Directory.CreateDirectory(dayDir);
            var id = string.IsNullOrWhiteSpace(result.InspectionId)
                ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                : result.InspectionId;

            string? rawPath = null;
            string? ovlPath = null;
            string? jsonPath = null;

            if (_cfg.SaveImages && rawGray != null)
            {
                rawPath = Path.Combine(dayDir, $"{id}_raw.png");
                ImageIo.SaveGrayPng(rawPath, rawGray);
                if (!File.Exists(rawPath))
                    throw new IOException($"原图落盘失败（文件未出现）: {rawPath}");
            }

            if (_cfg.SaveImages && overlayBgr != null && ovlW > 0 && ovlH > 0)
            {
                ovlPath = Path.Combine(dayDir, $"{id}_ovl.png");
                ImageIo.SaveBgrPng(ovlPath, overlayBgr, ovlW, ovlH);
                if (!File.Exists(ovlPath))
                    throw new IOException($"叠图落盘失败（文件未出现）: {ovlPath}");
            }

            result.RawImagePath = rawPath;
            result.OverlayImagePath = ovlPath;
            result.ImagePath = ovlPath ?? rawPath;

            var sidecar = new
            {
                inspectionId = result.InspectionId,
                partId = result.PartId,
                timestamp = result.Timestamp,
                verdict = result.Verdict,
                isOk = result.IsOk,
                faultCode = result.FaultCode,
                elapsedMs = result.ElapsedMs,
                defectCount = result.DefectCount,
                recipeId = result.RecipeId,
                recipeName = result.RecipeName,
                recipeVersion = result.RecipeVersion,
                recipeHash = result.RecipeHash,
                engine = result.EngineName,
                countsTowardYield = result.CountsTowardYield,
                operatorName = result.Operator,
                shift = result.Shift,
                triggerSource = result.TriggerSource,
                stationId = result.StationId,
                isVoid = result.IsVoid,
                voidReason = result.VoidReason,
                rawImagePath = rawPath,
                overlayImagePath = ovlPath,
                defects = result.Defects.Select(d => new
                {
                    d.TypeCode, d.X, d.Y, d.W, d.H, d.Score
                }).ToList()
            };

            jsonPath = Path.Combine(dayDir, $"{id}.json");
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }),
                Utf8NoBom);
            result.SidecarJsonPath = jsonPath;

            // index.jsonl
            File.AppendAllText(_indexPath,
                JsonSerializer.Serialize(new
                {
                    inspectionId = result.InspectionId,
                    partId = result.PartId,
                    timestamp = result.Timestamp,
                    verdict = result.Verdict,
                    faultCode = result.FaultCode,
                    recipeId = result.RecipeId,
                    recipeVersion = result.RecipeVersion,
                    recipeHash = result.RecipeHash,
                    countsTowardYield = result.CountsTowardYield,
                    operatorName = result.Operator,
                    shift = result.Shift,
                    triggerSource = result.TriggerSource,
                    isVoid = result.IsVoid,
                    voidReason = result.VoidReason,
                    sidecar = jsonPath,
                    raw = rawPath,
                    ovl = ovlPath
                }) + "\n", Utf8NoBom);

            // csv
            var fmt = (_cfg.LogFormat ?? "both").ToLowerInvariant();
            if (fmt is "csv" or "both")
            {
                var line = string.Format(CultureInfo.InvariantCulture,
                    "{0:O},{1},{2},{3},{4:F2},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18}\n",
                    result.Timestamp,
                    Esc(result.InspectionId),
                    Esc(result.PartId ?? ""),
                    result.Verdict,
                    result.ElapsedMs,
                    result.DefectCount,
                    Esc(result.RecipeId),
                    Esc(result.RecipeVersion),
                    Esc(result.RecipeHash),
                    Esc(result.EngineName),
                    Esc(result.FaultCode ?? ""),
                    Esc(rawPath ?? ""),
                    Esc(ovlPath ?? ""),
                    result.CountsTowardYield ? "1" : "0",
                    Esc(result.Operator ?? ""),
                    Esc(result.Shift),
                    Esc(result.TriggerSource),
                    result.IsVoid ? "1" : "0",
                    Esc(result.VoidReason ?? ""));
                File.AppendAllText(_csvPath, line, Utf8NoBom);
            }

            // also keep jsonl under logs for兼容
            if (fmt is "json" or "both")
            {
                var jsonlLogs = Path.Combine(_root, "logs", "inspection.jsonl");
                File.AppendAllText(jsonlLogs, JsonSerializer.Serialize(sidecar) + "\n", Utf8NoBom);
            }

            return result;
        }
    }

    /// <summary>仅追加摘要（无图），用于握手 TIMEOUT/DISCONNECT 等。</summary>
    public void AppendFaultSummary(InspectionResult result) => Save(result, null, null);

    private static string Esc(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
