using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 检测阶段计时（诊断用）。默认关闭，环境变量 DPCB_PROFILE=1 打开。
///
/// 纪律：只读计时，不改任何判定；关闭时 <see cref="Mark"/> 返回 0、<see cref="Add"/> 直接 return，
/// 开销是一次静态 bool 判断。
/// </summary>
public static class StageTimer
{
    public static bool Enabled { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("DPCB_PROFILE"), "1", StringComparison.Ordinal);

    private static readonly Dictionary<string, long> Ticks = new();
    private static readonly Dictionary<string, int> Counts = new();
    private static readonly object Gate = new();

    /// <summary>取时间戳；未启用时返回 0，下游 Add 会直接忽略。</summary>
    public static long Mark() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void Add(string name, long startTs)
    {
        if (!Enabled || startTs == 0L) return;
        var delta = Stopwatch.GetTimestamp() - startTs;
        lock (Gate)
        {
            Ticks[name] = Ticks.TryGetValue(name, out var v) ? v + delta : delta;
            Counts[name] = Counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Ticks.Clear();
            Counts.Clear();
        }
    }

    /// <summary>按耗时降序输出；pairs 用于换算每对均值。</summary>
    public static string Report(int pairs)
    {
        lock (Gate)
        {
            if (Ticks.Count == 0) return "[Profile] 无数据（DPCB_PROFILE 未开启？）";
            var total = Ticks.Where(k => !k.Key.StartsWith(">")).Sum(x => x.Value);
            var sb = new StringBuilder();
            sb.AppendLine($"[Profile] pairs={pairs} 各阶段累计（按占比降序）");
            sb.AppendLine($"  {"阶段",-14}{"总耗时ms",12}{"占比",10}{"每对ms",10}{"次数",8}");
            foreach (var kv in Ticks.OrderByDescending(x => x.Value))
            {
                var ms = kv.Value * 1000.0 / Stopwatch.Frequency;
                var share = total == 0 ? 0 : kv.Value * 100.0 / total;
                var per = pairs > 0 ? ms / pairs : 0;
                sb.AppendLine($"  {kv.Key,-14}{ms,12:F0}{share,9:F1}%{per,10:F1}{Counts[kv.Key],8}");
            }
            var allMs = total * 1000.0 / Stopwatch.Frequency;
            sb.AppendLine($"  {"合计",-14}{allMs,12:F0}{100,9:F1}%{(pairs > 0 ? allMs / pairs : 0),10:F1}");
            return sb.ToString();
        }
    }
}
