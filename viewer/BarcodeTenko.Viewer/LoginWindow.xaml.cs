using System.Windows;
using BarcodeTenko.Viewer.Services;

namespace BarcodeTenko.Viewer;

public partial class LoginWindow : Window
{
    private readonly ViewerApiClient _api;
    private bool _busy;

    public LoginWindow(ViewerApiClient api, string? initialMessage = null)
    {
        InitializeComponent();
        _api = api;
        ErrorText.Text = initialMessage ?? "";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void PasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        ErrorText.Text = "";
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ErrorText.Text = "パスワードを入力してください。";
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _api.LoginAsync(PasswordBox.Password);
            if (result.Outcome == LoginOutcome.Success)
            {
                DialogResult = true;
                return;
            }

            ErrorText.Text = result.Message;
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        LoginButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ErrorText.Text = busy ? "ログイン中..." : ErrorText.Text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        DialogResult = false;
    }
}
