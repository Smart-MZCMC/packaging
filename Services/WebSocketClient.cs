using System.Net.WebSockets;
using System.Text;
using PackagingApp.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackagingApp.Services;

public class WebSocketClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;
    private Timer? _reconnectTimer;
    private bool _intentionalClose;
    private int _reconnectAttempts;
    private AppConfig _config;

    public event Action<bool>? ConnectionChanged;
    public event Action<string, string, string>? MessageReceived; // (type, subType, content)
    public event Action<string>? SystemMessage;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public WebSocketClient(AppConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        _intentionalClose = false;
        await DoConnectAsync();
    }

    private async Task DoConnectAsync()
    {
        try
        {
            _cts?.Cancel();
            _ws?.Dispose();

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var url = $"{_config.WsUrl}?project_id={_config.ProjectId}&role={_config.Role}";
            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _reconnectAttempts = 0;
            ConnectionChanged?.Invoke(true);

            _ = ReceiveLoopAsync();
            StartHeartbeat();
        }
        catch
        {
            ConnectionChanged?.Invoke(false);
            ScheduleReconnect();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts?.Token.IsCancellationRequested == true)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ConnectionChanged?.Invoke(false);
                    if (!_intentionalClose) ScheduleReconnect();
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            ConnectionChanged?.Invoke(false);
            if (!_intentionalClose) ScheduleReconnect();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString() ?? "";
            var payload = msg["payload"] as JObject;

            switch (type)
            {
                case "next_shot":
                    var content = payload?["content"]?.ToString() ?? "";
                    MessageReceived?.Invoke("next_shot", "next", content);
                    break;
                case "confirm_switch":
                    var switchContent = payload?["content"]?.ToString() ?? "";
                    MessageReceived?.Invoke("confirm_switch", "confirm", switchContent);
                    break;
                case "chat":
                    var chatMsg = payload?["message"]?.ToString() ?? "";
                    if (chatMsg == "heartbeat") break;
                    MessageReceived?.Invoke("chat", "message", chatMsg);
                    break;
                case "lock_update":
                    var action = payload?["action"]?.ToString() ?? "";
                    MessageReceived?.Invoke("lock_update", action, "");
                    break;
                case "interview_status":
                    var pointCode = payload?["point_code"]?.ToString() ?? "";
                    var status = payload?["status"]?.ToString() ?? "";
                    MessageReceived?.Invoke("interview_status", pointCode, status);
                    break;
                case "system":
                    var sysMsg = payload?["message"]?.ToString() ?? "";
                    SystemMessage?.Invoke(sysMsg);
                    break;
            }
        }
        catch { }
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(_ =>
        {
            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    var msg = JsonConvert.SerializeObject(new
                    {
                        type = "chat",
                        project_id = _config.ProjectId,
                        payload = new { message = "heartbeat" }
                    });
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    _ws?.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void ScheduleReconnect()
    {
        _reconnectAttempts++;
        var delay = TimeSpan.FromSeconds(Math.Min(1 * (1 << (_reconnectAttempts - 1)), 10));
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(async _ => await DoConnectAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Disconnect()
    {
        _intentionalClose = true;
        _heartbeatTimer?.Dispose();
        _reconnectTimer?.Dispose();
        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
