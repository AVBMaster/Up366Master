using System.Collections.ObjectModel;
using System.Diagnostics;
using Up366Net;

namespace Up366Master;

public partial class EndToEndWizardPage : ContentPage
{
    private List<ClassInfo> _classes = new();
    private ObservableCollection<JobDisplayItem> _wizardJobs = new();
    private JobDisplayItem _selectedJob = null;
    private long _selectedCourseId;
    private string _downloadFolder = string.Empty;
    private int _currentStep = 1;

    public EndToEndWizardPage()
    {
        InitializeComponent();
        WizardJobsList.ItemsSource = _wizardJobs;
        LoadClasses();
    }

    private async void LoadClasses()
    {
        try
        {
            _classes = await AppShell.Client.GetClassListAsync();

            ClassPicker.ItemsSource = null;
            ClassPicker.ItemsSource = _classes.Select(c => $"{c.CourseName} (教师:{c.TeacherName})").ToList();

            Log($"已加载 {_classes.Count} 个班级");
        }
        catch (Exception ex)
        {
            Log($"加载失败: {ex.Message}");
            await DisplayAlert("错误", $"加载班级失败: {ex.Message}", "确定");
        }
    }

    private async void OnStep1Next(object sender, EventArgs e)
    {
        if (ClassPicker.SelectedIndex == -1)
        {
            await DisplayAlert("提示", "请选择班级", "确定");
            return;
        }

        _selectedCourseId = _classes[ClassPicker.SelectedIndex].CourseId;
        Log($"选中班级: {_classes[ClassPicker.SelectedIndex].CourseName}, CourseId={_selectedCourseId}");

        await LoadJobsForWizard();
        SetStep(2);
    }

    private async Task LoadJobsForWizard()
    {
        try
        {
            var jobs = await AppShell.Client.GetJobListAsync(_selectedCourseId, onlyValid: true);
            _wizardJobs.Clear();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var j in jobs)
            {
                _wizardJobs.Add(new JobDisplayItem
                {
                    JobId = j.JobId,
                    JobName = j.JobName,
                    CourseId = j.CourseId,
                    BookId = j.ExtParam?.BookId,
                    ContentId = j.ContentId,
                    ChapterId = j.ExtParam?.ChapterId,
                    PageId = j.ExtParam?.PageId,
                    TimeRange = $"{FormatTime(j.AriseTime)} ~ {FormatTime(j.EndTime)}"
                });
            }
            Log($"加载了 {jobs.Count} 个作业");
        }
        catch (Exception ex)
        {
            Log($"加载作业失败: {ex.Message}");
        }
    }

    private void OnJobTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is JobDisplayItem job)
        {
            foreach (var j in _wizardJobs)
            {
                if (j.IsSelected)
                    j.IsSelected = false;
            }

            job.IsSelected = true;
            _selectedJob = job;
            Step2NextBtn.IsEnabled = true;

            Log($"已选择作业: {job.JobName}");
        }
    }

    private void OnStep2Back(object sender, EventArgs e) => SetStep(1);

    private async void OnStep2Next(object sender, EventArgs e)
    {
        if (_selectedJob == null)
        {
            await DisplayAlert("提示", "请先选择一个作业", "确定");
            return;
        }

        try
        {
            Log("正在获取资源链...");
            var chain = await AppShell.Client.GetTaskLinkedAsync(_selectedJob.BookId, _selectedJob.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);

            ConfirmJobName.Text = _selectedJob.JobName;
            ConfirmBookId.Text = $"BookId: {_selectedJob.BookId}";
            ConfirmTaskId.Text = $"TaskId: {_selectedJob.ContentId}";
            ConfirmFileId.Text = $"PC FileId: {pcFileId ?? "未找到"}";

            if (string.IsNullOrEmpty(pcFileId))
            {
                await DisplayAlert("错误", "未提取到PC FileId", "确定");
                return;
            }

            SetStep(3);
        }
        catch (Exception ex)
        {
            Log($"获取资源链失败: {ex.Message}");
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private void OnStep3Back(object sender, EventArgs e) => SetStep(2);

    private async void OnStep3Download(object sender, EventArgs e)
    {
        Step3DownloadBtn.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        Log("开始下载...");

        try
        {
            var chain = await AppShell.Client.GetTaskLinkedAsync(_selectedJob.BookId, _selectedJob.ContentId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);

            DownloadProgress.Progress = 0.3;
            var entries = await AppShell.Client.DownloadAndExtractAsync(pcFileId);
            DownloadProgress.Progress = 0.8;

            var safeName = string.Join("_", _selectedJob.JobName.Split(Path.GetInvalidFileNameChars()));
            if (safeName.Length > 30) safeName = safeName[..30];
            _downloadFolder = Path.Combine(FileSystem.AppDataDirectory, "downloads", $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(_downloadFolder);

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

                var path = Path.Combine(_downloadFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(path, entry.Content);
            }

            DownloadProgress.Progress = 1.0;
            ResultLabel.Text = $"共提取 {entries.Count} 个文件，保存到 downloads 目录";
            Log($"下载完成: {_downloadFolder}");
            SetStep(4);
        }
        catch (Exception ex)
        {
            Log($"下载失败: {ex.Message}");
            await DisplayAlert("错误", ex.Message, "确定");
            Step3DownloadBtn.IsEnabled = true;
        }
    }

    private async void OnGoToParser(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_downloadFolder))
        {
            var qPath = Path.Combine(_downloadFolder, "questions");
            if (Directory.Exists(qPath))
                await Shell.Current.GoToAsync($"{nameof(ParserPage)}?path={Uri.EscapeDataString(qPath)}");
            else
                await DisplayAlert("提示", "未找到题目数据", "确定");
        }
    }

    private async void OnFinish(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//DashboardPage");
    }

    private void SetStep(int step)
    {
        _currentStep = step;
        Step1Panel.IsVisible = step == 1;
        Step2Panel.IsVisible = step == 2;
        Step3Panel.IsVisible = step == 3;
        Step4Panel.IsVisible = step == 4;

        Step1Indicator.BackgroundColor = step >= 1 ? Colors.Blue : Colors.Gray;
        Step2Indicator.BackgroundColor = step >= 2 ? Colors.Blue : Colors.Gray;
        Step3Indicator.BackgroundColor = step >= 3 ? Colors.Blue : Colors.Gray;
        Step4Indicator.BackgroundColor = step >= 4 ? Colors.Blue : Colors.Gray;
    }

    private void Log(string msg)
    {
        LogLabel.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        Debug.WriteLine($"[EndToEndWizard] {msg}");
    }

    private static string FormatTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd HH:mm");
}