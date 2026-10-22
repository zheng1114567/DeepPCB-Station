using System;
using System.IO;
using System.Linq;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Storage.Db;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// M2：存储层集成测试（此前该层测试数为 0）。
///
/// 为什么值得测：良率口径一旦被污染，现场看到的是「良率莫名变好」，
/// 而这类问题在 UI 上完全看不出来——只有落库口径能证伪。
/// 本文件钉住三条工业语义：
///   1. VOID（超时/断连后完成）只留证据、不计良率，且不能被复判「洗回」良率；
///   2. 复判回写终判，伪点（NG→OK）单列统计以便降 FCR；
///   3. schema 升级的 DROP 重建**必须先备份**——这条若被改坏，就是不可逆的生产数据丢失。
/// </summary>
public sealed class StationDbTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly StationDb _db;

    public StationDbTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dpdb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "station.db");
        _db = new StationDb(_path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录，失败无所谓 */ }
    }

    private static string Ts(DateTime t) => t.ToString("O");

    private static InspectionRow Row(string id, string verdict, bool isVoid = false, string? voidReason = null)
        => new()
        {
            Id = id,
            PartId = "P-" + id,
            Ts = Ts(DateTime.Now),
            Verdict = verdict,
            IsOk = verdict == "OK",
            RecipeId = "pcb_open_mousebite",
            RecipeVersion = "v1.2.1",
            RecipeHash = "26d991859176a03ee31cc7d5b728d646",
            Engine = "Halcon",
            CountsTowardYield = !isVoid,
            Operator = "tester",
            Shift = "A",
            TriggerSource = "PLC",
            StationId = "ST-1",
            IsVoid = isVoid,
            VoidReason = voidReason
        };

    // ---------------------------------------------------------- VOID 不计良率

    /// <summary>
    /// 作废记录（超时/断连后完成）保留证据但不进良率：Total 不含它，Void 单列。
    /// 这是「断连不污染良率」这句承诺在存储层的落点。
    /// </summary>
    [Fact]
    public void Void_ExcludedFromYield_ButRetainedAsEvidence()
    {
        _db.InsertInspection(Row("I-OK", "OK"), Array.Empty<DefectRow>());
        _db.InsertInspection(Row("I-VOID", "OK", isVoid: true, voidReason: "DISCONNECT"), Array.Empty<DefectRow>());
        _db.InsertInspection(Row("I-NG", "NG"), Array.Empty<DefectRow>());

        var s = _db.Summary(null, null, null);
        Assert.Equal(2, s.Total);       // 有效板 = OK + NG（VOID 不算）
        Assert.Equal(1, s.Ok);
        Assert.Equal(1, s.Ng);
        Assert.Equal(1, s.Void);        // 作废行数单独统计，可追溯

        // 默认列表隐藏 VOID；显式 IncludeVoid 才可见
        Assert.Equal(2, _db.QueryInspections(new InspectionQuery()).Total);
        Assert.Equal(3, _db.QueryInspections(new InspectionQuery { IncludeVoid = true }).Total);
    }

    // ---------------------------------------------------------- 复判闭环

    [Fact]
    public void Review_RewritesFinalVerdict_AndCountsFalseCall()
    {
        _db.InsertInspection(Row("I-NG", "NG"), new[]
        {
            new DefectRow { InspectionId = "I-NG", TypeCode = "open", X = 10, Y = 20, W = 8, H = 6, Score = 0.9 }
        });

        var ok = _db.InsertReview(new ReviewRow
        {
            InspectionId = "I-NG",
            Ts = Ts(DateTime.Now),
            Reviewer = "qa1",
            Decision = "FALSE_CALL",
            ReasonCode = "GHOST",
            Note = "鬼影，非真缺陷",
            PrevVerdict = "NG"
        }, newVerdict: "OK");

        Assert.True(ok);
        var s = _db.Summary(null, null, null);
        Assert.Equal(1, s.Reviewed);
        Assert.Equal(1, s.FalseCall);          // 机器判 NG、人判 OK → 伪点，用于降 FCR
        Assert.Equal(0, s.FirstPassOk);        // 被改判的不算首检通过

        var row = _db.QueryInspections(new InspectionQuery()).Rows.Single();
        Assert.Equal("OK", row.FinalVerdict);
        Assert.Equal("OK", row.EffectiveVerdict);
        Assert.Equal("qa1", row.ReviewUser);

        // 复判历史留痕（同一单可多次复判，只认最新）
        Assert.Single(_db.ListReviews("I-NG"));
        Assert.Single(_db.QueryInspections(new InspectionQuery { OnlyFalseCall = true }).Rows);
    }

    /// <summary>
    /// 作废行不可复判：否则操作员能把「断连后迟到的结果」通过复判洗回良率，
    /// 等于绕开了 VOID 语义。
    /// </summary>
    [Fact]
    public void Review_OfVoidRow_IsRejected()
    {
        _db.InsertInspection(Row("I-V", "NG", isVoid: true, voidReason: "TIMEOUT"), Array.Empty<DefectRow>());

        var ok = _db.InsertReview(new ReviewRow
        {
            InspectionId = "I-V",
            Ts = Ts(DateTime.Now),
            Reviewer = "qa1",
            Decision = "CONFIRM_NG",
            PrevVerdict = "NG"
        }, newVerdict: "NG");

        Assert.False(ok);
        Assert.Empty(_db.ListReviews("I-V"));
    }

    // ---------------------------------------------------------- schema 升级

    /// <summary>
    /// 同版本重开库不得清空数据——若 DROP 重建被误触发，现场会「昨天的数据没了」。
    /// </summary>
    [Fact]
    public void Reopen_AtCurrentSchema_KeepsRows()
    {
        _db.InsertInspection(Row("I-1", "OK"), Array.Empty<DefectRow>());

        var reopened = new StationDb(_path);
        Assert.Equal(1, reopened.Summary(null, null, null).Total);
        Assert.False(Directory.GetFiles(_dir, "*.bak.*").Any());
    }

    /// <summary>
    /// L7 回归：未恢复告警必须**全量**读取。原 AlarmService 从 ListAlarms(limit:500)
    /// 里筛 !Cleared，未恢复告警超过 500 条时看板就会漏显示——库里有、界面上没有。
    /// </summary>
    [Fact]
    public void ListOpenAlarms_ReturnsAllUncleared_NotTruncated()
    {
        const int n = 520;   // 超过 ListAlarms 默认 limit 500
        for (var i = 0; i < n; i++)
        {
            _db.InsertAlarm(new AlarmRecord
            {
                Ts = Ts(DateTime.Now),
                LastTs = Ts(DateTime.Now),
                Severity = 1,
                Source = "TCP",
                Code = "C" + i,
                Message = "m" + i
            });
        }
        // 一条已恢复的：不应出现在未恢复列表里
        _db.InsertAlarm(new AlarmRecord
        {
            Ts = Ts(DateTime.Now), LastTs = Ts(DateTime.Now),
            Source = "TCP", Code = "CLEARED", Message = "已恢复"
        });
        _db.ClearAlarm("TCP", "CLEARED", DateTime.Now);

        Assert.Equal(n, _db.ListOpenAlarms().Count);          // 全量：520
        Assert.Equal(500, _db.ListAlarms(onlyUnacked: false).Count);   // 对照：被截断的旧行为
    }

    /// <summary>
    /// 老版本库升级走「先备份、再 DROP 重建」。这是有意设计（verdict 语义变了，
    /// 半迁移的旧数据比没有数据更危险），但**备份优先**是不可退让的硬保证：
    /// 备份不成就必须抛异常中止，绝不能无备份地清空生产数据。
    /// 本用例钉住「真的生成了备份文件」这一点——改坏了会立刻变红。
    /// </summary>
    [Fact]
    public void OldSchema_Upgrade_BacksUpBeforeDropping()
    {
        _db.InsertInspection(Row("I-OLD", "OK"), Array.Empty<DefectRow>());

        // 把 user_version 降回 1，模拟现场待升级的旧库
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=1";
            cmd.ExecuteNonQuery();
        }

        var upgraded = new StationDb(_path);

        // 旧数据按设计被清空
        Assert.Equal(0, upgraded.Summary(null, null, null).Total);
        // 但必须留下一份可事后取证的备份
        var backups = Directory.GetFiles(_dir, "*.bak.*");
        Assert.True(backups.Length >= 1, "schema 升级必须留下备份文件，否则等于无取证地清空生产数据");
        Assert.True(new FileInfo(backups[0]).Length > 0, "备份文件不能为空");
    }
}
