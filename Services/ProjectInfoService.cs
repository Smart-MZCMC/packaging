using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace PackagingApp.Services;

/// <summary>
/// 按项目 id 取项目名，带进程内缓存。
///
/// 为什么单独查一次而不是等 WebSocket 推：CSV 的第二行就是项目名，而切台是
/// 高频事件（一场比赛几十次），每次切台都打一次 HTTP 纯属浪费，还会让界面
/// 卡在等待里。项目名在一次运行期间不会变，查到一次就够。
///
/// 为什么走 HTTP 而不是给 WS 加一个新消息类型：为了一个静态字段改后端的
/// 推送协议，等于逼所有已发布的客户端都得能解析它，不划算——解说端的
/// <c>ProjectInfoService</c> 也是同一个理由。
/// </summary>
public sealed class ProjectInfoService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>
    /// 只缓存查到的名字。
    ///
    /// 失败**不写缓存**：网络抖一下不该把「没有项目名」固化成整个进程的结论。
    /// 不写的话，下一次切台还会重试，网络恢复了就自动填上；写了的话这次
    /// 抖动会让 CSV 的第二行空到应用重启为止。
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _cache = new();

    /// <summary>
    /// 登录换来的 JWT，由 <see cref="SetToken"/> 交进来。
    ///
    /// 这里不自己去登录：这个 App 的凭据已经交给 <c>WebSocketClient</c> 了，
    /// 让它再登录一次等于同一份凭据在进程里存在两份，过期时间各算各的。
    /// </summary>
    private string? _token;

    public ProjectInfoService(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        // 比 VersionService 宽松：版本提示挂掉无所谓，查项目名是 CSV 第二行的
        // 来源，慢一点可以接受，但太短了会误判成「服务端不通」。
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// 设置请求用的 JWT。传 null 表示「不带令牌」。
    ///
    /// <c>/api/projects</c> 挂在 Jwt() 组里，所以令牌过期时它会返回 401。
    /// 令牌由 WebSocketClient 在重连时重新换取，调用方要在拿到新令牌后
    /// 再调一次本方法，否则长时间运行的包装端会一直查不到名字。
    /// </summary>
    public void SetToken(string? token)
    {
        _token = token;
    }

    /// <summary>
    /// 取项目名。命中缓存直接返回；成功则写入缓存。
    /// 失败返回 null（不抛），且**不写缓存**——下次还会重试。
    /// </summary>
    public async Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(projectId, out var cached)) return cached;
        if (_baseUrl.Length == 0 || projectId <= 0) return null;

        try
        {
            // 令牌现取现用而不是挂在 DefaultRequestHeaders 上：它会随重连变化，
            // 而 HttpClient 的默认头一旦设上就只能加不能减。
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/projects");
            var token = _token;
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!TryPickName(body, projectId, out var name)) return null;

            _cache[projectId] = name;
            return name;
        }
        catch (Exception)
        {
            // 一次查名失败不该把整个应用带崩：项目名只是 CSV 第二行，查不到就
            // 写个空行，芯象那边照样能跑（它按行号映射，空行也是合法的一行）。
            // 应用退出时的取消也会落到这里，同样返回 null、同样不写缓存。
            return null;
        }
    }

    /// <summary>
    /// 从 <c>/api/projects</c> 的返回里挑出指定项目的 <c>name</c>。
    ///
    /// 接口返回的是数组、没有单查端点，所以整份拉下来自己按 id 找。
    /// 字段名是 <c>id</c> 与 <c>name</c>（后端 models.Project 的 JSON tag）。
    /// </summary>
    private static bool TryPickName(string body, int projectId, out string name)
    {
        name = "";

        foreach (var item in JArray.Parse(body))
        {
            if (item["id"]?.ToObject<int>() != projectId) continue;

            var value = item["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(value)) return false;

            name = value.Trim();
            return true;
        }

        // 项目不在列表里（多半是这个账号没被授权）也是一种失败，同样交给下次重试。
        return false;
    }

    public void Dispose() => _http.Dispose();
}