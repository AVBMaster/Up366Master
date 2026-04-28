namespace Up366Master;

public partial class ToolsPage : ContentPage
{
    public ToolsPage()
    {
        InitializeComponent();
    }

    private async void OnGetDownloadUrl(object sender, EventArgs e)
    {
        var pcFileId = PcFileIdEntry.Text?.Trim();
        if (string.IsNullOrEmpty(pcFileId))
        {
            await DisplayAlert("提示", "请输入PC FileId", "确定");
            return;
        }

        try
        {
            var url = await AppShell.Client.GetDownloadUrlAsync(pcFileId);
            UrlResultLabel.Text = url;
            UrlResultLabel.IsVisible = true;

            // 复制到剪贴板
            await Clipboard.SetTextAsync(url);
            await DisplayAlert("成功", "链接已复制到剪贴板", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private async void OnEndToEndTapped(object sender, TappedEventArgs e)
    {
        var confirm = await DisplayAlert("端到端向导",
            "此功能将自动完成：选择班级→选择作业→获取资源→下载文件\n\n是否继续？", "开始", "取消");

        if (confirm)
        {
            await Shell.Current.GoToAsync(nameof(EndToEndWizardPage));
        }
    }

    private async void OnParserTapped(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(ParserPage));
    }

    private async void OnSaveSession(object sender, EventArgs e)
    {
        try
        {
            await SessionManager.SaveSessionAsync(AppShell.Client);
            await DisplayAlert("成功", "会话已保存", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private async void OnClearSession(object sender, EventArgs e)
    {
        var confirm = await DisplayAlert("确认", "确定要清除当前会话吗？", "确定", "取消");
        if (confirm)
        {
            SessionManager.ClearSessionFile();
            await DisplayAlert("完成", "会话已清除", "确定");
        }
    }
}