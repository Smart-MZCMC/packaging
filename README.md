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
  "Role": "packaging",
  "Username": "",
  "Password": "",
  "CsvPath": "",
  "EventName": ""
}
```

| 字段 | 说明 |
| :--- | :--- |
| `WsUrl` | **实际生效**的地址。反代部署下是 `ws://<域名>/ws` |
| `ProjectId` | 目标项目 id。也是 `EventName` 为空时查项目名的依据 |
| `Role` | 固定 `packaging` |
| `ServerUrl` | HTTP 接口地址，**登录时用**（`POST /api/auth/login`），也用于查项目名（`GET /api/projects`） |
| `Username` / `Password` | 登录账号。**留空表示不登录** |
| `CsvPath` | CSV 输出路径（见下节）。**留空表示未配置** |
| `EventName` | 赛事名覆盖。**留空表示按 `ProjectId` 去后端查** |

::: tip 两个「留空」不是一回事
本应用的一贯取向是**本地配置优先，没配才走后端/默认**：

- `CsvPath` 留空 → CSV **写不出去**，状态栏提示原因，其它功能照常。
  界面上会按「exe 同目录下的 `state.csv`」填上默认值。
- `EventName` 留空 → 每次需要写 CSV 时按 `ProjectId` 调 `GET /api/projects`
  取项目名当赛事名（进程内缓存，查不到就写空行）。

两个字段都不写死默认值：`config.json` 是反序列化进已有值的，**现场已有的
配置文件不会被自动补上新键**，所以留空必须是合法可运行状态。
:::

### 关于登录账号

包装端此前在服务端是**匿名的**：任何人知道那个地址就能监听整个项目的实时
消息。后端把 `REQUIRE_PROJECT_MEMBERSHIP` 打开之后，WebSocket 会校验「这个
账号是不是该项目的成员」，没有令牌的连接会在握手阶段被拒。

**账号留空时不登录，行为与改动前完全一致**，所以可以先把配置发下去、确认
现场都没受影响，再打开后端的开关。

- 每次重连都会**重新登录**：JWT 默认 60 分钟过期，长时间运行的客户端拿旧
  令牌重连会一直失败。
- 登录失败会显示在底部状态栏（`[系统] 登录失败 HTTP 401：...`），
  不会只停在「连接中...」——现场能直接看出是账号问题还是网络问题。
- 开启 HTTPS 后 `ServerUrl` 也要跟着改成 `https://`。

::: danger 启用 HTTPS 后必须把 `ws://` 改成 `wss://`
原生应用不受「混合内容」限制，但服务端若只在 443 提供 TLS，`ws://` 仍然连不上。
:::

::: tip 反代与直连的区别
- **直连**（本地开发）：`ws://<服务器IP>:3002/ws`，`ServerUrl` 同理带 `:3000`
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
├── Services/CsvWriter.cs                  # 三行固定格式的 CSV 输出（芯象读取）
├── Services/ProjectInfoService.cs         # 按 ProjectId 查项目名，带进程内缓存
├── config.json
└── PackagingApp.csproj
```

## 通信

处理的消息类型：

| type | 行为 |
| :--- | :--- |
| `shot_state` | 左栏「正在播送」= `payload.current`；右栏「即将切台」= `payload.next`（空串则显示 `—`） |
| `chat` | 显示内部消息 |
| `lock_update` | 控制权变更提示（例如另一位导播接手，`payload.reason` 为 `disconnect` 或 `timeout`） |
| `interview_status` | 采访点状态变化，含 `offline`（后端在采访端失联时自动置为离线） |
| `system` | 系统提示；**连接成功的那条带 `current_shot`**，据此在连上的一瞬间就渲染当前状态 |

连接后周期性发送心跳（`chat` + `payload.message = "heartbeat"` + `ts`）保活。
后端在入库前就会丢弃心跳，所以它不会进日志、也不计入消息统计；`ts` 供服务端
刷新「最后一次见到这个客户端」的时刻，掉线扫描判断的就是它。断线自动重连。

::: tip 中途连上也看得到当前状态
后端在握手时的欢迎消息里带上项目当前的切台状态，所以**重连或中途启动的包装端
不必等下一次切台**就能看到两栏内容。字幕与包装的准备工作正好需要提前知道
下一条是什么。
:::

## CSV 输出（芯象读取）

解说端现场出问题时，包装人员改用芯象（现场的视频切换台）自带的包装顶上。
芯象只能从外部文件拿「现在播的是什么」，所以包装端每次切台就把状态写进
`CsvPath` 指向的那个文件。

### 格式

**固定三行，一行一个字段，顺序不变**：

| 行号 | 内容 |
| :--- | :--- |
| 1 | 赛事名 |
| 2 | 项目名 |
| 3 | 切台状态 |

现场例子（赛事「绵阳中学运动会」，项目「50米」，正在播送）：

```
绵阳中学运动会
50米
正在播送
```

- **没有分隔符、没有引号、没有标题行、没有 BOM**，读取方**按行号**映射。
  值里的首尾空白会被去掉。
- **空值写成空行**（那一行存在、长度为 0），**不跳行**——跳行会让后面的
  字段全部错位。
- 值里若混进换行会被压成一个空格：文件永远只有三行。

### 写入方式

每次切台都是「写同目录的 `state.csv.tmp` → 关闭 → 原子移动覆盖
`state.csv`」，**两步之间不保留任何打开的句柄**。这样芯象随时都能单独打开
这个 CSV 读，不会因为包装端占着句柄而读不到；而且它要么读到旧的完整三行，
要么读到新的完整三行，读不到写了一半的文件。

`CsvPath` 可以指向**可写的网络共享**，父目录不存在会自动创建。

### 取名规则

| 字段 | 取值 |
| :--- | :--- |
| 第 1 行 赛事名 | `EventName` 配了就用它；没配则按 `ProjectId` 调 `GET /api/projects` 取项目名 |
| 第 2 行 项目名 | `GET /api/projects` 里 `id == ProjectId` 的 `name`，进程内缓存 |
| 第 3 行 切台状态 | 收到 `shot_state` 时取「正在播送」那一项 |

`/api/projects` 挂在 `Jwt()` 组里，所以查名要带 JWT。包装端复用
`WebSocketClient` 登录时换来的令牌（`ProjectInfoService.SetToken`），
不重复登录。查名失败一律吞掉返回空、不缓存，下一次切台还会重试——一次网络
抖动不该把「没有项目名」固化成整个进程的结论。

## 部署

**下载最新版**：本仓库的 [Releases 页](https://github.com/Smart-MZCMC/packaging/releases)
取 `packaging-<版本>-win-x64.zip`（由 `.github/workflows/release.yml` 在打
`v<版本>` tag 时自动出包）；或本地 `dotnet publish --configuration Release --output publish`。

::: warning 首次运行需要 .NET 10 桌面运行时
Release 上发的是**依赖运行时**的包，不是自包含单文件：exe 旁边那些
`.dll` / `.runtimeconfig.json` 都要留着，目标机器必须先装
[.NET 10 桌面运行时（Desktop Runtime）](https://dotnet.microsoft.com/download/dotnet/10.0)，
否则双击会弹「必须安装 .NET 桌面运行时」。只有 SDK 也行，但没必要。
:::

发布后把 `publish/` 整个目录（含 `config.json`）拷到包装机，双击 exe 即可。改服务器地址直接编辑 `config.json`，无需重新编译。
