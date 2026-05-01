using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using System.Diagnostics;
using System.IO;
using System.Text;
using Up366Net;
using Up366Parser;

namespace Up366Master;

[QueryProperty(nameof(BookId), "bookId")]
[QueryProperty(nameof(TaskId), "taskId")]
[QueryProperty(nameof(CourseId), "courseId")]
[QueryProperty(nameof(ChapterId), "chapterId")]
[QueryProperty(nameof(PageId), "pageId")]
[QueryProperty(nameof(JobName), "jobName")]
public partial class AutoCompletePage : ContentPage
{
    private string _bookId = string.Empty;
    private string _taskId = string.Empty;
    private string _courseId = string.Empty;
    private string _chapterId = string.Empty;
    private string _pageId = string.Empty;
    private string _jobName = "未选择";
    private readonly StringBuilder _logBuilder = new();

    // ★★★ 等待相关状态 ★★★
    private CancellationTokenSource _waitCts;
    private bool _isWaiting = false;
    private DateTime _waitStartTime;
    private int _targetDurationMinutes = 18;

    public string BookId
    {
        get => _bookId;
        set => _bookId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public string TaskId
    {
        get => _taskId;
        set => _taskId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public string CourseId
    {
        get => _courseId;
        set => _courseId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public string ChapterId
    {
        get => _chapterId;
        set => _chapterId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public string PageId
    {
        get => _pageId;
        set => _pageId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public string JobName
    {
        get => _jobName;
        set
        {
            _jobName = Uri.UnescapeDataString(value ?? string.Empty);
            Dispatcher.Dispatch(() =>
            {
                if (JobNameLabel != null)
                    JobNameLabel.Text = string.IsNullOrEmpty(_jobName) ? "未选择" : _jobName;
            });
        }
    }

    public AutoCompletePage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AppShell.Client.LogAdded += OnClientLogAdded;
        JobNameLabel.Text = string.IsNullOrEmpty(_jobName) ? "未选择" : _jobName;
        BookIdLabel.Text = $"Book: {Truncate(_bookId, 14)}";
        TaskIdLabel.Text = $"Task: {Truncate(_taskId, 14)}";

        if (!string.IsNullOrEmpty(_bookId) && !string.IsNullOrEmpty(_taskId))
        {
            ScoreInfoGrid.IsVisible = true;
            TargetScoreLabel.Text = $"{(int)ScoreSlider.Value}%";
            TargetDurationLabel.Text = $"{(int)DurationSlider.Value}分钟";
        }
        else
        {
            ScoreInfoGrid.IsVisible = false;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppShell.Client.LogAdded -= OnClientLogAdded;
        // 页面消失时取消等待
        _waitCts?.Cancel();
    }

    private void OnClientLogAdded(string log)
    {
        _logBuilder.AppendLine(log);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogLabel.Text = _logBuilder.ToString();
            LogFrame.IsVisible = true;
        });
    }

    private void OnDurationChanged(object sender, ValueChangedEventArgs e)
    {
        DurationLabel.Text = $"{(int)e.NewValue} 分钟";
        if (TargetDurationLabel != null)
            TargetDurationLabel.Text = $"{(int)e.NewValue}分钟";
        _targetDurationMinutes = (int)e.NewValue;
    }

    private void OnScoreChanged(object sender, ValueChangedEventArgs e)
    {
        ScoreLabel.Text = $"{(int)e.NewValue} 分";
    }

    // ★★★ 取消等待 ★★★
    private void OnCancelWaitClicked(object sender, EventArgs e)
    {
        _waitCts?.Cancel();
        _isWaiting = false;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            WaitingFrame.IsVisible = false;
            ExecuteBtn.IsEnabled = true;
            ExecuteBtn.Text = "开始一键完成";
            ExecuteBtn.BackgroundColor = Colors.Red;
            UpdateProgress(0, "已取消");
        });
    }

    // ★★★ 主执行流程 ★★★
    private async void OnExecuteClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookId) || string.IsNullOrEmpty(_taskId))
        {
            await DisplayAlert("错误", "缺少必要的作业信息", "确定");
            return;
        }

        _targetDurationMinutes = (int)DurationSlider.Value;

        var confirm = await DisplayAlert("确认执行",
            $"即将自动完成作业：\n{_jobName}\n\n" +
            $"⚠️ 重要提示：\n" +
            $"• 程序将真实等待 {_targetDurationMinutes} 分钟后再提交\n" +
            $"• 期间请保持应用在前台运行\n" +
            $"• 请勿关闭屏幕或切换应用\n" +
            $"• 建议连接充电器\n\n" +
            $"确定要继续吗？", "确定", "取消");

        if (!confirm) return;

        ExecuteBtn.IsEnabled = false;
        ExecuteBtn.Text = "准备中...";
        ProgressFrame.IsVisible = true;
        _logBuilder.Clear();
        LogLabel.Text = "";
        LogFrame.IsVisible = false;

        try
        {
            // ========== Phase 1: 准备阶段（立即执行）==========
            UpdateProgress(0.05, "正在同步服务器时间...");

            // 步骤0: 同步服务器时间
            var serverTime = await AppShell.Client.GetServerTimeAsync();
            var startTime = AppShell.Client.GetCalibratedTime();
            Trace.WriteLine($"[AutoComplete] 服务器时间: {serverTime}, 开始时间: {startTime}");

            UpdateProgress(0.1, "正在获取资源链...");
            var chain = await AppShell.Client.GetTaskLinkedAsync(_bookId, _taskId);
            var pcFileId = AppShell.Client.ExtractPcFileId(chain);

            if (string.IsNullOrEmpty(pcFileId))
            {
                await DisplayAlert("错误", "未找到PC文件ID，无法下载题目", "确定");
                ResetUI();
                return;
            }

            var chapterId = _chapterId;
            var pageId = _pageId;
            var batchId = chain?.ExtendParams?.BatchId ?? _taskId;
            var studyTaskId = chain?.StudyTaskId ?? _taskId;

            if (string.IsNullOrEmpty(chapterId))
                chapterId = chain?.Chapters?.FirstOrDefault(c => c.IsContent == 1)?.Id ?? "";
            if (string.IsNullOrEmpty(pageId))
                pageId = chain?.Tasks?.FirstOrDefault()?.PcPageId ?? "";

            UpdateProgress(0.2, "正在下载并解析题目...");
            var entries = await AppShell.Client.DownloadAndExtractAsync(pcFileId);

            // 解压题目
            var tempDir = Path.Combine(FileSystem.CacheDirectory, "autocomplete", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var hasPrefix2 = entries.Any(e => e.FileName.StartsWith("2/", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in entries)
            {
                var relativePath = entry.FileName;
                if (hasPrefix2 && entry.FileName.StartsWith("2/", StringComparison.OrdinalIgnoreCase))
                    relativePath = entry.FileName[2..];
                var destPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(destPath, entry.Content);
            }

            var questionsDir = Path.Combine(tempDir, "questions");
            if (!Directory.Exists(questionsDir))
            {
                await DisplayAlert("错误", "未找到题目数据目录", "确定");
                ResetUI();
                return;
            }

            var questions = ListeningQuestionParser.ParseQuestionsFolder(questionsDir);
            Trace.WriteLine($"[AutoComplete] 解析完成: 共 {questions.Count} 道题目");

            if (questions.Count == 0)
            {
                await DisplayAlert("错误", "未能解析到任何题目", "确定");
                ResetUI();
                return;
            }

            // 生成答案（目标得分，服务器percent固定为100）
            var targetScore = (int)ScoreSlider.Value;
            var allAnswers = GenerateAnswers(questions, targetScore);
            if (allAnswers.Count == 0)
            {
                await DisplayAlert("错误", "未能生成答案", "确定");
                ResetUI();
                return;
            }

            // ========== Phase 2: 等待（关键！）==========
            var targetEndTime = startTime + (_targetDurationMinutes * 60 * 1000);
            var remainingMs = targetEndTime - AppShell.Client.GetCalibratedTime();

            if (remainingMs > 5000) // 如果还有超过5秒才到目标时间
            {
                _waitCts = new CancellationTokenSource();
                _isWaiting = true;
                _waitStartTime = DateTime.Now;

                // 显示等待界面
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    WaitingFrame.IsVisible = true;
                    ExecuteBtn.Text = "等待中...";
                    ExecuteBtn.BackgroundColor = Colors.Gray;
                });

                // 保持屏幕常亮
                DeviceDisplay.KeepScreenOn = true;

                Trace.WriteLine($"[AutoComplete] 开始等待 {remainingMs}ms ({remainingMs / 60000.0:F1}分钟)...");

                // 更新等待进度
                _ = UpdateWaitingProgressAsync(targetEndTime, _waitCts.Token);

                // 真实等待
                try
                {
                    await Task.Delay((int)remainingMs, _waitCts.Token);
                }
                catch (TaskCanceledException)
                {
                    Trace.WriteLine("[AutoComplete] 等待被取消");
                    DeviceDisplay.KeepScreenOn = false;
                    ResetUI();
                    return;
                }

                DeviceDisplay.KeepScreenOn = false;
                _isWaiting = false;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    WaitingFrame.IsVisible = false;
                });
            }

            // ========== Phase 3: 提交（等待结束后立即执行）==========
            UpdateProgress(0.8, "正在提交成绩...");

            // 重新校准时间戳（基于开始时间 + 随机分布）
            var actualDurationMs = AppShell.Client.GetCalibratedTime() - startTime;
            var timeStep = actualDurationMs / Math.Max(allAnswers.Count, 1);
            for (int i = 0; i < allAnswers.Count; i++)
            {
                allAnswers[i].Timestamp = startTime + (i * timeStep) + Random.Shared.Next(1000, 5000);
                allAnswers[i].Order = i + 1;
            }

            var result = await AppShell.Client.SubmitTaskScoreAsync(
                taskId: _taskId,
                bookId: _bookId,
                chapterId: chapterId,
                courseId: _courseId,
                pageId: pageId,
                questions: allAnswers,
                durationMinutes: _targetDurationMinutes,
                scorePercent: 100, // percent固定为100
                batchId: batchId,
                studyTaskId: studyTaskId,
                calibratedNow: AppShell.Client.GetCalibratedTime());

            UpdateProgress(1.0, result ? "完成！" : "提交失败");

            if (result)
            {
                await DisplayAlert("完成",
                    $"作业已完成！\n目标用时：{_targetDurationMinutes}分钟\n实际用时：{actualDurationMs / 60000.0:F1}分钟",
                    "确定");
            }
            else
            {
                await DisplayAlert("失败", "提交失败，请查看日志", "确定");
            }
        }
        catch (Up366Exception ex) when (ex.Message.Contains("没有登陆") || ex.ErrorCode == 401)
        {
            UpdateProgress(0, "会话已过期");
            await DisplayAlert("会话过期", "登录已失效，请重新登录", "确定");
            await AppShell.LogoutAsync();
        }
        catch (Exception ex)
        {
            UpdateProgress(0, $"错误: {ex.Message}");
            await DisplayAlert("错误", ex.Message, "确定");
        }
        finally
        {
            DeviceDisplay.KeepScreenOn = false;
            ResetUI();
        }
    }

    // ★★★ 更新等待进度 UI ★★★
    private async Task UpdateWaitingProgressAsync(long targetEndTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = AppShell.Client.GetCalibratedTime();
            var remaining = targetEndTime - now;
            var elapsed = now - (targetEndTime - _targetDurationMinutes * 60 * 1000);
            var total = _targetDurationMinutes * 60 * 1000L;

            if (remaining <= 0) break;

            var progress = 1.0 - (remaining / (double)total);
            var elapsedMin = elapsed / 60000;
            var elapsedSec = (elapsed % 60000) / 1000;
            var remainMin = remaining / 60000;
            var remainSec = (remaining % 60000) / 1000;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                WaitingProgressBar.Progress = Math.Clamp(progress, 0, 1);
                ElapsedTimeLabel.Text = $"{elapsedMin}:{elapsedSec:D2}";
                RemainingTimeLabel.Text = $"{remainMin}:{remainSec:D2}";
            });

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    // ★★★ 生成答案 ★★★
    private List<Up366Net.QuestionAnswer> GenerateAnswers(
        List<ListeningQuestion> questions, int targetScore)
    {
        var allAnswers = new List<Up366Net.QuestionAnswer>();
        int totalQuestions = 0;

        foreach (var q in questions)
        {
            totalQuestions += q.IsComposite && q.SubQuestions?.Count > 0 ? q.SubQuestions.Count : 1;
        }

        // 根据目标得分计算正确题数
        int correctCount = totalQuestions > 0 ? (int)(targetScore * totalQuestions / 30.0) : 0;
        var rng = new Random();
        var correctIndices = new HashSet<int>();
        while (correctIndices.Count < correctCount)
        {
            correctIndices.Add(rng.Next(totalQuestions));
        }

        int globalIdx = 0;
        foreach (var q in questions.OrderBy(q => q.QuestionNumber))
        {
            if (q.IsComposite && q.SubQuestions != null && q.SubQuestions.Count > 0)
            {
                foreach (var subQ in q.SubQuestions)
                {
                    var correctOpt = subQ.Options?.FirstOrDefault(o => o.IsCorrect)
                        ?? subQ.Options?.FirstOrDefault();

                    if (correctOpt == null || subQ.Options == null || subQ.Options.Count == 0) continue;

                    bool isCorrect = correctIndices.Contains(globalIdx++);
                    allAnswers.Add(new Up366Net.QuestionAnswer
                    {
                        QuestionId = subQ.QuestionId ?? q.QuestionId,
                        ElementId = subQ.ElementId ?? q.ElementId ?? subQ.QuestionId ?? q.QuestionId,
                        QuestionType = 1,
                        FullScore = subQ.Score > 0 ? subQ.Score : (q.Score / Math.Max(q.SubQuestions.Count, 1)),
                        UserAnswer = isCorrect ? correctOpt.Id : GetWrongAnswer(correctOpt.Id, subQ.Options),
                        CorrectAnswer = correctOpt.Id,
                        IsCorrect = isCorrect,
                        Timestamp = 0,
                        Order = 0
                    });
                }
            }
            else
            {
                var correctOpt = q.Options?.FirstOrDefault(o => o.IsCorrect)
                    ?? q.Options?.FirstOrDefault();

                if (correctOpt == null || q.Options == null || q.Options.Count == 0) continue;

                bool isCorrect = correctIndices.Contains(globalIdx++);
                allAnswers.Add(new Up366Net.QuestionAnswer
                {
                    QuestionId = q.QuestionId,
                    ElementId = q.ElementId ?? q.QuestionId,
                    QuestionType = q.QuestionType,
                    FullScore = q.Score,
                    UserAnswer = isCorrect ? correctOpt.Id : GetWrongAnswer(correctOpt.Id, q.Options),
                    CorrectAnswer = correctOpt.Id,
                    IsCorrect = isCorrect,
                    Timestamp = 0,
                    Order = 0
                });
            }
        }

        Trace.WriteLine($"[GenerateAnswers] 共{totalQuestions}题，目标{correctCount}题正确");
        return allAnswers;
    }

    private static string GetWrongAnswer(string correct, List<QuestionOption> options)
    {
        var wrongOptions = options
            .Where(o => !string.Equals(o.Id, correct, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (wrongOptions.Count == 0) return correct;
        return wrongOptions[Random.Shared.Next(wrongOptions.Count)].Id;
    }

    private void ResetUI()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ExecuteBtn.IsEnabled = true;
            ExecuteBtn.Text = "开始一键完成";
            ExecuteBtn.BackgroundColor = Colors.Red;
            WaitingFrame.IsVisible = false;
            ProgressFrame.IsVisible = false;
        });
    }

    private void UpdateProgress(double progress, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProgressBar.Progress = progress;
            StatusLabel.Text = status;
            DetailLabel.Text = DateTime.Now.ToString("HH:mm:ss");
            PercentLabel.Text = $"{(int)(progress * 100)}%";
        });
    }

    private async void OnCopyLogClicked(object sender, EventArgs e)
    {
        var log = _logBuilder.ToString();
        if (string.IsNullOrEmpty(log))
        {
            await DisplayAlert("提示", "暂无日志", "确定");
            return;
        }

        var logFilePath = await SaveLogToFileAsync(log);
        if (!string.IsNullOrEmpty(logFilePath))
        {
            var fileName = Path.GetFileName(logFilePath);
            await Clipboard.SetTextAsync(logFilePath);
            await DisplayAlert("成功", $"日志已保存到:\n{fileName}\n\n路径已复制到剪贴板", "确定");
        }
    }

    private async Task<string> SaveLogToFileAsync(string log)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Up366Master", "logs");
            Directory.CreateDirectory(logDir);
            var logFilePath = Path.Combine(logDir, $"autocomplete_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var fullLog = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AutoCompletePage Log Export\n" +
                        $"========================================\n" +
                        log + "\n";

            await File.WriteAllTextAsync(logFilePath, fullLog);
            return logFilePath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async void OnViewLogClicked(object sender, EventArgs e)
    {
        var logFilePath = await GetTodayLogFilePathAsync();
        if (string.IsNullOrEmpty(logFilePath))
        {
            await DisplayAlert("提示", "暂无日志文件", "确定");
            return;
        }

        var logContent = await File.ReadAllTextAsync(logFilePath);
        await DisplayAlert("日志内容", logContent, "确定");
    }

    private async void OnShareLogClicked(object sender, EventArgs e)
    {
        var logFilePath = await GetTodayLogFilePathAsync();
        if (string.IsNullOrEmpty(logFilePath))
        {
            await DisplayAlert("提示", "暂无日志文件", "确定");
            return;
        }

        await Share.RequestAsync(new ShareFileRequest
        {
            Title = "分享日志",
            File = new ShareFile(logFilePath)
        });
    }

    private async Task<string> GetTodayLogFilePathAsync()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Up366Master", "logs");
            if (!Directory.Exists(logDir)) return string.Empty;

            var todayFiles = Directory.GetFiles(logDir, "autocomplete_*.log")
                .OrderByDescending(f => f)
                .Take(1);

            foreach (var file in todayFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime.Date == DateTime.Today)
                    return file;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "N/A" : (value.Length <= max ? value : value[..(max - 3)] + "...");
}