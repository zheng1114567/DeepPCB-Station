using System.Windows;
using System.Windows.Input;
using DeepPCBStation.Services;
using DeepPCBStation.Views;

namespace DeepPCBStation;

public partial class LoginWindow : Window
{
    public UserAccount? CurrentUser { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        Services.WindowPlacement.Restore(this, "login");
        Closed += (_, _) => Services.WindowPlacement.Save(this, "login");
        UserBox.Focus();
    }

    private void Login_Click(object sender, RoutedEventArgs e) => TryLogin();

    private void UserBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PwdBox.Focus();
            return;
        }
    }

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryLogin();
    }

    private void TryLogin()
    {
        var station = App.Station;
        if (station == null)
        {
            ErrorText.Text = "工位服务未初始化。";
            return;
        }

        try
        {
            var svc = new UserService(station.Db);
            var result = svc.Login(UserBox.Text.Trim(), PwdBox.Password);
            switch (result.Status)
            {
                case LoginStatus.Ok:
                    CurrentUser = result.User;
                    DialogResult = true;
                    return;

                case LoginStatus.MustChangePassword:
                    // 首登 / 管理员重置后：先强改密，改不了就不放行
                    if (result.User == null) { ErrorText.Text = "登录状态异常，请联系管理员。"; break; }
                    var dlg = new ChangePasswordWindow(result.User, force: true) { Owner = this };
                    if (dlg.ShowDialog() == true)
                    {
                        CurrentUser = result.User;
                        DialogResult = true;
                        return;
                    }
                    ErrorText.Text = "必须完成改密才能进入系统。";
                    break;

                case LoginStatus.Locked:
                case LoginStatus.Disabled:
                case LoginStatus.BadCredentials:
                default:
                    ErrorText.Text = result.Message;
                    break;
            }
        }
        catch (Exception ex)
        {
            // 不回显内部异常细节（路径/DB 错误），完整信息进调试输出
            System.Diagnostics.Debug.WriteLine("[Login] " + ex);
            ErrorText.Text = "登录失败：服务暂不可用，请联系管理员。";
        }
        PwdBox.SelectAll();
        PwdBox.Focus();
    }
}
