using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeepPCBStation.Core.Handshake;
using DeepPCBStation.Core.Pipeline;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// M2：握手集成层测试（此前该层测试数为 0）。
///
/// 为什么值得测：握手是「一板双记录」「断连把未看清的板算进良率」的高发点。
/// 协议里每一行都是现场 PLC 程序依赖的契约——写错一行的代价是停机或误判，
/// 而且这类错误在文件回放模式下永远不会暴露（回放不走 TCP）。
/// 本文件钉住四类语义：协议行格式、拒收条件、超时/断连的 VOID 语义、Fault→RESET 恢复。
/// </summary>
public sealed class HandshakeServerTests
{
    // ---------------------------------------------------------------- 测试替身

    /// <summary>收集服务端回调：落库结果、故障行、在飞工单。</summary>
    private sealed class Rec
    {
        public readonly ConcurrentQueue<InspectionResult> Results = new();
        public readonly ConcurrentQueue<(string? PartId, string Code)> Faults = new();
        public volatile InspectionTicket? LastTicket;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static HandshakeServer NewServer(
        int timeoutMs,
        Func<InspectionTicket, CancellationToken, Task<InspectionResult>> run,
        Rec rec)
    {
        return new HandshakeServer(
            FreePort(),
            timeoutMs,
            runInspection: (t, ct) => { rec.LastTicket = t; return run(t, ct); },
            recipeInfo: () => ("pcb_open_mousebite", "v1.2.1", "26d991859176a03ee31cc7d5b728d646"),
            persistResult: r => rec.Results.Enqueue(r),
            persistFault: (partId, code, elapsed, id, ticket) =>
            {
                rec.Faults.Enqueue((partId, code));
                return "FAULT-1";   // 供迟到 VOID 行回填 parent_id
            });
    }

    /// <summary>慢检测：模拟超过握手超时的检测（超时路径用）。</summary>
    private static Func<InspectionTicket, CancellationToken, Task<InspectionResult>> Slow(int ms)
        => async (t, ct) =>
        {
            await Task.Delay(ms).ConfigureAwait(false);
            return new InspectionResult { InspectionId = "LATE-1", PartId = t.PartId, IsOk = true };
        };

    private static Func<InspectionTicket, CancellationToken, Task<InspectionResult>> Ok(string id = "I-1")
        => (t, ct) => Task.FromResult(new InspectionResult
        {
            InspectionId = id,
            PartId = t.PartId,
            IsOk = true,
            ElapsedMs = 12.5
        });

    /// <summary>最小 TCP 客户端：发一行、等一行（自带超时，避免测试挂死）。</summary>
    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public Client(int port)
        {
            _tcp = new TcpClient();
            _tcp.Connect(IPAddress.Loopback, port);
            var s = _tcp.GetStream();
            _reader = new StreamReader(s, Encoding.ASCII);
            _writer = new StreamWriter(s, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
        }

        public void Send(string line)
        {
            try { _writer.WriteLine(line); } catch (IOException) { /* 可能已被服务端断开 */ }
        }

        /// <summary>等一行；超时抛异常（宁可测试失败，也不要 CI 挂死）。</summary>
        public string Read(int ms = 5000)
        {
            var task = _reader.ReadLineAsync();
            if (Task.WaitAny(task) < 0 || !task.Wait(ms))
                throw new TimeoutException($"等握手回复超过 {ms}ms");
            return task.Result ?? "<EOF>";
        }

        /// <summary>模拟 PLC 掉线：不拔线，直接关 socket。</summary>
        public void Drop() { try { _tcp.Close(); } catch { } }

        public void Dispose() { try { _tcp.Close(); } catch { } }
    }

    private static void WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException("条件未在超时内成立");
    }

    // ---------------------------------------------------------------- 协议行格式

    [Fact]
    public async Task Ping_RepliesPong()
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok(), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("PING");
        Assert.Equal("PONG", c.Read());
    }

    [Fact]
    public async Task Status_ReportsStateAndRecipeIdentity()
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok(), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("STATUS");
        // 配方标识进 STATUS 是为了让 PLC/上位机能核对「现在跑的是不是这套配方」
        Assert.Equal("STATUS,Ready,pcb_open_mousebite,v1.2.1,26d991859176a03ee31cc7d5b728d646", c.Read());
    }

    [Fact]
    public async Task Last_BeforeAnyInspection_IsNone()
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok(), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("LAST");
        Assert.Equal("LAST,NONE", c.Read());
    }

    /// <summary>
    /// L5 回归：未知命令是**协议错误**（PLC 程序写错），不是设备故障。
    /// 原实现回 FAULT,-,INTERNAL 会让现场把「改 PLC 程序」误判成「视觉站需检修」。
    /// </summary>
    [Fact]
    public async Task UnknownCommand_RepliesErrUnknown_NotFault()
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok(), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("FOO");
        var line = c.Read();

        Assert.Equal("ERR,UNKNOWN", line);
        Assert.False(line.StartsWith("FAULT", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 拒收条件

    /// <summary>
    /// 协议严格为 TRIG,partId（恰好两段）。多段/缺段/非法字符一律拒收，
    /// 否则会出现「截断后拿 parts[1] 对错误料号开检」这类最难查的现场事故。
    /// </summary>
    [Theory]
    [InlineData("TRIG")]
    [InlineData("TRIG,")]
    [InlineData("TRIG,A,B")]
    [InlineData("TRIG,ab cd")]
    [InlineData("TRIG,板号#1")]
    public async Task Trig_MalformedPartId_Rejected(string line)
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok(), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send(line);
        Assert.Equal("FAULT,-,BAD_PART_ID", c.Read());
        Assert.Empty(rec.Results);          // 拒收的触发绝不产生检测记录
    }

    // ---------------------------------------------------------------- 正常路径

    [Fact]
    public async Task Trig_RepliesBusyThenResultLine_AndPersistsResult()
    {
        var rec = new Rec();
        await using var srv = NewServer(2000, Ok("I-1"), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("TRIG,P001");
        Assert.Equal("BUSY,P001", c.Read());
        var result = c.Read();

        Assert.StartsWith("OK,P001,", result, StringComparison.Ordinal);
        Assert.EndsWith(",I-1", result, StringComparison.Ordinal);
        Assert.Single(rec.Results);
    }

    // ---------------------------------------------------------------- VOID 语义

    /// <summary>
    /// 超时：不取消 Halcon（检测继续跑完），但**这一板必须作废**——
    /// 迟到结果只落盘留证、不计良率，且不允许补发 OK/NG 给 PLC。
    /// </summary>
    [Fact]
    public async Task Trig_Timeout_RepliesTimeout_AndMarksTicketVoid()
    {
        var rec = new Rec();
        await using var srv = NewServer(200, Slow(3000), rec);   // 握手超时 200ms，检测要 3s
        srv.Start();
        using var c = new Client(srv.Port);

        c.Send("TRIG,P002");
        Assert.Equal("BUSY,P002", c.Read());
        var line = c.Read(4000);

        Assert.Equal("TIMEOUT,P002,TIMEOUT", line);
        // 工单必须先作废再落故障行，保证迟到结果落盘时已是 VOID
        WaitUntil(() => rec.LastTicket != null);
        Assert.True(rec.LastTicket!.IsVoid);
        Assert.Equal("TIMEOUT", rec.LastTicket.VoidReason);
        // 故障行落库（TIMEOUT），供迟到 VOID 记录回填 parent_id
        WaitUntil(() => rec.Faults.TryPeek(out var f) && f.Code == "TIMEOUT");
    }

    /// <summary>
    /// 断连：PLC 在检测途中掉线。此时这一板「没收到结果」，
    /// 若不作废，检测完成后落盘就会把一个未确认的判定算进良率。
    /// 断言：故障码 DISCONNECT + 工单作废。
    /// </summary>
    [Fact]
    public async Task Trig_DisconnectMidInspection_PersistsDisconnectFaultAndVoids()
    {
        var rec = new Rec();
        await using var srv = NewServer(3000, Slow(3000), rec);
        srv.Start();

        var c = new Client(srv.Port);
        c.Send("TRIG,P003");
        Assert.Equal("BUSY,P003", c.Read());
        c.Drop();                       // 检测途中掉线

        WaitUntil(() => rec.Faults.TryPeek(out var f) && f.Code == "DISCONNECT", 5000);
        WaitUntil(() => rec.LastTicket != null);
        Assert.True(rec.LastTicket!.IsVoid);
        Assert.Equal("DISCONNECT", rec.LastTicket.VoidReason);
        c.Dispose();
    }

    // ---------------------------------------------------------------- Fault 恢复

    /// <summary>进入 Fault 后必须靠显式 RESET 恢复，防止「故障自愈后继续过板」。</summary>
    [Fact]
    public async Task Fault_ThenReset_ReturnsReadyAndAcceptsTrig()
    {
        var rec = new Rec();
        await using var srv = NewServer(200, Slow(3000), rec);
        srv.Start();
        using var c = new Client(srv.Port);

        // 制造 Fault：超时
        c.Send("TRIG,P004");
        Assert.Equal("BUSY,P004", c.Read());
        Assert.Equal("TIMEOUT,P004,TIMEOUT", c.Read(4000));

        // Fault 期间的 TRIG 必须被拒收（不能再过板）
        c.Send("TRIG,P005");
        Assert.Equal("FAULT,-,INTERNAL", c.Read());

        // RESET → Ready
        c.Send("RESET");
        Assert.StartsWith("STATUS,Ready,", c.Read(), StringComparison.Ordinal);

        // 恢复后正常受理
        c.Send("TRIG,P006");
        Assert.Equal("BUSY,P006", c.Read());
    }
}
