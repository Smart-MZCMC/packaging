namespace PackagingApp.Models;

public class AppConfig
{
    public string ServerUrl { get; set; } = "http://192.168.1.100:3000";
    public string WsUrl { get; set; } = "ws://192.168.1.100:3000/ws";
    public int ProjectId { get; set; } = 1;
    public string Role { get; set; } = "packaging";

    /// <summary>
    /// 登录凭据。
    ///
    /// 包装端此前**连登录这个概念都没有**，在服务端是匿名的：任何人拿到那个
    /// 地址就能监听整个项目的实时消息。后端把 REQUIRE_PROJECT_MEMBERSHIP 打开
    /// 之后，WebSocket 会校验「这个账号是不是该项目的成员」，届时没有凭据的
    /// 包装端会直接连不上。
    ///
    /// 留空表示不登录，行为与改动前一致——所以可以先把账号发下去、确认现场
    /// 都能连上，再打开后端那个开关。
    /// </summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// CSV 输出路径，芯象（现场的视频切换台）从这里读当前播的是什么。
    ///
    /// 留空表示未配置：写入会失败并在状态栏提示，应用其它部分照常工作。
    /// 这里刻意**不写死默认值**——config.json 是反序列化进已有实例的，
    /// 现场已有配置文件里没有这个键，改默认值既不会补上（老文件仍旧是空串，
    /// 于是任何写死的默认值都是空跑），也会让人以为路径是配好的。
    /// 真正的默认值由界面层按「exe 同目录下的 state.csv」填。
    /// </summary>
    public string CsvPath { get; set; } = "";

    /// <summary>
    /// 赛事名覆盖，写进 CSV 第一行。
    ///
    /// 留空表示按 <c>ProjectId</c> 去后端查项目名当赛事名（见
    /// <c>ProjectInfoService</c>），查不到就写空行。
    /// 与 <see cref="CsvPath"/> 同一条取向：本地配置优先，没配才走后端。
    /// 后端那份是权威答案，本地填的只是给「后端查不到、或现场就是只想写死」
    /// 的场景留个出口——运动会名这种事现场常有临时叫法。
    /// </summary>
    public string EventName { get; set; } = "";
}
