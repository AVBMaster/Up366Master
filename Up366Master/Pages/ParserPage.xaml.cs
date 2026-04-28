using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Maui.Storage;
using Up366Parser;

namespace Up366Master;

[QueryProperty(nameof(InitialPath), "path")]
public partial class ParserPage : ContentPage
{
    private string _initialPath = string.Empty;
    public string InitialPath
    {
        set
        {
            _initialPath = Uri.UnescapeDataString(value);
            if (!string.IsNullOrEmpty(_initialPath))
            {
                PathEntry.Text = _initialPath;
            }
        }
    }

    private ObservableCollection<QuestionDisplayItem> _questions = new();

    public ParserPage()
    {
        InitializeComponent();
        QuestionsCollection.ItemsSource = _questions;
        PathEntry.Text = Path.Combine(FileSystem.AppDataDirectory, "downloads");
    }

    private async void OnBrowseClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);

            if (result.IsSuccessful && result.Folder is not null)
            {
                PathEntry.Text = result.Folder.Path;
            }
            else if (result.Exception is not null)
            {
                await DisplayAlert("错误", $"选择文件夹失败: {result.Exception.Message}", "确定");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("错误", $"打开文件夹选择器失败: {ex.Message}", "确定");
        }
    }

    private async void OnParseClicked(object sender, EventArgs e)
    {
        var path = PathEntry.Text?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            await DisplayAlert("错误", "路径不存在或无效", "确定");
            return;
        }

        // 智能路径补全
        if (!path.EndsWith("questions", StringComparison.OrdinalIgnoreCase))
        {
            var qPath = Path.Combine(path, "2", "questions");
            if (Directory.Exists(qPath)) path = qPath;
        }

        try
        {
            _questions.Clear();
            var questions = ListeningQuestionParser.ParseQuestionsFolder(path);

            foreach (var q in questions.OrderBy(x => x.QuestionNumber))
            {
                var item = new QuestionDisplayItem
                {
                    IsComposite = q.IsComposite,
                    HeaderText = q.IsComposite
                        ? $"第 {q.QuestionNumber} 题 [复合题]"
                        : $"第 {q.QuestionNumber} 题",
                    TypeText = q.IsComposite ? "复合题" : "单题",
                    TypeColor = q.IsComposite ? Colors.Purple : Colors.Blue,
                    QuestionText = q.IsComposite ? q.CompositeMainText : q.QuestionText,
                    AnswerText = $"答案: {q.Answer}",
                    HasTranscript = !string.IsNullOrEmpty(q.FullTranscript),
                    Transcript = q.FullTranscript,
                    Options = q.Options.Select(o => new OptionDisplayItem
                    {
                        Id = o.Id,
                        Content = o.Content,
                        OptionColor = o.IsCorrect ? Colors.Green :
                            (Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black)
                    }).ToList(),
                    SubQuestions = q.SubQuestions.Select((s, idx) => new SubQuestionDisplayItem
                    {
                        SubHeader = $"小题 {idx + 1}",
                        QuestionText = s.QuestionText,
                        AnswerText = $"答案: {s.Answer}",
                        Options = s.Options.Select(o => new OptionDisplayItem
                        {
                            Id = o.Id,
                            Content = o.Content,
                            OptionColor = o.IsCorrect ? Colors.Green :
                                (Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black)
                        }).ToList()
                    }).ToList()
                };

                _questions.Add(item);
            }

            // ★ 有数据后启用导出按钮
            ExportBtn.IsEnabled = _questions.Count > 0;

            await DisplayAlert("解析完成", $"共解析 {questions.Count} 道题目", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("解析错误", ex.Message, "确定");
        }
    }

    // ★★★ 新增：一键导出答案 ★★★
    private async void OnExportClicked(object sender, EventArgs e)
    {
        if (_questions.Count == 0)
        {
            await DisplayAlert("提示", "请先解析题目", "确定");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("===== 天学网题目答案汇总 =====");
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"共 {_questions.Count} 道题目");
        sb.AppendLine(new string('=', 32));
        sb.AppendLine();

        int idx = 1;
        foreach (var q in _questions)
        {
            sb.AppendLine($"【第 {idx} 题】[{q.TypeText}]");

            if (q.IsComposite)
            {
                sb.AppendLine($"题目: {q.QuestionText}");
                for (int i = 0; i < q.SubQuestions.Count; i++)
                {
                    var sub = q.SubQuestions[i];
                    sb.AppendLine($"  ({i + 1}) {sub.QuestionText}");
                    if (sub.Options?.Count > 0)
                    {
                        sb.AppendLine($"      选项: {string.Join("  ", sub.Options.Select(o => $"{o.Id}. {o.Content}"))}");
                    }
                    sb.AppendLine($"      {sub.AnswerText}");
                }
            }
            else
            {
                sb.AppendLine($"题目: {q.QuestionText}");
                if (q.Options?.Count > 0)
                {
                    sb.AppendLine($"选项: {string.Join("  ", q.Options.Select(o => $"{o.Id}. {o.Content}"))}");
                }
                sb.AppendLine($"{q.AnswerText}");
            }

            if (!string.IsNullOrEmpty(q.Transcript))
            {
                sb.AppendLine($"原文: {q.Transcript}");
            }

            sb.AppendLine();
            idx++;
        }

        var text = sb.ToString();

        try
        {
            // 1. 复制到剪贴板
            await Clipboard.SetTextAsync(text);

            // 2. 保存到缓存目录
            var fileName = $"answers_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var filePath = Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllTextAsync(filePath, text);

            await DisplayAlert("导出成功",
                $"答案已复制到剪贴板\n同时保存到:\n{filePath}", "确定");
        }
        catch (Exception ex)
        {
            // 即使保存失败，剪贴板通常也能成功
            await Clipboard.SetTextAsync(text);
            await DisplayAlert("导出成功", "答案已复制到剪贴板", "确定");
        }
    }
}

#region 数据模型

public class QuestionDisplayItem
{
    public bool IsComposite { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public string TypeText { get; set; } = string.Empty;
    public Color TypeColor { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerText { get; set; } = string.Empty;
    public bool HasTranscript { get; set; }
    public string Transcript { get; set; } = string.Empty;
    public List<OptionDisplayItem> Options { get; set; } = new();
    public List<SubQuestionDisplayItem> SubQuestions { get; set; } = new();
}

public class OptionDisplayItem
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Color OptionColor { get; set; }
}

public class SubQuestionDisplayItem
{
    public string SubHeader { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerText { get; set; } = string.Empty;
    public List<OptionDisplayItem> Options { get; set; } = new();
}

#endregion