using System.Windows;
using DeepPCBStation.Services;

namespace DeepPCBStation.Views;

/// <summary>改密窗口。force=true 时不可取消（首登/管理员重置后的强制改密）。</summary>
public partial class ChangePasswordWindow : Window
{
    private readonly UserAccount _account;
    private readonly bool _force;

    public ChangePasswordWindow(UserAccount account, bool force)
    {
        InitializeComponent();
        _account = account;
        _force = force;
        UserBox.Text = account.Name + " · " + account.RoleText;
        SubtitleText.Text = force
            ? "出于合规要求，首次登录或密码被重置后必须修改密码。"
            : "修改后立即生效，请牢记新密码。";
        if (force)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            // 强制模式下关闭窗口等同"放弃登录"
            WindowStyle = WindowStyle.ToolWindow;
        }
        Loaded += (_, _) => OldBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_force) { DialogResult = false; return; }
        DialogResult = false;
    }

    private void Submit_Click(object sender, RoutedEventArgs e) => TrySubmit();

    private void TrySubmit()
    {
        var station = App.Station;
        if (station == null) { ErrorText.Text = "工位服务未初始化。"; return; }

        var oldPwd = OldBox.Password;
        var newPwd = NewBox.Password;
        var confirm = ConfirmBox.Password;

        if (!string.Equals(newPwd, confirm, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的新密码不一致。";
            ConfirmBox.SelectAll();
            ConfirmBox.Focus();
            return;
        }

        try
        {
            var svc = new UserService(station.Db);
            if (svc.ChangeOwnPassword(_account, oldPwd, newPwd, out var error))
            {
                DialogResult = true;
                return;
            }
            ErrorText.Text = error;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ChangePassword] " + ex);
            ErrorText.Text = "改密失败：服务暂不可用。";
        }
        NewBox.Clear();
        ConfirmBox.Clear();
        OldBox.Focus();
    }
}
