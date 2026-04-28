using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Up366Master;

public partial class DashboardPage : ContentPage, INotifyPropertyChanged
{
    private string _welcomeText = "欢迎回来";
    private string _subtitleText = "加载中...";
    private string _firstChar = "U";
    private int _classCount = 0;
    private int _pendingJobs = 0;

    public string WelcomeText
    {
        get => _welcomeText;
        set { _welcomeText = value; OnPropertyChanged(nameof(WelcomeText)); }
    }

    public string SubtitleText
    {
        get => _subtitleText;
        set { _subtitleText = value; OnPropertyChanged(nameof(SubtitleText)); }
    }

    public string FirstChar
    {
        get => _firstChar;
        set { _firstChar = value; OnPropertyChanged(nameof(FirstChar)); }
    }

    public int ClassCount
    {
        get => _classCount;
        set { _classCount = value; OnPropertyChanged(nameof(ClassCount)); }
    }

    public int PendingJobs
    {
        get => _pendingJobs;
        set { _pendingJobs = value; OnPropertyChanged(nameof(PendingJobs)); }
    }

    public ObservableCollection<ActivityItem> RecentActivities { get; set; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        BindingContext = this;
        RecentActivityList.ItemsSource = RecentActivities;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadData();
    }

    private async void LoadData()
    {
        try
        {
            var session = AppShell.Client.Session;
            if (session != null)
            {
                WelcomeText = $"你好，{session.RealName}";
                SubtitleText = $"UID: {session.Uid}";
                FirstChar = !string.IsNullOrEmpty(session.RealName) ? session.RealName[0].ToString() : "U";
            }

            if (AppShell.Client.IsAuthenticated)
            {
                var classes = await AppShell.Client.GetClassListAsync();
                ClassCount = classes.Count;

                var stats = await AppShell.Client.GetClassStatsAsync();
                PendingJobs = stats.Sum(s => s.TaskNum);
            }
        }
        catch (Exception ex)
        {
            SubtitleText = $"加载失败: {ex.Message}";
        }
    }

    private void OnRefreshing(object sender, EventArgs e)
    {
        LoadData();
        RefreshView.IsRefreshing = false;
    }

    private async void OnClassesTapped(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//ClassesPage");

    private async void OnHomeworkTapped(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//HomeworkPage");

    private async void OnToolsTapped(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//ToolsPage");

    private async void OnSessionTapped(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync(nameof(SessionManagePage));

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class ActivityItem
{
    public string Icon { get; set; }
    public Color IconColor { get; set; }
    public string Title { get; set; }
    public string Time { get; set; }
}