using System.Text;
using Dapper;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DeepPCBStation.Core.Storage.Db;

// 短连接 + 参数化 SQL；ts 为 ISO8601 "O" 文本，按字典序比较
public sealed class StationDb
{
    private readonly string _connectionString;

    public StationDb(string dbPath)
    {
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        DbPath = full;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        Initialize();
    }

    public string DbPath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // 应用与 ConsoleDemo 可能同开：写冲突等 5s 而非立即 locked
        c.Execute("PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        return c;
    }

    private static string Ts(DateTime t) => t.ToString("O");

    private void Initialize()
    {
        var version = ReadUserVersion();
        if (version < SchemaVersion && TableExists("inspections"))
            BackupAndRebuild();

        using var c = Open();
        c.Execute(SchemaSql);
        c.Execute($"PRAGMA user_version={SchemaVersion};");
    }

    /// <summary>当前 schema 版本；升级策略见 BackupAndRebuild。</summary>
    private const int SchemaVersion = 3;

    private int ReadUserVersion()
    {
        try
        {
            using var c = Open();
            return c.ExecuteScalar<int>("PRAGMA user_version;");
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private bool TableExists(string name)
    {
        try
        {
            using var c = Open();
            return c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@Name",
                new { Name = name }) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// schema 升级：先整体备份 db 文件，再 DROP 全部表重建。
    /// 不做逐列 ALTER —— verdict 语义已变（新增 VOID）且良率口径变化，
    /// 半迁移的旧数据比没有数据更危险。备份保留一份，便于事后取证。
    /// </summary>
    private void BackupAndRebuild()
    {
        // 1) 先把 WAL 中未落盘的事务刷入主库，确保备份完整、可事后取证
        try
        {
            using var chk = Open();
            chk.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (Exception ex)
        {
            StationLog.Warn(nameof(StationDb), "预写刷盘失败（仍尝试备份）: " + ex.Message);
        }

        // 2) 备份主库；备份不成功则绝不继续 DROP，避免无备份地清空生产数据
        var backup = DbPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
        try
        {
            File.Copy(DbPath, backup, overwrite: true);
            if (!File.Exists(backup) || new FileInfo(backup).Length == 0)
                throw new IOException("备份文件未生成或为空");
        }
        catch (Exception ex)
        {
            var msg = $"[Db] 旧库备份失败，已中止重建以防数据丢失: {ex.Message}";
            StationLog.Error(nameof(StationDb), msg);
            throw new InvalidOperationException(msg, ex);
        }
        StationLog.Info(nameof(StationDb), "旧库已备份: " + backup);

        using var c = Open();
        var tables = c.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").ToList();
        foreach (var t in tables)
            c.Execute($"DROP TABLE IF EXISTS \"{t}\"");
    }

    private const string SchemaSql = """
            CREATE TABLE IF NOT EXISTS inspections (
              id TEXT PRIMARY KEY,
              part_id TEXT,
              ts TEXT NOT NULL,
              verdict TEXT NOT NULL,
              is_ok INTEGER NOT NULL,
              fault_code TEXT,
              engine_ms REAL NOT NULL,
              defect_count INTEGER NOT NULL,
              recipe_id TEXT NOT NULL,
              recipe_version TEXT NOT NULL,
              recipe_hash TEXT NOT NULL,
              engine TEXT NOT NULL,
              counts_toward_yield INTEGER NOT NULL,
              operator TEXT,
              shift TEXT,
              trigger_source TEXT NOT NULL DEFAULT 'AUTO',
              station_id TEXT,
              is_void INTEGER NOT NULL DEFAULT 0,
              void_reason TEXT,
              parent_id TEXT,
              final_verdict TEXT,
              review_ts TEXT,
              review_user TEXT,
              raw_path TEXT,
              ovl_path TEXT,
              sidecar_path TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_inspections_ts ON inspections(ts);
            CREATE INDEX IF NOT EXISTS ix_inspections_verdict ON inspections(verdict);
            CREATE INDEX IF NOT EXISTS ix_inspections_recipe ON inspections(recipe_id);
            CREATE INDEX IF NOT EXISTS ix_inspections_operator ON inspections(operator);
            CREATE INDEX IF NOT EXISTS ix_inspections_void ON inspections(is_void);

            CREATE TABLE IF NOT EXISTS defects (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              inspection_id TEXT NOT NULL,
              type_code TEXT NOT NULL,
              x INTEGER NOT NULL,
              y INTEGER NOT NULL,
              w INTEGER NOT NULL,
              h INTEGER NOT NULL,
              score REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_defects_insp ON defects(inspection_id);
            CREATE INDEX IF NOT EXISTS ix_defects_type ON defects(type_code);

            CREATE TABLE IF NOT EXISTS recipe_snapshots (
              id TEXT NOT NULL,
              version TEXT NOT NULL,
              hash TEXT NOT NULL,
              note TEXT,
              json TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY (id, hash)
            );

            CREATE TABLE IF NOT EXISTS users (
              username TEXT PRIMARY KEY,
              password_hash TEXT NOT NULL,
              salt TEXT NOT NULL,
              role TEXT NOT NULL,
              created_at TEXT NOT NULL,
              last_login TEXT,
              failed_count INTEGER NOT NULL DEFAULT 0,
              locked_until TEXT,
              must_change_password INTEGER NOT NULL DEFAULT 0,
              enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS audit_log (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL,
              username TEXT,
              action TEXT NOT NULL,
              detail TEXT,
              entity TEXT,
              old_value TEXT,
              new_value TEXT,
              reason TEXT
            );

            CREATE TABLE IF NOT EXISTS alarms (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL,
              last_ts TEXT NOT NULL,
              severity INTEGER NOT NULL,
              source TEXT NOT NULL,
              code TEXT NOT NULL,
              message TEXT NOT NULL,
              occurrences INTEGER NOT NULL DEFAULT 1,
              acked INTEGER NOT NULL DEFAULT 0,
              ack_ts TEXT,
              ack_user TEXT,
              cleared INTEGER NOT NULL DEFAULT 0,
              cleared_ts TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_alarms_unacked ON alarms(acked, severity, last_ts);

            CREATE TABLE IF NOT EXISTS reviews (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              inspection_id TEXT NOT NULL,
              ts TEXT NOT NULL,
              reviewer TEXT NOT NULL,
              decision TEXT NOT NULL,
              reason_code TEXT,
              note TEXT,
              prev_verdict TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_reviews_inspection ON reviews(inspection_id);
            """;

    // ---------- 检测结果 ----------

    /// <summary>插入一条检测及其缺陷；id 已存在时整体忽略。返回是否新插入。</summary>
    public bool InsertInspection(InspectionRow r, IReadOnlyList<DefectRow> defects)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var affected = c.Execute("""
            INSERT OR IGNORE INTO inspections
              (id, part_id, ts, verdict, is_ok, fault_code, engine_ms, defect_count,
               recipe_id, recipe_version, recipe_hash, engine, counts_toward_yield,
               operator, shift, trigger_source, station_id, is_void, void_reason, parent_id,
               final_verdict, review_ts, review_user,
               raw_path, ovl_path, sidecar_path)
            VALUES
              (@Id, @PartId, @Ts, @Verdict, @IsOk, @FaultCode, @EngineMs, @DefectCount,
               @RecipeId, @RecipeVersion, @RecipeHash, @Engine, @CountsTowardYield,
               @Operator, @Shift, @TriggerSource, @StationId, @IsVoid, @VoidReason, @ParentId,
               @FinalVerdict, @ReviewTs, @ReviewUser,
               @RawPath, @OvlPath, @SidecarPath)
            """, r, tx);
        if (affected > 0 && defects.Count > 0)
        {
            c.Execute("""
                INSERT INTO defects (inspection_id, type_code, x, y, w, h, score)
                VALUES (@InspectionId, @TypeCode, @X, @Y, @W, @H, @Score)
                """, defects, tx);
        }
        tx.Commit();
        return affected > 0;
    }

    public (IReadOnlyList<InspectionRow> Rows, int Total) QueryInspections(InspectionQuery q)
    {
        using var c = Open();
        var where = new StringBuilder(" WHERE 1=1");
        var p = new DynamicParameters();
        if (q.From != null) { where.Append(" AND ts >= @From"); p.Add("@From", Ts(q.From.Value)); }
        if (q.To != null) { where.Append(" AND ts <= @To"); p.Add("@To", Ts(q.To.Value)); }
        if (q.Verdict is { Length: > 0 } verdict)
        {
            where.Append(verdict.Equals("FAULT", StringComparison.OrdinalIgnoreCase)
                ? " AND verdict LIKE @Verdict"
                : " AND verdict = @Verdict");
            p.Add("@Verdict", verdict.Equals("FAULT", StringComparison.OrdinalIgnoreCase) ? "FAULT:%" : verdict);
        }
        if (!string.IsNullOrEmpty(q.TypeCode))
        {
            where.Append(" AND EXISTS (SELECT 1 FROM defects d WHERE d.inspection_id = inspections.id AND d.type_code = @TypeCode)");
            p.Add("@TypeCode", q.TypeCode);
        }
        if (!string.IsNullOrEmpty(q.RecipeId)) { where.Append(" AND recipe_id = @RecipeId"); p.Add("@RecipeId", q.RecipeId); }
        if (!string.IsNullOrEmpty(q.PartIdContains)) { where.Append(" AND part_id LIKE @PartId"); p.Add("@PartId", "%" + q.PartIdContains + "%"); }
        if (!string.IsNullOrEmpty(q.Operator)) { where.Append(" AND operator = @Operator"); p.Add("@Operator", q.Operator); }
        // 默认隐藏作废行：作废结果保留证据，但不参与列表默认视图与良率口径
        if (!q.IncludeVoid) where.Append(" AND is_void = 0");
        if (q.OnlyPendingReview) where.Append(" AND review_ts IS NULL AND is_void = 0 AND verdict IN ('OK','NG')");
        if (q.OnlyFalseCall) where.Append(" AND final_verdict = 'OK' AND verdict = 'NG'");

        var total = c.ExecuteScalar<int>("SELECT COUNT(*) FROM inspections" + where, p);
        p.Add("@Limit", Math.Max(1, q.Limit));
        p.Add("@Offset", Math.Max(0, q.Offset));
        var rows = c.Query<InspectionRow>("SELECT " + InspectionCols + " FROM inspections" + where +
            " ORDER BY ts DESC LIMIT @Limit OFFSET @Offset", p).ToList();
        return (rows, total);
    }

    public IReadOnlyList<DefectRow> GetDefects(string inspectionId)
    {
        using var c = Open();
        return c.Query<DefectRow>("SELECT id AS Id, inspection_id AS InspectionId, type_code AS TypeCode, x AS X, y AS Y, w AS W, h AS H, score AS Score FROM defects WHERE inspection_id = @Id ORDER BY id",
            new { Id = inspectionId }).ToList();
    }

    // ---------- 统计 ----------

    public KpiSummaryRow Summary(DateTime? from, DateTime? to, string? recipeId)
    {
        using var c = Open();
        var (w, p) = RangeWhere(from, to, recipeId);
        var row = c.QueryFirst<KpiSummaryRow>($"""
            SELECT
              SUM(CASE WHEN is_void = 0 THEN 1 ELSE 0 END) AS Total,
              SUM(CASE WHEN is_void = 0 AND verdict = 'OK' THEN 1 ELSE 0 END) AS Ok,
              SUM(CASE WHEN is_void = 0 AND verdict = 'NG' THEN 1 ELSE 0 END) AS Ng,
              SUM(CASE WHEN is_void = 0 AND verdict LIKE 'FAULT:%' THEN 1 ELSE 0 END) AS Fault,
              SUM(CASE WHEN is_void = 1 THEN 1 ELSE 0 END) AS Void,
              SUM(CASE WHEN is_void = 0 AND verdict = 'OK'
                        AND (final_verdict IS NULL OR final_verdict = '') THEN 1 ELSE 0 END) AS FirstPassOk,
              SUM(CASE WHEN final_verdict IS NOT NULL AND final_verdict <> '' THEN 1 ELSE 0 END) AS Reviewed,
              SUM(CASE WHEN verdict = 'NG' AND final_verdict = 'OK' THEN 1 ELSE 0 END) AS FalseCall,
              AVG(CASE WHEN counts_toward_yield = 1 THEN engine_ms END) AS AvgMs
            FROM inspections{w}
            """, p);
        // 缺陷总数（DPPM 分子）：defects 无 ts/recipe_id，与 inspections 联查不产生歧义
        row.DefectTotal = c.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM defects d JOIN inspections i ON i.id = d.inspection_id{w}", p);
        return row;
    }

    public IReadOnlyList<DailyYieldRow> DailyYield(DateTime? from, DateTime? to, string? recipeId)
    {
        using var c = Open();
        var (w, p) = RangeWhere(from, to, recipeId);
        var rows = c.Query<DailyYieldRow>($"""
            SELECT substr(ts, 1, 10) AS Day,
                   COUNT(*) AS Total,
                   SUM(CASE WHEN verdict = 'OK' THEN 1 ELSE 0 END) AS Ok,
                   0.0 AS YieldPct
            FROM inspections{w} AND counts_toward_yield = 1
            GROUP BY substr(ts, 1, 10)
            ORDER BY Day
            """, p).ToList();
        foreach (var r in rows)
            r.YieldPct = r.Total == 0 ? 0 : 100.0 * r.Ok / r.Total;
        return rows;
    }

    public IReadOnlyList<ParetoRow> DefectPareto(DateTime? from, DateTime? to, string? recipeId, int topN = 10)
    {
        using var c = Open();
        var (w, p) = RangeWhere(from, to, recipeId);
        var dp = new DynamicParameters();
        dp.AddDynamicParams(p);
        dp.Add("@TopN", topN);
        return c.Query<ParetoRow>($"""
            SELECT d.type_code AS TypeCode, COUNT(*) AS Count
            FROM defects d
            JOIN inspections i ON i.id = d.inspection_id
            WHERE 1=1{w.Replace(" WHERE 1=1", "")}
            GROUP BY d.type_code
            ORDER BY Count DESC
            LIMIT @TopN
            """, dp).ToList();
    }

    private static (string Where, DynamicParameters P) RangeWhere(DateTime? from, DateTime? to, string? recipeId)
    {
        var w = new StringBuilder(" WHERE 1=1");
        var p = new DynamicParameters();
        if (from != null) { w.Append(" AND ts >= @From"); p.Add("@From", Ts(from.Value)); }
        if (to != null) { w.Append(" AND ts <= @To"); p.Add("@To", Ts(to.Value)); }
        if (!string.IsNullOrEmpty(recipeId)) { w.Append(" AND recipe_id = @RecipeId"); p.Add("@RecipeId", recipeId); }
        return (w.ToString(), p);
    }

    private const string InspectionCols =
        "id AS Id, part_id AS PartId, ts AS Ts, verdict AS Verdict, is_ok AS IsOk, fault_code AS FaultCode, " +
        "engine_ms AS EngineMs, defect_count AS DefectCount, recipe_id AS RecipeId, recipe_version AS RecipeVersion, " +
        "recipe_hash AS RecipeHash, engine AS Engine, counts_toward_yield AS CountsTowardYield, " +
        "operator AS Operator, shift AS Shift, trigger_source AS TriggerSource, station_id AS StationId, " +
        "is_void AS IsVoid, void_reason AS VoidReason, parent_id AS ParentId, " +
        "final_verdict AS FinalVerdict, review_ts AS ReviewTs, review_user AS ReviewUser, " +
        "raw_path AS RawPath, ovl_path AS OvlPath, sidecar_path AS SidecarPath";

    // ---------- 配方快照 ----------

    public void SaveRecipeSnapshot(RecipeSnapshotRow r)
    {
        using var c = Open();
        c.Execute("""
            INSERT OR REPLACE INTO recipe_snapshots (id, version, hash, note, json, updated_at)
            VALUES (@Id, @Version, @Hash, @Note, @Json, @UpdatedAt)
            """, r);
    }

    public IReadOnlyList<RecipeSnapshotRow> ListRecipeSnapshots(string? id = null)
    {
        using var c = Open();
        return c.Query<RecipeSnapshotRow>(
            "SELECT id AS Id, version AS Version, hash AS Hash, note AS Note, json AS Json, updated_at AS UpdatedAt " +
            "FROM recipe_snapshots WHERE (@Id IS NULL OR id = @Id) ORDER BY updated_at DESC",
            new { Id = id }).ToList();
    }

    // ---------- 用户 ----------

    private const string UserCols =
        "username AS Username, password_hash AS PasswordHash, salt AS Salt, role AS Role, " +
        "created_at AS CreatedAt, last_login AS LastLogin, failed_count AS FailedCount, " +
        "locked_until AS LockedUntil, must_change_password AS MustChangePassword, enabled AS Enabled";

    public void UpsertUser(UserRow u)
    {
        using var c = Open();
        c.Execute("""
            INSERT OR REPLACE INTO users
              (username, password_hash, salt, role, created_at, last_login,
               failed_count, locked_until, must_change_password, enabled)
            VALUES
              (@Username, @PasswordHash, @Salt, @Role, @CreatedAt, @LastLogin,
               @FailedCount, @LockedUntil, @MustChangePassword, @Enabled)
            """, u);
    }

    public UserRow? GetUser(string username)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<UserRow>(
            "SELECT " + UserCols + " FROM users WHERE username = @Username",
            new { Username = username });
    }

    public IReadOnlyList<UserRow> ListUsers()
    {
        using var c = Open();
        return c.Query<UserRow>("SELECT " + UserCols + " FROM users ORDER BY username").ToList();
    }

    // ---------- 登录策略（21 CFR Part 11：连续失败锁定 / 首登改密 / 停用）----------

    /// <summary>累加失败次数；达到阈值时直接写入锁定截止时间。返回本次写入后的失败次数。</summary>
    public int RegisterLoginFailure(string username, int threshold, TimeSpan lockFor)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE users SET failed_count = failed_count + 1 WHERE username = @Username",
            new { Username = username }, tx);
        var count = c.ExecuteScalar<int?>("SELECT failed_count FROM users WHERE username = @Username",
            new { Username = username }, tx) ?? 0;
        if (count >= threshold)
        {
            c.Execute("UPDATE users SET locked_until = @Until WHERE username = @Username",
                new { Until = Ts(DateTime.Now + lockFor), Username = username }, tx);
        }
        tx.Commit();
        return count;
    }

    public void ResetLoginFailure(string username)
    {
        using var c = Open();
        c.Execute("UPDATE users SET failed_count = 0, locked_until = NULL WHERE username = @Username",
            new { Username = username });
    }

    public void SetMustChangePassword(string username, bool must)
    {
        using var c = Open();
        c.Execute("UPDATE users SET must_change_password = @Must WHERE username = @Username",
            new { Must = must ? 1 : 0, Username = username });
    }

    public void SetUserEnabled(string username, bool enabled)
    {
        using var c = Open();
        c.Execute("UPDATE users SET enabled = @Enabled WHERE username = @Username",
            new { Enabled = enabled ? 1 : 0, Username = username });
    }

    public bool DeleteUser(string username)
    {
        using var c = Open();
        return c.Execute("DELETE FROM users WHERE username = @Username", new { Username = username }) > 0;
    }

    public void TouchLastLogin(string username, DateTime when)
    {
        using var c = Open();
        c.Execute("UPDATE users SET last_login = @Ts WHERE username = @Username",
            new { Ts = Ts(when), Username = username });
    }

    public bool UpdateUserRole(string username, string role)
    {
        using var c = Open();
        return c.Execute("UPDATE users SET role = @Role WHERE username = @Username",
            new { Role = role, Username = username }) > 0;
    }

    public bool UpdateUserPassword(string username, string passwordHash, string salt)
    {
        using var c = Open();
        return c.Execute("UPDATE users SET password_hash = @Hash, salt = @Salt WHERE username = @Username",
            new { Hash = passwordHash, Salt = salt, Username = username }) > 0;
    }

    public int UserCount()
    {
        using var c = Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM users");
    }

    // ---------- 审计 ----------

    public void AddAudit(string username, string action, string? detail)
        => AddAudit(username, action, detail, null, null, null, null);

    /// <summary>ALCOA+：谁、何时、对什么实体、原值→新值、原因。</summary>
    public void AddAudit(string username, string action, string? detail,
        string? entity, string? oldValue, string? newValue, string? reason)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO audit_log (ts, username, action, detail, entity, old_value, new_value, reason)
            VALUES (@Ts, @Username, @Action, @Detail, @Entity, @OldValue, @NewValue, @Reason)
            """, new
        {
            Ts = Ts(DateTime.Now), Username = username, Action = action, Detail = detail,
            Entity = entity, OldValue = oldValue, NewValue = newValue, Reason = reason
        });
    }

    public IReadOnlyList<AuditRow> ListAudit(int limit = 200)
    {
        using var c = Open();
        return c.Query<AuditRow>(
            "SELECT id AS Id, ts AS Ts, username AS Username, action AS Action, detail AS Detail, " +
            "entity AS Entity, old_value AS OldValue, new_value AS NewValue, reason AS Reason " +
            "FROM audit_log ORDER BY id DESC LIMIT @Limit", new { Limit = limit }).ToList();
    }

    // ---------- 告警 ----------

    private const string AlarmCols =
        "id AS Id, ts AS Ts, last_ts AS LastTs, severity AS Severity, source AS Source, " +
        "code AS Code, message AS Message, occurrences AS Occurrences, acked AS Acked, " +
        "ack_ts AS AckTs, ack_user AS AckUser, cleared AS Cleared, cleared_ts AS ClearedTs";

    public AlarmRecord? FindUnackedAlarm(string source, string code)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<AlarmRecord>(
            "SELECT " + AlarmCols + " FROM alarms WHERE acked = 0 AND cleared = 0 AND source = @Source AND code = @Code " +
            "ORDER BY id DESC LIMIT 1", new { Source = source, Code = code });
    }

    public long InsertAlarm(AlarmRecord a)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO alarms (ts, last_ts, severity, source, code, message, occurrences, acked)
            VALUES (@Ts, @LastTs, @Severity, @Source, @Code, @Message, @Occurrences, @Acked)
            """, a);
        return c.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>合并同一未确认告警：次数 +1、刷新最后发生时间与内容。</summary>
    public void TouchAlarm(long id, string lastTs, string message, int severity)
    {
        using var c = Open();
        c.Execute("""
            UPDATE alarms SET last_ts = @LastTs, message = @Message, severity = @Severity,
                              occurrences = occurrences + 1
            WHERE id = @Id
            """, new { Id = id, LastTs = lastTs, Message = message, Severity = severity });
    }

    public IReadOnlyList<AlarmRecord> ListAlarms(bool onlyUnacked, int limit = 500)
    {
        using var c = Open();
        return c.Query<AlarmRecord>(
            "SELECT " + AlarmCols + " FROM alarms" + (onlyUnacked ? " WHERE acked = 0 AND cleared = 0" : "") +
            " ORDER BY last_ts DESC, id DESC LIMIT @Limit", new { Limit = Math.Max(1, limit) }).ToList();
    }

    /// <summary>
    /// 未恢复（cleared=0）告警**全量**，供看板/状态机在进程启动时重建内存态。
    /// 不能用 ListAlarms(limit: N) 替代：那会截断，造成「库里还有未恢复告警、看板看不见」。
    /// 告警是安全相关信号，漏显示比多显示危险得多，故这里不设上限。
    /// </summary>
    public IReadOnlyList<AlarmRecord> ListOpenAlarms()
    {
        using var c = Open();
        return c.Query<AlarmRecord>(
            "SELECT " + AlarmCols + " FROM alarms WHERE cleared = 0 ORDER BY last_ts DESC, id DESC").ToList();
    }

    /// <summary>条件恢复：只清同 source + code 且尚未清除的告警，并记录恢复时间。</summary>
    public int ClearAlarm(string source, string code, DateTime when)
    {
        using var c = Open();
        return c.Execute(
            "UPDATE alarms SET cleared = 1, cleared_ts = @Ts WHERE source = @Source AND code = @Code AND cleared = 0",
            new { Ts = Ts(when), Source = source, Code = code });
    }

    /// <summary>批量恢复（如 TCP 回到 Ready 后一次清掉所有 TCP 告警）。</summary>
    public int ClearAlarmsBySource(string source, DateTime when)
    {
        using var c = Open();
        return c.Execute(
            "UPDATE alarms SET cleared = 1, cleared_ts = @Ts WHERE source = @Source AND cleared = 0",
            new { Ts = Ts(when), Source = source });
    }

    public int AckAlarm(long id, string user, DateTime when)
    {
        using var c = Open();
        return c.Execute("UPDATE alarms SET acked = 1, ack_ts = @Ts, ack_user = @User WHERE id = @Id AND acked = 0",
            new { Id = id, Ts = Ts(when), User = user });
    }

    public int AckAllAlarms(string user, DateTime when)
    {
        using var c = Open();
        return c.Execute("UPDATE alarms SET acked = 1, ack_ts = @Ts, ack_user = @User WHERE acked = 0",
            new { Ts = Ts(when), User = user });
    }

    /// <summary>同一告警再次发生时重新置为未确认（ISA-18.2：新一次事件需重新确认）。</summary>
    public int ReopenAlarm(long id)
    {
        using var c = Open();
        return c.Execute(
            "UPDATE alarms SET acked = 0, ack_ts = NULL, ack_user = NULL WHERE id = @Id",
            new { Id = id });
    }

    public int UnackedAlarmCount()
    {
        using var c = Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM alarms WHERE acked = 0 AND cleared = 0");
    }

    // ---------- 复判（人工终判）----------

    /// <summary>
    /// 提交一次复判：插 reviews + 回写 inspections 终判字段（同一事务）。
    /// 同一检测单只认最新一条复判，历史行保留可追溯。
    /// </summary>
    public bool InsertReview(ReviewRow r, string? newVerdict)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var updated = c.Execute("""
            UPDATE inspections
               SET final_verdict = @NewVerdict, review_ts = @Ts, review_user = @Reviewer
             WHERE id = @InspectionId AND is_void = 0
            """, new { NewVerdict = newVerdict, Ts = r.Ts, Reviewer = r.Reviewer, InspectionId = r.InspectionId }, tx);
        if (updated == 0) { tx.Rollback(); return false; }

        c.Execute("""
            INSERT INTO reviews (inspection_id, ts, reviewer, decision, reason_code, note, prev_verdict)
            VALUES (@InspectionId, @Ts, @Reviewer, @Decision, @ReasonCode, @Note, @PrevVerdict)
            """, r, tx);
        r.Id = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<ReviewRow> ListReviews(string inspectionId)
    {
        using var c = Open();
        return c.Query<ReviewRow>("""
            SELECT id AS Id, inspection_id AS InspectionId, ts AS Ts, reviewer AS Reviewer,
                   decision AS Decision, reason_code AS ReasonCode, note AS Note, prev_verdict AS PrevVerdict
              FROM reviews WHERE inspection_id = @Id ORDER BY id DESC
            """, new { Id = inspectionId }).ToList();
    }

    // ---------- 维护 ----------

    public (long Inspections, long Defects, long Snapshots, long Users, long Audit, long Alarms) RowCounts()
    {
        using var c = Open();
        return (
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM inspections"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM defects"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM recipe_snapshots"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM users"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM alarms"));
    }
}
