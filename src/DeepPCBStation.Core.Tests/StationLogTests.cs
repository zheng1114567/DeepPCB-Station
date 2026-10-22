using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepPCBStation.Core.Diagnostics;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 统一日志的契约测试。
/// 重点：**日志永远不能成为故障源**——未装 sink 是空操作，sink 抛异常也不得冒泡到业务代码。
/// </summary>
public class StationLogTests : IDisposable
{
    private sealed class RecordingSink : IStationLogSink
    {
        public List<(LogLevel Level, string Source, string Message)> Entries { get; } = new();

        public void Write(LogLevel level, string source, string message)
            => Entries.Add((level, source, message));

        public void Write(LogLevel level, string source, string message, Exception ex)
            => Entries.Add((level, source, message + " | " + ex.GetType().Name));
    }

    private sealed class ThrowingSink : IStationLogSink
    {
        public void Write(LogLevel level, string source, string message) => throw new InvalidOperationException("boom");

        public void Write(LogLevel level, string source, string message, Exception ex) => throw new InvalidOperationException("boom");
    }

    public void Dispose()
    {
        StationLog.SetSink(null);
        StationLog.MinLevel = LogLevel.Info;
    }

    [Fact]
    public void 未装填sink时_写日志是空操作且不抛()
    {
        StationLog.SetSink(null);
        Assert.False(StationLog.IsEnabled(LogLevel.Error));
        StationLog.Error("t", "不该抛");
        StationLog.Warn("t", "不该抛", new Exception("x"));
    }

    [Fact]
    public void 装填后_按级别过滤_默认不含Debug()
    {
        var sink = new RecordingSink();
        StationLog.SetSink(sink);
        StationLog.Debug("s", "debug");
        StationLog.Info("s", "info");
        StationLog.Warn("s", "warn");
        StationLog.Error("s", "error");

        Assert.DoesNotContain(sink.Entries, e => e.Level == LogLevel.Debug);
        Assert.Equal(3, sink.Entries.Count);
        Assert.Equal(new[] { LogLevel.Info, LogLevel.Warn, LogLevel.Error },
            sink.Entries.Select(e => e.Level).ToArray());
    }

    [Fact]
    public void 调低阈值后_Debug可见()
    {
        var sink = new RecordingSink();
        StationLog.SetSink(sink);
        StationLog.MinLevel = LogLevel.Debug;
        StationLog.Debug("s", "debug");
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Debug && e.Message == "debug");
    }

    [Fact]
    public void 带异常的日志_把异常类型带上()
    {
        var sink = new RecordingSink();
        StationLog.SetSink(sink);
        StationLog.Error("s", "写库失败", new IOException("disk"));
        Assert.Contains("IOException", sink.Entries[0].Message);
    }

    [Fact]
    public void sink自身抛异常_不得冒泡到调用方()
    {
        StationLog.SetSink(new ThrowingSink());
        StationLog.Error("s", "x");           // 若冒泡，这里会失败
        StationLog.Warn("s", "x", new Exception());
        Assert.True(true);
    }

    [Fact]
    public void 复合sink_单个端点抛异常不影响其它端点()
    {
        var good = new RecordingSink();
        StationLog.SetSink(new StationLog.CompositeSink(new ThrowingSink(), good));
        StationLog.Warn("s", "仍然要到达");
        Assert.Single(good.Entries);
    }

    [Fact]
    public void 文件sink_写入可回看_且目录非法时不抛()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dpcb-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sink = new FileLogSink(dir);
            sink.Write(LogLevel.Warn, "Test", "hello 现场排障");
            var files = Directory.GetFiles(dir, "station-*.log");
            Assert.Single(files);
            Assert.Contains("hello 现场排障", File.ReadAllText(files[0]));
            Assert.Contains("WRN", File.ReadAllText(files[0]));

            // 日志目录不可建（这里用「以文件为父目录」制造）时不得抛——日志不能把业务打挂
            var blocker = Path.Combine(dir, "notadir");
            File.WriteAllText(blocker, "x");
            var bad = new FileLogSink(blocker);
            bad.Write(LogLevel.Error, "Test", "写不进去也不许抛");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
