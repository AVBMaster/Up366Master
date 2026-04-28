using System.Collections.ObjectModel;
using Up366Net;

namespace Up366Master;

[QueryProperty(nameof(CourseId), "courseId")]
[QueryProperty(nameof(CourseName), "courseName")]
public partial class HomeworkDetailPage : ContentPage
{
    private long _courseId;
    private string _courseName = string.Empty;
    private ObservableCollection<JobDetailItem> _jobs = new();

    public long CourseId
    {
        get => _courseId;
        set { _courseId = value; _ = LoadJobs(); }
    }

    public string CourseName
    {
        get => _courseName;
        set { _courseName = Uri.UnescapeDataString(value); HeaderClassLabel.Text = _courseName; }
    }

    public HomeworkDetailPage()
    {
        InitializeComponent();
        JobsCollection.ItemsSource = _jobs;
    }

    private async Task LoadJobs()
    {
        if (_courseId == 0) return;

        try
        {
            var jobs = await AppShell.Client.GetJobListAsync(_courseId, onlyValid: false);
            _jobs.Clear();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var j in jobs)
            {
                var isExpired = j.EndTime < now;
                var isFuture = j.AriseTime > now;
                var statusColor = isExpired ? Colors.Gray : (isFuture ? Colors.Orange : Colors.Green);
                var statusText = isExpired ? "已截止" : (isFuture ? "未开始" : "进行中");

                _jobs.Add(new JobDetailItem
                {
                    JobId = j.JobId,
                    JobName = j.JobName,
                    CourseId = j.CourseId,
                    BookId = j.ExtParam?.BookId,
                    ContentId = j.ContentId,
                    ChapterId = j.ExtParam?.ChapterId,
                    PageId = j.ExtParam?.PageId,
                    StatusColor = statusColor,
                    StatusText = statusText,
                    TimeRange = $"{FormatTime(j.AriseTime)} 至 {FormatTime(j.EndTime)}",
                    BookIdShort = $"B:{Truncate(j.ExtParam?.BookId, 6)}",
                    ContentIdShort = $"T:{Truncate(j.ContentId, 6)}",
                    ShowProgress = false,
                    ProgressValue = 0
                });
            }

            HeaderCountLabel.Text = $"共 {jobs.Count} 个作业";
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private async void OnJobTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is JobDetailItem item)
        {
            var action = await DisplayActionSheet(item.JobName, "取消", null,
                "查看资源链", "获取下载链接", "下载文件", "解析题目", "⚡ 一键完成");

            switch (action)
            {
                case "查看资源链": await ViewResourceChain(item); break;
                case "获取下载链接": await GetDownloadLink(item); break;
                case "下载文件": await DownloadFile(item); break;
                case "解析题目": await ParseQuestions(item); break;
                case "⚡ 一键完成": await AutoComplete(item); break;
            }
        }
    }

    private async Task AutoComplete(JobDetailItem item)
    {
        if (!ValidateIds(item)) return;

        var param = new Dictionary<string, object>
        {
            { "bookId", item.BookId },
            { "taskId", item.ContentId },
            { "courseId", item.CourseId.ToString() },
            { "chapterId", item.ChapterId ?? "" },
            { "pageId", item.PageId ?? "" },
            { "jobName", Uri.EscapeDataString(item.JobName) }
        };

        await Shell.Current.GoToAsync(nameof(AutoCompletePage), param);
    }

    private async void OnDownloadSwipe(object sender, EventArgs e)
    {
        if (sender is SwipeItem swipe && swipe.BindingContext is JobDetailItem item)
            await DownloadFile(item);
    }

    private async void OnParseSwipe(object sender, EventArgs e)
    {
        if (sender is SwipeItem swipe && swipe.BindingContext is JobDetailItem item)
            await ParseQuestions(item);
    }

    private async Task ViewResourceChain(JobDetailItem item)
    {
        if (!ValidateIds(item)) return;
        try
        {
            var chain = await AppShell.Client.GetTaskLinkedAsync(item.BookId, item.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);
            var msg = $"教材: {chain.Book?.Name}\n章节: {chain.Chapters?.Count ?? 0}\nPC FileId: {pcFileId ?? "无"}";
            await DisplayAlert("资源链", msg, "确定");
        }
        catch (Exception ex) { await DisplayAlert("错误", ex.Message, "确定"); }
    }

    private async Task GetDownloadLink(JobDetailItem item)
    {
        if (!ValidateIds(item)) return;
        try
        {
            var chain = await AppShell.Client.GetTaskLinkedAsync(item.BookId, item.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);
            if (string.IsNullOrEmpty(pcFileId)) { await DisplayAlert("错误", "未找到PC文件", "确定"); return; }

            var url = await AppShell.Client.GetDownloadUrlAsync(pcFileId);
            await Clipboard.SetTextAsync(url);
            await DisplayAlert("成功", "下载链接已复制到剪贴板", "确定");
        }
        catch (Exception ex) { await DisplayAlert("错误", ex.Message, "确定"); }
    }

    private async Task DownloadFile(JobDetailItem item)
    {
        if (!ValidateIds(item)) return;
        item.ShowProgress = true;
        item.ProgressValue = 0.3;

        try
        {
            var chain = await AppShell.Client.GetTaskLinkedAsync(item.BookId, item.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);
            if (string.IsNullOrEmpty(pcFileId)) { await DisplayAlert("错误", "未找到PC文件", "确定"); return; }

            item.ProgressValue = 0.6;
            var entries = await AppShell.Client.DownloadAndExtractAsync(pcFileId);
            item.ProgressValue = 0.9;

            var safeName = string.Join("_", item.JobName.Split(Path.GetInvalidFileNameChars()));
            if (safeName.Length > 30) safeName = safeName[..30];
            var folder = Path.Combine(FileSystem.AppDataDirectory, "downloads", $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(folder);

            foreach (var entry in entries)
            {
                if (entry.FileName.StartsWith("1/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FileName.StartsWith("3/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FileName.StartsWith("4/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FileName.StartsWith("5/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = entry.FileName;
                if (relativePath.StartsWith("2/", StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath[2..];
                }

                var path = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(path, entry.Content);
            }

            item.ProgressValue = 1.0;
            await DisplayAlert("完成", $"已保存到:\n{folder}", "确定");
        }
        catch (Exception ex) { await DisplayAlert("错误", ex.Message, "确定"); }
        finally { item.ShowProgress = false; }
    }

    private async Task ParseQuestions(JobDetailItem item)
    {
        var downloadsDir = Path.Combine(FileSystem.AppDataDirectory, "downloads");
        if (!Directory.Exists(downloadsDir)) { await DisplayAlert("提示", "请先下载作业文件", "确定"); return; }

        var dirs = Directory.GetDirectories(downloadsDir)
            .Where(d => d.Contains(item.JobName[..Math.Min(item.JobName.Length, 10)]))
            .OrderByDescending(d => new DirectoryInfo(d).LastWriteTime)
            .ToList();

        if (dirs.Count == 0) { await DisplayAlert("提示", "未找到下载文件，请先下载", "确定"); return; }

        string? qPath = null;
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, "questions");
            if (Directory.Exists(candidate)) { qPath = candidate; break; }
        }

        if (qPath == null) { await DisplayAlert("错误", "未找到题目数据目录", "确定"); return; }

        await Shell.Current.GoToAsync($"{nameof(ParserPage)}?path={Uri.EscapeDataString(qPath)}");
    }

    private bool ValidateIds(JobDetailItem item)
    {
        if (string.IsNullOrEmpty(item.BookId) || string.IsNullOrEmpty(item.ContentId))
        { DisplayAlert("错误", "缺少BookId或ContentId", "确定"); return false; }
        return true;
    }

    private void OnRefreshing(object sender, EventArgs e)
    {
        _ = LoadJobs();
        RefreshView.IsRefreshing = false;
    }

    private static string FormatTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd HH:mm");

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "N/A" : (value.Length <= max ? value : value[..(max - 3)] + "...");
}

public class JobDetailItem : JobDisplayItem
{
    public string BookIdShort { get; set; } = string.Empty;
    public string ContentIdShort { get; set; } = string.Empty;
    public bool ShowProgress { get; set; }
    public double ProgressValue { get; set; }
}