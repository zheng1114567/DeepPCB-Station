using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed class UserListItemVm
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string RoleText { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string LastLogin { get; set; } = "";
}

public sealed class AuditListItemVm
{
    public string TsText { get; set; } = "";
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed partial class UsersViewModel : ObservableObject, IRefreshable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;
    private readonly UserService _users;

    public UsersViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        _users = new UserService(station.Db);
        Users = new ObservableCollection<UserListItemVm>();
        Audit = new ObservableCollection<AuditListItemVm>();
        Roles = new List<string> { "Operator", "Engineer", "Admin" };
        ReloadCommand.Execute(null);
    }

    public ObservableCollection<UserListItemVm> Users { get; }
    public ObservableCollection<AuditListItemVm> Audit { get; }
    public IReadOnlyList<string> Roles { get; }

    [ObservableProperty] private UserListItemVm? _selectedUser;
    [ObservableProperty] private string _statusText = "";

    public bool HasSelection => SelectedUser != null;

    partial void OnSelectedUserChanged(UserListItemVm? value) => OnPropertyChanged(nameof(HasSelection));

    [RelayCommand]
    private void AddUser()
    {
        var dlg = new UserEditWindow("添加用户", null);
        if (dlg.ShowDialog() != true) return;
        if (_users.AddUser(dlg.Username.Trim(), dlg.Password, dlg.SelectedRole, out var err))
        {
            StatusText = $"已添加 {dlg.Username}";
            _ = Reload();
        }
        else
        {
            _dialogs.Warn(err, "用户权限");
        }
    }

    [RelayCommand]
    private void ResetPassword()
    {
        if (SelectedUser == null) return;
        var dlg = new UserEditWindow($"重置密码 · {SelectedUser.Username}", SelectedUser.Username);
        if (dlg.ShowDialog() != true) return;
        if (_users.ResetPassword(SelectedUser.Username, dlg.Password, out var err))
        {
            StatusText = $"已重置 {SelectedUser.Username} 的密码";
            _ = Reload();
        }
        else
        {
            _dialogs.Warn(err, "用户权限");
        }
    }

    [RelayCommand]
    private void SetRole()
    {
        if (SelectedUser == null) return;
        var dlg = new UserEditWindow($"修改角色 · {SelectedUser.Username}", SelectedUser.Username, showPassword: false);
        if (dlg.ShowDialog() != true) return;
        if (_users.SetRole(SelectedUser.Username, dlg.SelectedRole))
        {
            StatusText = $"已将 {SelectedUser.Username} 设为 {dlg.SelectedRole}";
            _ = Reload();
        }
    }

    [RelayCommand]
    private void RemoveUser()
    {
        if (SelectedUser == null) return;
        if (!_dialogs.Confirm($"确定删除用户 {SelectedUser.Username}？", "用户权限"))
            return;
        if (_users.RemoveUser(SelectedUser.Username, out var err))
        {
            StatusText = $"已删除 {SelectedUser.Username}";
            _ = Reload();
        }
        else
        {
            _dialogs.Warn(err, "用户权限");
        }
    }

    public void Refresh() => ReloadCommand.Execute(null);

    private static string FmtTs(string? s) =>
        DateTime.TryParse(s, out var t) ? t.ToString("yyyy-MM-dd HH:mm:ss") : s ?? "";

    [RelayCommand]
    private async Task Reload()
    {
        // 用户列表与审计日志的查库移到线程池，避免 UI 线程同步阻塞（P3）
        var users = await Task.Run(() => _users.Users()).ConfigureAwait(true);
        var audit = await Task.Run(() => _station.Db.ListAudit(200)).ConfigureAwait(true);
        Users.Clear();
        foreach (var u in users)
            Users.Add(new UserListItemVm
            {
                Username = u.Username,
                Role = u.Role,
                RoleText = u.Role switch { "Admin" => "管理员", "Engineer" => "工程师", _ => "操作员" },
                CreatedAt = FmtTs(u.CreatedAt),
                LastLogin = string.IsNullOrEmpty(u.LastLogin) ? "—" : FmtTs(u.LastLogin)
            });
        Audit.Clear();
        foreach (var a in audit)
            Audit.Add(new AuditListItemVm
            {
                TsText = DateTime.TryParse(a.Ts, out var t) ? t.ToString("MM-dd HH:mm:ss") : a.Ts,
                Username = a.Username ?? "—",
                Action = a.Action,
                Detail = a.Detail ?? ""
            });
    }
}
