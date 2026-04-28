using System.Collections.ObjectModel;
using Up366Net;

namespace Up366Master;

public partial class ClassesPage : ContentPage
{
    private ObservableCollection<ClassDisplayItem> _allClasses = new();
    private ObservableCollection<ClassDisplayItem> _filteredClasses = new();

    public ClassesPage()
    {
        InitializeComponent();
        ClassesCollection.ItemsSource = _filteredClasses;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadClasses();
    }

    private async void LoadClasses()
    {
        if (!AppShell.Client.IsAuthenticated)
        {
            await DisplayAlert("提示", "请先登录", "确定");
            return;
        }

        try
        {
            var classes = await AppShell.Client.GetClassListAsync();
            _allClasses.Clear();

            var colors = new[] { "#007AFF", "#5856D6", "#FF9500", "#FF3B30", "#34C759", "#AF52DE", "#5AC8FA" };
            int colorIdx = 0;

            foreach (var c in classes)
            {
                _allClasses.Add(new ClassDisplayItem
                {
                    CourseId = c.CourseId,
                    CourseName = c.CourseName,
                    TeacherName = $"教师: {c.TeacherName}",
                    OrganName = c.OrganName,
                    AvatarText = !string.IsNullOrEmpty(c.CourseName) ? c.CourseName[0].ToString() : "班",
                    AvatarColor = Color.FromArgb(colors[colorIdx % colors.Length])
                });
                colorIdx++;
            }

            FilterClasses();
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"获取班级失败: {ex.Message}", "确定");
        }
    }

    private void FilterClasses(string searchText = "")
    {
        _filteredClasses.Clear();
        var filtered = string.IsNullOrWhiteSpace(searchText)
            ? _allClasses
            : new ObservableCollection<ClassDisplayItem>(_allClasses.Where(c =>
                c.CourseName.Contains(searchText) ||
                c.TeacherName.Contains(searchText) ||
                c.OrganName.Contains(searchText)));

        foreach (var item in filtered)
        {
            _filteredClasses.Add(item);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterClasses(e.NewTextValue);
    }

    private void OnRefreshing(object sender, EventArgs e)
    {
        LoadClasses();
        RefreshView.IsRefreshing = false;
    }

    // 修改 OnClassTapped 方法，确保使用相对路径导航
    private async void OnClassTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is ClassDisplayItem item)
        {
            // 使用 ../ 前缀确保在导航栈中正确层级
            var navigationParameter = new Dictionary<string, object>
        {
            { "courseId", item.CourseId },
            { "courseName", item.CourseName }
        };

            await Shell.Current.GoToAsync(nameof(ClassDetailPage), navigationParameter);
        }
    }

    private async void OnViewHomework(object sender, EventArgs e)
    {
        if (sender is SwipeItem swipeItem && swipeItem.BindingContext is ClassDisplayItem item)
        {
            await Shell.Current.GoToAsync($"{nameof(HomeworkDetailPage)}?courseId={item.CourseId}&courseName={Uri.EscapeDataString(item.CourseName)}");
        }
    }
}

public class ClassDisplayItem
{
    public long CourseId { get; set; }
    public string CourseName { get; set; }
    public string TeacherName { get; set; }
    public string OrganName { get; set; }
    public string AvatarText { get; set; }
    public Color AvatarColor { get; set; }
}