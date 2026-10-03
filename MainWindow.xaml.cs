using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PackagingApp.Models;
using PackagingApp.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackagingApp;

public partial class MainWindow : Window
{
    /// <summary>「即将切台」配色：琥珀色。</summary>
    private static readonly Brush PendingBrush = Freeze("#e0a030");

    /// <summary>没有待切项时「即将切台」栏的配色。</summary>
    private static readonly Brush IdleBrush = Freeze("#5a6b7d");

    /// <summary>正常（CSV 已写入）的配色，与两栏里「正在播送」一致。</summary>
    private static readonly Brush OkBrush = Freeze("#4ecca3");

    /// <summary>写入失败的配色，与版本提示条一致。</summary>
    private static readonly Brush ErrorBrush = Freeze("#e94560");

    /// <summary>
    /// 首次启动写出的默认配置上那句说明。
    ///
    /// 单独放在这里而不是从 config.json 读：那份文件正是「不存在」的时候才
    /// 需要它。现场的人多半是先双击 exe 跑起来、再去翻配置，不写这句他们
    /// 只看到一个空文件。
    /// </summary>
    private const string DefaultConfigComment =
        "包装端配置。Username/Password 是登录用的账号，留空表示不登录。"
        + "CsvPath 是 CSV 输出路径（现场的视频切换台从这里读当前播的是什么，"
        + "固定三行，顺序是『赛事名 / 项目名 / 切台状态』，无分隔符无标题行）。"
        + "EventName 是赛事名覆盖，留空表示按 ProjectId 去后端查项目名。"
        + "Role 固定为 packaging，不要改。"
        + "以上各项都能在界面右上角的「设置」里改，不必手改本文件。";

    /// <summary>
    /// 配置文件固定在 exe 同目录。
    ///
    /// 不跟着进程的工作目录走：现场是从资源管理器双击启动的，工作目录是
    /// 「用户文档」，配置和 CSV 落到那里既找不到也带不走。
    /// </summary>
    private static string ConfigPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private WebSocketClient? _wsClient;
    private AppConfig _config = new();
    private VersionService? _versionService;
    private DispatcherTimer? _versionTimer;
    private bool _versionBannerDismissed;

    /// <summary>CSV 写入器。地址或路径变了就重建。</summary>
    private CsvWriter? _csv;

    /// <summary>取项目名的服务。它自带缓存，这里只在地址变了时重建。</summary>
    private ProjectInfoService? _projectInfo;

    /// <summary>
    /// 后端查回来的项目名，同时充当 CSV 第一行。
    ///
    /// 只在查成功时写。查不到就沿用上一次的值：失败绝大多数是令牌过期或
    /// 网络抖动，而让第一行突然变空会让现场的赛事标题整个消失——那比一个
    /// 可能过时几分钟的名字糟糕得多。
    /// </summary>
    private string? _projectName;

    /// <summary>
    /// 最近一次切台状态。
    ///
    /// 连上之后、取名成功之后都要补写一次 CSV，而那两次并不会正好伴着一次
    /// 切台；没有它就只能在「等下一次切台」时才把新值写进文件。
    /// </summary>
    private ShotState? _lastShot;

    /// <summary>取项目名的防重入标志。</summary>
    private int _projectNameBusy;

    /// <summary>
    /// 当前配置的代号，每次「保存并重连」加一。
    ///
    /// 用途只有一个：改项目时可能还有一次旧项目的取名查询在飞，它回来后
    /// 必须被丢弃，否则会把上一个项目的名字写进 CSV 第一行，而现场根本
    /// 分不清那个名字是从哪来的。
    /// </summary>
    private int _settingsGeneration;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        // 底栏原来硬编码 "v1.0"，与实际构建版本无关（csproj 甚至没声明
        // Version，SDK 默认就是 1.0.0）。改成读程序集，客户端才能据此判断
        // 自己是否低于服务端要求的最低适配版本。
        var selfVersion = $"校园直播导播协调系统 v{AppVersion.Current}";
        FooterVersionText.Text = selfVersion;
        VersionSelfText.Text = $"v{AppVersion.Current}";

        RebuildBackingServices();
        FillSettingsFields();
        ShowCsvIdle();
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
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                return;
            }

            // 首次启动：写出一份能跑也能改的默认配置。
            // 为什么要写文件：设置面板的「保存并重连」只能往这个文件里写，
            // 而现场常常是把 exe 连同整个文件夹一起拷过去用的——那种情况下
            // 没有文件，「保存」注定失败，操作员会以为按钮坏了。
            _config = CreateDefaultConfig();
            SaveConfig(_config);
        }
        catch
        {
            // 配置坏了不该让应用起不来：退回内置默认值，包装员仍然能连上、
            // 能看状态，CSV 那边则会在状态栏明说没配好，从设置里改回来即可。
            _config = CreateDefaultConfig();
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            // AppConfig.CsvPath 的默认值刻意留空：老配置文件里没有这个键，
            // 反序列化之后仍然是空串，写死默认值既补不上（老文件读出来还是
            // 空）也会让人以为路径是配好的。所以默认值只在这里给，落在 exe
            // 同目录，整个文件夹拷走就能用。
            CsvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state.csv"),
        };
    }

    /// <summary>
    /// 把配置写回 exe 同目录的 <c>config.json</c>。成功返回 null。
    /// </summary>
    private static string? SaveConfig(AppConfig config)
    {
        try
        {
            // 读成 JObject 再改，而不是直接把 AppConfig 序列化出去：
            // 文件里有 <c>_comment</c> 这类说明性键，现场的人靠它理解字段，
            // 直接序列化会把它们全部抹掉，而下次启动新键又没有地方解释。
            JObject root;
            if (File.Exists(ConfigPath))
            {
                root = JObject.Parse(File.ReadAllText(ConfigPath));
            }
            else
            {
                root = new JObject { ["_comment"] = DefaultConfigComment };
            }

            root["ServerUrl"] = config.ServerUrl;
            root["WsUrl"] = config.WsUrl;
            root["ProjectId"] = config.ProjectId;
            root["Role"] = config.Role;
            root["Username"] = config.Username;
            root["Password"] = config.Password;
            root["CsvPath"] = config.CsvPath;
            root["EventName"] = config.EventName;

            File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented));
            return null;
        }
        catch (Exception ex)
        {
            // 手改坏过的 JSON 走到这里会解析失败。不覆盖它：那份文件里可能有
            // 现场自己加的说明或刚填好的账号，替他们猜一个内容代为决定更糟。
            return $"写入 config.json 失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 重建依赖配置地址的对象。
    ///
    /// 构造器与「保存并重连」都会调：这两处是配置唯一会变的地方，而对象
    /// 都是拿着地址构造的、不看后续变化，不重建就会一直往旧地址发请求。
    /// </summary>
    private void RebuildBackingServices()
    {
        // 先 Dispose 再新建：CsvWriter 被释放后路径会清空，留着旧实例只会
        // 得到一句「未配置 CSV 输出路径」。
        _csv?.Dispose();
        _csv = new CsvWriter(_config.CsvPath);

        _projectInfo?.Dispose();
        _projectInfo = new ProjectInfoService(_config.ServerUrl);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = ConnectWebSocket();
        StartVersionWatch();
    }

    /// <summary>
    /// 周期性检查与服务端的版本是否匹配。
    ///
    /// 只做提示，不阻断任何功能：这是包装端，播送中出问题比提示更重要。
    /// </summary>
    private void StartVersionWatch()
    {
        try
        {
            _versionService = new VersionService(_config.ServerUrl);
        }
        catch
        {
            return;
        }

        var timer = new DispatcherTimer
        {
            Interval = VersionService.PollInterval,
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                await RefreshVersionBannerAsync();

                // 顺带刷一次项目名，不另开定时器。
                // 这个节拍之所以合适：它本来就会醒来，而项目名是 CSV 的第一行，
                // 值得定期重试（上次失败多半是令牌过期，见 RefreshProjectNameAsync）。
                // 服务端不在线时失败也无妨——取名容错，不会打断播送。
                RequestProjectNameRefresh();
            }
            finally
            {
                // 只在「我还是当前那个」时才重新开始：保存设置会把旧 timer 换掉，
                // 无条件 Start 会让旧的那个也活过来，此后每保存一次就多一个
                // 5 分钟轮询，几轮之后版本检查的请求量会翻好几倍。
                if (ReferenceEquals(timer, _versionTimer)) timer.Start();
            }
        };
        _versionTimer = timer;
        timer.Start();

        // 立刻查一次：现场部署完新版本，操作员不该等 5 分钟才看到提示。
        _ = RefreshVersionBannerAsync();
    }

    /// <summary>换服务器地址后重建版本轮询。</summary>
    private void RestartVersionWatch()
    {
        _versionTimer?.Stop();
        _versionTimer = null;
        // 旧服务持有 HttpClient，不释放就是白占着一条连接池。
        _versionService?.Dispose();
        _versionService = null;
        StartVersionWatch();
    }

    private async Task RefreshVersionBannerAsync()
    {
        var service = _versionService;
        if (service is null) return;

        var check = await service.FetchAsync().ConfigureAwait(true);
        if (_versionBannerDismissed) return;

        var text = DescribeVersionCheck(check);
        if (string.IsNullOrEmpty(text))
        {
            VersionBanner.Visibility = Visibility.Collapsed;
            return;
        }

        VersionBannerText.Text = text;
        VersionSelfVersionText.Text = $"本端 v{AppVersion.Current}";
        VersionBanner.Visibility = Visibility.Visible;
    }

    private static string DescribeVersionCheck(AppVersion.VersionCheck check)
    {
        return check.Status switch
        {
            AppVersion.VersionStatus.Unsupported =>
                $"包装端版本 {check.Client} 已低于服务端要求的最低适配版本 {check.Minimum}，" +
                "部分功能可能异常，请尽快更新。",
            AppVersion.VersionStatus.ClientBehind =>
                $"包装端版本 {check.Client} 落后于服务端 {check.Server}，建议更新后再使用。",
            AppVersion.VersionStatus.ClientAhead =>
                $"包装端版本 {check.Client} 新于服务端 {check.Server}，" +
                "服务端可能缺少接口，请升级服务端。",
            _ => string.Empty,
        };
    }

    private void OnVersionBannerClose(object sender, RoutedEventArgs e)
    {
        // 只隐藏本次，不停止轮询：版本再次变化时还会提示。
        _versionBannerDismissed = true;
        VersionBanner.Visibility = Visibility.Collapsed;
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
                ConnectedText.Text = connected ? $"连接于 {DateTime.Now:HH:mm:ss}" : "";
            });

            if (!connected) return;

            // 连上就补写一次 CSV。理由：切换台可能还捏着上次退出时留下的那份
            // 文件，而「一个都不写」与「写一份空的三行」对它来说是两种状态——
            // 前者让它继续叠上一个项目已经结束的标题。
            PushCsv();

            // 令牌只有连上之后才有，第一次取名多半要等这一刻。
            RequestProjectNameRefresh();
        };

        // 切台状态：后端每次都同时给出「当前播送」和「即将切台」，直接照着渲染。
        _wsClient.ShotStateReceived += state =>
        {
            // 先记住，供「连上后」与「取名成功后」补写使用。
            _lastShot = state;

            Dispatcher.Invoke(() =>
            {
                CurrentInstruction.Text = string.IsNullOrEmpty(state.Current) ? "—" : state.Current;
                NextInstruction.Text = state.HasPending ? state.Next : "—";
                NextInstruction.Foreground = state.HasPending ? PendingBrush : IdleBrush;
            });

            PushCsv();
        };

        _wsClient.MessageReceived += (type, subType, content) =>
        {
            // 只剩采访点状态还值得占一行界面：它是包装员判断「能不能立刻切
            // 过去」的最后依据。聊天与控制权变化原来只进日志，日志区删掉后
            // 没有位置了，而它们对包装这个动作没有影响——控制权在导播手里。
            if (type != "interview_status") return;

            var statusText = subType switch
            {
                "ready" => "就绪",
                "preparing" => "准备中",
                "not_ready" => "未就绪",
                "offline" => "离线",
                _ => subType,
            };
            Dispatcher.Invoke(() => InterviewStatusText.Text = $"采访点 {content}: {statusText}");
        };

        _wsClient.SystemMessage += msg =>
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            // 登录失败也走这个事件。必须透出：否则现场只看到一直「连接中...」，
            // 完全分不清是凭据问题还是网络问题。
            Dispatcher.Invoke(() => LastUpdateText.Text = $"[系统] {msg}");
        };

        await _wsClient.ConnectAsync();
    }

    /// <summary>
    /// 把当前状态写一次 CSV，并把结果报到状态栏。
    ///
    /// 三行的映射全在这里，且与现场那份读取端按行号对齐——改任何一处顺序都
    /// 等于让所有标题错位，比改任何一行代码都要危险。
    /// </summary>
    private void PushCsv()
    {
        var csv = _csv;
        if (csv is null) return;

        // EventName 有覆盖值时第一行就定了，不看后端查回来的名字。
        var eventName = string.IsNullOrWhiteSpace(_config.EventName)
            ? (_projectName ?? "")
            : _config.EventName;

        var state = _lastShot ?? new ShotState();
        // 「即将切台」时第 2 行写 Next 而不是 Current：这是与现场一起定下的，
        // 包装人员看到的状态就是切换台该预览的那一条。
        var programName = state.HasPending ? state.Next : state.Current;
        var shotState = state.HasPending ? "即将切台" : "正在播送";

        // 同步写而不是丢到后台排队：两次写一旦顺序颠倒（例如「即将切台」晚于
        // 「正在播送」落地），切换台会一直叠着错误的那一行，而界面上看不出
        // 任何异常。最坏情况是移动重试的 300 毫秒阻塞一次接收线程，可以接受。
        var error = csv.Write(eventName, programName, shotState);

        Dispatcher.Invoke(() => ReportCsvResult(error, csv.Path));
    }

    private void ReportCsvResult(string? error, string path)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        if (error is not null)
        {
            // 原样显示写入失败的原因，并且**不弹窗**：播送中弹框会挡视线，
            // 而这一行就是包装员判断切换台有没有在读到最新状态的全部依据。
            CsvStatusText.Text = $"CSV 写入失败 {stamp}：{error}";
            CsvStatusText.Foreground = ErrorBrush;
            return;
        }

        // 连路径一起报出来：设置区收起时不看那一栏，包装员得能在状态栏
        // 确认「写到了哪里」——写错文件是比没写更难查的问题。
        CsvStatusText.Text = path.Length > 0
            ? $"CSV 已写入 {stamp} · {path}"
            : $"CSV 已写入 {stamp}";
        CsvStatusText.Foreground = OkBrush;
    }

    /// <summary>还没写过、或根本没配路径时的初始提示。</summary>
    private void ShowCsvIdle()
    {
        var path = _csv?.Path ?? "";
        if (path.Length == 0)
        {
            CsvStatusText.Text = "未配置 CSV 输出路径，切台时不会写文件（设置里可填）";
            CsvStatusText.Foreground = ErrorBrush;
            return;
        }

        CsvStatusText.Text = $"CSV 尚未写入 · {path}";
        CsvStatusText.Foreground = OkBrush;
    }

    /// <summary>发起一次（防重入的）项目名刷新。</summary>
    private void RequestProjectNameRefresh()
    {
        // 「连上」与「5 分钟轮询」可能撞在一起，两次并发查询纯属浪费。
        if (Interlocked.CompareExchange(ref _projectNameBusy, 1, 0) != 0) return;
        _ = RefreshProjectNameAsync();
    }

    private async Task RefreshProjectNameAsync()
    {
        var generation = Volatile.Read(ref _settingsGeneration);
        try
        {
            var service = _projectInfo;
            // 配了覆盖值就不必问后端：第一行已经定了，查回来的名字用不上，
            // 白白多一次可能 401 的请求。
            if (service is null || !string.IsNullOrWhiteSpace(_config.EventName)) return;

            // 令牌现取现用：WebSocketClient 只在重连时重新登录，读到的这个
            // 可能早就过期了（JWT 默认 60 分钟，而连接稳定时不会重连）。
            service.SetToken(_wsClient?.Token);

            var name = await service.GetProjectNameAsync(_config.ProjectId);

            // 换过设置就丢弃：这次查的是上一个项目。
            if (Volatile.Read(ref _settingsGeneration) != generation) return;

            // 失败（含令牌过期被吞成 null）就沿用上一次成功的名字，不清缓存、
            // 不在界面上报错。取名是 CSV 第一行的**锦上添花**，让现场的赛事
            // 标题整个消失比显示一个几分钟前的名字糟糕得多。
            if (string.IsNullOrWhiteSpace(name)) return;

            _projectName = name;
            // 补写：第一次切台常常发生在取名成功之前，不补写的话第一行要等
            // 下一次切台才填得上。
            PushCsv();
        }
        finally
        {
            Interlocked.Exchange(ref _projectNameBusy, 0);
        }
    }

    private void FillSettingsFields()
    {
        ServerUrlBox.Text = _config.ServerUrl;
        WsUrlBox.Text = _config.WsUrl;
        ProjectIdBox.Text = _config.ProjectId.ToString();
        UsernameBox.Text = _config.Username;
        PasswordBox.Password = _config.Password;
        CsvPathBox.Text = _config.CsvPath;
        EventNameBox.Text = _config.EventName;
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        var expand = SettingsPanel.Visibility != Visibility.Visible;
        // 打开时用当前生效值覆盖：上一轮编辑残留的半截内容比旧值更危险，
        // 操作员分不清那是「生效的」还是「没保存的」。
        if (expand) FillSettingsFields();
        SettingsHintText.Text = "";
        SetSettingsExpanded(expand);
    }

    private void SetSettingsExpanded(bool expanded)
    {
        SettingsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggleButton.Content = expanded ? "收起设置" : "设置";
    }

    private void OnSettingsCancel(object sender, RoutedEventArgs e)
    {
        // 只撤销编辑，面板保持展开：这里是修错字的地方，点了取消就把面板
        // 关掉等于逼人重新点一次开关。
        SettingsHintText.Text = "";
        FillSettingsFields();
    }

    /// <summary>
    /// 打开文件对话框帮操作员填 CSV 路径。
    ///
    /// 为什么必须给这个按钮：这个路径填错了**不会立刻暴露**——芯象读不到
    /// 文件时只会「标题不变化」，现场看到的是一个静止的标题，没有任何人会
    /// 联想到是包装端的路径写错了。手打长路径既慢又容易打错，尤其是
    /// `\\192.168.x.x\share\state.csv` 这种。
    ///
    /// 用 SaveFileDialog 而不是 OpenFileDialog：这里要的是**将要写入**的目标
    /// 路径，它此刻通常还不存在。OpenFileDialog 默认只列已存在的文件，会让
    /// 「文件还没建」这件事看起来像是路径无效。
    ///
    /// 只能选本机路径。写到网络共享时对话框给不了，那一栏仍需手打——
    /// 这是这个按钮的能力边界，注释里写明以免下一个人试图去"修复"它。
    /// </summary>
    private void OnBrowseCsvPath(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            // 「只列已存在」会让尚未创建的目标看起来像是无效路径。
            OverwritePrompt = false,
            FileName = Path.GetFileName(CsvPathBox.Text.Trim()),
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            Title = "选择 CSV 输出位置（芯象从这里读）",
        };

        var currentDir = Path.GetDirectoryName(CsvPathBox.Text.Trim());
        if (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
        {
            dialog.InitialDirectory = currentDir;
        }

        // 取消时对话框返回 false，直接原样保留已填内容——用户可能只是看了一眼。
        if (dialog.ShowDialog() != true) return;

        CsvPathBox.Text = dialog.FileName;
        SettingsHintText.Text = "";
    }

    private void OnSettingsSave(object sender, RoutedEventArgs e)
    {
        SettingsHintText.Text = "";

        var projectIdText = ProjectIdBox.Text.Trim();
        if (!int.TryParse(projectIdText, out var projectId) || projectId <= 0)
        {
            SettingsHintText.Text = $"项目 ID「{projectIdText}」不是一个正整数。";
            return;
        }

        var next = new AppConfig
        {
            ServerUrl = ServerUrlBox.Text.Trim(),
            WsUrl = WsUrlBox.Text.Trim(),
            ProjectId = projectId,
            // 角色不暴露在界面上：包装端在后端只有这一个身份，把它摆出来只会
            // 让操作员填成别的角色然后连不上，而现场根本猜不出该填什么。
            Role = "packaging",
            Username = UsernameBox.Text.Trim(),
            Password = PasswordBox.Password,
            CsvPath = CsvPathBox.Text.Trim(),
            EventName = EventNameBox.Text.Trim(),
        };

        if (next.ServerUrl.Length == 0 || next.WsUrl.Length == 0)
        {
            SettingsHintText.Text = "服务器地址与 WebSocket 地址不能为空。";
            return;
        }

        var error = SaveConfig(next);
        if (error is not null)
        {
            // 保存失败就**不重连**：连上的是旧配置，界面却显示新值，
            // 排查时两边对不上，比直接拒绝更费时间。
            SettingsHintText.Text = error;
            return;
        }

        _config = next;
        Interlocked.Increment(ref _settingsGeneration);

        // 换项目就要丢掉上一个项目的名字与状态：否则 CSV 第一行会一直写着
        // 上一个项目的赛事名，而界面上两栏还显示着它的机位。
        _projectName = null;
        _lastShot = null;
        CurrentInstruction.Text = "—";
        NextInstruction.Text = "—";
        NextInstruction.Foreground = IdleBrush;

        // CSV 路径、服务器地址、项目 id、赛事名覆盖都可能变了，这些对象都是
        // 按地址构造的，不重建就还在往旧地址写/读。
        RebuildBackingServices();
        RestartVersionWatch();
        ShowCsvIdle();

        SetSettingsExpanded(false);
        // ConnectWebSocket 内部会 Dispose 旧客户端并重建。
        _ = ConnectWebSocket();
    }

    protected override void OnClosed(EventArgs e)
    {
        _versionTimer?.Stop();
        _versionService?.Dispose();
        _csv?.Dispose();
        _projectInfo?.Dispose();
        _wsClient?.Dispose();
        base.OnClosed(e);
    }
}