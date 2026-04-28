using Up366Net;

namespace Up366Master;

public partial class AppShell : Shell
{
    public static Up366Client Client { get; private set; } = new(includeOptionalLoggingHeaders: true);

    // 缓存 LoginPage 的 ShellContent 引用
    private ShellContent _loginShellContent;

    public AppShell()
    {
        InitializeComponent();

        // 找到 LoginPage 的 ShellContent
        _loginShellContent = this.Items
            .OfType<ShellContent>()
            .FirstOrDefault(sc => sc.Route == "LoginPage");

        // 注册所有路由
        RegisterRoutes();

        // 隐藏 TabBar 直到登录成功
        MainTabBar.IsVisible = false;

        // 延迟检查登录状态
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(200);
            await CheckAutoLoginAsync();
        });
    }

    private void RegisterRoutes()
    {
        // 子页面路由
        Routing.RegisterRoute(nameof(ClassDetailPage), typeof(ClassDetailPage));
        Routing.RegisterRoute(nameof(HomeworkDetailPage), typeof(HomeworkDetailPage));
        Routing.RegisterRoute(nameof(EndToEndWizardPage), typeof(EndToEndWizardPage));
        Routing.RegisterRoute(nameof(ParserPage), typeof(ParserPage));
        Routing.RegisterRoute(nameof(SessionManagePage), typeof(SessionManagePage));
        Routing.RegisterRoute(nameof(AutoCompletePage), typeof(AutoCompletePage));
    }

    private async Task CheckAutoLoginAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[AppShell] 检查自动登录...");

            // 先尝试加载本地会话
            var loaded = await SessionManager.TryLoadSessionAsync(Client);

            if (loaded)
            {
                System.Diagnostics.Debug.WriteLine("[AppShell] 本地会话已加载，验证有效性...");

                // 验证会话是否仍然有效（2小时过期检查）
                var isValid = await SessionManager.ValidateSessionAsync(Client);

                if (isValid)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppShell] 会话有效，自动登录成功: {Client.Session?.RealName}");
                    ShowMainUI();
                    await GoToAsync("//DashboardPage");
                    return;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AppShell] 会话已过期，清除本地会话");
                    SessionManager.ClearSessionFile();
                    // 重置客户端
                    Client.Dispose();
                    Client = new Up366Client(includeOptionalLoggingHeaders: true);
                }
            }

            // 无有效会话，显示登录页
            System.Diagnostics.Debug.WriteLine("[AppShell] 无有效会话，显示登录页");
            ShowLoginPage();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppShell] 自动登录异常: {ex.Message}");
            ShowLoginPage();
        }
    }

    /// <summary>
    /// 显示登录页（安全方式）
    /// </summary>
    private void ShowLoginPage()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                MainTabBar.IsVisible = false;

                // 方式1：通过 GoToAsync 导航到 LoginPage
                if (_loginShellContent != null)
                {
                    // 直接设置 CurrentItem 为 LoginPage 所在的 ShellItem
                    // 但 LoginPage 是 ShellContent，需要包装在 ShellItem 中
                    CurrentItem = _loginShellContent.Parent as ShellItem ?? _loginShellContent;
                }
                else
                {
                    // 方式2：直接导航
                    _ = GoToAsync("//LoginPage");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppShell] 显示登录页失败: {ex.Message}");
            }
        });
    }

    public static void ShowMainUI()
    {
        if (Current is AppShell shell)
        {
            shell.MainTabBar.IsVisible = true;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    shell.CurrentItem = shell.MainTabBar;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppShell] 显示主UI失败: {ex.Message}");
                }
            });
        }
    }

    public static void HideMainUI()
    {
        if (Current is AppShell shell)
        {
            shell.MainTabBar.IsVisible = false;
        }
    }

    public static async Task LogoutAsync()
    {
        Client?.Dispose();
        Client = new Up366Client(includeOptionalLoggingHeaders: true);
        SessionManager.ClearSessionFile();

        if (Current is AppShell shell)
        {
            shell.MainTabBar.IsVisible = false;
            await shell.GoToAsync("//LoginPage");
        }
    }
}