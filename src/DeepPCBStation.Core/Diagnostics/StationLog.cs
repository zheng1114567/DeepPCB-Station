using System;
using System.Collections.Generic;
using System.IO;

namespace DeepPCBStation.Core.Diagnostics;

/// <summary>日志级别。<see cref="Debug"/> 只在排障时打开，默认阈值是 <see cref="Info"/>。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 日志落地端点。上位机/CLI 各自实现（控制台、文件、调试窗口），Core 只依赖本接口。
/// 实现**不得抛异常**：日志失败不能反过来把业务打挂。
/// </summary>
public interface IStationLogSink
{
    void Write(LogLevel level, string source, string message);
    void Write(LogLevel level, string source, string message, Exception ex);
}

/// <summary>
/// 全站统一日志入口。
///
/// 为什么要有它：改之前库代码直接 <c>Console.Error.WriteLine</c>（上位机里看不到，
/// 控制台里和评测报告抢 stdout），上位机则用 <c>Debug.WriteLine</c>（Release 下被整段裁掉，
/// 现场出问题拿不到任何线索）。两端都不受控。这里给一个默认空实现的可插拔入口，
/// 由宿主在启动时装填 sink；未装填时所有写日志调用是空操作，行为与改前一致。
/// </summary>
public static class StationLog
{
    private static readonly object Gate = new();
    private static IStationLogSink? _sink;
    private static LogLevel _minLevel = LogLevel.Info;

    /// <summary>装填落地端点；传 null 恢复为「不记录」。</summary>
    public static void SetSink(IStationLogSink? sink)
    {
        lock (Gate) _sink = sink;
    }

    /// <summary>当前端点（未装填为 null）。</summary>
    public static IStationLogSink? Sink
    {
        get { lock (Gate) return _sink; }
    }

    /// <summary>最低记录级别，默认 <see cref="LogLevel.Info"/>（不含 Debug）。</summary>
    public static LogLevel MinLevel
    {
        get { lock (Gate) return _minLevel; }
        set { lock (Gate) _minLevel = value; }
    }

    public static bool IsEnabled(LogLevel level)
    {
        lock (Gate) return _sink != null && level >= _minLevel;
    }

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message, null);

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message, null);

    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message, null);

    public static void Warn(string source, string message, Exception ex) => Write(LogLevel.Warn, source, message, ex);

    public static void Error(string source, string message) => Write(LogLevel.Error, source, message, null);

    public static void Error(string source, string message, Exception ex) => Write(LogLevel.Error, source, message, ex);

    private static void Write(LogLevel level, string source, string message, Exception? ex)
    {
        IStationLogSink? sink;
        LogLevel min;
        lock (Gate) { sink = _sink; min = _minLevel; }
        if (sink == null || level < min) return;
        try
        {
            if (ex == null) sink.Write(level, source, message);
            else sink.Write(level, source, message, ex);
        }
        catch
        {
            // 日志本身永远不能成为故障源；这里连 Debug 都不写（Debug 也可能抛）。
        }
    }

    /// <summary>桥接用：把日志转发给委托（例如转发到调试窗口）。**不缓存**，可长期挂在生产。</summary>
    public sealed class DelegateSink : IStationLogSink
    {
        private readonly Action<LogLevel, string, string> _onMessage;
        private readonly Action<LogLevel, string, string, Exception>? _onException;

        public DelegateSink(
            Action<LogLevel, string, string> onMessage,
            Action<LogLevel, string, string, Exception>? onException = null)
        {
            _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
            _onException = onException;
        }

        public void Write(LogLevel level, string source, string message) => _onMessage(level, source, message);

        public void Write(LogLevel level, string source, string message, Exception ex)
        {
            if (_onException != null) _onException(level, source, message, ex);
            else _onMessage(level, source, message + " | " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 复合端点：按序转发给多个实现（例如「调试窗口 + 文件」）。
    /// 单个实现抛异常不影响其它实现。
    /// </summary>
    public sealed class CompositeSink : IStationLogSink
    {
        private readonly IStationLogSink[] _sinks;

        public CompositeSink(params IStationLogSink[] sinks)
        {
            _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        }

        public void Write(LogLevel level, string source, string message)
        {
            foreach (var s in _sinks)
            {
                try { s.Write(level, source, message); }
                catch { /* 单个端点失败不影响其它端点 */ }
            }
        }

        public void Write(LogLevel level, string source, string message, Exception ex)
        {
            foreach (var s in _sinks)
            {
                try { s.Write(level, source, message, ex); }
                catch { /* 同上 */ }
            }
        }
    }
}

/// <summary>
/// 滚动文件日志：每天一个文件，单文件超过上限后另起 <c>.1/.2…</c>，保留最近若干个。
/// 定位是「现场可诊断性」——上位机 Release 下也要能回看发生了什么。
/// 全部写盘失败都静默吞掉：磁盘满/只读目录不应影响检测。
/// </summary>
public sealed class FileLogSink : IStationLogSink
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly int _keepFiles;

    public FileLogSink(string dir, long maxBytes = 4L * 1024 * 1024, int keepFiles = 5)
    {
        _dir = dir;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _keepFiles = Math.Max(1, keepFiles);
    }

    /// <summary>日志目录。故意不叫 <c>Directory</c>——会遮蔽 <see cref="Directory"/> 类型名。</summary>
    public string LogDirectory => _dir;

    public void Write(LogLevel level, string source, string message) => Write(level, source, message, null);

    public void Write(LogLevel level, string source, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(level)}] {source}: {message}";
            if (ex != null) line += " | " + ex.GetType().Name + ": " + ex.Message;
            AppendLine(line);
        }
        catch
        {
            // 写盘失败（磁盘满/权限/路径非法）一律不向上抛
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };

    private void AppendLine(string line)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var path = CurrentPath();
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    /// <summary>当前写入目标：单文件超限则滚动到 <c>.N</c>。</summary>
    private string CurrentPath()
    {
        var day = DateTime.Now.ToString("yyyyMMdd");
        var path = Path.Combine(_dir, "station-" + day + ".log");
        for (var i = 1; i < _keepFiles; i++)
        {
            if (!File.Exists(path)) return path;
            var len = new FileInfo(path).Length;
            if (len < _maxBytes) return path;
            path = Path.Combine(_dir, $"station-{day}.{i}.log");
        }
        return path;
    }
}
