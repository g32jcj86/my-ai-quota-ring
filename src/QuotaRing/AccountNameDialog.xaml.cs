using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaRing.Core;
using Microsoft.Win32;

namespace QuotaRing;

internal partial class AccountNameDialog : Window
{
    private readonly IEnumerable<string> _existing;

    public AccountNameDialog(IEnumerable<string> existing, string action, string? email, string provider = "Claude")
    {
        InitializeComponent();
        Logo.Source = BitmapDecoder.Create(new Uri("pack://application:,,,/QuotaRing;component/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[3];
        _existing = existing;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        if (key?.GetValue("AppsUseLightTheme") is int value && value == 1)
        {
            Resources["Card"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            Resources["Edge"] = new SolidColorBrush(Color.FromRgb(226, 227, 232));
            Resources["Primary"] = new SolidColorBrush(Color.FromRgb(31, 32, 40));
            Resources["Secondary"] = new SolidColorBrush(Color.FromRgb(107, 111, 122));
            Resources["InputBackground"] = new SolidColorBrush(Color.FromRgb(246, 247, 249));
        }
        Title = Heading.Text = action == "新增" ? $"新增 {provider} 帳號" : provider == "Codex" ? "命名目前 Codex 帳號" : "命名目前帳號";
        Description.Text = action == "新增"
            ? $"輸入新帳號名稱，接著會關閉並重開 {provider} 桌面版讓你登入。"
            : provider == "Codex" ? "替目前登入的 Codex 帳號命名，之後可一鍵切換。" : "替目前登入的帳號命名，之後可一鍵切換。";
        Email.Text = action != "新增" && !string.IsNullOrEmpty(email) ? "目前登入：" + email : "";
        Email.Visibility = Email.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => { Activate(); Input.Focus(); };
    }

    public string AccountName => Input.Text.Trim();

    private void DragTitle(object sender, MouseButtonEventArgs e) => DragMove();

    private void Confirm(object sender, RoutedEventArgs e)
    {
        Error.Text = ClaudeAccountStore.ValidateName(Input.Text, _existing);
        Error.Visibility = string.IsNullOrEmpty(Error.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (Error.Visibility == Visibility.Collapsed) DialogResult = true;
    }
}