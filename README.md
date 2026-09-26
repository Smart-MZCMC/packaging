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

`config.json`（随构建复制到输出目录，**运行期读取，改完重启 exe 即可生效，不需要重新编译**）：

```json
{
  "ServerUrl": "http://zhdb.647382.xyz",
  "WsUrl": "ws://zhdb.647382.xyz/ws",
  "ProjectId": 1,
  "Role": "packaging"
}
```

| 字段 | 说明 |
| :--- | :--- |
| `WsUrl` | **实际生效**的地址。反代部署下是 `ws://<域名>/ws` |
| `ProjectId` | 目标项目 id |
| `Role` | 固定 `packaging` |
| `ServerUrl` | **当前没有任何代码使用**——本应用只通过 WebSocket 通信，不发 HTTP 请求。留着备用 |

本应用**不使用 HTTP API**，所以只需要 WebSocket 能连通。

::: danger 启用 HTTPS 后必须把 `ws://` 改成 `wss://`
原生应用不受「混合内容」限制，但服务端若只在 443 提供 TLS，`ws://` 仍然连不上。
:::

::: tip 反代与直连的区别
- **直连**（本地开发）：`ws://<服务器IP>:3002/ws`
- **反代**（生产）：`ws://<域名>/ws`，nginx 把 `/ws` 转到 3002

两种形态都由后端首页自动识别并显示，配置照着首页「接入地址」区块填即可。
:::

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
| `shot_state` | 左栏「正在播送」= `payload.current`；右栏「即将切台」= `payload.next`（空串则显示 `—`） |
| `chat` | 显示内部消息 |
| `lock_update` | 控制权变更提示（例如另一位导播接手） |
| `interview_status` | 采访点状态变化 |

连接后周期性发送心跳（`chat` + `payload.message = "heartbeat"`）保活。
后端在入库前就会丢弃心跳，所以它不会进日志、也不计入消息统计。断线自动重连。

## 部署

发布后把 `publish/` 整个目录（含 `config.json`）拷到包装机，双击 exe 即可。改服务器地址直接编辑 `config.json`，无需重新编译。
