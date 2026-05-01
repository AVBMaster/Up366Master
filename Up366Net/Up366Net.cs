using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Up366Net
{
    /// <summary>
    /// 天学网 API 客户端
    /// </summary>
    public class Up366Client : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;
        private readonly HttpClientHandler _handler;
        private SessionContext _session;
        private static readonly string ClientId = "7DE08EE71FBD3DA75A260946416B7188DBE077E4";
        private readonly bool _includeOptionalLoggingHeaders;
        private static readonly string UserAgent = "PC-Up366-Student 6.11.0";
        private static readonly string LogFilePath;
        private static readonly object _logFileLock = new();

        static Up366Client()
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Up366Net", "logs");
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, $"autocomplete_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }

        public SessionContext Session => _session;
        public bool IsAuthenticated => _session?.Up366C != null;

        /// <summary>
        /// 请求/响应日志事件，订阅以在 UI 中显示
        /// </summary>
        public event Action<string> LogAdded;

        public Up366Client(HttpClient httpClient = null, bool includeOptionalLoggingHeaders = true)
        {
            _includeOptionalLoggingHeaders = includeOptionalLoggingHeaders;

            if (httpClient != null)
            {
                _httpClient = httpClient;
            }
            else
            {
                _cookieContainer = new CookieContainer();
                _handler = new HttpClientHandler
                {
                    CookieContainer = _cookieContainer,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                };
                _httpClient = new HttpClient(_handler);
            }

            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.Add("x-app-name", "student-pc");
            if (_includeOptionalLoggingHeaders)
            {
                _httpClient.DefaultRequestHeaders.Add("clientid", ClientId);
            }

            LogToFile($"[Init] 日志文件: {LogFilePath}");
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Up366Net] {message}");
            LogAdded?.Invoke(message);
            LogToFile(message);
        }

        private void LogToFile(string message)
        {
            try
            {
                lock (_logFileLock)
                {
                    var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                    File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private void LogException(Exception ex, string context = "")
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine($"[Error Context] {context}");
            }
            sb.AppendLine($"[Exception] {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine($"[StackTrace] {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                sb.AppendLine($"[InnerException] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                sb.AppendLine($"[InnerStackTrace] {ex.InnerException.StackTrace}");
            }

            var logMsg = sb.ToString();
            System.Diagnostics.Debug.WriteLine($"[Up366Net] {logMsg}");
            LogAdded?.Invoke(logMsg);
            LogToFile(logMsg);
        }

        #region Step 1: 发送验证码

        public async Task<bool> SendVerifyCodeAsync(string mobile, string secret = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mobile))
                throw new ArgumentException("手机号不能为空", nameof(mobile));

            // 使用固定的ut值（与CLI一致）
            var ut = "7a8fc3eea5d7ec1788ac457375c38718c6627502a42163c681a10c466d596683";

            var url = $"https://user-api.up366.cn/front/user/verify/send?checkExists=2&mobile={Uri.EscapeDataString(mobile)}&ut={ut}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));

            Log($"[SendVerifyCode] GET {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[SendVerifyCode] Response: {content}");
            var result = JsonSerializer.Deserialize<ApiResponse<object>>(content, GetJsonOptions());

            return result?.Result?.Code == 0;
        }

        #endregion

        #region Step 2: 校验验证码

        // 内部使用：用于已登录后的其他API调用
        private string GenerateUt(string mobile, long timestamp, string secret)
        {
            // 生成与时间戳相关的ut签名（非验证码场景使用）
            var input = $"{timestamp}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public async Task<SessionContext> VerifyCodeAsync(string mobile, string verifyCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mobile))
                throw new ArgumentException("手机号不能为空", nameof(mobile));
            if (string.IsNullOrWhiteSpace(verifyCode))
                throw new ArgumentException("验证码不能为空", nameof(verifyCode));

            var url = "https://sso.up366.cn/v2/code/tickets";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mobile"] = mobile,
                ["verifyCode"] = verifyCode
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));

            Log($"[VerifyCode] POST {url}");
            Log($"[VerifyCode] Body: mobile={mobile}&verifyCode={verifyCode}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[VerifyCode] Response: {responseContent}");
            var result = JsonSerializer.Deserialize<ApiResponse<VerifyCodeResponse>>(responseContent, GetJsonOptions());

            if (result?.Result?.Code != -26 && result?.Result?.Code != 0)
            {
                var msg = $"验证码校验失败: {result?.Result?.Msg}";
                LogException(new Up366Exception(msg, result?.Result?.Code ?? -1), "[VerifyCode]");
                throw new Up366Exception(msg, result?.Result?.Code ?? -1);
            }

            _session = new SessionContext
            {
                Tgt = result.Data.Tgt,
                Token = result.Data.Token,
                Uuid = result.Data.Uuid,
                Uid = result.Data.Uid,
                RealName = result.Data.Realname,
                Username = result.Data.Username,
                OrganId = result.Data.OrganId,
                ClientId = result.Data.ClientId
            };

            return _session;
        }

        #endregion

        #region Step 3: 自动登录

        public async Task<SessionContext> AutoLoginAsync(CancellationToken cancellationToken = default)
        {
            EnsureSession();

            var url = "https://sso.up366.cn/v2/auto/tickets";
            var ut = GenerateUt(string.Empty, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = _session.Token,
                ["ut"] = ut
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));

            if (!string.IsNullOrEmpty(_session.Tgt))
            {
                _cookieContainer.Add(new Uri("https://sso.up366.cn"), new Cookie("CASTGC", _session.Tgt));
            }

            Log($"[AutoLogin] POST {url}");
            Log($"[AutoLogin] Body: token={_session.Token?.Substring(0, Math.Min(20, _session.Token?.Length ?? 0))}...&ut={ut}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var ssoUri = new Uri("https://sso.up366.cn");
            var responseCookies = _cookieContainer.GetCookies(ssoUri);

            var up366Cookie = responseCookies["UP366-C"];
            var castgcCookie = responseCookies["CASTGC"];

            if (up366Cookie != null)
                _session.Up366C = up366Cookie.Value;
            if (castgcCookie != null)
                _session.Castgc = castgcCookie.Value;

            if (string.IsNullOrEmpty(_session.Up366C) && response.Headers.TryGetValues("Set-Cookie", out var rawCookies))
            {
                foreach (var cookie in rawCookies)
                {
                    if (string.IsNullOrEmpty(_session.Up366C) && cookie.Contains("UP366-C="))
                        _session.Up366C = ExtractCookieValue(cookie, "UP366-C");
                    if (string.IsNullOrEmpty(_session.Castgc) && cookie.Contains("CASTGC="))
                        _session.Castgc = ExtractCookieValue(cookie, "CASTGC");
                }
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[AutoLogin] Response: {responseContent}");
            var result = JsonSerializer.Deserialize<ApiResponse<VerifyCodeResponse>>(responseContent, GetJsonOptions());

            if (result?.Data != null)
            {
                _session.Tgt = result.Data.Tgt;
            }

            if (!string.IsNullOrEmpty(_session.Up366C))
            {
                _cookieContainer.Add(new Uri("https://course-api.up366.cn"), new Cookie("UP366-C", _session.Up366C));
                _cookieContainer.Add(new Uri("https://studytask-api.up366.cn"), new Cookie("UP366-C", _session.Up366C));
                _cookieContainer.Add(new Uri("https://book-api.up366.cn"), new Cookie("UP366-C", _session.Up366C));
                _cookieContainer.Add(new Uri("https://fs-v2.up366.cn"), new Cookie("UP366-C", _session.Up366C));
                _cookieContainer.Add(new Uri("https://study-api.up366.cn"), new Cookie("UP366-C", _session.Up366C));
                _cookieContainer.Add(new Uri("https://growth-api.up366.cn"), new Cookie("UP366-C", _session.Up366C));
            }

            if (!string.IsNullOrEmpty(_session.Castgc))
            {
                _cookieContainer.Add(new Uri("https://course-api.up366.cn"), new Cookie("CASTGC", _session.Castgc));
                _cookieContainer.Add(new Uri("https://studytask-api.up366.cn"), new Cookie("CASTGC", _session.Castgc));
                _cookieContainer.Add(new Uri("https://book-api.up366.cn"), new Cookie("CASTGC", _session.Castgc));
                _cookieContainer.Add(new Uri("https://fs-v2.up366.cn"), new Cookie("CASTGC", _session.Castgc));
                _cookieContainer.Add(new Uri("https://study-api.up366.cn"), new Cookie("CASTGC", _session.Castgc));
                _cookieContainer.Add(new Uri("https://growth-api.up366.cn"), new Cookie("CASTGC", _session.Castgc));
            }

            return _session;
        }

        private string ExtractCookieValue(string cookieHeader, string cookieName)
        {
            var prefix = $"{cookieName}=";
            var start = cookieHeader.IndexOf(prefix);
            if (start == -1) return null;
            start += prefix.Length;
            var end = cookieHeader.IndexOf(";", start);
            if (end == -1) end = cookieHeader.Length;
            return cookieHeader.Substring(start, end - start);
        }

        #endregion

        #region Step 4: 获取班级列表

        public async Task<List<ClassInfo>> GetClassListAsync(CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = "https://course-api.up366.cn/client/course/list-of-student/v2";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["status"] = "1",
                ["pager.pageSize"] = "999"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            Log($"[GetClassList] POST {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[GetClassList] Response length: {responseContent.Length}");
            var result = JsonSerializer.Deserialize<ApiResponse<ClassListResponse>>(responseContent, GetJsonOptions());

            return result?.Data?.List ?? new List<ClassInfo>();
        }

        #endregion

        #region Step 5: 查询班级任务统计

        public async Task<List<ClassStat>> GetClassStatsAsync(CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = "https://studytask-api.up366.cn/client/student/course/stat/list";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["conditions"] = "1,2,3,4,5,6,7,8,9"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            Log($"[GetClassStats] POST {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[GetClassStats] Response length: {responseContent.Length}");
            var result = JsonSerializer.Deserialize<ApiResponse<List<ClassStat>>>(responseContent, GetJsonOptions());

            return result?.Data ?? new List<ClassStat>();
        }

        #endregion

        #region Step 6: 获取作业列表

        public async Task<List<JobInfo>> GetJobListAsync(long courseId, bool onlyValid = true, CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = "https://studytask-api.up366.cn/client/student/course/job";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["courseId"] = courseId.ToString(),
                ["pageIdFlag"] = "1",
                ["version"] = ""
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            Log($"[GetJobList] POST {url} | courseId={courseId}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[GetJobList] Response length: {responseContent.Length}");
            var result = JsonSerializer.Deserialize<ApiResponse<JobListResponse>>(responseContent, GetJsonOptions());

            var jobs = result?.Data?.AllTaskList ?? new List<JobInfo>();

            if (onlyValid)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                jobs = jobs.Where(j => j.AriseTime <= now && now <= j.EndTime).ToList();
            }

            return jobs;
        }

        #endregion

        #region Step 7: 获取作业资源链

        public async Task<ResourceChain> GetTaskLinkedAsync(string bookId, string taskId, CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrWhiteSpace(bookId))
                throw new ArgumentException("bookId不能为空", nameof(bookId));
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("taskId不能为空", nameof(taskId));

            var url = "https://book-api.up366.cn/client/task/linked";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["bookId"] = bookId,
                ["type"] = "1",
                ["taskId"] = taskId
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            Log($"[GetTaskLinked] POST {url} | bookId={bookId}, taskId={taskId}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[GetTaskLinked] Response length: {responseContent.Length}");
            var result = JsonSerializer.Deserialize<ApiResponse<ResourceChain>>(responseContent, GetJsonOptions());

            return result?.Data;
        }

        public string ExtractPcFileId(ResourceChain chain)
        {
            if (chain?.Chapters == null) return null;

            var contentChapter = chain.Chapters
                .FirstOrDefault(c => c.IsContent == 1 && !string.IsNullOrEmpty(c.PcFileId));

            return contentChapter?.PcFileId;
        }

        #endregion

        #region Step 8: 下载作业文件

        public async Task<string> GetDownloadUrlAsync(string pcFileId, CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrWhiteSpace(pcFileId))
                throw new ArgumentException("pcFileId不能为空", nameof(pcFileId));

            var url = $"https://fs-v2.up366.cn/download/{pcFileId}";

            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = _cookieContainer,
                AllowAutoRedirect = false
            };

            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            client.DefaultRequestHeaders.Add("x-app-name", "student-pc");
            if (_includeOptionalLoggingHeaders)
            {
                client.DefaultRequestHeaders.Add("clientid", ClientId);
                client.DefaultRequestHeaders.Add("u3r", Guid.NewGuid().ToString("N"));
                if (!string.IsNullOrEmpty(_session?.Tgt))
                {
                    client.DefaultRequestHeaders.Add("u3t", _session.Tgt);
                }
            }
            client.DefaultRequestHeaders.Add("x-requested-with", "PC");

            Log($"[GetDownloadUrl] GET {url}");
            var response = await client.GetAsync(url, cancellationToken);
            Log($"[GetDownloadUrl] Status: {response.StatusCode}");
            if (response.Headers.Location != null)
                Log($"[GetDownloadUrl] Location: {response.Headers.Location}");

            if (response.StatusCode == HttpStatusCode.Redirect ||
                response.StatusCode == HttpStatusCode.MovedPermanently ||
                response.StatusCode == HttpStatusCode.RedirectMethod)
            {
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                    return location;
            }

            var ex = new Up366Exception($"获取下载链接失败，状态码: {response.StatusCode}");
            LogException(ex, "[GetDownloadUrl]");
            throw ex;
        }

        public async Task<byte[]> DownloadFileAsync(string pcFileId, CancellationToken cancellationToken = default)
        {
            var cdnUrl = await GetDownloadUrlAsync(pcFileId, cancellationToken);

            using var client = new HttpClient();
            Log($"[DownloadFile] GET {cdnUrl}");
            return await client.GetByteArrayAsync(cdnUrl, cancellationToken);
        }

        public async Task<List<ZipEntry>> DownloadAndExtractAsync(string pcFileId, CancellationToken cancellationToken = default)
        {
            var bytes = await DownloadFileAsync(pcFileId, cancellationToken);
            var entries = new List<ZipEntry>();

            using var stream = new MemoryStream(bytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;

                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken);

                entries.Add(new ZipEntry
                {
                    FileName = entry.FullName,
                    Content = ms.ToArray()
                });
            }

            Log($"[DownloadAndExtract] Extracted {entries.Count} entries");
            return entries;
        }

        #endregion

        #region ★★★ 新增：phonetic-word/analysis 预检接口 ★★★

        public async Task<bool> GetPhoneticWordAnalysisAsync(string questionIds, CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrEmpty(questionIds)) return false;

            var url = $"https://growth-api.up366.cn/client/growth/listening/phonetic-word/analysis?questionIds={Uri.EscapeDataString(questionIds)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("x-requested-with", "PC");

            Log($"[PhoneticWordAnalysis] GET {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[PhoneticWordAnalysis] Response: {content}");

            return response.IsSuccessStatusCode;
        }

        #endregion

        #region ★★★ 新增：person-practice/status 状态刷新接口 ★★★

        public async Task<bool> GetPersonPracticeStatusAsync(string batchId, string courseId, CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrEmpty(batchId) || string.IsNullOrEmpty(courseId)) return false;

            var url = $"https://growth-api.up366.cn/client/growth/listening/person-practice/status?batchId={Uri.EscapeDataString(batchId)}&courseId={Uri.EscapeDataString(courseId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("x-requested-with", "PC");

            Log($"[PersonPracticeStatus] GET {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[PersonPracticeStatus] Response: {content}");

            return response.IsSuccessStatusCode;
        }

        #endregion

        #region Step 9: 提交作业评分（★★★ 完全重写，严格匹配抓包格式 ★★★）

        public async Task<bool> SubmitTaskScoreAsync(
            string taskId,
            string bookId,
            string chapterId,
            string courseId,
            string pageId,
            List<QuestionAnswer> questions,
            int durationMinutes = 18,
            int scorePercent = 95,
            string batchId = null,
            string studyTaskId = null,
            long? calibratedNow = null,  // ★ 新增：传入校准后的时间
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("taskId不能为空", nameof(taskId));
            if (string.IsNullOrWhiteSpace(bookId))
                throw new ArgumentException("bookId不能为空", nameof(bookId));
            if (questions == null || questions.Count == 0)
                throw new ArgumentException("题目列表不能为空", nameof(questions));

            var url = "https://study-api.up366.cn/client/task/score/submit/v2";

            // ★★★ 使用传入的校准时间或当前时间 ★★★
            var now = calibratedNow ?? GetCalibratedTime();
            var seconds = durationMinutes * 60;

            // 计算实际得分
            var totalScore = questions.Sum(q => q.FullScore);
            var userScore = questions.Where(q => q.IsCorrect).Sum(q => q.FullScore);

            var actualBatchId = batchId ?? Guid.NewGuid().ToString("N");
            var actualStudyTaskId = studyTaskId ?? taskId;

            // ★★★ 构造 qstJson，严格匹配抓包格式 ★★★
            var qstJson = new List<object>();
            int orderIdx = 1;

            // 先处理普通题目（question_type != 99）
            foreach (var q in questions.Where(q => q.QuestionType != 99))
            {
                // elementAttr 必须是 JSON 字符串，且字段名与抓包完全一致
                var elementAttrDict = new Dictionary<string, object>
                {
                    ["question_id"] = q.QuestionId ?? "",
                    ["question_type"] = q.QuestionType,
                    ["order"] = orderIdx,
                    ["user_score"] = q.IsCorrect ? q.FullScore : 0,
                    ["user_answer"] = q.UserAnswer ?? "",
                    ["answer"] = q.CorrectAnswer ?? "",
                    ["score"] = q.FullScore,
                    ["result"] = q.IsCorrect ? 1 : 2,
                    ["timestamp"] = q.Timestamp > 0 ? q.Timestamp : (now - Random.Shared.Next(5000, 60000)),
                    ["wrongnote_flag"] = 1
                };

                // 手动序列化，确保与抓包格式一致
                var elementAttrJson = JsonSerializer.Serialize(elementAttrDict, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null
                });

                qstJson.Add(new Dictionary<string, object>
                {
                    ["pageId"] = pageId ?? "",
                    ["elementId"] = q.ElementId ?? q.QuestionId ?? "",
                    ["elementType"] = 1,
                    ["elementAttr"] = elementAttrJson,
                    ["addTime"] = now - 16,
                    ["order"] = orderIdx
                });
                orderIdx++;
            }

            // 再处理特殊题目（question_type == 99），order从1开始
            foreach (var q in questions.Where(q => q.QuestionType == 99))
            {
                var elementAttrDict = new Dictionary<string, object>
                {
                    ["question_id"] = q.QuestionId ?? "",
                    ["question_type"] = q.QuestionType,
                    ["order"] = 1,
                    ["user_score"] = 0,
                    ["user_answer"] = "",
                    ["answer"] = "",
                    ["score"] = q.FullScore,
                    ["result"] = -1,
                    ["timestamp"] = q.Timestamp > 0 ? q.Timestamp : now,
                    ["wrongnote_flag"] = 1
                };

                var elementAttrJson = JsonSerializer.Serialize(elementAttrDict, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null
                });

                qstJson.Add(new Dictionary<string, object>
                {
                    ["pageId"] = pageId ?? "",
                    ["elementId"] = q.ElementId ?? q.QuestionId ?? "",
                    ["elementType"] = 1,
                    ["elementAttr"] = elementAttrJson,
                    ["addTime"] = now - 16,
                    ["order"] = 1
                });
            }

            // 构造 tasksJson - 严格匹配抓包
            var taskObj = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["taskId"] = taskId,
                ["uid"] = Session.Uid,
                ["bookId"] = bookId,
                ["chapterId"] = chapterId ?? "",
                ["taskNo"] = 1,
                ["score"] = userScore,
                ["percent"] = 100,
                ["studyDate"] = now,
                ["seconds"] = seconds,
                ["result"] = "",
                ["srcType"] = 1,
                ["batchTag"] = actualBatchId,
                ["uploaded"] = 0,
                ["msg"] = "",
                ["studyTaskId"] = actualStudyTaskId,
                ["courseId"] = courseId ?? "",
                ["extendParams"] = new Dictionary<string, string>
                {
                    ["batchId"] = actualBatchId
                },
                ["qstJson"] = qstJson,
                ["failed"] = 0,
                ["killStudyTask"] = null,
                ["submitType"] = null
            };

            var tasksJsonRaw = JsonSerializer.Serialize(new[] { taskObj }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            });

            var ut = "dbf593abb373c775628175e63c76df9af1ca9eb2ab6655f90c3330e420f84ed3"; //使用固定值即可

            Log("========== 提交作业评分请求 ==========");
            Log($"URL: {url}");
            Log($"服务器校准时间: {now}");
            Log($"本地时间: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            Log($"时间偏移: {_serverTimeOffset}ms");
            Log($"--- tasksJson (原始 JSON, 长度 {tasksJsonRaw.Length}) ---");
            Log(tasksJsonRaw.Length > 3000 ? tasksJsonRaw.Substring(0, 3000) + "... [截断]" : tasksJsonRaw);

            // ★★★ 使用 FormUrlEncodedContent 自动编码 ★★★
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["tasksJson"] = tasksJsonRaw,
                ["submitType"] = "0",
                ["ut"] = ut
            });

using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);

            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("x-requested-with", "PC");
            if (!string.IsNullOrEmpty(Session.Tgt))
                request.Headers.TryAddWithoutValidation("authorization", Session.Tgt);

            Log("========== 发送请求 ==========");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            Log($"HTTP Status: {(int)response.StatusCode} {response.StatusCode}");

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Log("========== 响应内容 ==========");
            Log(responseContent);
            Log("========== 请求结束 ==========");

            var result = JsonSerializer.Deserialize<ApiResponse<object>>(responseContent, GetJsonOptions());

            return result?.Result?.Code == 0;
        }
        public async Task<string> DownloadTaskQuestionsAsync(
            string bookId,
            string taskId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var chain = await GetTaskLinkedAsync(bookId, taskId, cancellationToken);
            var pcFileId = ExtractPcFileId(chain);

            if (string.IsNullOrEmpty(pcFileId))
            {
                var ex = new Up366Exception("未找到PC文件ID");
                LogException(ex, "[GetTaskQuestionsData]");
                throw ex;
            }

            var entries = await DownloadAndExtractAsync(pcFileId, cancellationToken);

            var questionsEntry = entries.FirstOrDefault(e =>
                e.FileName.Contains("/questions/") && e.FileName.EndsWith("questionData.js"));

            if (questionsEntry == null)
            {
                var ex = new Up366Exception("未找到题目数据文件");
                LogException(ex, "[GetTaskQuestionsData]");
                throw ex;
            }

            return System.Text.Encoding.UTF8.GetString(questionsEntry.Content);
        }

        // ★★★ 重写 AutoCompleteTaskAsync，添加四步调用流程 + 服务器时间校准 ★★★
        public async Task<(bool Success, string Message, double Score, int Percent)> AutoCompleteTaskAsync(
            string bookId,
            string taskId,
            string courseId,
            string chapterId,
            string pageId,
            List<QuestionAnswer> answers,
            int durationMinutes = 18,
            int scorePercent = 95,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrEmpty(bookId) || string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(courseId))
            {
                return (false, "缺少必要参数", 0, 0);
            }

            if (answers == null || answers.Count == 0)
            {
                return (false, "答案列表为空", 0, 0);
            }

            // ========== 步骤0: 同步服务器时间（关键！）==========
            Log("[AutoCompleteTask] 步骤0: 同步服务器时间...");
            var serverTime = await GetServerTimeAsync(cancellationToken);
            var now = GetCalibratedTime(); // 使用校准后的时间

            // 获取资源链信息
            string batchId = null;
            string studyTaskId = null;

            if (string.IsNullOrEmpty(chapterId) || string.IsNullOrEmpty(pageId))
            {
                Log("[AutoCompleteTask] 尝试从资源链获取补充信息...");
                try
                {
                    var chain = await GetTaskLinkedAsync(bookId, taskId, cancellationToken);
                    if (string.IsNullOrEmpty(chapterId))
                        chapterId = chain?.Chapters?.FirstOrDefault(c => c.IsContent == 1)?.Id ?? "";
                    if (string.IsNullOrEmpty(pageId))
                        pageId = chain?.Tasks?.FirstOrDefault()?.PcPageId ?? "";

                    batchId = chain?.ExtendParams?.BatchId;
                    studyTaskId = chain?.StudyTaskId ?? taskId;
                }
                catch (Exception ex)
                {
                    Log($"[AutoCompleteTask] 获取资源链失败: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(chapterId))
            {
                return (false, "未找到章节信息(chapterId)", 0, 0);
            }
            if (string.IsNullOrEmpty(pageId))
            {
                return (false, "未找到页面信息(pageId)", 0, 0);
            }

            batchId ??= taskId;
            studyTaskId ??= taskId;

            Log($"[AutoCompleteTask] 最终参数: bookId={bookId}, taskId={taskId}, courseId={courseId}, chapterId={chapterId}, pageId={pageId}, batchId={batchId}, studyTaskId={studyTaskId}");
            Log($"[AutoCompleteTask] 使用服务器校准时间: {now}");

            // 校准所有答案的时间戳（使用服务器时间）
            var durationMs = durationMinutes * 60 * 1000;
            var startTime = now - durationMs;
            var timeStep = durationMs / Math.Max(answers.Count, 1);

            for (int i = 0; i < answers.Count; i++)
            {
                if (answers[i].Timestamp == 0)
                {
                    // 使用校准后的时间生成时间戳
                    answers[i].Timestamp = startTime + (i * timeStep) + Random.Shared.Next(1000, 5000);
                }
                else
                {
                    // 将已有时间戳也加上偏移量校准
                    answers[i].Timestamp += _serverTimeOffset;
                }
                answers[i].Order = i + 1;
            }

            // 步骤1: 提交成绩（使用校准后的时间）
            Log("[AutoCompleteTask] 步骤1: 提交成绩...");
            var success = await SubmitTaskScoreAsync(
                taskId, bookId, chapterId, courseId, pageId,
                answers, durationMinutes, scorePercent,
                batchId, studyTaskId, now, cancellationToken); // 传入校准后的 now

            var actualScore = answers.Where(a => a.IsCorrect).Sum(a => a.FullScore);
            var actualPercent = answers.Count > 0 ? (int)(answers.Count(a => a.IsCorrect) * 100.0 / answers.Count) : 0;

            return (success, success ? "提交成功" : "提交失败", actualScore, actualPercent);
        }
        #endregion

        #region ★★★ 新增：front/current-time 时间同步接口 ★★★

        public async Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default)
        {
            var url = "https://setup-api.up366.cn/front/current-time";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, Guid.NewGuid().ToString("N"));
            AddAuthCookies(request);
            request.Headers.Add("Referer", "https://student.up366.cn/");
            request.Headers.Add("x-requested-with", "PC");

            Log($"[GetServerTime] GET {url}");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[GetServerTime] Response: {content}");

            if (!response.IsSuccessStatusCode) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                var result = JsonSerializer.Deserialize<ApiResponse<ServerTimeData>>(content, GetJsonOptions());
                var serverTime = result?.Data?.CurrentTime ?? 0;
                if (serverTime > 0)
                {
                    var localNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _serverTimeOffset = serverTime - localNow;
                    Log($"[GetServerTime] 服务器时间: {serverTime}, 本地时间: {localNow}, 偏移: {_serverTimeOffset}ms");
                    return serverTime;
                }
            }
            catch (Exception ex)
            {
                Log($"[GetServerTime] 解析失败: {ex.Message}");
            }

            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // 获取校准后的当前时间（服务器时间）
        public long GetCalibratedTime()
        {
            if (_serverTimeOffset != 0)
            {
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverTimeOffset;
            }
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private long _serverTimeOffset = 0;

        internal class ServerTimeData
        {
            [JsonPropertyName("currentTime")]
            public long CurrentTime { get; set; }

            [JsonPropertyName("command")]
            public string Command { get; set; }
        }

        #endregion

        #region 辅助方法

        private void AddCommonHeaders(HttpRequestMessage request, string u3r)
        {
            if (!string.IsNullOrEmpty(u3r))
            {
                request.Headers.TryAddWithoutValidation("u3r", u3r);
            }
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("Accept-Language", "zh-CN");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Sec-Fetch-Mode", "no-cors");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("x-app-name", "student-pc");
            request.Headers.TryAddWithoutValidation("clientid", "7DE08EE71FBD3DA75A260946416B7188DBE077E4");
        }

        private void AddAuthCookies(HttpRequestMessage request)
        {
            // Cookie 由 CookieContainer 自动处理
        }

        private void EnsureSession()
        {
            if (_session == null)
                throw new InvalidOperationException("请先执行 VerifyCodeAsync 获取会话");
        }

        private void EnsureAuthenticated()
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("请先完成登录流程（VerifyCodeAsync -> AutoLoginAsync）");
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _handler?.Dispose();
        }

        #endregion
    }

    #region 数据模型

    public class SessionContext
    {
        public string Tgt { get; set; }
        public string Token { get; set; }
        public string Up366C { get; set; }
        public string Castgc { get; set; }
        public string Uuid { get; set; }
        public long Uid { get; set; }
        public string RealName { get; set; }
        public string Username { get; set; }
        public long OrganId { get; set; }
        public string ClientId { get; set; }
    }

    public class ClassInfo
    {
        [JsonPropertyName("courseId")]
        public long CourseId { get; set; }

        [JsonPropertyName("courseName")]
        public string CourseName { get; set; }

        [JsonPropertyName("courseCode")]
        public string CourseCode { get; set; }

        [JsonPropertyName("teacherName")]
        public string TeacherName { get; set; }

        [JsonPropertyName("lecturerName")]
        public string LecturerName { get; set; }

        [JsonPropertyName("organName")]
        public string OrganName { get; set; }

        [JsonPropertyName("joinTime")]
        public long JoinTime { get; set; }

        [JsonPropertyName("lastStudyTime")]
        public long LastStudyTime { get; set; }
    }

    public class ClassStat
    {
        [JsonPropertyName("courseId")]
        public long CourseId { get; set; }

        [JsonPropertyName("taskNum")]
        public int TaskNum { get; set; }

        [JsonPropertyName("bookTaskNum")]
        public int BookTaskNum { get; set; }

        [JsonPropertyName("testNum")]
        public int TestNum { get; set; }

        [JsonPropertyName("wrongNoteNum")]
        public int WrongNoteNum { get; set; }
    }

    public class JobInfo
    {
        [JsonPropertyName("jobId")]
        public string JobId { get; set; }

        [JsonPropertyName("jobName")]
        public string JobName { get; set; }

        [JsonPropertyName("contentId")]
        public string ContentId { get; set; }

        [JsonPropertyName("courseId")]
        public long CourseId { get; set; }

        [JsonPropertyName("ariseTime")]
        public long AriseTime { get; set; }

        [JsonPropertyName("endTime")]
        public long EndTime { get; set; }

        [JsonPropertyName("beginTime")]
        public long BeginTime { get; set; }

        [JsonPropertyName("extParam")]
        public ExtParam ExtParam { get; set; }
    }

    public class ExtParam
    {
        [JsonPropertyName("bookId")]
        public string BookId { get; set; }

        [JsonPropertyName("bookName")]
        public string BookName { get; set; }

        [JsonPropertyName("chapterId")]
        public string ChapterId { get; set; }

        [JsonPropertyName("pageId")]
        public string PageId { get; set; }

        [JsonPropertyName("paperId")]
        public string PaperId { get; set; }

        [JsonPropertyName("taskScore")]
        public double TaskScore { get; set; }

        [JsonPropertyName("taskStatus")]
        public int TaskStatus { get; set; }

        [JsonPropertyName("taskType")]
        public int TaskType { get; set; }
    }

    public class ResourceChain
    {
        [JsonPropertyName("chapters")]
        public List<Chapter> Chapters { get; set; }

        [JsonPropertyName("book")]
        public BookInfo Book { get; set; }

        [JsonPropertyName("tasks")]
        public List<TaskInfo> Tasks { get; set; }

        // ★★★ 新增：扩展参数 ★★★
        [JsonPropertyName("extendParams")]
        public ExtendParams ExtendParams { get; set; }

        [JsonPropertyName("studyTaskId")]
        public string StudyTaskId { get; set; }
    }

    public class ExtendParams
    {
        [JsonPropertyName("batchId")]
        public string BatchId { get; set; }
    }

    public class Chapter
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("isContent")]
        public int IsContent { get; set; }

        [JsonPropertyName("pcFileId")]
        public string PcFileId { get; set; }

        [JsonPropertyName("fileId")]
        public string FileId { get; set; }

        [JsonPropertyName("parentId")]
        public string ParentId { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    public class BookInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("bookType")]
        public string BookType { get; set; }
    }

    public class TaskInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("pcPageId")]
        public string PcPageId { get; set; }

        [JsonPropertyName("taskType")]
        public string TaskType { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }
    }

    public class ZipEntry
    {
        public string FileName { get; set; }
        public byte[] Content { get; set; }
    }

    #endregion

    #region 内部模型

    internal class ApiResponse<T>
    {
        [JsonPropertyName("data")]
        public T Data { get; set; }

        [JsonPropertyName("result")]
        public ResultInfo Result { get; set; }
    }

    // ★★★ 重写 QuestionAnswer，添加 Timestamp 和 Order 字段 ★★★
    public class QuestionAnswer
    {
        public string QuestionId { get; set; }
        public string ElementId { get; set; }
        public int QuestionType { get; set; }
        public double FullScore { get; set; }
        public string UserAnswer { get; set; }
        public string CorrectAnswer { get; set; }
        public bool IsCorrect { get; set; }

        // ★★★ 新增：答题时间戳（严格匹配抓包）★★★
        public long Timestamp { get; set; }

        // ★★★ 新增：题目顺序 ★★★
        public int Order { get; set; }
    }

    internal class ResultInfo
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; }
    }

    internal class VerifyCodeResponse
    {
        [JsonPropertyName("tgt")]
        public string Tgt { get; set; }

        [JsonPropertyName("token")]
        public string Token { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; }

        [JsonPropertyName("uid")]
        public long Uid { get; set; }

        [JsonPropertyName("realname")]
        public string Realname { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("organId")]
        public long OrganId { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("weakPwd")]
        public int WeakPwd { get; set; }

        [JsonPropertyName("firstLogin")]
        public bool FirstLogin { get; set; }

        [JsonPropertyName("backupPwd")]
        public int BackupPwd { get; set; }

        [JsonPropertyName("uType")]
        public int UType { get; set; }

        [JsonPropertyName("tgtFlag")]
        public int TgtFlag { get; set; }
    }

    internal class ClassListResponse
    {
        [JsonPropertyName("list")]
        public List<ClassInfo> List { get; set; }

        [JsonPropertyName("pager")]
        public PagerInfo Pager { get; set; }
    }

    internal class PagerInfo
    {
        [JsonPropertyName("currentPage")]
        public int CurrentPage { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }

    internal class JobListResponse
    {
        [JsonPropertyName("allTaskList")]
        public List<JobInfo> AllTaskList { get; set; }

        [JsonPropertyName("courseName")]
        public string CourseName { get; set; }

        [JsonPropertyName("hasMore")]
        public string HasMore { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("courseId")]
        public long CourseId { get; set; }
    }

    #endregion

    #region 异常

    public class Up366Exception : Exception
    {
        public int ErrorCode { get; }

        public Up366Exception(string message) : base(message)
        {
        }

        public Up366Exception(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    #endregion
}