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
}
