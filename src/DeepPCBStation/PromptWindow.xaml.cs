using System.Windows;
using System.Windows.Input;

namespace DeepPCBStation;

public partial class PromptWindow : Window
{
    public string PromptText { get; }
    public string? InputValue { get; private set; }

    public PromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText = prompt;
        DataContext = this;
        InputBox.Text = initial;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputValue = InputBox.Text;
        DialogResult = true;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            InputValue = InputBox.Text;
            DialogResult = true;
        }
    }
}
