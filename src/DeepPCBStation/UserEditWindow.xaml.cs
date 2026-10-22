using System.Windows;
using System.Windows.Controls;

namespace DeepPCBStation;

public partial class UserEditWindow : Window
{
    public string Username => UserBox.Text.Trim();
    public string Password => PwdBox.Password;
    public string SelectedRole { get; private set; } = "Operator";

    public UserEditWindow(string title, string? username, bool showPassword = true)
    {
        InitializeComponent();
        Title = title;
        if (username != null)
        {
            UserBox.Text = username;
            UserBox.IsEnabled = false;
        }
        RoleBox.SelectedIndex = 0;
        if (!showPassword)
        {
            PwdLabel.Visibility = Visibility.Collapsed;
            PwdBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            PwdBox.Focus();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RoleBox.SelectedItem is ComboBoxItem item && item.Tag is string role)
            SelectedRole = role;
        DialogResult = true;
    }
}
