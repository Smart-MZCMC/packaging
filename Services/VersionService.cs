using System.Net.Http;
using System.Text.Json;

using PackagingApp.Models;

namespace PackagingApp.Services;

/// <summary>
/// 拉取服务端版本信息。
///
/// 与 WebSocket 分开：这个请求只用来做版本提示，走 HTTP 更直接，
/// 而且服务端不通时不会干扰播送（异常全部吞掉，界面不显示提示条）。
/// </summary>
public sealed class VersionService : IDisposable
{
    /// <summary>轮询间隔。版本提示不需要很及时。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public VersionService(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// 拉一次服务端状态并做版本比对。
    ///
    /// <c>/api/status</c> 是公开路由，不需要令牌——这样即使服务端接口有问题，
    /// 版本提示依然能工作。
    /// </summary>
    public async Task<AppVersion.VersionCheck> FetchAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return Unknown();

        try
        {
            var uri = $"{_baseUrl}/api/status";
            using var resp = await _http.GetAsync(uri).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Unknown();

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown();

            var server = GetString(root, "version");
            if (string.IsNullOrWhiteSpace(server)) return Unknown();

            // 老服务端没有这个字段，留空即可：横幅会退回「版本不等就提醒」。
            var min = GetString(root, "min_client_version");

            return AppVersion.Check(AppVersion.Current, server, min);
        }
        catch (Exception)
        {
            // 内网抖动、服务器重启中都很常见，不该因此影响解说本身。
            return Unknown();
        }
    }

    private static string GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static AppVersion.VersionCheck Unknown()
    {
        return new AppVersion.VersionCheck(AppVersion.VersionStatus.Unknown, "", "", "");
    }

    public void Dispose() => _http.Dispose();
}