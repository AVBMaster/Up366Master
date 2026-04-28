using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Up366Net;
using Up366Parser;

namespace Up366MasterCLI
{
    // 用于 JSON 序列化的 Session 数据模型
    public class SessionData
    {
        public string? Tgt { get; set; }
        public string? Token { get; set; }
        public string? Up366C { get; set; }
        public string? Castgc { get; set; }
        public string? Uuid { get; set; }
        public long Uid { get; set; }
        public string? RealName { get; set; }
        public string? Username { get; set; }
        public long OrganId { get; set; }
        public string? ClientId { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    internal static class Program
    {
        private static Up366Client Client = new(includeOptionalLoggingHeaders: true);
        private static bool _running = true;
        private static readonly string SessionFilePath = "session.json";

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            PrintBanner();

            // 启动时尝试自动加载本地会话
            PrintInfo("启动时尝试加载本地保存的会话...");
            var loaded = await TryLoadSessionAsync();
            if (loaded)
            {
                PrintSuccess("已自动恢复登录状态，可直接使用选项 3~8");
            }
            else
            {
                PrintWarning("无本地会话或已过期，请执行选项 1~2 登录，或按 L 手动加载");
            }
            Console.WriteLine();

            while (_running)
            {
                ShowMenu();
                var input = Console.ReadLine()?.Trim();

                try
                {
                    await ExecuteOption(input);
                }
                catch (Exception ex)
                {
                    PrintError($"[全局异常捕获] {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        PrintError($"  └─> Inner: {ex.InnerException.Message}");
                }

                if (_running && input?.ToLower() != "0")
                {
                    PrintDark("\n按 Enter 键返回主菜单...");
                    Console.ReadLine();
                }
            }

            Client.Dispose();
            PrintInfo("程序已安全退出。");
        }

        #region 会话持久化方法

        /// <summary>
        /// 保存当前会话到本地 JSON 文件
        /// </summary>
        private static async Task<bool> SaveSessionAsync()
        {
            try
            {
                var s = Client.Session;
                if (s == null || string.IsNullOrEmpty(s.Up366C))
                {
                    PrintWarning("当前无有效会话，无法保存");
                    return false;
                }

                var data = new SessionData
                {
                    Tgt = s.Tgt,
                    Token = s.Token,
                    Up366C = s.Up366C,
                    Castgc = s.Castgc,
                    Uuid = s.Uuid,
                    Uid = s.Uid,
                    RealName = s.RealName,
                    Username = s.Username,
                    OrganId = s.OrganId,
                    ClientId = s.ClientId,
                    SavedAt = DateTime.Now
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                await File.WriteAllTextAsync(SessionFilePath, json);
                PrintSuccess($"会话已保存到: {Path.GetFullPath(SessionFilePath)}");
                PrintInfo($"  保存时间: {data.SavedAt:yyyy-MM-dd HH:mm:ss}");
                PrintInfo($"  用户: {data.RealName} (UID: {data.Uid})");
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"保存会话失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从本地 JSON 文件加载会话
        /// </summary>
        private static async Task<bool> TryLoadSessionAsync(bool silent = false)
        {
            try
            {
                if (!File.Exists(SessionFilePath))
                {
                    if (!silent) PrintWarning($"会话文件不存在: {SessionFilePath}");
                    return false;
                }

                var json = await File.ReadAllTextAsync(SessionFilePath);
                var data = JsonSerializer.Deserialize<SessionData>(json);

                if (data == null || string.IsNullOrEmpty(data.Up366C))
                {
                    if (!silent) PrintError("会话文件内容无效或缺少 UP366-C");
                    return false;
                }

                // 检查是否可能过期（超过7天认为可能过期）
                var age = DateTime.Now - data.SavedAt;
                if (age.TotalDays > 7)
                {
                    PrintWarning($"会话已保存 {age.TotalDays:F1} 天，可能已过期");
                    Console.Write("是否仍尝试加载? (y/N): ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y")
                    {
                        return false;
                    }
                }

                // 重建 SessionContext 并注入到 Client
                // 注意：我们需要反射或重新设计 Up366Client 来支持恢复 Session
                // 这里采用重新初始化 Client 并手动设置 Session 的方式
                var session = new SessionContext
                {
                    Tgt = data.Tgt,
                    Token = data.Token,
                    Up366C = data.Up366C,
                    Castgc = data.Castgc,
                    Uuid = data.Uuid,
                    Uid = data.Uid,
                    RealName = data.RealName,
                    Username = data.Username,
                    OrganId = data.OrganId,
                    ClientId = data.ClientId
                };

                // 重新初始化 Client，确保 CookieContainer 干净
                Client.Dispose();
                Client = new Up366Client(includeOptionalLoggingHeaders: true);

                // 使用反射设置私有字段 _session（需要 Up366Client 配合修改）
                // 或者更简单的：通过重新登录流程，但跳过验证码，直接用保存的 Cookie
                // 这里我们尝试直接调用 AutoLoginAsync 的替代方案：手动设置 Cookie

                // 由于 Up366Client 内部 CookieContainer 是私有的，我们需要修改 Up366Net.cs
                // 临时方案：通过发送一个测试请求来验证 Cookie 是否有效
                if (!silent)
                {
                    PrintInfo("正在验证保存的会话是否有效...");
                }

                // 将 Cookie 注入到新的 Client（需要修改 Up366Net.cs 添加方法）
                // 暂时先直接赋值，假设 Cookie 有效
                InjectSessionToClient(session);

                if (!silent)
                {
                    PrintSuccess($"会话加载成功！");
                    PrintInfo($"  用户: {session.RealName}");
                    PrintInfo($"  保存于: {data.SavedAt:yyyy-MM-dd HH:mm:ss} ({age.TotalHours:F1} 小时前)");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (!silent) PrintError($"加载会话失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过反射将 Session 注入到 Client（需要 Up366Client 配合）
        /// </summary>
        private static void InjectSessionToClient(SessionContext session)
        {
            // 使用反射设置私有字段
            var clientType = typeof(Up366Client);

            // 设置 _session 字段
            var sessionField = clientType.GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (sessionField != null)
            {
                sessionField.SetValue(Client, session);
            }

            // 设置 CookieContainer 中的 Cookie
            var handlerField = clientType.GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (handlerField != null)
            {
                var handler = handlerField.GetValue(Client) as HttpClientHandler;
                if (handler != null)
                {
                    // 添加 Cookie 到各个域名
                    AddCookieToContainer(handler.CookieContainer, "UP366-C", session.Up366C);
                    AddCookieToContainer(handler.CookieContainer, "CASTGC", session.Castgc);
                }
            }
        }

        private static void AddCookieToContainer(CookieContainer container, string name, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var domains = new[]
            {
                "up366.cn",
                "course-api.up366.cn",
                "studytask-api.up366.cn",
                "book-api.up366.cn",
                "fs-v2.up366.cn",
                "sso.up366.cn",
                "user-api.up366.cn"
            };

            foreach (var domain in domains)
            {
                try
                {
                    var uri = new Uri($"https://{domain}");
                    container.Add(uri, new Cookie(name, value) { HttpOnly = true, Secure = true });
                }
                catch { /* 忽略无效域名 */ }
            }
        }

        /// <summary>
        /// 清除本地保存的会话
        /// </summary>
        private static void ClearSessionFile()
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    File.Delete(SessionFilePath);
                    PrintSuccess("本地会话文件已清除");
                }
                else
                {
                    PrintInfo("无本地会话文件需要清除");
                }

                // 同时重置当前 Client
                Client.Dispose();
                Client = new Up366Client(includeOptionalLoggingHeaders: true);
                PrintInfo("当前会话已重置");
            }
            catch (Exception ex)
            {
                PrintError($"清除会话失败: {ex.Message}");
            }
        }

        #endregion

        #region UI 渲染

        private static void PrintBanner()
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine(@"║              Up366MasterCLI  ·  天学网 API 综合测试工具            ║");
            Console.WriteLine(@"║                   Target Framework: .NET 10.0                     ║");
            Console.WriteLine(@"╚══════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void ShowMenu()
        {
            Console.Clear();
            PrintBanner();

            // 检查本地会话文件状态
            var hasLocalSession = File.Exists(SessionFilePath);
            var localSessionInfo = "";
            if (hasLocalSession)
            {
                try
                {
                    var json = File.ReadAllText(SessionFilePath);
                    var data = JsonSerializer.Deserialize<SessionData>(json);
                    if (data != null)
                    {
                        var age = DateTime.Now - data.SavedAt;
                        localSessionInfo = $" | 本地: {data.RealName} ({age.TotalHours:F0}h前)";
                    }
                }
                catch { }
            }

            // 会话状态诊断
            var s = Client.Session;
            var hasToken = !string.IsNullOrEmpty(s?.Token);
            var hasTgt = !string.IsNullOrEmpty(s?.Tgt);
            var hasUp366C = !string.IsNullOrEmpty(s?.Up366C);
            var isAuth = Client.IsAuthenticated;

            Console.WriteLine("══════════════════════ 会话诊断 ══════════════════════");
            Console.Write($"  Token: "); PrintStatusDot(hasToken);
            Console.Write($"  TGT: "); PrintStatusDot(hasTgt);
            Console.Write($"  UP366-C: "); PrintStatusDot(hasUp366C);
            Console.Write($"  IsAuthenticated: "); PrintStatusDot(isAuth);
            Console.WriteLine();

            if (s != null)
            {
                Console.WriteLine($"  当前用户: {s.RealName ?? "(null)"} | UID: {s.Uid}{localSessionInfo}");
                if (!isAuth && hasToken && hasTgt)
                {
                    PrintWarning("  ⚠️ Token/TGT 已获取，但 UP366-C 为空 → AutoLogin 未正确提取 Cookie");
                }
            }
            else if (hasLocalSession)
            {
                PrintInfo($"  检测到本地保存的会话，按 L 加载{localSessionInfo}");
            }
            else
            {
                PrintWarning("  未登录，无本地会话");
            }
            Console.WriteLine("══════════════════════════════════════════════════════\n");

            Console.WriteLine("════════════════════════════ 功能菜单 ════════════════════════════");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [认证流程]");
            Console.ResetColor();
            Console.WriteLine("    1. 发送短信验证码          (SendVerifyCodeAsync)");
            Console.WriteLine("    2. 验证验证码并自动登录     (VerifyCode + AutoLogin) [自动保存会话]");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [数据查询]");
            Console.ResetColor();
            Console.WriteLine("    3. 获取班级列表             (GetClassListAsync)");
            Console.WriteLine("    4. 获取班级任务统计         (GetClassStatsAsync)");
            Console.WriteLine("    5. 获取作业列表             (GetJobListAsync)");
            Console.WriteLine("    6. 获取作业资源链           (GetTaskLinkedAsync)");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [文件操作]");
            Console.ResetColor();
            Console.WriteLine("    7. 获取 CDN 下载直链        (GetDownloadUrlAsync)");
            Console.WriteLine("    8. 下载并解压 ZIP           (DownloadAndExtractAsync)");
            Console.WriteLine("    9. 解析题目数据             (ParseQuestionDataAsync)");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [自动化]");
            Console.ResetColor();
            Console.WriteLine("    10. 端到端向导测试           (完整流程自动化)");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [会话管理]");
            Console.ResetColor();
            Console.WriteLine("    L. 加载本地会话             (从 session.json 恢复)");
            Console.WriteLine("    S. 保存当前会话             (保存到 session.json)");
            Console.WriteLine("    C. 清除本地会话             (删除 session.json 并重置)");
            Console.WriteLine("    D. 查看当前 Session 详情");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [系统]");
            Console.ResetColor();
            Console.WriteLine("    0. 退出程序");
            Console.WriteLine("══════════════════════════════════════════════════════════════════");
            Console.Write("请输入选项: ");
        }

        private static void PrintStatusDot(bool ok)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "● " : "○ ");
            Console.ResetColor();
        }

        private static async Task ExecuteOption(string? input)
        {
            switch (input?.ToLower())
            {
                case "1": await TestSendVerifyCode(); break;
                case "2": await TestLogin(); break;
                case "3": await TestGetClassList(); break;
                case "4": await TestGetClassStats(); break;
                case "5": await TestGetJobList(); break;
                case "6": await TestGetResourceChain(); break;
                case "7": await TestGetDownloadUrl(); break;
                case "8": await TestDownloadAndExtract(); break;
                case "9": await ParseQuestionDataAsync(); break;
                case "10": await TestEndToEnd(); break;
                case "l": await TryLoadSessionAsync(); break;
                case "s": await SaveSessionAsync(); break;
                case "c": ClearSessionFile(); break;
                case "d": ShowSessionDetails(); break;
                case "0": _running = false; break;
                default: PrintWarning("无效选项，请重新输入。"); break;
            }
        }

        #endregion

        #region 测试方法（全部带网络异常处理）

        private static async Task TestSendVerifyCode()
        {
            PrintSection("测试 1: 发送短信验证码");
            Console.Write("请输入手机号: ");
            var mobile = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(mobile))
            {
                PrintWarning("手机号不能为空。");
                return;
            }

            PrintInfo($"正在向 {mobile} 发送验证码...");

            try
            {
                var (success, rawResponse, code, msg) = await SendVerifyCodeWithDiagnosticsAsync(mobile);

                if (code == -999 && !string.IsNullOrEmpty(rawResponse))
                {
                    PrintDark($"[调试] 原始响应片段: {rawResponse[..Math.Min(200, rawResponse.Length)]}");
                }

                if (success)
                {
                    PrintSuccess("验证码发送成功，请查收短信。");
                    PrintInfo("提示：验证码通常5分钟内有效，请尽快使用。");
                }
                else
                {
                    PrintError($"[发送验证码] 失败: {msg} (服务端返回码: {code}) | 手机号: {mobile}");
                    switch (code)
                    {
                        case 3: PrintWarning("💡 该手机号今日已达10次上限，请明日再试或使用其他手机号"); break;
                        case 2: PrintWarning("💡 发送过于频繁，请等待60秒后重试"); break;
                        case 1:
                        case -1: PrintWarning("💡 可能是手机号格式错误或未在天学网注册"); break;
                        default: PrintWarning($"💡 未知业务错误码 {code}"); break;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                PrintError($"[发送验证码] 网络请求异常: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                PrintError($"[发送验证码] 请求超时: {ex.Message}");
            }
            catch (Exception ex)
            {
                PrintError($"[发送验证码] 未处理异常 ({ex.GetType().Name}): {ex.Message}");
            }
        }

        private static async Task TestLogin()
        {
            PrintSection("测试 2: 验证验证码并自动登录");

            Console.Write("请输入手机号: ");
            var mobile = Console.ReadLine()?.Trim();
            Console.Write("请输入6位验证码: ");
            var code = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(mobile) || string.IsNullOrWhiteSpace(code))
            {
                PrintWarning("手机号和验证码不能为空。");
                return;
            }

            // Step 2: 校验验证码
            PrintInfo("[Step 2/3] 校验验证码 (VerifyCodeAsync)...");
            try
            {
                var session = await Client.VerifyCodeAsync(mobile, code);
                PrintSuccess($"验证通过！用户: {session.RealName} (UID: {session.Uid})");
                PrintInfo($"  TGT: {Truncate(session.Tgt, 40)}...");
                PrintInfo($"  Token: {Truncate(session.Token, 40)}...");
            }
            catch (Up366Exception ex)
            {
                PrintError($"[VerifyCodeAsync] 校验验证码失败 | Code={ex.ErrorCode}, Message={ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                PrintError($"[VerifyCodeAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            // Step 3: 自动登录换取 Cookie
            PrintInfo("[Step 3/3] 换取持久会话 Cookie (AutoLoginAsync)...");
            try
            {
                var session = await Client.AutoLoginAsync();

                var hasUp366C = !string.IsNullOrEmpty(session.Up366C);
                if (!hasUp366C)
                {
                    PrintError("[AutoLoginAsync] 服务端未返回 UP366-C Cookie！");
                    PrintWarning("  请修改 Up366Net.cs，从 _cookieContainer.GetCookies() 读取");
                    return;
                }

                PrintSuccess("登录完成！");
                PrintInfo($"  UP366-C: 已获取 ({session.Up366C!.Length} chars)");

                // 自动保存会话
                PrintInfo("\n→ 自动保存会话到本地...");
                await SaveSessionAsync();
            }
            catch (Exception ex)
            {
                PrintError($"[AutoLoginAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestGetClassList()
        {
            PrintSection("测试 3: 获取班级列表 (GetClassListAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录（IsAuthenticated=false）");
                if (File.Exists(SessionFilePath))
                {
                    PrintInfo("检测到本地会话文件，建议执行 L 加载，或执行 2 重新登录");
                }
                else
                {
                    PrintInfo("请先执行选项 2 登录");
                }
                return;
            }

            try
            {
                var classes = await Client.GetClassListAsync();
                if (classes.Count == 0)
                {
                    PrintWarning("未找到任何班级。");
                    return;
                }

                PrintSuccess($"共 {classes.Count} 个班级:");
                PrintRow("CourseId", "班级名称", "教师", "学校/机构");
                PrintSeparator();
                foreach (var c in classes)
                {
                    PrintRow(c.CourseId.ToString(), c.CourseName, c.TeacherName, c.OrganName);
                }
            }
            catch (Up366Exception ex)
            {
                PrintError($"[GetClassListAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message}");
                if (ex.ErrorCode == 401 || ex.Message.Contains("登录") || ex.Message.Contains("认证"))
                {
                    PrintWarning("💡 会话可能已过期，请尝试重新登录（选项2）或清除本地会话（选项C）");
                }
            }
            catch (Exception ex)
            {
                PrintError($"[GetClassListAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestGetClassStats()
        {
            PrintSection("测试 4: 获取班级任务统计 (GetClassStatsAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录，请先执行选项 2 登录或 L 加载本地会话。");
                return;
            }

            try
            {
                var stats = await Client.GetClassStatsAsync();
                if (stats.Count == 0)
                {
                    PrintWarning("无统计数据。");
                    return;
                }

                PrintSuccess($"共 {stats.Count} 条统计:");
                PrintRow("CourseId", "总任务", "教材任务", "测验", "错题本");
                PrintSeparator();
                foreach (var s in stats)
                {
                    PrintRow(s.CourseId.ToString(), s.TaskNum.ToString(), s.BookTaskNum.ToString(),
                             s.TestNum.ToString(), s.WrongNoteNum.ToString());
                }
            }
            catch (Up366Exception ex)
            {
                PrintError($"[GetClassStatsAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message}");
            }
            catch (Exception ex)
            {
                PrintError($"[GetClassStatsAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestGetJobList()
        {
            PrintSection("测试 5: 获取作业列表 (GetJobListAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录，请先执行选项 2 登录或 L 加载本地会话。");
                return;
            }

            Console.Write("请输入班级 CourseId: ");
            if (!long.TryParse(Console.ReadLine()?.Trim(), out var courseId))
            {
                PrintWarning("无效的 CourseId。");
                return;
            }

            Console.Write("仅显示有效期内的作业? (直接回车=Y/n): ");
            var onlyValid = Console.ReadLine()?.Trim().ToLower() != "n";

            PrintInfo($"请求班级 {courseId} 的作业 (onlyValid={onlyValid})...");

            try
            {
                var jobs = await Client.GetJobListAsync(courseId, onlyValid);

                if (jobs.Count == 0)
                {
                    PrintWarning("该班级下没有符合条件的作业。");
                    return;
                }

                PrintSuccess($"找到 {jobs.Count} 个作业:");
                Console.WriteLine($"{"序号",-4} │ {"JobId",-18} │ {"作业名称",-20} │ {"开始时间",-16} │ {"截止时间",-16} │ {"BookId",-14} │ {"ContentId(TaskId)",-18}");
                PrintSeparator();

                for (int idx = 0; idx < jobs.Count; idx++)
                {
                    var j = jobs[idx];
                    var start = FromMs(j.AriseTime).ToString("yyyy-MM-dd HH:mm");
                    var end = FromMs(j.EndTime).ToString("yyyy-MM-dd HH:mm");
                    Console.WriteLine(
                        $"{idx,-4} │ {Truncate(j.JobId, 18),-18} │ {Truncate(j.JobName, 20),-20} │ {start,-16} │ {end,-16} │ {j.ExtParam?.BookId ?? "N/A",-14} │ {j.ContentId ?? "N/A",-18}");
                }

                Console.WriteLine();
                PrintInfo("提示: 使用选项6测试资源链时，需要输入上表中的 BookId 和 ContentId（即 TaskId）");
                PrintInfo("      使用选项8下载时，需要输入选项6提取出的 PC FileId");
                var first = jobs.FirstOrDefault();
                if (first != null)
                {
                    PrintDark($"  示例 BookId: {first.ExtParam?.BookId}");
                    PrintDark($"  示例 TaskId:  {first.ContentId}");
                }
            }
            catch (Up366Exception ex)
            {
                PrintError($"[GetJobListAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message} | 参数: courseId={courseId}");
            }
            catch (Exception ex)
            {
                PrintError($"[GetJobListAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestGetResourceChain()
        {
            PrintSection("测试 6: 获取作业资源链 (GetTaskLinkedAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录，请先执行选项 2 登录或 L 加载本地会话。");
                return;
            }

            Console.Write("请输入 BookId: ");
            var bookId = Console.ReadLine()?.Trim();
            Console.Write("请输入 TaskId (contentId): ");
            var taskId = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(taskId))
            {
                PrintWarning("BookId 和 TaskId 不能为空。");
                return;
            }

            PrintInfo($"请求资源链... | 参数: bookId={bookId}, taskId={taskId}");

            try
            {
                var chain = await Client.GetTaskLinkedAsync(bookId, taskId);

                if (chain?.Chapters == null || chain.Chapters.Count == 0)
                {
                    PrintWarning("资源链为空或缺少章节数据。");
                    return;
                }

                PrintInfo($"  教材: {chain.Book?.Name} ({chain.Book?.Id})");
                PrintInfo($"  章节数: {chain.Chapters?.Count ?? 0}\n");

                PrintRow("ChapterId", "章节名称", "IsContent", "PC FileId");
                PrintSeparator();
                foreach (var ch in chain.Chapters)
                {
                    var flag = ch.IsContent == 1 ? "✓" : "";
                    PrintRow(ch.Id, ch.Name, flag, ch.PcFileId ?? "(none)");
                }

                PrintSuccess("获取成功！");
                Console.WriteLine($"教材: {chain.Book?.Name} ({chain.Book?.Id})");
                Console.WriteLine($"章节数: {chain.Chapters.Count}");

                PrintRow("ChapterId", "章节名称", "IsContent", "PC FileId");
                PrintSeparator();
                foreach (var ch in chain.Chapters)
                {
                    var flag = ch.IsContent == 1 ? "✓" : "";
                    PrintRow(ch.Id, ch.Name, flag, ch.PcFileId ?? "(none)");
                }

                var pcFileId = Client.ExtractPcFileId(chain);
                if (!string.IsNullOrEmpty(pcFileId))
                {
                    Console.WriteLine();
                    PrintSuccess($"自动提取到 PC FileId: {pcFileId}");
                    PrintInfo("可执行选项 7 或 8 进行下载测试。");
                }
                else
                {
                    PrintWarning("未能自动提取到 PC FileId（可能 isContent 标记不符）。");
                }
            }
            catch (Up366Exception ex)
            {
                PrintError($"[GetTaskLinkedAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message}");
            }
            catch (Exception ex)
            {
                PrintError($"[GetTaskLinkedAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestGetDownloadUrl()
        {
            PrintSection("测试 7: 获取 CDN 下载直链 (GetDownloadUrlAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录，请先执行选项 2 登录或 L 加载本地会话。");
                return;
            }

            Console.Write("请输入 PC FileId: ");
            var pcFileId = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(pcFileId))
            {
                PrintWarning("PC FileId 不能为空。");
                return;
            }

            PrintInfo($"正在请求 302 跳转地址... | 参数: pcFileId={pcFileId}");

            try
            {
                var url = await Client.GetDownloadUrlAsync(pcFileId);
                PrintSuccess("获取成功！CDN 直链:");
                Console.WriteLine(url);
                Console.WriteLine();
                PrintInfo("提示: 此链接通常无需任何 Cookie 即可直接下载。");
            }
            catch (Up366Exception ex)
            {
                PrintError($"[GetDownloadUrlAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message}");
            }
            catch (Exception ex)
            {
                PrintError($"[GetDownloadUrlAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task TestDownloadAndExtract()
        {
            PrintSection("测试 8: 下载并解压 ZIP (DownloadAndExtractAsync)");

            if (!Client.IsAuthenticated)
            {
                PrintWarning("当前未登录，请先执行选项 2 登录或 L 加载本地会话。");
                return;
            }

            Console.Write("请输入 PC FileId: ");
            var pcFileId = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(pcFileId))
            {
                PrintWarning("PC FileId 不能为空。");
                return;
            }

            Console.Write("保存目录 (默认 ./downloads/): ");
            var saveDir = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(saveDir)) saveDir = "./downloads";

            PrintInfo($"正在下载并解压... | 参数: pcFileId={pcFileId}, saveDir={saveDir}");

            try
            {
                var entries = await Client.DownloadAndExtractAsync(pcFileId);

                if (entries.Count == 0)
                {
                    PrintWarning("ZIP 包为空或不含文件。");
                    return;
                }

                PrintSuccess($"解压成功！共 {entries.Count} 个文件。");

                if (!Directory.Exists(saveDir))
                    Directory.CreateDirectory(saveDir);

                foreach (var entry in entries)
                {
                    var relativePath = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.Combine(saveDir, relativePath);
                    var dir = Path.GetDirectoryName(fullPath);


                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllBytesAsync(fullPath, entry.Content);
                    Console.WriteLine($"  💾 {entry.FileName} ({entry.Content.Length,8} bytes)");
                }

                PrintSuccess($"文件已保存至: {Path.GetFullPath(saveDir)}");
            }
            catch (Up366Exception ex)
            {
                PrintError($"[DownloadAndExtractAsync] API 异常 | Code={ex.ErrorCode}, Message={ex.Message}");
            }
            catch (InvalidDataException ex)
            {
                PrintError($"[DownloadAndExtractAsync] 文件格式错误 | {ex.Message}");
            }
            catch (Exception ex)
            {
                PrintError($"[DownloadAndExtractAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        private static async Task ParseQuestionDataAsync()
        {
            PrintSection("测试 9: 解析题目数据 (ParseQuestionDataAsync)");

            // 询问用户输入题目文件夹路径
            Console.Write($"请输入题目文件夹路径 (默认 {GetLatestQuestionsPathFromDownloads()}): ");
            var inputPath = Console.ReadLine()?.Trim();

            var questionsPath = string.IsNullOrWhiteSpace(inputPath)
                ? GetLatestQuestionsPathFromDownloads()
                : inputPath;

            // 如果用户输入的是上级目录（如 ./downloads/2），自动补全到 questions
            if (!questionsPath.EndsWith("questions", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(questionsPath, "questions")))
            {
                questionsPath = Path.Combine(questionsPath, "questions");
            }


            if (!Directory.Exists(questionsPath))
            {
                PrintError($"目录不存在: {Path.GetFullPath(questionsPath)}");
                PrintInfo("提示: 先执行选项 8 或 10 下载作业文件，或手动输入正确的 questions 文件夹路径");
                return;
            }

            PrintInfo($"正在解析目录: {Path.GetFullPath(questionsPath)}...");

            try
            {
                var questions = ListeningQuestionParser.ParseQuestionsFolder(questionsPath);

                if (questions.Count == 0)
                {
                    PrintWarning("未找到任何题目数据。请确保路径下包含以 MD5 命名的子文件夹。");
                    return;
                }

                PrintSuccess($"共解析 {questions.Count} 道题目\n");

                foreach (var q in questions.OrderBy(x => x.QuestionNumber))
                {
                    if (q.IsComposite)
                    {
                        // ===== 复合题（长对话/独白）=====
                        PrintCompositeQuestion(q);
                    }
                    else
                    {
                        // ===== 简单题（单题）=====
                        PrintSimpleQuestion(q);
                    }
                }

                // 汇总统计
                PrintSection("解析统计");
                var simpleCount = questions.Count(q => !q.IsComposite);
                var compositeCount = questions.Count(q => q.IsComposite);
                var withAudio = questions.Count(q => q.AudioFilePath != null);

                Console.WriteLine($"  总题数: {questions.Count} (简单题 {simpleCount} + 复合题 {compositeCount})");
                Console.WriteLine($"  有音频: {withAudio}/{questions.Count}");

                // 计算总小题数
                var totalSubQuestions = questions.Sum(q => q.SubQuestions?.Count ?? 1);
                Console.WriteLine($"  总小题数: {totalSubQuestions}");

                // 按知识点分组（简单题根知识点 + 子题知识点）
                var knowledgeList = new List<string>();
                foreach (var q in questions)
                {
                    if (!string.IsNullOrEmpty(q.Knowledge))
                        knowledgeList.Add(q.Knowledge);
                    foreach (var sub in q.SubQuestions)
                    {
                        if (!string.IsNullOrEmpty(sub.Knowledge))
                            knowledgeList.Add(sub.Knowledge);
                    }
                }

                var knowledgeGroups = knowledgeList
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .GroupBy(k => k)
                    .Select(g => new { Knowledge = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToList();

                if (knowledgeGroups.Count > 0)
                {
                    Console.WriteLine("\n  知识点分布:");
                    foreach (var g in knowledgeGroups)
                    {
                        Console.WriteLine($"    - {g.Knowledge}: {g.Count}题");
                    }
                }

                // 询问是否导出
                Console.Write("\n是否导出为文本文件? (y/N): ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    await ExportQuestionsToFileAsync(questions);
                }
            }
            catch (Exception ex)
            {
                PrintError($"[ParseQuestionDataAsync] 异常 ({ex.GetType().Name}) | {ex.Message}");
                if (ex.InnerException != null)
                    PrintError($"  └─> {ex.InnerException.Message}");
            }
        }

        /// <summary>
        /// 打印简单题（单题）
        /// </summary>
        private static void PrintSimpleQuestion(ListeningQuestion q)
        {
            // 打印分隔线
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╔══════════ 第 {q.QuestionNumber} 题 ══════════");
            Console.ResetColor();

            // 题目信息
            Console.WriteLine($"  题型: {q.Knowledge} | 分值: {q.Score}");
            Console.WriteLine($"  问题: {q.QuestionText}");

            // 选项
            Console.WriteLine("\n  选项:");
            foreach (var opt in q.Options)
            {
                if (opt.IsCorrect)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"    ★ {opt.Id}. {opt.Content}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"      {opt.Id}. {opt.Content}");
                }
            }

            // 答案
            Console.WriteLine($"\n  答案: {q.Answer}");

            // 原文
            PrintTranscript(q.FullTranscript);

            // 解析
            if (!string.IsNullOrWhiteSpace(q.Analysis))
            {
                Console.WriteLine($"\n  解析: {q.Analysis}");
            }

            // 音频文件状态
            PrintAudioStatus(q);
            Console.WriteLine();
        }

        /// <summary>
        /// 打印复合题（长对话/独白含多个子题）
        /// </summary>
        private static void PrintCompositeQuestion(ListeningQuestion q)
        {
            var displayNum = q.GetDisplayNumber();

            // 打印分隔线
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╔══════════ 第 {q.QuestionNumber} 题 [复合题 {displayNum}] ══════════");
            Console.ResetColor();

            // 引导语
            Console.WriteLine($"  引导: {q.CompositeMainText}");
            Console.WriteLine($"  总分值: {q.Score} | 子题数: {q.SubQuestions.Count}");

            // 打印每个子题
            for (int i = 0; i < q.SubQuestions.Count; i++)
            {
                var sub = q.SubQuestions[i];
                var subNum = q.SubQuestionStartNumber.HasValue
                    ? q.SubQuestionStartNumber.Value + i
                    : i + 1;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  ┌── 小题 {subNum} ──");
                Console.ResetColor();

                Console.WriteLine($"  │ 题型: {sub.Knowledge} | 分值: {sub.Score}");
                Console.WriteLine($"  │ 问题: {sub.QuestionText}");

                // 子题选项
                Console.WriteLine("  │ 选项:");
                foreach (var opt in sub.Options)
                {
                    if (opt.IsCorrect)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  │   ★ {opt.Id}. {opt.Content}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"  │     {opt.Id}. {opt.Content}");
                    }
                }

                Console.WriteLine($"  │ 答案: {sub.Answer}");

                if (!string.IsNullOrWhiteSpace(sub.Analysis))
                {
                    Console.WriteLine($"  │ 解析: {sub.Analysis}");
                }
            }

            // 原文（复合题共享同一段原文）
            Console.WriteLine("\n  听力原文:");
            PrintTranscript(q.FullTranscript);

            // 音频文件状态
            PrintAudioStatus(q);
            Console.WriteLine();
        }

        /// <summary>
        /// 打印原文（带关键句标记）
        /// </summary>
        private static void PrintTranscript(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
                return;

            Console.WriteLine("\n  听力原文:");
            var lines = transcript.Split('\n');
            foreach (var line in lines)
            {
                // 包含题号标记的行（如 (8), (8)(9)）用高亮显示
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\(\d+\)"))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"    {line}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"    {line}");
                }
            }
        }
        /// <summary>
        /// 打印音频文件状态
        /// </summary>
        private static void PrintAudioStatus(ListeningQuestion q)
        {
            var audioStatus = q.AudioFilePath != null
                ? $"✓ 音频就绪 ({Path.GetFileName(q.AudioFilePath)})"
                : "✗ 音频文件缺失";
            PrintDark($"  [{audioStatus}]");
        }

        /// <summary>
        /// 将解析的题目导出为文本文件
        /// </summary>
        private static async Task ExportQuestionsToFileAsync(List<ListeningQuestion> questions)
        {
            try
            {
                var exportDir = @"./exports";
                if (!Directory.Exists(exportDir))
                    Directory.CreateDirectory(exportDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"questions_{timestamp}.txt";
                var filePath = Path.Combine(exportDir, fileName);

                using var writer = new StreamWriter(filePath);

                await writer.WriteLineAsync("========================================");
                await writer.WriteLineAsync("  天学网听力题目解析报告");
                await writer.WriteLineAsync($"  生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await writer.WriteLineAsync($"  总题数: {questions.Count}");

                var simpleCount = questions.Count(q => !q.IsComposite);
                var compositeCount = questions.Count(q => q.IsComposite);
                var totalSub = questions.Sum(q => q.SubQuestions?.Count ?? 1);

                await writer.WriteLineAsync($"  简单题: {simpleCount} | 复合题: {compositeCount}");
                await writer.WriteLineAsync($"  总小题数: {totalSub}");
                await writer.WriteLineAsync("========================================\n");

                foreach (var q in questions.OrderBy(x => x.QuestionNumber))
                {
                    if (q.IsComposite)
                    {
                        // 导出复合题
                        await writer.WriteLineAsync($"【第 {q.QuestionNumber} 题 - 复合题 {q.GetDisplayNumber()}】");
                        await writer.WriteLineAsync($"引导语: {q.CompositeMainText}");
                        await writer.WriteLineAsync($"总分值: {q.Score} | 子题数: {q.SubQuestions.Count}");
                        await writer.WriteLineAsync();

                        for (int i = 0; i < q.SubQuestions.Count; i++)
                        {
                            var sub = q.SubQuestions[i];
                            var subNum = q.SubQuestionStartNumber.HasValue
                                ? q.SubQuestionStartNumber.Value + i
                                : i + 1;

                            await writer.WriteLineAsync($"  小题 {subNum}:");
                            await writer.WriteLineAsync($"  题型: {sub.Knowledge} | 分值: {sub.Score}");
                            await writer.WriteLineAsync($"  问题: {sub.QuestionText}");

                            await writer.WriteLineAsync("  选项:");
                            foreach (var opt in sub.Options)
                            {
                                var marker = opt.IsCorrect ? " [正确答案]" : "";
                                await writer.WriteLineAsync($"    {opt.Id}. {opt.Content}{marker}");
                            }

                            await writer.WriteLineAsync($"  答案: {sub.Answer}");

                            if (!string.IsNullOrWhiteSpace(sub.Analysis))
                            {
                                await writer.WriteLineAsync($"  解析: {sub.Analysis}");
                            }
                            await writer.WriteLineAsync();
                        }
                    }
                    else
                    {
                        // 导出简单题
                        await writer.WriteLineAsync($"【第 {q.QuestionNumber} 题】");
                        await writer.WriteLineAsync($"题型: {q.Knowledge} | 分值: {q.Score}");
                        await writer.WriteLineAsync($"问题: {q.QuestionText}");
                        await writer.WriteLineAsync();

                        await writer.WriteLineAsync("选项:");
                        foreach (var opt in q.Options)
                        {
                            var marker = opt.IsCorrect ? " [正确答案]" : "";
                            await writer.WriteLineAsync($"  {opt.Id}. {opt.Content}{marker}");
                        }
                        await writer.WriteLineAsync();

                        await writer.WriteLineAsync($"答案: {q.Answer}");
                    }

                    // 原文（简单题和复合题都输出）
                    await writer.WriteLineAsync("听力原文:");
                    await writer.WriteLineAsync(q.FullTranscript);
                    await writer.WriteLineAsync();

                    if (!string.IsNullOrWhiteSpace(q.Analysis))
                    {
                        await writer.WriteLineAsync("解析:");
                        await writer.WriteLineAsync($"  {q.Analysis}");
                        await writer.WriteLineAsync();
                    }

                    await writer.WriteLineAsync($"音频文件: {q.MediaFile}");
                    await writer.WriteLineAsync("----------------------------------------\n");
                }

                PrintSuccess($"题目已导出到: {Path.GetFullPath(filePath)}");
            }
            catch (Exception ex)
            {
                PrintError($"导出失败: {ex.Message}");
            }
        }
        private static async Task TestEndToEnd()
        {
            PrintSection("测试 10: 端到端向导 (完整流程)");
            PrintInfo("此向导将带你完成: 发送验证码 → 登录 → 选班 → 选作业 → 提取资源 → 下载");

            // ==================== 登录流程 ====================
            if (!Client.IsAuthenticated)
            {
                Console.Write("\n请输入手机号: ");
                var mobile = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(mobile))
                {
                    PrintWarning("手机号不能为空，中断。");
                    return;
                }

                // Step 1: 发送验证码
                PrintInfo($"\n→ [1/6] 正在向 {mobile} 发送验证码...");
                try
                {
                    var (sendOk, _, sendCode, sendMsg) = await SendVerifyCodeWithDiagnosticsAsync(mobile);

                    if (!sendOk)
                    {
                        PrintError($"[向导-发送验证码] 失败: {sendMsg} (Code: {sendCode})");
                        switch (sendCode)
                        {
                            case 3: PrintWarning("💡 该手机号今日已达10次上限，请明日再试"); break;
                            case 2: PrintWarning("💡 发送过于频繁，请等待60秒后重试"); break;
                        }
                        return;
                    }

                    PrintSuccess("验证码发送成功！");
                }
                catch (Exception ex)
                {
                    PrintError($"[向导-发送验证码] 异常 ({ex.GetType().Name}) | {ex.Message}");
                    return;
                }

                // Step 2: 输入验证码
                Console.Write("\n请输入收到的6位验证码: ");
                var verifyCode = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(verifyCode) || verifyCode.Length != 6)
                {
                    PrintWarning("请输入有效的6位验证码，中断。");
                    return;
                }

                // Step 3: 验证并登录
                PrintInfo("\n→ [2/6] 正在验证验证码并登录...");
                try
                {
                    await Client.VerifyCodeAsync(mobile, verifyCode);
                    await Client.AutoLoginAsync();

                    if (!Client.IsAuthenticated)
                    {
                        PrintError("[向导-登录] 登录后 IsAuthenticated 仍为 false！");
                        return;
                    }

                    PrintSuccess($"登录成功！欢迎，{Client.Session?.RealName}");

                    // 自动保存
                    PrintInfo("→ 自动保存会话...");
                    await SaveSessionAsync();
                }
                catch (Exception ex)
                {
                    PrintError($"[向导-登录] 异常 ({ex.GetType().Name}) | {ex.Message}");
                    return;
                }
            }
            else
            {
                PrintInfo($"\n→ [2/6] 已登录用户: {Client.Session?.RealName}，跳过登录步骤");
            }

            // ==================== 选择班级 ====================
            PrintInfo("\n→ [3/6] 获取班级列表...");
            var classes = new List<ClassInfo>();
            try
            {
                classes = await Client.GetClassListAsync();
                if (classes.Count == 0)
                {
                    PrintWarning("未加入任何班级，测试结束");
                    return;
                }

                for (int i = 0; i < classes.Count; i++)
                {
                    var stat = await GetClassTaskCountSafe(classes[i].CourseId);
                    var marker = stat > 0 ? "★" : " ";
                    Console.WriteLine($"  [{i}] {marker} {classes[i].CourseName} (ID:{classes[i].CourseId}, 作业:{stat})");
                }
            }
            catch (Exception ex)
            {
                PrintError($"[向导-获取班级] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            Console.Write("\n请选择班级索引: ");
            if (!int.TryParse(Console.ReadLine(), out var cIdx) || cIdx < 0 || cIdx >= classes.Count)
            {
                PrintWarning("无效选择，中断");
                return;
            }

            var cls = classes[cIdx];
            PrintSuccess($"已选择班级: {cls.CourseName} (ID: {cls.CourseId})");

            // ==================== 获取作业 ====================
            PrintInfo($"\n→ [4/6] 正在获取 [{cls.CourseName}] 的作业列表...");
            var jobs = new List<JobInfo>();
            try
            {
                jobs = await Client.GetJobListAsync(cls.CourseId, onlyValid: true);
                if (jobs.Count == 0)
                {
                    PrintWarning("该班级暂无有效作业，测试结束");
                    return;
                }

                // 输出表格，包含索引、JobId、BookId、ContentId、作业名称、截止时间，便于一次看到所有关键参数
                Console.WriteLine($"{"Index",-5} │ {"JobId",-20} │ {"BookId",-14} │ {"ContentId",-18} │ {"作业名称",-30} │ {"截止时间",-16}");
                Console.WriteLine(new string('─', 110));
                for (int i = 0; i < jobs.Count; i++)
                {
                    var ji = jobs[i];
                    var end = DateTimeOffset.FromUnixTimeMilliseconds(ji.EndTime).LocalDateTime;
                    var bookIdDisplay = ji.ExtParam?.BookId ?? "N/A";
                    var contentDisplay = ji.ContentId ?? "N/A";

                    Console.WriteLine($"{i,-5} │ {Truncate(ji.JobId,20),-20} │ {Truncate(bookIdDisplay,14),-14} │ {Truncate(contentDisplay,18),-18} │ {Truncate(ji.JobName,30),-30} │ {end:yyyy-MM-dd HH:mm}");
                }
            }
            catch (Exception ex)
            {
                PrintError($"[向导-获取作业] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            Console.Write("\n请选择作业索引: ");
            if (!int.TryParse(Console.ReadLine(), out var jIdx) || jIdx < 0 || jIdx >= jobs.Count)
            {
                PrintWarning("无效选择，中断");
                return;
            }

            var job = jobs[jIdx];
            var bookId = job.ExtParam?.BookId;
            var taskId = job.ContentId;

            PrintSuccess($"已选择作业: {job.JobName}");
            // 一步到位：输出选中作业的关键参数，便于复制使用
            PrintInfo($"选中参数 -> CourseId: {cls.CourseId} | JobId: {Truncate(job.JobId,40)} | BookId: {bookId ?? "N/A"} | ContentId: {taskId ?? "N/A"}");

            if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(taskId))
            {
                PrintError($"[向导] 作业数据不完整 | BookId={bookId}, ContentId={taskId}，无法继续");
                return;
            }

            // ==================== 获取资源链 ====================
            PrintInfo($"\n→ [5/6] 获取资源链 (BookId={bookId}, TaskId={taskId})...");
            string pcFileId;
            try
            {
                var chain = await Client.GetTaskLinkedAsync(bookId, taskId);
                pcFileId = Client.ExtractPcFileId(chain);

                PrintSuccess("获取资源链成功！");
                PrintInfo($"  教材: {chain.Book?.Name} ({chain.Book?.Id})");
                PrintInfo($"  章节数: {chain.Chapters?.Count ?? 0}");

                if (string.IsNullOrEmpty(pcFileId))
                {
                    PrintError("[向导-资源链] 未提取到 PC FileId，可能该作业无 PC 端文件");
                    return;
                }
                PrintSuccess($"  自动提取 PC FileId: {pcFileId}");
                PrintInfo("  下一步将获取 CDN 直链并下载");
            }
            catch (Exception ex)
            {
                PrintError($"[向导-资源链] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            // ==================== 获取下载链接 ====================
            PrintInfo("\n→ [6/6] 获取 CDN 下载链接...");
            string url;
            try
            {
                url = await Client.GetDownloadUrlAsync(pcFileId);
                PrintSuccess($"CDN 直链: {Truncate(url, 60)}...");
            }
            catch (Exception ex)
            {
                PrintError($"[向导-获取下载链接] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            // ==================== 下载文件 ====================
            Console.Write("\n是否立即下载并解压保存? (y/N): ");
            if (Console.ReadLine()?.Trim().ToLower() != "y")
            {
                PrintInfo("已跳过下载，向导完成");
                return;
            }

            PrintInfo("正在下载并解压，请稍候...");
            List<ZipEntry> entries;
            try
            {
                entries = await Client.DownloadAndExtractAsync(pcFileId);
                if (entries.Count == 0)
                {
                    PrintWarning("ZIP 包为空");
                    return;
                }
                PrintSuccess($"下载成功！共 {entries.Count} 个文件");
            }
            catch (Exception ex)
            {
                PrintError($"[向导-下载] 异常 ({ex.GetType().Name}) | {ex.Message}");
                return;
            }

            // 保存到本地
            try
            {
                // 用作业名创建安全目录名
                var safeJobName = new string(job.JobName
                    .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                    .ToArray())
                    .Trim('_');
                if (string.IsNullOrWhiteSpace(safeJobName)) safeJobName = "未命名作业";
                if (safeJobName.Length > 40) safeJobName = safeJobName.Substring(0, 40);

                var folder = Path.Combine("downloads", $"{safeJobName}_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(folder);

                foreach (var e in entries)
                {
                    var safeName = e.FileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var path = Path.Combine(folder, safeName);
                    var dir = Path.GetDirectoryName(path);

                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllBytesAsync(path, e.Content);
                }

                PrintSuccess($"\n✓ 端到端测试完成！文件保存至: {Path.GetFullPath(folder)}");
                PrintInfo($"  共 {entries.Count} 个文件，总大小: {entries.Sum(e => e.Content.Length) / 1024.0:F2} KB");
            }
            catch (Exception ex)
            {
                PrintError($"[向导-保存文件] 异常 ({ex.GetType().Name}) | {ex.Message}");
            }
        }

        // 安全版：获取班级作业数量（带异常处理）
        private static async Task<int> GetClassTaskCountSafe(long courseId)
        {
            try
            {
                var stats = await Client.GetClassStatsAsync();
                return stats.FirstOrDefault(s => s.CourseId == courseId)?.TaskNum ?? 0;
            }
            catch (Exception ex)
            {
                PrintDark($"    [DEBUG] 获取班级 {courseId} 统计失败: {ex.Message}");
                return 0;
            }
        }

        private static void ShowSessionDetails()
        {
            PrintSection("Session 详情");

            var s = Client.Session;
            if (s == null)
            {
                PrintWarning("当前无会话。请先执行选项 2 登录，或 L 加载本地会话。");
                return;
            }

            PrintKv("UID", s.Uid.ToString());
            PrintKv("RealName", s.RealName);
            PrintKv("Username", s.Username);
            PrintKv("UUID", s.Uuid);
            PrintKv("OrganId", s.OrganId.ToString());
            PrintKv("ClientId", s.ClientId);
            PrintKv("Token", Truncate(s.Token, 60));
            PrintKv("Token 状态", string.IsNullOrEmpty(s.Token) ? "❌ 为空" : "✅ 已获取");
            PrintKv("TGT", Truncate(s.Tgt, 60));
            PrintKv("TGT 状态", string.IsNullOrEmpty(s.Tgt) ? "❌ 为空" : "✅ 已获取");
            PrintKv("UP366-C", Truncate(s.Up366C, 60));
            PrintKv("UP366-C 状态", string.IsNullOrEmpty(s.Up366C) ? "❌ 为空 (导致 IsAuthenticated=false)" : "✅ 已获取");
            PrintKv("CASTGC", Truncate(s.Castgc, 60));
            PrintKv("CASTGC 状态", string.IsNullOrEmpty(s.Castgc) ? "❌ 为空" : "✅ 已获取");

            // 显示本地文件状态
            if (File.Exists(SessionFilePath))
            {
                try
                {
                    var json = File.ReadAllText(SessionFilePath);
                    var data = JsonSerializer.Deserialize<SessionData>(json);
                    if (data != null)
                    {
                        var age = DateTime.Now - data.SavedAt;
                        PrintInfo($"\n本地会话文件: {SessionFilePath}");
                        PrintInfo($"  保存用户: {data.RealName} (UID: {data.Uid})");
                        PrintInfo($"  保存时间: {data.SavedAt:yyyy-MM-dd HH:mm:ss} ({age.TotalHours:F1} 小时前)");
                        PrintInfo($"  文件大小: {new FileInfo(SessionFilePath).Length} bytes");
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"读取本地会话文件失败: {ex.Message}");
                }
            }
            else
            {
                PrintWarning($"\n无本地会话文件: {SessionFilePath}");
            }
        }

        #endregion

        #region 诊断工具方法

        /// <summary>
        /// 诊断版发送验证码：带完整异常处理和原始响应捕获
        /// </summary>
        private static async Task<(bool Success, string RawResponse, int Code, string Msg)>
            SendVerifyCodeWithDiagnosticsAsync(string mobile)
        {
            const string apiEndpoint = "https://user-api.up366.cn/front/user/verify/send";
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.Add("User-Agent", "PC-Up366-Student 6.11.0");
            http.DefaultRequestHeaders.Add("x-app-name", "student-pc");
            http.DefaultRequestHeaders.Add("clientid", "7DE08EE71FBD3DA75A260946416B7188DBE077E4");
            http.DefaultRequestHeaders.Add("u3r", Guid.NewGuid().ToString("N"));
            http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN");
            http.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ut = "7a8fc3eea5d7ec1788ac457375c38718c6627502a42163c681a10c466d596683";

            var url = $"{apiEndpoint}?checkExists=2&mobile={Uri.EscapeDataString(mobile)}&ut={ut}";

            try
            {
                var response = await http.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (false, content, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                }

                try
                {
                    using var doc = JsonDocument.Parse(content);
                    var resultCode = doc.RootElement.GetProperty("result").GetProperty("code").GetInt32();
                    var resultMsg = doc.RootElement.GetProperty("result").GetProperty("msg").GetString() ?? "无消息";
                    return (resultCode == 0, content, resultCode, resultMsg);
                }
                catch (JsonException jsonEx)
                {
                    return (false, content, -999, $"JSON解析失败: {jsonEx.Message}");
                }
            }
            catch (HttpRequestException ex)
            {
                return (false, ex.Message, -999, $"网络请求失败: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                return (false, ex.Message, -999, $"请求超时: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message, -999, $"未知异常 ({ex.GetType().Name}): {ex.Message}");
            }
        }

        #endregion

        #region 工具方法

        private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

        /// <summary>
        /// 尝试从 ./downloads 目录中选择最新的子文件夹，并返回其下的 /2/questions 路径。
        /// 若无法找到则返回默认 "./downloads/2/questions"。
        /// </summary>
        private static string GetLatestQuestionsPathFromDownloads()
        {
            var defaultPath = @"./downloads/2/questions";
            try
            {
                var downloadsDir = Path.GetFullPath("./downloads");
                if (!Directory.Exists(downloadsDir))
                    return defaultPath;

                var candidateList = new List<(DirectoryInfo Dir, DateTime Time)>();

                foreach (var sub in Directory.GetDirectories(downloadsDir))
                {
                    try
                    {
                        var di = new DirectoryInfo(sub);

                        // 首选: 子目录下存在 2/questions
                        var qPath = Path.Combine(di.FullName, "2", "questions");
                        if (Directory.Exists(qPath))
                        {
                            candidateList.Add((di, di.LastWriteTimeUtc));
                            continue;
                        }

                        // 其次: 如果目录本身名为 "2" 且父目录包含 questions
                        if (di.Name == "2")
                        {
                            var parent = di.Parent;
                            if (parent != null)
                            {
                                var qp = Path.Combine(parent.FullName, "2", "questions");
                                if (Directory.Exists(qp))
                                {
                                    candidateList.Add((parent, parent.LastWriteTimeUtc));
                                    continue;
                                }
                            }
                        }
                    }
                    catch { /* ignore individual errors */ }
                }

                if (candidateList.Count == 0)
                {
                    // 最后尝试直接看 ./downloads/2/questions
                    var direct = Path.Combine(downloadsDir, "2", "questions");
                    if (Directory.Exists(direct)) return direct;
                    return defaultPath;
                }

                var latest = candidateList.OrderByDescending(x => x.Time).First().Dir;
                var latestQ = Path.Combine(latest.FullName, "2", "questions");
                if (Directory.Exists(latestQ)) return latestQ;

                return defaultPath;
            }
            catch
            {
                return defaultPath;
            }
        }

        private static string Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "N/A";
            return value.Length <= max ? value : value[..(max - 3)] + "...";
        }

        private static void PrintSection(string title)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"▶ {title}");
            Console.ResetColor();
            Console.WriteLine(new string('─', 68));
        }

        private static void PrintRow(string c1, string c2, string c3, string c4)
        {
            Console.WriteLine($"{Truncate(c1, 14),-14} │ {Truncate(c2, 22),-22} │ {Truncate(c3, 10),-10} │ {Truncate(c4, 16),-16}");
        }

        private static void PrintRow(string c1, string c2, string c3, string c4, string c5)
        {
            Console.WriteLine($"{Truncate(c1, 14),-14} │ {Truncate(c2, 10),-10} │ {Truncate(c3, 10),-10} │ {Truncate(c4, 10),-10} │ {Truncate(c5, 10),-10}");
        }

        private static void PrintSeparator() => Console.WriteLine(new string('─', 70));

        private static void PrintKv(string key, string? value)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{key,-12}: ");
            Console.ResetColor();
            Console.WriteLine(value ?? "(null)");
        }

        private static void PrintInfo(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"[i] {msg}");
            Console.ResetColor();
        }

        private static void PrintSuccess(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓] {msg}");
            Console.ResetColor();
        }

        private static void PrintWarning(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[!] {msg}");
            Console.ResetColor();
        }

        private static void PrintError(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] {msg}");
            Console.ResetColor();
        }

        private static void PrintDark(string msg)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        #endregion
    }
}