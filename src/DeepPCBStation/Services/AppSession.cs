using System;
using System.Windows;
using DeepPCBStation.Core.Diagnostics;

namespace DeepPCBStation.Services;

/// <summary>
/// 会话（21 CFR Part 11 / IEC 62443）：谁在线、是否锁屏、空闲多久。
/// 锁屏只锁 UI，不停检测——产线检测由 PLC 触发时仍要出结果，
/// 但这类结果必须带 operator 归属，所以锁屏≠登出，会话仍在。
/// </summary>
public sealed class AppSession
{
    /// <summary>进程内唯一会话。</summary>
    public static AppSession Current { get; } = new();

    private readonly object _gate = new();
    private UserAccount? _user;
    private bool _isLocked;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    /// <summary>会话变化（登录 / 登出 / 上锁 / 解锁）。可能在任意线程触发。</summary>
    public event Action? Changed;

    public UserAccount? User { get { lock (_gate) return _user; } }

    public bool IsLocked { get { lock (_gate) return _isLocked; } }

    /// <summary>是否已登录（未登录时不允许产出可追溯检测记录）。</summary>
    public bool IsAuthenticated => User != null;

    public DateTime LastActivityUtc { get { lock (_gate) return _lastActivityUtc; } }

    public TimeSpan Idle => DateTime.UtcNow - LastActivityUtc;

    public DateTime? LockedAtLocal { get; private set; }

    /// <summary>开始会话（登录成功后调用）。</summary>
    public void Begin(UserAccount user)
    {
        lock (_gate)
        {
            _user = user;
            _isLocked = false;
            _lastActivityUtc = DateTime.UtcNow;
            LockedAtLocal = null;
        }
        RaiseChanged();
    }

    /// <summary>记录一次用户活动。锁屏状态下不刷新空闲计时（否则遮罩会被自家心跳续命）。</summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_user == null || _isLocked) return;
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>空闲超时上锁。返回 true 表示本次调用真的上了锁。</summary>
    public bool Lock()
    {
        bool changed;
        lock (_gate)
        {
            if (_user == null || _isLocked) return false;
            _isLocked = true;
            LockedAtLocal = DateTime.Now;
            changed = true;
        }
        if (changed) RaiseChanged();
        return true;
    }

    /// <summary>解锁（密码校验由 UserService 完成后调用）。</summary>
    public void Unlock()
    {
        lock (_gate)
        {
            if (!_isLocked) return;
            _isLocked = false;
            LockedAtLocal = null;
            _lastActivityUtc = DateTime.UtcNow;
        }
        RaiseChanged();
    }

    /// <summary>结束会话（登出）。必须清空用户，否则下一个登录者会继承上一个的身份写审计。</summary>
    public void End()
    {
        lock (_gate)
        {
            _user = null;
            _isLocked = false;
            LockedAtLocal = null;
            _lastActivityUtc = DateTime.UtcNow;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null) return;
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess())
        {
            // 会话回调异常**不得影响检测线程**（订阅方是 UI，UI 崩了不该拖垮检测），
            // 但也不能彻底无声：谁订阅了、抛什么，事后要能查。
            try { handler(); }
            catch (Exception ex) { StationLog.Warn(nameof(AppSession), "会话回调异常: " + ex.Message); }
            return;
        }
        d.BeginInvoke(new Action(() =>
        {
            try { handler(); }
            catch (Exception ex) { StationLog.Warn(nameof(AppSession), "会话回调异常(UI线程): " + ex.Message); }
        }));
    }
}
