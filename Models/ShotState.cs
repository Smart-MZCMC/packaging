using Newtonsoft.Json;

namespace PackagingApp.Models;

/// <summary>
/// 切台状态。
///
/// 导播端每次切台都同时上报「当前播送」和「即将切台」，包装端据此同时
/// 呈现两栏：正在播送的是画面上真正在跑的机位，即将切台的是待命项。
/// </summary>
public sealed class ShotState
{
    /// <summary>当前正在播送的机位。</summary>
    [JsonProperty("current")]
    public string Current { get; set; } = "";

    /// <summary>
    /// 即将切过去的机位。空串表示导播已确认切完，画面就是 <see cref="Current"/>。
    /// </summary>
    [JsonProperty("next")]
    public string Next { get; set; } = "";

    /// <summary>是否还有待切的机位。</summary>
    [JsonIgnore]
    public bool HasPending => !string.IsNullOrEmpty(Next);
}
