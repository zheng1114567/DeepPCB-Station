using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Pipeline;

namespace DeepPCBStation.Core.Handshake;

public enum StationState
{
    Ready,
    Busy,
    Fault
}

/// <summary>
/// SoftPLC TCP 字符串握手（TDD §4.5）。
/// TRIG,partId → BUSY → OK|NG|TIMEOUT|FAULT；LAST / STATUS / RESET / PING；未知命令 → ERR,UNKNOWN。
/// TIMEOUT/DISCONNECT 不取消 Halcon；迟到结果标 VOID（落盘留证，不计良率），禁止补发 OK/NG。
/// </summary>
public sealed class HandshakeServer : IAsyncDisposable
{
    private static readonly Regex PartIdRe = new(@"^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly Func<InspectionTicket, CancellationToken, Task<InspectionResult>> _runInspection;
    private readonly Action<InspectionResult>? _persistResult;
    private readonly PersistFaultHandler? _persistFault;
    private readonly Func<(string recipeId, string version, string hash)> _recipeInfo;
    private readonly Func<bool>? _isChangeoverConfirmed;
    private readonly StationArbiter? _arbiter;
    private readonly Func<string?>? _currentOperator;
    private readonly string _stationId;

    private readonly object _gate = new();
    private StationState _state = StationState.Ready;
    private string? _activePartId;
    private long _generation;
    private readonly ConcurrentDictionary<long, byte> _discardGens = new();
    /// <summary>gen → 在飞的工单；断连时用于标记作废（否则迟到结果会污染良率）。</summary>
    private readonly ConcurrentDictionary<long, InspectionTicket> _tickets = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>并发连接上限：防止异常/恶意客户端无限建连拖垮工位（P2-⑦）。</summary>
    private const int MaxConnections = 16;
    private readonly SemaphoreSlim _connSlots = new(MaxConnections);
    /// <summary>单连接「等下一行」的空闲超时；仅在没有在飞检测时计时，不会误杀长检测（P2-⑦）。</summary>
    private readonly int _idleTimeoutMs;

    public StationState State { get { lock (_gate) return _state; } }
    public string? ActivePartId { get { lock (_gate) return _activePartId; } }
    public int Port => _port;
    public bool IsRunning => _loop is { IsCompleted: false };
    public event Action<string>? Log;

    /// <summary>落一条握手层面的故障记录，返回落库用的 inspectionId（用于回填 VOID 行的 parent_id）。</summary>
    public delegate string? PersistFaultHandler(string? partId, string faultCode, double elapsedMs,
        string? inspectionId, InspectionTicket? ticket);

    public HandshakeServer(
        int port,
        int timeoutMs,
        Func<InspectionTicket, CancellationToken, Task<InspectionResult>> runInspection,
        Func<(string recipeId, string version, string hash)> recipeInfo,
        Action<InspectionResult>? persistResult = null,
        PersistFaultHandler? persistFault = null,
        Func<bool>? isChangeoverConfirmed = null,
        StationArbiter? arbiter = null,
        Func<string?>? currentOperator = null,
        string? stationId = null,
        int idleTimeoutMs = 120_000)
    {
        _port = port;
        _timeoutMs = Math.Max(100, timeoutMs);
        _idleTimeoutMs = Math.Max(1_000, idleTimeoutMs);
        _runInspection = runInspection;
        _recipeInfo = recipeInfo;
        _persistResult = persistResult;
        _persistFault = persistFault;
        _isChangeoverConfirmed = isChangeoverConfirmed;
        _arbiter = arbiter;
        _currentOperator = currentOperator;
        _stationId = stationId ?? Environment.MachineName;
    }

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"Handshake 监听 :{_port}（TRIG,partId / LAST / STATUS / RESET / PING）timeout={_timeoutMs}ms");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _listener = null;
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        Log?.Invoke("Handshake 已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                if (!_connSlots.Wait(0))
                {
                    Log?.Invoke("并发连接已满，拒绝新连接");
                    try { client.Close(); }
                    // 预期：对端已断开 / 连接对象已释放。剩下的异常类型不能吞——
                    // 静默 catch 会让「拒绝连接路径本身坏了」这件事永远没人知道。
                    catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                    {
                        StationLog.Debug(nameof(HandshakeServer), "拒绝连接后关闭失败（对端已断开）: " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        StationLog.Warn(nameof(HandshakeServer), "拒绝连接后关闭失败: " + ex.Message, ex);
                    }
                    continue;
                }
                // 不用 Task.Run(..., ct)：避免 ct 取消时槽位 finally 不执行导致泄漏
                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, ct).ConfigureAwait(false); }
                    finally { _connSlots.Release(); }
                });
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Accept 异常: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string? lastResultLine = null;
        long? owningGen = null;
        using (client)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    // 空闲超时只约束「等下一行」的时段；HandleTrigAsync 执行期间本循环挂起，
                    // 因此不会误杀正在跑的检测（P2-⑦）。
                    var readTask = reader.ReadLineAsync();
                    var done = await Task.WhenAny(readTask, Task.Delay(_idleTimeoutMs, ct)).ConfigureAwait(false);
                    if (done != readTask && !readTask.IsCompleted)
                    {
                        if (!ct.IsCancellationRequested)
                            Log?.Invoke($"空闲 {_idleTimeoutMs}ms 无数据，断开连接");
                        break;
                    }
                    var line = await readTask.ConfigureAwait(false);
                    if (line is null) break;
                    var raw = line.Trim();
                    if (raw.Length == 0) continue;

                    var parts = raw.Split(',');
                    var cmd = parts[0].Trim().ToUpperInvariant();

                    switch (cmd)
                    {
                        case "PING":
                            await WriteAsync(writer, "PONG").ConfigureAwait(false);
                            break;

                        case "STATUS":
                        {
                            StationState st;
                            lock (_gate) st = _state;
                            var (rid, ver, hash) = _recipeInfo();
                            await WriteAsync(writer, $"STATUS,{st},{rid},{ver},{hash}").ConfigureAwait(false);
                            break;
                        }

                        case "LAST":
                            await WriteAsync(writer, lastResultLine ?? "LAST,NONE").ConfigureAwait(false);
                            break;

                        case "RESET":
                        {
                            lock (_gate)
                            {
                                if (_state == StationState.Fault)
                                {
                                    _state = StationState.Ready;
                                    _activePartId = null;
                                }
                            }
                            Log?.Invoke("RESET → Ready");
                            var (rid, ver, hash) = _recipeInfo();
                            StationState st;
                            lock (_gate) st = _state;
                            await WriteAsync(writer, $"STATUS,{st},{rid},{ver},{hash}").ConfigureAwait(false);
                            break;
                        }

                        case "TRIG":
                        case "TRIGGER":
                            await HandleTrigAsync(client, parts, writer, ct,
                                setLast: s => lastResultLine = s,
                                setOwning: g => owningGen = g,
                                clearOwning: () => owningGen = null).ConfigureAwait(false);
                            break;

                        default:
                            // L5：未知命令是**协议错误**，不是设备故障。原实现回 FAULT,-,INTERNAL
                            // 会让现场把「PLC 程序发错命令」误判为「视觉站故障需检修」。
                            // ERR,UNKNOWN 与 FAULT,-,*（BUSY/RECIPE_NOT_CONFIRMED/…）区分开。
                            Log?.Invoke($"未知命令: {cmd}");
                            await WriteAsync(writer, "ERR,UNKNOWN").ConfigureAwait(false);
                            break;
                    }
                }
            }
            // 这两类是**正常收尾**而非故障：站停机取消 / PLC 侧直接断开连接。
            // 保持吞掉（否则每次客户端断连都会刷一条错误），但留 Debug 级痕迹供排障。
            catch (OperationCanceledException ex)
            {
                StationLog.Debug(nameof(HandshakeServer), "客户端连接被取消: " + ex.Message);
            }
            catch (IOException ex)
            {
                StationLog.Debug(nameof(HandshakeServer), "客户端连接断开: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"客户端异常: {ex.Message}");
            }
            finally
            {
                if (owningGen is long g)
                {
                    string? pid = null;
                    bool wasOwning = false;
                    lock (_gate)
                    {
                        if (_state == StationState.Busy && _generation == g)
                        {
                            pid = _activePartId;
                            _state = StationState.Fault;
                            _activePartId = null;
                            _discardGens[g] = 1;
                            wasOwning = true;
                        }
                    }
                    // 取在飞 ticket（含 operator/trigger/stationId），避免故障行丢归属信息；
                    // 故障行 id 回填给迟到 VOID 行作 parent，两条记录可溯源关联。
                    string? faultId = null;
                    if (wasOwning)
                    {
                        _tickets.TryGetValue(g, out var t);
                        faultId = _persistFault?.Invoke(pid, "DISCONNECT", 0, null, t);
                        Log?.Invoke($"Busy 中断连 → Fault DISCONNECT（gen={g}）");
                    }
                    // 断连时还在跑的检测必须作废：否则它落盘后会把这一板算进良率
                    if (_tickets.TryRemove(g, out var orphan))
                    {
                        if (!string.IsNullOrEmpty(faultId))
                            orphan.SetParentInspectionId(faultId);
                        orphan.MarkVoid("DISCONNECT");
                        Log?.Invoke($"断连：在飞检测已标记 VOID（gen={g}）");
                    }
                }
            }
        }
    }

    private async Task HandleTrigAsync(
        TcpClient client,
        string[] parts,
        StreamWriter writer,
        CancellationToken ct,
        Action<string> setLast,
        Action<long> setOwning,
        Action clearOwning)
    {
        // 协议严格为 TRIG,partId（恰好两段）。多于/少于两段（如 TRIG,A,B 误带额外字段）
        // 一律按非法拒收，避免截断后拿 partId[1] 对错误工位开检。
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[1].Trim()) || !PartIdRe.IsMatch(parts[1].Trim()))
        {
            var fault = "FAULT,-,BAD_PART_ID";
            setLast(fault);
            await WriteAsync(writer, fault).ConfigureAwait(false);
            return;
        }
        var partId = parts[1].Trim();

        if (_isChangeoverConfirmed != null && !_isChangeoverConfirmed())
        {
            var fault = "FAULT,-,RECIPE_NOT_CONFIRMED";
            setLast(fault);
            _persistFault?.Invoke(partId, "RECIPE_NOT_CONFIRMED", 0, null, null);
            await WriteAsync(writer, fault).ConfigureAwait(false);
            Log?.Invoke($"TRIG 拒收：换型未确认 ({partId})");
            return;
        }

        // 无登录会话 → 拒绝：不允许产出无归属的检测记录（21 CFR Part 11 / IPC-1782）
        var operatorName = _currentOperator?.Invoke();
        if (_arbiter != null && string.IsNullOrWhiteSpace(operatorName))
        {
            var fault = "FAULT,-,NO_OPERATOR";
            setLast(fault);
            await WriteAsync(writer, fault).ConfigureAwait(false);
            Log?.Invoke($"TRIG 拒收：无登录操作员 ({partId})");
            return;
        }

        // 触发权：UI 正在检测时立即 BUSY，不排队到超时（排队正是“一板双记录”的根源）
        if (_arbiter != null)
        {
            var lease = _arbiter.TryAcquire(TriggerSource.Plc, partId, out var busyReason);
            if (lease != LeaseResult.Acquired)
            {
                var fault = "FAULT,-,BUSY";
                setLast(fault);
                await WriteAsync(writer, fault).ConfigureAwait(false);
                Log?.Invoke($"TRIG 拒收：{busyReason}（拒 {partId}）");
                return;
            }
        }

        try
        {
            await RunTrigCoreAsync(client, partId, operatorName, writer, ct,
                setLast, setOwning, clearOwning).ConfigureAwait(false);
        }
        finally
        {
            _arbiter?.Release(TriggerSource.Plc);
        }
    }

    private async Task RunTrigCoreAsync(
        TcpClient client,
        string partId,
        string? operatorName,
        StreamWriter writer,
        CancellationToken ct,
        Action<string> setLast,
        Action<long> setOwning,
        Action clearOwning)
    {
        long gen;
        bool accepted;
        bool inFault;
        lock (_gate)
        {
            inFault = _state == StationState.Fault;
            if (_state == StationState.Busy)
            {
                accepted = false;
                gen = 0;
            }
            else if (inFault)
            {
                accepted = false;
                gen = -1;
            }
            else
            {
                _state = StationState.Busy;
                _activePartId = partId;
                gen = ++_generation;
                accepted = true;
            }
        }

        if (!accepted)
        {
            if (gen < 0)
            {
                var fault = "FAULT,-,INTERNAL";
                setLast(fault);
                await WriteAsync(writer, fault).ConfigureAwait(false);
                Log?.Invoke("TRIG 拒收：当前 Fault，需 RESET");
            }
            else
            {
                await WriteAsync(writer, "FAULT,-,BUSY").ConfigureAwait(false);
                Log?.Invoke($"TRIG 冲突 BUSY（拒 {partId}）");
            }
            return;
        }

        var ticket = new InspectionTicket
        {
            PartId = partId,
            Operator = operatorName,
            Shift = InspectionTicket.ShiftOf(DateTime.Now),
            Trigger = TriggerSource.Plc,
            StationId = _stationId,
            RequireChangeover = true,
            Persist = true
        };
        _tickets[gen] = ticket;

        setOwning(gen);
        await WriteAsync(writer, $"BUSY,{partId}").ConfigureAwait(false);
        Log?.Invoke($"TRIG {partId} → Busy gen={gen}");

        var sw = Stopwatch.StartNew();
        // 超时不取消 Halcon；并行轮询 TCP 断连（每个 TRIG 独立，用完即停，不再累积泄漏）
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inspectTask = _runInspection(ticket, CancellationToken.None);
        var delayTask = Task.Delay(_timeoutMs, ct);
        var disconnectTask = PollDisconnectAsync(client, pollCts.Token);
        var winner = await Task.WhenAny(inspectTask, delayTask, disconnectTask).ConfigureAwait(false);

        if (winner == disconnectTask && !inspectTask.IsCompleted)
        {
            // 先作废再落故障：保证迟到结果即使此刻落盘也是 VOID
            ticket.MarkVoid("DISCONNECT");
            _discardGens[gen] = 1;
            lock (_gate)
            {
                if (_generation == gen)
                {
                    _state = StationState.Fault;
                    _activePartId = null;
                }
            }
            _persistFault?.Invoke(partId, "DISCONNECT", sw.Elapsed.TotalMilliseconds, null, ticket);
            _tickets.TryRemove(gen, out _);
            Log?.Invoke($"Busy 中检测到断连 → Fault DISCONNECT（gen={gen}）");
            _ = inspectTask.ContinueWith(t =>
            {
                _discardGens.TryRemove(gen, out _);
                Log?.Invoke($"断连后后台完成（已作废，不补发） gen={gen}");
            }, TaskContinuationOptions.ExecuteSynchronously);
            clearOwning();
            return;
        }

        if (winner == delayTask && !inspectTask.IsCompleted)
        {
            ticket.MarkVoid("TIMEOUT");
            _discardGens[gen] = 1;
            lock (_gate)
            {
                if (_generation == gen)
                {
                    _state = StationState.Fault;
                    _activePartId = null;
                }
            }
            var timeoutLine = $"TIMEOUT,{partId},TIMEOUT";
            setLast(timeoutLine);
            var timeoutId = _persistFault?.Invoke(partId, "TIMEOUT", sw.Elapsed.TotalMilliseconds, null, ticket);
            ticket.SetParentInspectionId(timeoutId);
            _tickets.TryRemove(gen, out _);
            try { await WriteAsync(writer, timeoutLine).ConfigureAwait(false); }
            catch { /* 可能已断 */ }
            Log?.Invoke($"TIMEOUT {partId} → Fault（迟到结果将落 VOID，不计良率）");

            _ = inspectTask.ContinueWith(t =>
            {
                _discardGens.TryRemove(gen, out _);
                if (t.IsFaulted)
                    Log?.Invoke($"超时后后台异常（已作废）: {t.Exception?.GetBaseException().Message}");
                else
                    Log?.Invoke($"超时后后台完成 inspectionId={t.Result.InspectionId}（已作废，不补发）");
            }, TaskContinuationOptions.ExecuteSynchronously);
            clearOwning();
            return;
        }

        InspectionResult result;
        try
        {
            result = await inspectTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _discardGens.TryRemove(gen, out _);
            _tickets.TryRemove(gen, out _);
            lock (_gate)
            {
                if (_generation == gen)
                {
                    _state = StationState.Fault;
                    _activePartId = null;
                }
            }
            var fault = $"FAULT,{partId},INTERNAL";
            setLast(fault);
            _persistFault?.Invoke(partId, "INTERNAL", sw.Elapsed.TotalMilliseconds, null, ticket);
            // 下发失败只可能是连接已断（IOException/ObjectDisposed/取消）——PLC 收不到 FAULT 会走超时，
            // 但**必须留痕**：否则「站已置 Fault 而 PLC 一直等」在日志里完全看不出来。
            try { await WriteAsync(writer, fault).ConfigureAwait(false); }
            catch (Exception wex) when (wex is IOException || wex is ObjectDisposedException
                                        || wex is OperationCanceledException)
            {
                StationLog.Warn(nameof(HandshakeServer), "FAULT 下发失败（连接已断开）: " + wex.Message);
            }
            catch (Exception wex)
            {
                StationLog.Error(nameof(HandshakeServer), "FAULT 下发失败: " + wex.Message, wex);
            }
            StationLog.Error(nameof(HandshakeServer), $"检测异常: {ex.Message}", ex);
            Log?.Invoke($"检测异常: {ex.Message}");
            clearOwning();
            return;
        }

        _tickets.TryRemove(gen, out _);
        if (_discardGens.TryRemove(gen, out _))
        {
            clearOwning();
            return;
        }

        string resultLine;
        if (result.IsFault)
        {
            var code = result.FaultCode ?? "ENGINE";
            resultLine = $"FAULT,{partId},{code}";
            lock (_gate)
            {
                if (_generation == gen)
                {
                    _state = StationState.Fault;
                    _activePartId = null;
                }
            }
        }
        else
        {
            var tag = result.IsOk ? "OK" : "NG";
            resultLine = string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2:F1},{3}", tag, partId, result.ElapsedMs, result.InspectionId);
            lock (_gate)
            {
                if (_generation == gen)
                {
                    _state = StationState.Ready;
                    _activePartId = null;
                }
            }
        }

        _persistResult?.Invoke(result);
        setLast(resultLine);
        try
        {
            await WriteAsync(writer, resultLine).ConfigureAwait(false);
            Log?.Invoke($"回复 {resultLine}");
        }
        catch (Exception ex)
        {
            _discardGens[gen] = 1;
            lock (_gate)
            {
                _state = StationState.Fault;
                _activePartId = null;
            }
            _persistFault?.Invoke(partId, "DISCONNECT", result.ElapsedMs, result.InspectionId, ticket);
            Log?.Invoke($"写出失败→DISCONNECT Fault: {ex.Message}");
        }
        clearOwning();
    }

    private static async Task PollDisconnectAsync(TcpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsSocketConnected(client))
                return;
            try
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>远端关闭：Poll 可读且 Available==0。</summary>
    private static bool IsSocketConnected(TcpClient client)
    {
        try
        {
            if (client is null || !client.Connected)
                return false;
            var s = client.Client;
            if (s is null || !s.Connected)
                return false;
            return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private static Task WriteAsync(StreamWriter writer, string line) => writer.WriteLineAsync(line);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
