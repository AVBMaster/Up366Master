using System.Text.Json;

namespace Up366Master;

public partial class SessionManagePage : ContentPage
{
    public SessionManagePage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var session = AppShell.Client.Session;
        var isAuth = AppShell.Client.IsAuthenticated;

        // 当前状态
        AuthStatusLabel.Text = isAuth ? "● 已登录" : "○ 未登录";
        AuthStatusLabel.TextColor = isAuth ? Colors.Green : Colors.Gray;
        UserNameLabel.Text = session?.RealName ?? "(未登录)";
        UidLabel.Text = session?.Uid.ToString() ?? "-";
        TokenStatusLabel.Text = !string.IsNullOrEmpty(session?.Token) ? "✓ 已获取" : "✗ 未获取";
        TokenStatusLabel.TextColor = !string.IsNullOrEmpty(session?.Token) ? Colors.Green : Colors.Red;
        CookieStatusLabel.Text = !string.IsNullOrEmpty(session?.Up366C) ? "✓ 已获取" : "✗ 未获取";
        CookieStatusLabel.TextColor = !string.IsNullOrEmpty(session?.Up366C) ? Colors.Green : Colors.Red;

        // 本地文件
        var filePath = Path.Combine(FileSystem.AppDataDirectory, "session.json");
        var exists = File.Exists(filePath);
        FileStatusLabel.Text = exists ? "✓ 存在" : "✗ 不存在";
        FileStatusLabel.TextColor = exists ? Colors.Green : Colors.Gray;

        if (exists)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data != null)
                {
                    SavedUserLabel.Text = $"{data.RealName} (UID:{data.Uid})";
                    SavedTimeLabel.Text = data.SavedAt.ToString("yyyy-MM-dd HH:mm:ss");
                    var age = DateTime.Now - data.SavedAt;
                    ExpiredLabel.Text = age.TotalDays > 7 ? $"✗ 已过期 ({age.TotalDays:F1}天)" : $"✓ 有效 ({age.TotalHours:F1}小时前)";
                    ExpiredLabel.TextColor = age.TotalDays > 7 ? Colors.Red : Colors.Green;
                }
            }
            catch
            {
                SavedUserLabel.Text = "读取失败";
                SavedTimeLabel.Text = "-";
                ExpiredLabel.Text = "未知";
            }
        }
        else
        {
            SavedUserLabel.Text = "-";
            SavedTimeLabel.Text = "-";
            ExpiredLabel.Text = "-";
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var success = await SessionManager.SaveSessionAsync(AppShell.Client);
        await DisplayAlert(success ? "成功" : "失败", success ? "会话已保存" : "保存失败", "确定");
        RefreshStatus();
    }

    private async void OnLoadClicked(object sender, EventArgs e)
    {
        var success = await SessionManager.TryLoadSessionAsync(AppShell.Client);
        await DisplayAlert(success ? "成功" : "失败", success ? "会话已加载" : "加载失败或文件不存在", "确定");
        RefreshStatus();
    }

    private async void OnClearClicked(object sender, EventArgs e)
    {
        var confirm = await DisplayAlert("确认", "确定要清除会话并退出登录吗？", "确定", "取消");
        if (confirm)
        {
            await AppShell.LogoutAsync();
        }
    }
}