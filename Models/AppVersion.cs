using System.Reflection;

namespace PackagingApp.Models;

/// <summary>
/// 版本一致性检查。与管理后台、导播端、采访端保持同一套语义。
/// </summary>
/// <remarks>
/// 数据来源：编译期打进程序集的版本号，与服务端 <c>GET /api/status</c> 返回的
/// <c>version</c> / <c>min_client_version</c>。
///
/// 为什么不简单地「两端不等就告警」：后端发新版不代表客户端必须跟着重建。
/// 改文案、修 bug 对客户端完全透明，那样每次发版都会把现场吵一遍，
/// 久而久之这个提示就没人看了——告警只有在该响的时候响才有意义。
/// </remarks>
public static class AppVersion
{
    /// <summary>
    /// 本端版本，取自程序集的 InformationalVersion。
    /// 由 CI 用 <c>-p:Version=</c> 注入；本地构建没注入时回落到程序集版本。
    /// </summary>
    public static string Current { get; } = ResolveCurrentVersion();

    private static string ResolveCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SDK 会在 InformationalVersion 后面追加 "+<commit sha>"，
            // 版本比较只需要前三段，取 '+' 之前的部分。
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return asm.GetName().Version?.ToString(3) ?? "dev";
    }

    /// <summary>版本关系。</summary>
    public enum VersionStatus
    {
        /// <summary>无法比较（未注入版本号、服务端不可达、或格式非法）。</summary>
        Unknown,
        /// <summary>一致且满足最低适配要求。</summary>
        Match,
        /// <summary>低于服务端要求的最低适配版本：部分功能会异常，必须更新。</summary>
        Unsupported,
        /// <summary>满足最低要求、只是落后于服务端：建议更新，不影响使用。</summary>
        ClientBehind,
        /// <summary>本端新于服务端：服务端可能缺少接口，该更新服务端。</summary>
        ClientAhead,
    }

    /// <summary>一次比较的完整结果，供界面显示两侧版本号。</summary>
    /// <param name="Status">比较结论。</param>
    /// <param name="Client">本端版本号。</param>
    /// <param name="Server">服务端版本号。</param>
    /// <param name="Minimum">服务端要求的最低适配版本。</param>
    public readonly record struct VersionCheck(
        VersionStatus Status,
        string Client,
        string Server,
        string Minimum)
    {
        /// <summary>是否需要显示提示条。</summary>
        public bool ShouldWarn =>
            Status != VersionStatus.Match && Status != VersionStatus.Unknown;

        /// <summary>是否意味着「现在就有功能异常」，界面该用醒目样式。</summary>
        public bool IsUrgent => Status == VersionStatus.Unsupported;
    }

    /// <summary>解析版本号为三段数字；无法解析时返回 null。</summary>
    private static int[]? ParseVersion(string version)
    {
        var cleaned = version.Trim();
        if (cleaned.Length == 0) return null;
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[1..];
        }

        // 去掉预发布/构建元数据：1.2.0-rc1+build → 1.2.0
        var cut = cleaned.IndexOfAny(['-', '+']);
        if (cut >= 0) cleaned = cleaned[..cut];

        var parts = cleaned.Split('.');
        if (parts.Length == 0 || parts.Length > 3) return null;

        var result = new[] { 0, 0, 0 };
        for (var i = 0; i < parts.Length; i++)
        {
            // 必须先判空再 TryParse：空串若被当作 0 处理，「未注入版本号」
            // 就会被误判成「比服务端旧很多」。
            var raw = parts[i].Trim();
            if (raw.Length == 0) return null;
            if (!int.TryParse(raw, out result[i])) return null;
        }
        return result;
    }

    /// <summary>比较两个版本：-1 a 更旧，0 相同，1 a 更新。无法解析时返回 null。</summary>
    private static int? CompareVersions(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        if (pa is null || pb is null) return null;
        for (var i = 0; i < 3; i++)
        {
            if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
        }
        return 0;
    }

    /// <summary>比对本端、服务端与最低适配版本。</summary>
    /// <remarks>
    /// 老服务端没有 <c>min_client_version</c> 字段，此时退化为「只要不等就提醒」，
    /// 而不是因为拿不到最低版本就保持沉默——那会让新客户端对着老服务端
    /// 静默运行，出问题时反而更难查。
    /// </remarks>
    public static VersionCheck Check(string client, string server, string minClientVersion)
    {
        if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(server))
        {
            return new VersionCheck(VersionStatus.Unknown, "", "", "");
        }

        var cmpToServer = CompareVersions(client, server);
        if (cmpToServer is null)
        {
            return new VersionCheck(VersionStatus.Unknown, "", "", "");
        }
        if (cmpToServer == 0)
        {
            return new VersionCheck(VersionStatus.Match, client, server, minClientVersion);
        }
        if (cmpToServer > 0)
        {
            return new VersionCheck(VersionStatus.ClientAhead, client, server, minClientVersion);
        }

        if (!string.IsNullOrWhiteSpace(minClientVersion))
        {
            var cmpToMin = CompareVersions(client, minClientVersion);
            if (cmpToMin is not null && cmpToMin < 0)
            {
                return new VersionCheck(VersionStatus.Unsupported, client, server, minClientVersion);
            }
        }
        return new VersionCheck(VersionStatus.ClientBehind, client, server, minClientVersion);
    }
}