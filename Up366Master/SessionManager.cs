using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Up366Net;

namespace Up366Master;

public static class SessionManager
{
    private static readonly string SessionFilePath = Path.Combine(FileSystem.AppDataDirectory, "session.json");

    /// <summary>
    /// 验证当前会话是否有效（通过调用班级列表API测试）
    /// </summary>
    public static async Task<bool> ValidateSessionAsync(Up366Client client)
    {
        try
        {
            if (!client.IsAuthenticated) return false;

            // 尝试调用班级列表API验证会话
            var classes = await client.GetClassListAsync();
            // 如果能正常获取（不抛异常），会话有效
            return true;
        }
        catch (Up366Exception ex) when (ex.ErrorCode == 401 ||
                                        ex.Message.Contains("没有登陆") ||
                                        ex.Message.Contains("未登录") ||
                                        ex.Message.Contains("登录"))
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 会话已失效: {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") ||
                                              ex.Message.Contains("Unauthorized"))
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] HTTP 401: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 验证异常: {ex.Message}");
            // 网络错误时不直接判定失效，保守返回true
            return true;
        }
    }
    public static async Task<bool> SaveSessionAsync(Up366Client client)
    {
        try
        {
            var s = client.Session;
            if (s == null || string.IsNullOrEmpty(s.Up366C))
            {
                System.Diagnostics.Debug.WriteLine("[SessionManager] 无有效会话，无法保存");
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
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 会话已保存: {SessionFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 保存失败: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> TryLoadSessionAsync(Up366Client client)
    {
        try
        {
            if (!File.Exists(SessionFilePath))
            {
                System.Diagnostics.Debug.WriteLine("[SessionManager] 会话文件不存在");
                return false;
            }

            var json = await File.ReadAllTextAsync(SessionFilePath);
            var data = JsonSerializer.Deserialize<SessionData>(json);

            if (data == null || string.IsNullOrEmpty(data.Up366C))
            {
                System.Diagnostics.Debug.WriteLine("[SessionManager] 会话文件无效");
                return false;
            }

            // 检查是否过期（7天）
            var age = DateTime.Now - data.SavedAt;
            if (age.TotalDays > 7)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] 会话已过期 {age.TotalDays:F1} 天");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[SessionManager] 正在恢复会话: {data.RealName} ({age.TotalHours:F1}小时前)");

            // 重建 SessionContext
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

            // 使用反射注入到客户端
            InjectSessionToClient(client, session);

            System.Diagnostics.Debug.WriteLine("[SessionManager] 会话恢复成功");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 加载失败: {ex.Message}");
            return false;
        }
    }

    private static void InjectSessionToClient(Up366Client client, SessionContext session)
    {
        var clientType = typeof(Up366Client);

        // 1. 设置 _session 字段
        var sessionField = clientType.GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (sessionField != null)
        {
            sessionField.SetValue(client, session);
            System.Diagnostics.Debug.WriteLine("[SessionManager] _session 已注入");
        }

        // 2. 获取 _handler 并注入 Cookie
        var handlerField = clientType.GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (handlerField?.GetValue(client) is HttpClientHandler handler)
        {
            // 清除旧 Cookie
            handler.CookieContainer = new CookieContainer();

            // 注入所有必要的 Cookie
            AddCookieToContainer(handler.CookieContainer, "UP366-C", session.Up366C);
            AddCookieToContainer(handler.CookieContainer, "CASTGC", session.Castgc);

            System.Diagnostics.Debug.WriteLine("[SessionManager] Cookie 已注入");
        }

        // 3. 如果有 _cookieContainer 字段也设置
        var cookieField = clientType.GetField("_cookieContainer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (cookieField != null)
        {
            var newContainer = new CookieContainer();
            AddCookieToContainer(newContainer, "UP366-C", session.Up366C);
            AddCookieToContainer(newContainer, "CASTGC", session.Castgc);
            cookieField.SetValue(client, newContainer);
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
                container.Add(uri, new Cookie(name, value)
                {
                    HttpOnly = true,
                    Secure = true,
                    Domain = domain
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Cookie添加失败 {domain}: {ex.Message}");
            }
        }
    }

    public static void ClearSessionFile()
    {
        try
        {
            if (File.Exists(SessionFilePath))
            {
                File.Delete(SessionFilePath);
                System.Diagnostics.Debug.WriteLine("[SessionManager] 会话文件已清除");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] 清除失败: {ex.Message}");
        }
    }

    public static bool HasSessionFile() => File.Exists(SessionFilePath);

    public static DateTime? GetSessionSaveTime()
    {
        try
        {
            if (!File.Exists(SessionFilePath)) return null;
            var json = File.ReadAllText(SessionFilePath);
            var data = JsonSerializer.Deserialize<SessionData>(json);
            return data?.SavedAt;
        }
        catch
        {
            return null;
        }
    }
}

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
    public DateTime SavedAt { get; set; }
}