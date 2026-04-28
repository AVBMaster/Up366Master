namespace Up366Master;

public partial class ProfilePage : ContentPage
{
    public ProfilePage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadUserInfo();
    }

    private void LoadUserInfo()
    {
        var session = AppShell.Client.Session;
        if (session == null) return;

        AvatarLabel.Text = !string.IsNullOrEmpty(session.RealName) ? session.RealName[0].ToString() : "U";
        NameLabel.Text = session.RealName ?? "未登录";
        UidLabel.Text = $"UID: {session.Uid}";
        StatusLabel.Text = AppShell.Client.IsAuthenticated ? "● 已登录" : "○ 未登录";
        StatusLabel.TextColor = AppShell.Client.IsAuthenticated ? Colors.Green : Colors.Gray;

        TokenLabel.Text = Truncate(session.Token, 30);
        TgtLabel.Text = Truncate(session.Tgt, 30);
        CookieLabel.Text = Truncate(session.Up366C, 30);
    }

    private async void OnSessionManage(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SessionManagePage));
    }

    private async void OnLogout(object sender, TappedEventArgs e)
    {
        var confirm = await DisplayAlert("确认", "确定要退出登录吗？", "确定", "取消");
        if (confirm)
        {
            await AppShell.LogoutAsync();
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "N/A" : (value.Length <= max ? value : value[..(max - 3)] + "...");
}