# smart-mzcmc-packaging

包装端 —— 给导播 B / 字幕员用的同步屏幕。

包装环节（字幕、角标、转场包装）需要跟着导播的切台节奏走。这个应用把导播的项目切换指令和内部沟通消息集中显示出来，避免包装人员靠喊话或事后才发现已经切了画面。

**C# / .NET 10 WPF** 桌面应用（`net10.0-windows`），无需登录，启动时读配置订阅项目。

## 界面

```
┌──────────────────────────────────┐
│ 包装端                ● 连接中... │
├──────────────────────────────────┤
│ 当前指令                          │
│   等待导播指令...                 │
├──────────────────────────────────┤
│ 项目切换: 无                      │
├──────────────────────────────────┤
│ 消息日志                          │
│   ...                            │
└──────────────────────────────────┘
```

- **当前指令**：显示最近一次切台/切换指令
- **项目切换**：显示当前项目切换状态
- **消息日志**：滚动显示内部通信与系统消息，便于回溯

## 配置

`config.json`（随构建复制到输出目录，可直接改）：

```json
{
  "ServerUrl": "http://127.0.0.1:3000",
  "WsUrl": "ws://127.0.0.1:3002/ws",
  "ProjectId": 1,
  "Role": "packaging"
}
```

> 注意 WebSocket 服务跑在 **3002** 端口，而 HTTP API 在 3000。

## 构建与运行

需要 **.NET 10 SDK**（Windows）。

```bash
dotnet restore
dotnet build --configuration Release
dotnet run                        # 直接运行

dotnet publish --configuration Release --output publish
```

## 目录

```
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs   # 指令区与日志区
├── Models/AppConfig.cs                    # 配置模型
├── Services/WebSocketClient.cs            # 连接、心跳、断线重连、消息分发
├── config.json
└── PackagingApp.csproj
```

## 通信

处理的消息类型：

| type | 行为 |
| :--- | :--- |
| `next_shot` | 记录导播推送的下一项 |
| `confirm_switch` | 记录确认切换，更新「当前指令」 |
| `chat` | 显示内部消息（跳过 `heartbeat` 心跳） |
| `lock_update` | 控制权变更提示（例如另一位导播接手） |
| `interview_status` | 采访点状态变化 |

连接后周期性发送心跳（`chat` + `payload.message = "heartbeat"`）保活，断线自动重连。

## 部署

发布后把 `publish/` 整个目录（含 `config.json`）拷到包装机，双击 exe 即可。改服务器地址直接编辑 `config.json`，无需重新编译。
