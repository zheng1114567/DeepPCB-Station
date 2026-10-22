using System.Security.Cryptography;
using DeepPCBStation.Core.Storage.Db;

namespace DeepPCBStation.Services;

public static class PasswordHasher
{
    public static string NewSalt()
    {
        var buf = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(buf);
        return Convert.ToBase64String(buf);
    }

    public static string Hash(string password, string saltBase64)
    {
        using var kdf = new Rfc2898DeriveBytes(password, Convert.FromBase64String(saltBase64), 100_000, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(kdf.GetBytes(32));
    }

    /// <summary>
    /// 恒定时间比较（.NET Framework 4.8 没有 CryptographicOperations.FixedTimeEquals）。
    /// 字符串 != 比较会在首个不同字节提前返回，理论上有计时侧信道。
    /// </summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a ?? "");
        var bb = System.Text.Encoding.UTF8.GetBytes(b ?? "");
        if (ba.Length != bb.Length) return false;
        var diff = 0;
        for (var i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }
}

/// <summary>密码策略（21 CFR Part 11 §11.300：强度 + 定期更换）。</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static bool Validate(string? password, out string error)
    {
        error = "";
        // net48 参考程序集上没有 NotNullWhen 标注，这里显式收窄为局部非空变量
        var pwd = password ?? "";
        if (pwd.Length == 0) { error = "密码不能为空。"; return false; }
        if (pwd.Length < MinLength) { error = $"密码至少 {MinLength} 位。"; return false; }
        if (pwd.Length > MaxLength) { error = $"密码不能超过 {MaxLength} 位。"; return false; }
        var hasLetter = false;
        var hasDigit = false;
        foreach (var ch in pwd)
        {
            if (char.IsLetter(ch)) hasLetter = true;
            else if (char.IsDigit(ch)) hasDigit = true;
        }
        if (!hasLetter || !hasDigit) { error = "密码需同时包含字母与数字。"; return false; }
        return true;
    }
}

public enum LoginStatus
{
    Ok,
    BadCredentials,
    /// <summary>连续失败达阈值，账号临时锁定。</summary>
    Locked,
    /// <summary>账号被管理员停用。</summary>
    Disabled,
    /// <summary>策略要求先改密（首登 admin/admin123 等）。</summary>
    MustChangePassword
}

public sealed class LoginResult
{
    public LoginStatus Status { get; init; }
    public UserAccount? User { get; init; }
    /// <summary>锁定剩余秒数（Status=Locked 时有意义）。</summary>
    public int LockRemainingSec { get; init; }
    public string Message { get; init; } = "";

    public bool IsOk => Status is LoginStatus.Ok or LoginStatus.MustChangePassword;
    public bool MustChange => Status == LoginStatus.MustChangePassword;
}

// 首个用户自动成为 admin
public sealed class UserService
{
    /// <summary>连续失败多少次触发锁定。</summary>
    public const int MaxFailures = 5;
    /// <summary>锁定时长。</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    private static readonly string[] AllowedRoles = { "Operator", "Engineer", "Admin" };

    private readonly StationDb _db;

    public UserService(StationDb db)
    {
        _db = db;
        if (_db.UserCount() == 0)
        {
            var salt = PasswordHasher.NewSalt();
            _db.UpsertUser(new UserRow
            {
                Username = "admin",
                PasswordHash = PasswordHasher.Hash("admin123", salt),
                Salt = salt,
                Role = "Admin",
                CreatedAt = DateTime.Now.ToString("O"),
                // 出厂密码必须首登即改，否则等于没有账号体系
                MustChangePassword = true,
                Enabled = true
            });
            _db.AddAudit("system", "BOOTSTRAP", "初始管理员 admin 创建（admin123，首登强制改密）",
                entity: "user/admin", oldValue: null, newValue: "Admin", reason: "首次安装");
        }
    }

    public LoginResult Login(string username, string password)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0 || password == null)
            return Fail("用户名或密码错误。");

        var u = _db.GetUser(username);
        if (u == null)
        {
            _db.AddAudit(username.Length == 0 ? "-" : username, "LOGIN_FAIL", "用户不存在");
            return Fail("用户名或密码错误。");
        }

        if (!u.Enabled)
        {
            _db.AddAudit(username, "LOGIN_DISABLED", "");
            return new LoginResult { Status = LoginStatus.Disabled, Message = "账号已停用，请联系管理员。" };
        }

        if (TryGetLockRemaining(u, out var remaining))
        {
            _db.AddAudit(username, "LOGIN_LOCKED", $"剩余 { (int)remaining.TotalSeconds }s");
            return new LoginResult
            {
                Status = LoginStatus.Locked,
                LockRemainingSec = (int)Math.Ceiling(remaining.TotalSeconds),
                Message = $"连续登录失败，账号已锁定，请 {(int)Math.Ceiling(remaining.TotalSeconds)} 秒后重试。"
            };
        }

        if (!VerifyPassword(u, password))
        {
            var failures = _db.RegisterLoginFailure(username, MaxFailures, LockDuration);
            _db.AddAudit(username, "LOGIN_FAIL", $"连续失败 {failures} 次");
            if (failures >= MaxFailures)
            {
                return new LoginResult
                {
                    Status = LoginStatus.Locked,
                    LockRemainingSec = (int)LockDuration.TotalSeconds,
                    Message = $"连续失败 {failures} 次，账号已锁定 {(int)LockDuration.TotalMinutes} 分钟。"
                };
            }
            return Fail($"用户名或密码错误（连续失败 {failures}/{MaxFailures}）。");
        }

        _db.ResetLoginFailure(username);
        _db.TouchLastLogin(username, DateTime.Now);
        _db.AddAudit(username, "LOGIN_OK", u.Role);

        var account = new UserAccount { Name = u.Username, Role = u.Role };
        if (u.MustChangePassword)
        {
            return new LoginResult
            {
                Status = LoginStatus.MustChangePassword,
                User = account,
                Message = "首次登录或密码已被重置，请先修改密码。"
            };
        }
        return new LoginResult { Status = LoginStatus.Ok, User = account, Message = "" };
    }

    /// <summary>仅校验凭据（锁屏解锁用），同样计入失败次数。</summary>
    public bool Validate(string username, string password, UserAccount? expectUser, out string error)
    {
        error = "";
        if (expectUser != null && !string.Equals(username, expectUser.Name, StringComparison.Ordinal))
        {
            error = "用户名不匹配当前会话。";
            return false;
        }
        var u = _db.GetUser(username);
        if (u == null || !u.Enabled)
        {
            error = "账号不可用。";
            return false;
        }
        if (TryGetLockRemaining(u, out var remaining))
        {
            error = $"账号已锁定，请 {(int)Math.Ceiling(remaining.TotalSeconds)} 秒后重试。";
            return false;
        }
        if (!VerifyPassword(u, password))
        {
            var failures = _db.RegisterLoginFailure(username, MaxFailures, LockDuration);
            _db.AddAudit(username, "UNLOCK_FAIL", $"连续失败 {failures} 次");
            error = failures >= MaxFailures
                ? $"连续失败 {failures} 次，账号已锁定 {(int)LockDuration.TotalMinutes} 分钟。"
                : $"密码错误（连续失败 {failures}/{MaxFailures}）。";
            return false;
        }
        _db.ResetLoginFailure(username);
        _db.AddAudit(username, "UNLOCK_OK", "");
        return true;
    }

    /// <summary>用户自助改密（需旧密码）。成功后会清除强制改密标记。</summary>
    public bool ChangeOwnPassword(UserAccount account, string oldPassword, string newPassword, out string error)
    {
        error = "";
        if (account == null) { error = "未登录。"; return false; }
        var u = _db.GetUser(account.Name);
        if (u == null) { error = "用户不存在。"; return false; }
        if (!VerifyPassword(u, oldPassword)) { error = "当前密码错误。"; return false; }
        var pwd = newPassword ?? "";
        if (!PasswordPolicy.Validate(pwd, out error)) return false;
        if (PasswordHasher.FixedTimeEquals(oldPassword ?? "", pwd))
        {
            error = "新密码不能与当前密码相同。";
            return false;
        }

        var salt = PasswordHasher.NewSalt();
        var ok = _db.UpdateUserPassword(account.Name, PasswordHasher.Hash(pwd, salt), salt);
        if (!ok) { error = "改密失败（用户不存在）。"; return false; }
        _db.SetMustChangePassword(account.Name, false);
        _db.ResetLoginFailure(account.Name);
        _db.AddAudit(account.Name, "PASSWORD_CHANGE", "自助修改密码",
            entity: "user/" + account.Name, oldValue: "******", newValue: "******", reason: "用户自助改密");
        return true;
    }

    public void ClearMustChange(string username)
    {
        _db.SetMustChangePassword(username, false);
        _db.AddAudit(username, "PASSWORD_CHANGE_DONE", "强制改密完成",
            entity: "user/" + username, oldValue: "must_change=1", newValue: "must_change=0", reason: "策略要求");
    }

    public IReadOnlyList<UserRow> Users() => _db.ListUsers();

    public bool AddUser(string username, string password, string role, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(username) || username.Contains(' '))
        {
            error = "用户名不能为空或含空格。";
            return false;
        }
        if (!AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            error = "角色只能是 Operator / Engineer / Admin。";
            return false;
        }
        role = AllowedRoles.First(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        if (!PasswordPolicy.Validate(password, out error)) return false;
        if (_db.GetUser(username) != null)
        {
            error = "用户名已存在。";
            return false;
        }
        var salt = PasswordHasher.NewSalt();
        _db.UpsertUser(new UserRow
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password, salt),
            Salt = salt,
            Role = role,
            CreatedAt = DateTime.Now.ToString("O"),
            // 管理员设的初始密码同样要求首登修改
            MustChangePassword = true,
            Enabled = true
        });
        _db.AddAudit(username, "USER_ADD", role, entity: "user/" + username,
            oldValue: null, newValue: role, reason: "管理员创建账号");
        return true;
    }

    public bool ResetPassword(string username, string password, out string error)
    {
        error = "";
        if (!PasswordPolicy.Validate(password, out error)) return false;
        var salt = PasswordHasher.NewSalt();
        var ok = _db.UpdateUserPassword(username, PasswordHasher.Hash(password, salt), salt);
        if (ok)
        {
            _db.SetMustChangePassword(username, true);
            _db.ResetLoginFailure(username);
            _db.AddAudit(username, "USER_RESET_PWD", "管理员重置密码（首登强制改密）",
                entity: "user/" + username, oldValue: "******", newValue: "******", reason: "管理员重置");
        }
        return ok;
    }

    public bool SetRole(string username, string role)
    {
        if (!AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) return false;
        role = AllowedRoles.First(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        var old = _db.GetUser(username)?.Role;
        var ok = _db.UpdateUserRole(username, role);
        if (ok)
            _db.AddAudit(username, "USER_SET_ROLE", role, entity: "user/" + username,
                oldValue: old, newValue: role, reason: "权限调整");
        return ok;
    }

    public bool SetEnabled(string username, bool enabled, string operatorName)
    {
        _db.SetUserEnabled(username, enabled);
        if (!enabled) _db.ResetLoginFailure(username);
        _db.AddAudit(operatorName, enabled ? "USER_ENABLE" : "USER_DISABLE", "",
            entity: "user/" + username, oldValue: enabled ? "0" : "1", newValue: enabled ? "1" : "0",
            reason: "账号状态调整");
        return true;
    }

    public bool RemoveUser(string username, out string error)
    {
        error = "";
        var u = _db.GetUser(username);
        if (u == null)
        {
            error = "用户不存在。";
            return false;
        }
        if (string.Equals(u.Role, "Admin", StringComparison.OrdinalIgnoreCase) &&
            _db.ListUsers().Count(x => string.Equals(x.Role, "Admin", StringComparison.OrdinalIgnoreCase)) <= 1)
        {
            error = "不能删除最后一个管理员。";
            return false;
        }
        var ok = _db.DeleteUser(username);
        if (ok)
            _db.AddAudit(username, "USER_REMOVE", "", entity: "user/" + username,
                oldValue: u.Role, newValue: null, reason: "管理员删除账号");
        return ok;
    }

    private static LoginResult Fail(string message) =>
        new() { Status = LoginStatus.BadCredentials, Message = message };

    private static bool VerifyPassword(UserRow u, string? password)
    {
        if (password == null) return false;
        // 超长输入直接判失败：PBKDF2 对输入长度无上限，可被当作 CPU 放大器
        if (password.Length > PasswordPolicy.MaxLength) return false;
        return PasswordHasher.FixedTimeEquals(PasswordHasher.Hash(password, u.Salt), u.PasswordHash);
    }

    private static bool TryGetLockRemaining(UserRow u, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (string.IsNullOrEmpty(u.LockedUntil)) return false;
        if (!DateTime.TryParse(u.LockedUntil, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            return false;
        remaining = until - DateTime.Now;
        return remaining > TimeSpan.Zero;
    }
}
