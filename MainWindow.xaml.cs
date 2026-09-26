using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PackagingApp.Models;
using PackagingApp.Services;
using Newtonsoft.Json;

namespace PackagingApp;

public partial class MainWindow : Window
{
    /// <summary>「即将切台」配色：琥珀色。</summary>
    private static readonly Brush PendingBrush = Freeze("#e0a030");

    /// <summary>没有待切项时「即将切台」栏的配色。</summary>
    private static readonly Brush IdleBrush = Freeze("#5a6b7d");

    private WebSocketClient? _wsClient;
    private AppConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        // 冻结后可跨线程安全共用。
        brush.Freeze();
        return brush;
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = ConnectWebSocket();
    }

    private async Task ConnectWebSocket()
    {
        _wsClient?.Dispose();
        _wsClient = new WebSocketClient(_config);

        _wsClient.ConnectionChanged += connected =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = connected ? Brushes.Green : Brushes.Red;
                StatusText.Text = connected ? "已连接" : "连接中...";
            });
        };

        // 切台状态：后端每次都同时给出「当前播送」和「即将切台」，直接照着渲染。
        _wsClient.ShotStateReceived += state =>
        {
            Dispatcher.Invoke(() =>
            {
                CurrentInstruction.Text = string.IsNullOrEmpty(state.Current) ? "—" : state.Current;
                NextInstruction.Text = state.HasPending ? state.Next : "—";
                NextInstruction.Foreground = state.HasPending ? PendingBrush : IdleBrush;
                AddLog(state.HasPending
                    ? $"[导播] 即将切台: {state.Next}（当前: {state.Current}）"
                    : $"[导播] 正在播送: {state.Current}");
            });
        };

        _wsClient.MessageReceived += (type, subType, content) =>
        {
            Dispatcher.Invoke(() =>
            {
                switch (type)
                {
                    case "chat":
                        AddLog($"[内部消息] {content}");
                        break;

                    case "lock_update":
                        var actionText = subType == "acquire" ? "获取控制权" : "释放控制权";
                        AddLog($"[控制权] 导播{actionText}");
                        break;

                    case "interview_status":
                        var statusText = subType switch
                        {
                            "ready" => "就绪",
                            "preparing" => "准备中",
                            "not_ready" => "未就绪",
                            "offline" => "离线",
                            _ => subType
                        };
                        InterviewStatusText.Text = $"采访点 {content}: {statusText}";
                        AddLog($"[采访] {content} → {statusText}");
                        break;
                }
            });
        };

        _wsClient.SystemMessage += msg =>
        {
            Dispatcher.Invoke(() =>
            {
                LastUpdateText.Text = $"[系统] {msg}";
                AddLog($"[系统] {msg}");
            });
        };

        await _wsClient.ConnectAsync();
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var item = new TextBlock
        {
            Text = $"[{timestamp}] {message}",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ccc")),
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 2)
        };
        MessageLog.Items.Add(item);

        // 保留最近100条
        while (MessageLog.Items.Count > 100)
        {
            MessageLog.Items.RemoveAt(0);
        }

        // 滚动到底部
        MessageLog.ScrollIntoView(MessageLog.Items[MessageLog.Items.Count - 1]);
    }

    protected override void OnClosed(EventArgs e)
    {
        _wsClient?.Dispose();
        base.OnClosed(e);
    }
}
