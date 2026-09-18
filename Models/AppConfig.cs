namespace PackagingApp.Models;

public class AppConfig
{
    public string ServerUrl { get; set; } = "http://192.168.1.100:3000";
    public string WsUrl { get; set; } = "ws://192.168.1.100:3000/ws";
    public int ProjectId { get; set; } = 1;
    public string Role { get; set; } = "packaging";
}
