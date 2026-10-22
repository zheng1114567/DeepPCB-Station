using DeepPCBStation.Core.Storage.Db;

namespace DeepPCBStation.Core.Review;

/// <summary>人工终判结论。</summary>
public enum ReviewDecision
{
    /// <summary>维持机器判定 NG。</summary>
    ConfirmNg = 0,
    /// <summary>过判/伪点：改判 OK。</summary>
    FalseCall = 1,
    /// <summary>可返修：改判 OK（修复后放行）。</summary>
    Repair = 2,
    /// <summary>报废。</summary>
    Scrap = 3
}

public static class ReviewDecisions
{
    public static string ToCode(ReviewDecision d) => d switch
    {
        ReviewDecision.FalseCall => "FALSE_CALL",
        ReviewDecision.Repair => "REPAIR",
        ReviewDecision.Scrap => "SCRAP",
        _ => "CONFIRM_NG"
    };

    public static string Label(ReviewDecision d) => d switch
    {
        ReviewDecision.FalseCall => "伪点（改判 OK）",
        ReviewDecision.Repair => "返修（改判 OK）",
        ReviewDecision.Scrap => "报废",
        _ => "确认为 NG"
    };

    /// <summary>复判后的终判值（写回 inspections.final_verdict）。</summary>
    public static string FinalVerdictOf(ReviewDecision d) =>
        d is ReviewDecision.FalseCall or ReviewDecision.Repair ? "OK" : "NG";

    /// <summary>固定原因码集：统计口径要能聚合，不能自由文本。</summary>
    public static readonly (string Code, string Label)[] Reasons =
    {
        ("FP_PSEUDO", "伪点 / 过判"),
        ("REAL_DEFECT", "真实缺陷"),
        ("PROCESS", "制程问题"),
        ("MATERIAL", "来料问题"),
        ("EQUIPMENT", "设备异常"),
        ("OTHER", "其他")
    };

    public static string ReasonLabel(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        foreach (var (c, label) in Reasons)
            if (string.Equals(c, code, StringComparison.Ordinal)) return label;
        return code!;
    }
}

/// <summary>
/// 复判（人工终判）：写 reviews + 同事务回写 inspections 终判字段 + 审计。
/// 良率口径以 final_verdict 为准，因此这一步直接决定"报出去的良率是否真实"。
/// </summary>
public sealed class ReviewService
{
    private readonly StationDb _db;

    public ReviewService(StationDb db) => _db = db;

    public bool Submit(InspectionRow row, ReviewDecision decision, string? reasonCode,
        string? note, string reviewer, out string error)
    {
        error = "";
        if (row == null) { error = "未选择记录。"; return false; }
        if (row.IsVoid) { error = "作废记录不可复判。"; return false; }
        if (string.IsNullOrWhiteSpace(reviewer)) { error = "未登录，无法复判。"; return false; }

        var machine = row.Verdict ?? "";
        if (!string.Equals(machine, "OK", StringComparison.Ordinal) &&
            !string.Equals(machine, "NG", StringComparison.Ordinal))
        {
            error = "只有 OK / NG 的检测结果可复判（故障与作废除外）。";
            return false;
        }

        var newVerdict = ReviewDecisions.FinalVerdictOf(decision);
        var review = new ReviewRow
        {
            InspectionId = row.Id,
            Ts = DateTime.Now.ToString("O"),
            Reviewer = reviewer,
            Decision = ReviewDecisions.ToCode(decision),
            ReasonCode = reasonCode,
            Note = note,
            PrevVerdict = machine
        };

        if (!_db.InsertReview(review, newVerdict))
        {
            error = "复判失败（记录不存在或已作废）。";
            return false;
        }

        // ALCOA+：谁、何时、哪一条、原值 → 新值、原因
        _db.AddAudit(reviewer, "REVIEW_SUBMIT",
            $"{row.Id} · {review.Decision} · {reasonCode ?? "-"}",
            entity: "inspection/" + row.Id,
            oldValue: machine,
            newValue: newVerdict,
            reason: reasonCode ?? "-");
        return true;
    }

    public IReadOnlyList<ReviewRow> History(string inspectionId) => _db.ListReviews(inspectionId);
}
