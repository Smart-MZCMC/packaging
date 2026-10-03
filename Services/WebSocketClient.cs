using System.IO;
using System.Net.Http;
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

    /// <summary>
    /// 登录换来的 JWT。
    ///
    /// 包装端此前**连登录这个概念都没有**：它在服务端是匿名的，任何人拿到
    /// 那个地址就能监听整个项目的实时消息。后端启用 REQUIRE_PROJECT_MEMBERSHIP
    /// 之后，没有令牌的连接会被直接拒绝，所以凭据必须在那之前就位。
    /// </summary>
    private string? _token;

    /// <summary>
    /// 最近一次登录拿到的 JWT，供需要带令牌调 HTTP 接口的地方复用。
    ///
    /// 为什么暴露出来：取项目名（<c>GET /api/projects</c>）挂在后端的 Jwt() 组里，
    /// 没有令牌就是 401，而令牌只在这个类里登录时拿得到。此前它是私有的，
    /// 于是 ProjectInfoService 只能等外部喂——而这个类没有对外抛令牌的通道，
    /// 结果就是「服务写了、却永远拿不到令牌」。
    ///
    /// 每次重连都会重新登录，所以读到的永远是**当前**这份，不是最初那份。
    /// 但反过来也要知道：连接稳定不重连时它不会自动续期，而 JWT 默认 60 分钟
    /// 过期。所以用它去调接口必须容错——过期的表现是 401，被当成软失败吞掉。
    /// 调用方应当缓存最后一次成功的结果，不要指望每次都能拿到新令牌。
    /// </summary>
    public string? Token => _token;

    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// 其它消息：(type, subType, content)。
    /// 切台状态不走这个事件，见 <see cref="ShotStateReceived"/>。
    /// </summary>
    public event Action<string, string, string>? MessageReceived;

    /// <summary>收到切台状态，一次给出「当前播送」和「即将切台」。</summary>
    public event Action<ShotState>? ShotStateReceived;

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

    /// <summary>
    /// 用配置里的账号换一个令牌。
    ///
    /// 没配账号时返回 null 且不发请求：这样在没有启用成员校验的部署上，
    /// 包装端的行为与改动前完全一致——现场不会因为少填一个字段而起不来。
    /// </summary>
    private async Task<string?> LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrEmpty(_config.Password))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = JsonConvert.SerializeObject(new
            {
                username = _config.Username,
                password = _config.Password
            });
            var url = $"{_config.ServerUrl.TrimEnd('/')}/api/auth/login";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                // 密码错、账号被停用都会走到这里。一定要透出到界面：否则现场
                // 只看到一直「连接中...」，完全分不清是凭据问题还是网络问题。
                var detail = await resp.Content.ReadAsStringAsync();
                SystemMessage?.Invoke($"登录失败 HTTP {(int)resp.StatusCode}：{detail}");
                return null;
            }

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["token"]?.ToString();
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"登录异常：{ex.Message}");
            return null;
        }
    }

    private async Task DoConnectAsync()
    {
        try
        {
            _cts?.Cancel();
            _ws?.Dispose();

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            // 每次重连都重新换令牌：JWT_TTL 默认 60 分钟，长时间运行的包装端
            // 用旧令牌重连会一直失败，而失败原因只表现为「连不上」。
            _token = await LoginAsync();

            var url = $"{_config.WsUrl}?project_id={_config.ProjectId}&role={_config.Role}";
            if (!string.IsNullOrEmpty(_token))
            {
                url += $"&token={Uri.EscapeDataString(_token)}";
            }

            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _reconnectAttempts = 0;
            ConnectionChanged?.Invoke(true);

            _ = ReceiveLoopAsync();
            StartHeartbeat();
        }
        catch
        {
            // 连不上时丢掉令牌，下一轮重新登录。绝大多数失败是令牌过期，
            // 拿着同一个过期令牌重试是白费。
            _token = null;
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
                // 一条 WS 消息可能被拆成多帧，按 EndOfMessage 拼完再解析。
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConnectionChanged?.Invoke(false);
                        if (!_intentionalClose) ScheduleReconnect();
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        frame.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (frame.Length > 0)
                {
                    HandleMessage(Encoding.UTF8.GetString(frame.ToArray()));
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
                case "shot_state":
                    // 导播端每次切台都下发完整状态：current=当前播送，next=即将切台。
                    ShotStateReceived?.Invoke(payload?.ToObject<ShotState>() ?? new ShotState());
                    break;
                case "chat":
                    var chatMsg = payload?["message"]?.ToString() ?? "";
                    // 心跳只是保活信号，后端不会再转发，这里只是兜底跳过。
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
                    // 连接与断线重连时，后端会把项目当前的切台状态放进欢迎消息。
                    //
                    // 没有这一步的话，中途连上来的包装端两栏都是空的，直到下一次
                    // 切台——字幕与包装的准备工作正好需要提前知道下一条是什么。
                    ApplyWelcomeState(payload);
                    SystemMessage?.Invoke(payload?["message"]?.ToString()
                        ?? payload?["error"]?.ToString()
                        ?? "");
                    break;
            }
        }
        catch { }
    }

    /// <summary>
    /// 渲染欢迎消息里带回来的当前切台状态。
    /// </summary>
    private void ApplyWelcomeState(JObject? payload)
    {
        if (payload == null) return;
        if (payload["state_available"]?.Value<bool>() != true) return;

        var state = new ShotState
        {
            Current = payload["current_shot"]?.ToString() ?? "",
            Next = payload["next_shot"]?.ToString() ?? ""
        };

        // 什么都没有时不要冒充成一次切台，交给界面继续显示空栏。
        if (string.IsNullOrEmpty(state.Current) && string.IsNullOrEmpty(state.Next)) return;

        ShotStateReceived?.Invoke(state);
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
                        payload = new
                        {
                            message = "heartbeat",
                            // 时间戳供服务端刷新「最后一次见到这个客户端」的时刻，
                            // 掉线扫描判断的就是它。
                            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }
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
        GC.SuppressFinalize(this);
    }
}
