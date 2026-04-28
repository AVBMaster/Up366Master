using System.Text.RegularExpressions;
using Up366Net;

namespace Up366Master;

public partial class LoginPage : ContentPage
{
    private bool _isSendingCode = false;
    private int _countdown = 60;

    public LoginPage()
    {
        InitializeComponent();

        // 检查是否有本地会话，显示恢复按钮
        CheckLocalSession();
    }

    private void CheckLocalSession()
    {
        if (SessionManager.HasSessionFile())
        {
            var saveTime = SessionManager.GetSessionSaveTime();
            if (saveTime.HasValue)
            {
                var age = DateTime.Now - saveTime.Value;
                if (age.TotalDays <= 7)
                {
                    LoadSessionBtn.Text = $"恢复上次登录 ({saveTime.Value:MM-dd HH:mm})";
                    LoadSessionBtn.IsVisible = true;
                }
            }
        }
    }

    private async void OnLoadSessionClicked(object sender, EventArgs e)
    {
        LoadSessionBtn.IsEnabled = false;
        LoadSessionBtn.Text = "恢复中...";

        try
        {
            var loaded = await SessionManager.TryLoadSessionAsync(AppShell.Client);
            if (loaded && AppShell.Client.IsAuthenticated)
            {
                AppShell.ShowMainUI();
                await Shell.Current.GoToAsync("//DashboardPage");
            }
            else
            {
                await DisplayAlert("提示", "会话已过期，请重新登录", "确定");
                LoadSessionBtn.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"恢复失败: {ex.Message}", "确定");
            LoadSessionBtn.IsEnabled = true;
            LoadSessionBtn.Text = "恢复上次登录";
        }
    }

    private async void OnSendCodeClicked(object sender, EventArgs e)
    {
        var mobile = PhoneEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(mobile) || !Regex.IsMatch(mobile, @"^1[3-9]\d{9}$"))
        {
            await ShowStatus("请输入有效的手机号");
            return;
        }

        if (_isSendingCode) return;

        _isSendingCode = true;
        SendCodeBtn.IsEnabled = false;

        try
        {
            var success = await AppShell.Client.SendVerifyCodeAsync(mobile);
            if (success)
            {
                await ShowStatus("验证码已发送", Colors.Green);
                StartCountdown();
            }
            else
            {
                await ShowStatus("发送失败，请稍后重试");
                SendCodeBtn.IsEnabled = true;
                _isSendingCode = false;
            }
        }
        catch (Exception ex)
        {
            await ShowStatus($"发送失败: {ex.Message}");
            SendCodeBtn.IsEnabled = true;
            _isSendingCode = false;
        }
    }

    private void StartCountdown()
    {
        _countdown = 60;
        Device.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            _countdown--;
            if (_countdown <= 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SendCodeBtn.Text = "获取验证码";
                    SendCodeBtn.IsEnabled = true;
                    _isSendingCode = false;
                });
                return false;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                SendCodeBtn.Text = $"{_countdown}s";
            });
            return true;
        });
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        var mobile = PhoneEntry.Text?.Trim();
        var code = CodeEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(mobile) || string.IsNullOrWhiteSpace(code))
        {
            await ShowStatus("请填写手机号和验证码");
            return;
        }

        LoginBtn.IsEnabled = false;
        LoginBtn.Text = "登录中...";

        try
        {
            // Step 1: 验证验证码
            await AppShell.Client.VerifyCodeAsync(mobile, code);

            // Step 2: 自动登录获取Cookie
            await AppShell.Client.AutoLoginAsync();

            if (!AppShell.Client.IsAuthenticated)
            {
                await ShowStatus("登录失败，未获取到会话");
                LoginBtn.IsEnabled = true;
                LoginBtn.Text = "登录";
                return;
            }

            // 保存会话（关键！）
            var saved = await SessionManager.SaveSessionAsync(AppShell.Client);
            System.Diagnostics.Debug.WriteLine($"[LoginPage] 会话保存结果: {saved}");

            // 显示主界面
            AppShell.ShowMainUI();
            await Shell.Current.GoToAsync("//DashboardPage");

            // 清空输入
            PhoneEntry.Text = "";
            CodeEntry.Text = "";
            LoadSessionBtn.IsVisible = false;
        }
        catch (Up366Exception ex)
        {
            await ShowStatus($"登录失败: {ex.Message}");
            LoginBtn.IsEnabled = true;
            LoginBtn.Text = "登录";
        }
        catch (Exception ex)
        {
            await ShowStatus($"异常: {ex.Message}");
            LoginBtn.IsEnabled = true;
            LoginBtn.Text = "登录";
        }
    }

    private async Task ShowStatus(string message, Color color = null)
    {
        color ??= Colors.Red;
        StatusLabel.Text = message;
        StatusLabel.TextColor = color;
        StatusLabel.IsVisible = true;

        if (color == Colors.Red)
        {
            await Task.Delay(3000);
            StatusLabel.IsVisible = false;
        }
    }
}