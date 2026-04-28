using System.Collections.ObjectModel;
using System.ComponentModel;
using Up366Net;

namespace Up366Master;

public partial class HomeworkPage : ContentPage
{
    private List<ClassInfo> _classes = new();
    private ObservableCollection<JobDisplayItem> _jobs = new();
    private long _selectedCourseId;

    public HomeworkPage()
    {
        InitializeComponent();
        JobsCollection.ItemsSource = _jobs;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadClasses();
    }

    private async Task LoadClasses()
    {
        try
        {
            _classes = await AppShell.Client.GetClassListAsync();
            ClassPicker.ItemsSource = _classes.Select(c => c.CourseName).ToList();

            if (_classes.Count > 0 && ClassPicker.SelectedIndex == -1)
            {
                ClassPicker.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"加载班级失败: {ex.Message}", "确定");
        }
    }

    private async void OnClassSelected(object sender, EventArgs e)
    {
        if (ClassPicker.SelectedIndex == -1) return;

        _selectedCourseId = _classes[ClassPicker.SelectedIndex].CourseId;
        await LoadJobs();
    }

    private async Task LoadJobs(bool onlyValid = true)
    {
        try
        {
            var jobs = await AppShell.Client.GetJobListAsync(_selectedCourseId, onlyValid);
            _jobs.Clear();

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var j in jobs)
            {
                var isExpired = j.EndTime < now;
                var statusColor = isExpired ? Colors.Gray : (j.AriseTime > now ? Colors.Orange : Colors.Green);
                var statusText = isExpired ? "已截止" : (j.AriseTime > now ? "未开始" : "进行中");

                _jobs.Add(new JobDisplayItem
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
                    TimeRange = $"{FormatTime(j.AriseTime)} ~ {FormatTime(j.EndTime)}",
                    BookIdDisplay = $"Book: {Truncate(j.ExtParam?.BookId, 8)}",
                    ContentIdDisplay = $"Task: {Truncate(j.ContentId, 8)}"
                });
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"加载作业失败: {ex.Message}", "确定");
        }
    }

    private async void OnJobTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is JobDisplayItem item)
        {
            var action = await DisplayActionSheet(item.JobName, "取消", null, "查看资源链", "下载文件", "解析题目");

            switch (action)
            {
                case "查看资源链":
                    await ViewResourceChain(item);
                    break;
                case "下载文件":
                    await DownloadJob(item);
                    break;
                case "解析题目":
                    await ParseJob(item);
                    break;
            }
        }
    }

    private async Task ViewResourceChain(JobDisplayItem item)
    {
        if (string.IsNullOrEmpty(item.BookId) || string.IsNullOrEmpty(item.ContentId))
        {
            await DisplayAlert("错误", "缺少BookId或ContentId", "确定");
            return;
        }

        try
        {
            await DisplayAlert("加载中", "正在获取资源链...", "好的");
            var chain = await AppShell.Client.GetTaskLinkedAsync(item.BookId, item.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);

            var message = $"教材: {chain.Book?.Name}\n" +
                         $"章节数: {chain.Chapters?.Count ?? 0}\n" +
                         $"PC FileId: {pcFileId ?? "未找到"}";

            await DisplayAlert("资源链信息", message, "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private async Task DownloadJob(JobDisplayItem item)
    {
        if (string.IsNullOrEmpty(item.BookId) || string.IsNullOrEmpty(item.ContentId))
        {
            await DisplayAlert("错误", "缺少BookId或ContentId", "确定");
            return;
        }

        try
        {
            var chain = await AppShell.Client.GetTaskLinkedAsync(item.BookId, item.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);

            if (string.IsNullOrEmpty(pcFileId))
            {
                await DisplayAlert("错误", "未找到PC文件ID", "确定");
                return;
            }

            var progress = await DisplayAlert("确认下载", $"即将下载: {item.JobName}", "开始下载", "取消");
            if (!progress) return;

            var entries = await AppShell.Client.DownloadAndExtractAsync(pcFileId);

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

            await DisplayAlert("完成", $"已下载到:\n{folder}", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"下载失败: {ex.Message}", "确定");
        }
    }

    private async Task ParseJob(JobDisplayItem item)
    {
        await DisplayAlert("提示", "请先在工具页选择题目解析功能", "确定");
        await Shell.Current.GoToAsync("//ToolsPage");
    }

    private void OnRefreshing(object sender, EventArgs e)
    {
        if (_selectedCourseId != 0)
        {
            _ = LoadJobs();
        }
        RefreshView.IsRefreshing = false;
    }

    private static string FormatTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd HH:mm");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "N/A" : (value.Length <= max ? value : value[..(max - 3)] + "...");
}

public class JobDisplayItem : INotifyPropertyChanged
{
    public string JobId { get; set; }
    public string JobName { get; set; }
    public long CourseId { get; set; }
    public string BookId { get; set; }
    public string ContentId { get; set; }
    public string ChapterId { get; set; }
    public string PageId { get; set; }
    public Color StatusColor { get; set; }
    public string StatusText { get; set; }
    public string TimeRange { get; set; }
    public string BookIdDisplay { get; set; }
    public string ContentIdDisplay { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}