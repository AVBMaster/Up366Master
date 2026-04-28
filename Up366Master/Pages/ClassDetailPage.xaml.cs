using Up366Net;

namespace Up366Master;

[QueryProperty(nameof(CourseId), "courseId")]
[QueryProperty(nameof(CourseName), "courseName")]
public partial class ClassDetailPage : ContentPage
{
    private long _courseId;
    private string _courseName;

    public long CourseId
    {
        get => _courseId;
        set
        {
            _courseId = value;
            // 延迟加载确保页面已完全初始化
            if (_courseId != 0 && !string.IsNullOrEmpty(_courseName))
            {
                Dispatcher.Dispatch(async () => await LoadClassDetail());
            }
        }
    }

    public string CourseName
    {
        get => _courseName;
        set
        {
            _courseName = value;
            ClassNameLabel.Text = _courseName;
            // 检查是否可以加载
            if (_courseId != 0 && !string.IsNullOrEmpty(_courseName))
            {
                Dispatcher.Dispatch(async () => await LoadClassDetail());
            }
        }
    }

    public ClassDetailPage()
    {
        InitializeComponent();
    }

    private void OnBackButtonPressed(object sender, EventArgs e)
    {
        // 确保返回操作完成
        if (Navigation.NavigationStack.Count > 1)
        {
            Navigation.PopAsync();
        }
        else
        {
            // 如果在 Shell 导航中，使用 GoToAsync 返回
            Shell.Current.GoToAsync("..");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 备用加载方案
        if (_courseId != 0 && string.IsNullOrEmpty(TeacherLabel.Text))
        {
            _ = LoadClassDetail();
        }
    }

    private async Task LoadClassDetail()
    {
        if (_courseId == 0) return;

        try
        {
            var classes = await AppShell.Client.GetClassListAsync();
            var cls = classes.FirstOrDefault(c => c.CourseId == _courseId);

            if (cls != null)
            {
                TeacherLabel.Text = $"教师: {cls.TeacherName}";
                OrganLabel.Text = cls.OrganName;
                JoinTimeLabel.Text = $"加入时间: {FormatTime(cls.JoinTime)}";
            }

            await LoadStats();
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", ex.Message, "确定");
        }
    }

    private async Task LoadStats()
    {
        try
        {
            var stats = await AppShell.Client.GetClassStatsAsync();
            var stat = stats.FirstOrDefault(s => s.CourseId == _courseId);

            if (stat != null)
            {
                TaskNumLabel.Text = stat.TaskNum.ToString();
                BookTaskLabel.Text = stat.BookTaskNum.ToString();
                TestLabel.Text = stat.TestNum.ToString();
                WrongLabel.Text = stat.WrongNoteNum.ToString();
            }
            else
            {
                TaskNumLabel.Text = "0";
                BookTaskLabel.Text = "0";
                TestLabel.Text = "0";
                WrongLabel.Text = "0";
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"获取统计失败: {ex.Message}", "确定");
        }
    }

    private async void OnViewHomeworkClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync($"{nameof(HomeworkDetailPage)}?courseId={_courseId}&courseName={Uri.EscapeDataString(_courseName)}");
    }

    private async void OnRefreshStatsClicked(object sender, EventArgs e)
    {
        await LoadStats();
    }

    private void OnRefreshing(object sender, EventArgs e)
    {
        LoadClassDetail();
        RefreshView.IsRefreshing = false;
    }

    private static string FormatTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
}