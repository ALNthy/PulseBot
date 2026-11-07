# AnBot 使用教程

这份教程带你从零走完 AnBot 的两条主线：**把机器人跑起来**（上篇）和**给它写插件**（下篇）。
全部示例都在本仓库上验证过，命令可以直接复制执行。

- **上篇 · 部署与运维**（第 1–6 章）：适合想先把机器人跑起来、接入真实平台、用上后台管理的人。
- **下篇 · 插件开发**（第 7–15 章）：适合要写功能的人。下篇的示例插件会一路长大，
  从一条 `ping` 命令长到带权限、多轮对话、定时播报的完整插件。

两篇可以分开读。上篇第 2 章的控制台联调是下篇的必备前置——写插件时全靠它来验证。

> 术语约定：本教程里 **会话**（conversation）指私聊 / 群 / 频道这类消息通道；
> **交互会话**（session）指「某个会话里的某个用户」的多轮对话状态。两者不是一回事，读代码时留意。

---

## 上篇 · 部署与运维

### 1. 准备环境

需要 **.NET 10 SDK**（AnBot 全程基于 .NET 10 与 C# 14，无法在 .NET 8/9 上运行）。

```bash
dotnet --version        # 应输出 10.x
```

拉取代码后，先确认基线是绿的：

```bash
dotnet build            # 应输出 0 警告 0 错误
dotnet test             # 350 个测试全绿（Core 193 + Console 12 + OneBot 29 + Admin 116）
```

测试通过说明本机环境没问题。构建产物在各自的 `bin/Debug/net10.0/` 下。

仓库结构（本教程会反复提到）：

| 目录 | 作用 |
| --- | --- |
| `src/AnBot.Core/` | 内核：事件、插件、命令、会话、权限、定时任务、中间件 |
| `src/AnBot.Cli/` | **宿主入口**——你要运行的就是它 |
| `src/AnBot.Adapter.Console/` | 控制台适配器：把标准输入输出当作聊天平台 |
| `src/AnBot.Adapter.OneBot/` | OneBot 12 适配器：接入真实平台 |
| `src/AnBot.Plugins.Echo/` | 示例插件，也是写新插件时最好的模板 |
| `src/AnBot.Admin.*/` | 后台管理（API + 数据层 + 共享 DTO） |
| `tests/` | 对应各项目的测试 |

### 2. 第一次对话：控制台适配器

宿主自带控制台适配器，**不接任何平台就能跑通完整闭环**。

```bash
dotnet run --project src/AnBot.Cli
```

启动后会看到类似输出：

```
控制台适配器已启动，当前会话 console/Private/console-user。
输入 exit 结束输入，输入 :help 查看本地指令。
你>
```

在 `你>` 提示符后输入：

```
你> /echo 你好
机器人> 你好
```

一条消息从输入到回复，走完了整条管道：

```
标准输入 → 控制台适配器 → 事件入口 → 接入控制 → 命令分发 → EchoPlugin → 回复 → 控制台打印
```

#### 本地指令

以 `:` 开头的行由**适配器自己消化**，不会当作消息发给机器人，因此不会和插件命令冲突：

| 指令 | 作用 |
| --- | --- |
| `:help` | 显示帮助 |
| `:session` | 显示当前会话 |
| `:switch private` | 切换到私聊会话 |
| `:switch group 123456` | 切换到群 123456 |
| `exit` | **结束输入**（机器人继续运行，按 Ctrl+C 才退出宿主） |

`exit` 只停掉输入循环，宿主本身还在跑。这是为了让你在脚本里结束喂数据后还能观察日志。
真正退出请按 **Ctrl+C**。

#### 试试示例插件

仓库自带的 Echo 插件演示了两种插件形态：

```
你> /echo 你好              # 单轮命令：直接回复参数
机器人> 你好
你> 复读 你好               # 别名；且前缀可以省略
机器人> 你好
你> /注册                   # 多轮对话：机器人提问并等待
机器人> 请输入你的昵称（发送 /cancel 放弃）
你> 小明                    # 这条消息被路由回插件
机器人> 你好，小明！
你> /我是谁
机器人> 你是 小明。
```

多轮对话的取消由框架统一接管（默认 `/cancel`，可配），插件不用自己写。

#### 换个身份 / 换个群

`console-user` 是配置里的默认身份，也是默认超级管理员——所以上面的命令畅通无阻。
用 `:switch group 123456` 切到群会话后，`SenderId` 不变但会话变了，
**交互会话是「会话 + 用户」维度的**，群里的状态和私聊里互不影响。

#### 验证「无前缀」与权限

命令默认前缀是 `/`，同时允许不带前缀（两者都能触发），这一点由 `Commands` 配置节控制。
权限方面，`console-user` 是默认超管，所以看不到拒绝提示；把 `appsettings.json` 里的
`Permissions:SuperAdmins` 改成别的值重启，再执行受保护命令就会收到拒绝提示——这正是后端权限系统的效果。

### 3. 配置文件

宿主的配置文件是 `src/AnBot.Cli/appsettings.json`，随构建复制到可执行文件目录。

> ⚠ **内容根 = 可执行文件所在目录**。程序以 `AppContext.BaseDirectory` 作为内容根，
> 所以无论你从哪个目录运行 `dotnet run`，读到的都是 `bin/.../appsettings.json`。
> 改了仓库里的源文件却没重新构建，运行时读到的还是旧副本——这个坑值得先记住。

默认配置（节选，均有中文注释）：

```jsonc
{
  "Permissions": {
    // 超级管理员列表，拥有全部权限节点；默认与控制台适配器的身份一致
    "SuperAdmins": [ "console-user" ]
  },
  "Sessions": {
    "IdleTimeout": "00:30:00",       // 交互会话空闲多久后丢弃
    "PromptTimeout": "00:05:00",     // 多轮对话等待回答的超时
    "CancelCommands": [ "/cancel" ], // 取消当前提问的指令
    "CancelMessage": "已取消。",
    "CleanupInterval": "00:05:00"    // 后台清理间隔
  },
  "Scheduling": {
    "PollInterval": "00:00:01",      // 定时任务轮询间隔
    "HistoryCapacity": 200,          // 执行历史保留条数
    "LockLease": "00:10:00"          // 分布式锁租约（多实例防重跑）
  },
  "Adapters": {
    "Console": {
      "UserId": "console-user",
      "UserDisplayName": "控制台用户"
    },
    "OneBot12": {
      "Host": "localhost",
      "Port": 6700,
      "Path": "/onebot/v12/",
      "AccessToken": "",
      "ActionTimeout": "00:00:10"
    }
  },
  "Admin": { /* 见第 5 章 */ }
}
```

配置节与代码里的 Options 类一一对应：

| 配置节 | Options 类 | 归属 |
| --- | --- | --- |
| `Permissions` | `PermissionOptions` | Core |
| `Sessions` | `SessionOptions` | Core |
| `Commands` | `CommandOptions` | Core |
| `Scheduling` | `SchedulingOptions` | Core |
| `Adapters:Console` | `ConsoleAdapterOptions` | 控制台适配器 |
| `Adapters:OneBot12` | `OneBot12Options` | OneBot 适配器 |
| `Admin` | `AdminOptions` | 后台管理 |

#### 环境变量覆盖

生产环境不要改文件，用环境变量覆盖（层级用**双下划线**）：

```bash
export Admin__Auth__SigningKey="$(openssl rand -base64 48)"
export Admin__Enabled=false
```

#### 配置改动何时生效

**全部配置都是启动快照**：程序启动时读取一次，之后不再变化。改配置必须重启进程。
唯一的例外是后台管理里的**权限名单与插件启停**，它们落库并立即生效（第 5 章）。

### 4. 接入真实平台：OneBot 12

AnBot 的 OneBot 12 适配器使用**反向 WebSocket**：**本进程监听端口，OneBot 实现端（如 NapCat、Lagrange）主动连入**。

1. 保持 `Adapters:OneBot12` 的默认配置（监听 `localhost:6700`，路径 `/onebot/v12/`）。
2. 在你的 OneBot 实现里配置反向 WebSocket：
   - 地址：`ws://localhost:6700/onebot/v12/`
   - 若设置了 `AccessToken`，OneBot 端需带同样的令牌（放在 `Authorization: Bearer <token>` 头里）。
3. 启动 AnBot，再启动 OneBot 实现端。连接建立后日志会出现连接建立记录，之后群里的消息就会进入管道。

适配器已翻译三类事件：**消息事件、通知事件、请求事件**。插件只面对平台无关的 `MessageEvent`，
换成别的平台（甚至多个平台并存）插件代码都不用改。

> 生产提示：`AccessToken` 留空只适合本机联调。对外开放时必须设置令牌，
> 并考虑用反向代理 + TLS 终止。适配器不做公网暴露假设。

**没收到消息怎么办**（按顺序排查）：

1. OneBot 实现端的连接日志里有没有「已连接」？没有就是路径或端口不对。
2. 有连接但收不到消息 → 检查 OneBot 端的**事件上报开关**是否包含群消息。
3. 收到消息但机器人不回 → 看宿主日志里有没有插件异常（插件异常不会让宿主崩溃，只会记录并跳过）。

### 5. 后台管理

后台管理与机器人**同进程**运行，共用同一个 DI 容器，因此它能看到真实的适配器、插件与调度器状态。
访问入口：`http://localhost:5187`（`Admin:Url`），健康检查在 `/health`。

把 `Admin:Enabled` 设为 `false` 就完全不挂载：不开端口、不建数据库文件。只想跑机器人时可以关掉。

#### 首次登录

首次启动时，数据库为空，会用配置里的引导账号建一个管理员：

```
用户名：admin
密码：anbot-dev        ← Admin:Auth:BootstrapPassword
```

> ⚠ **登录后第一件事就是改密码**，并把 `BootstrapPassword` 从配置里删掉。
> 引导账号只在「账号表为空」时创建，删掉配置不会影响已有账号。

签名密钥（`Admin:Auth:SigningKey`）是 JWT 的签名密钥，仓库里的是**开发默认值**，
上生产必须换成随机串（`openssl rand -base64 48`），否则任何人都能伪造管理员令牌。

#### 能做什么

| 功能 | 说明 |
| --- | --- |
| **看** | 适配器 / 插件 / 调度器的运行状态、事件与日志的实时流 |
| **管** | 账号与密码管理（改密、重置、停用、强制下线）、插件启停、权限名单热更新 |
| **推** | 通过 SignalR 实时推送状态、日志、事件三类数据 |

几条值得注意的规则：

- **不能停用自己**，也不能停用**最后一个启用的账号**——防止把自己锁在门外。
- **改密或重置密码会吊销该账号的全部刷新令牌**，需要重新登录。
- **登录限流**按账号与来源 IP 两个维度独立计数（默认 15 分钟内失败 5 次锁 15 分钟）。
  账号不存在同样计数、被锁定时仍然走一次完整的密码哈希校验，
  因此**无法从响应快慢推断用户名是否存在**。
- **插件启停是命令级的**：停用后该插件注册的命令不再触发，**事件处理器不受影响**；
  状态落库，重启后保持。
- **权限名单热更新**：后台改完**立即生效，无需重启**（数据库有记录时以数据库为准，
  库为空时用 `appsettings.json` 播种）。
- 所有敏感写操作记入 `AuditLogs`。

完整的端点清单与请求体示例见 [README 的后台管理章节](../README.md#后台管理)。

#### 用 curl 看实时推送

后台的实时流走 SignalR。浏览器里由前端处理，命令行下要用**长轮询**，
协议是四步，**少一步就什么都收不到**：

```bash
BASE=http://localhost:5187
HUB=$BASE/admin/hub

# 0) 先登录拿访问令牌（具体请求体见 README 接口表）
TOKEN=<你的访问令牌>
AUTH="Authorization: Bearer $TOKEN"

# 1) 协商，拿到 connectionToken
NEG=$(curl -s -X POST "$HUB/negotiate?negotiateVersion=1" -H "$AUTH")
CONN=$(echo "$NEG" | sed -n 's/.*"connectionToken":"\([^"]*\)".*/\1/p')

# 2) 建立连接（立即返回 200 空响应）
curl -s -m 5 "$HUB?id=$CONN" -H "$AUTH" -o /dev/null

# 3) 握手 —— 这是单独的一条 POST，漏了它服务端会一直等，十几秒后报 "Handshake was canceled."
curl -s -m 5 -X POST "$HUB?id=$CONN" -H "$AUTH" \
  -H 'Content-Type: text/plain;charset=UTF-8' \
  --data-binary $'{"protocol":"json","version":1}\x1e'

# 4) 轮询取推送（这一步会挂住直到有数据或超时）
curl -s -m 20 "$HUB?id=$CONN" -H "$AUTH" | tr '\036' '\n'
```

收到的推送格式为 `{"type":1,"target":"方法名","arguments":[...]}`，三个方法：

| 方法 | 内容 | 频率 |
| --- | --- | --- |
| `StatusChanged` | 运行状态（启动时间、在线时长、进程号、适配器数量） | 连接时立即推一份，之后每 5 秒一份 |
| `LogReceived` | 宿主日志 | 有新日志时 |
| `EventReceived` | 事件摘要（类型、发送者、会话、正文预览） | 每条进入管道的事件 |

> 注意：事件镜像挂在接入控制**内层**，因此**看不到被黑白名单拦下的消息**。
> 这是已知边界，不是 bug。

### 6. 排错手册

| 现象 | 常见原因 |
| --- | --- |
| 改了 `appsettings.json` 不生效 | 读的是可执行文件目录下的副本，重新构建；或者忘了重启（配置是启动快照） |
| 后台报「表不存在 / no such table」 | 旧数据库是在加表之前建的。删掉 `anbot-admin.db` 重启会重建（**开发期过渡方案，会丢数据**） |
| SignalR 一条推送都收不到 | 漏了第 5 章的**第 3 步握手**；或令牌没带上（Hub 需要 `Authorization`） |
| 插件命令没反应 | 命令被后台停用了？看启动日志里的「停用插件 N 个」 |
| 中文日志乱码（Windows 控制台 / 重定向） | 控制台输出是 GBK，脚本处理前先 `iconv -f GBK -t UTF-8 -c` |
| `dotnet test` 没重建 Cli | 测试项目不依赖 Cli，改完 Core 要显式 `dotnet build src/AnBot.Cli` 才能验证 |
| 推 GitHub 连接超时 | TCP 层不通，先确认代理是否开着，别误判成凭据问题 |

---

## 下篇 · 插件开发

下篇围绕一个**群助手**插件展开：先让它回答 `ping`，再逐步加上权限、多轮对话、消息段、
定时播报、中间件与测试。每一节都是完整可编译的代码，全部在仓库上验证过。

### 7. 第一个插件

#### 7.1 建项目

在 `src/` 下新建插件项目 `AnBot.Plugins.GroupHelper/AnBot.Plugins.GroupHelper.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>AnBot.Plugins.GroupHelper</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AnBot.Core\AnBot.Core.csproj" />
  </ItemGroup>

</Project>
```

**只引用 Core**——这是硬约束：插件不得引用任何适配器、后台或其它插件项目。
目标框架、语言版本、可空引用类型都由仓库根目录的 `Directory.Build.props` 统一声明，不用重复写。

#### 7.2 最小插件

```csharp
using AnBot.Core.Commands;
using AnBot.Core.Plugins;

namespace AnBot.Plugins.GroupHelper;

/// <summary>群助手：从一条 ping 命令开始。</summary>
[PluginMetadata("GroupHelper", Version = "0.1.0", Description = "群助手示例插件。")]
public sealed class GroupHelperPlugin : IPlugin
{
    /// <summary>最基本的单轮命令：收到什么就回什么。</summary>
    [OnCommand("ping", Description = "连通性检查")]
    public ValueTask PingAsync(CommandContext context, CancellationToken cancellationToken)
        => context.ReplyAsync("pong", cancellationToken);
}
```

三个要点：

- **`IPlugin`** 是插件入口，生命周期钩子 `OnLoadAsync` / `OnUnloadAsync` 都有默认空实现，按需重写。
- **`[PluginMetadata]`** 声明名称、版本、描述，供日志与后台展示。
- **`[OnCommand]`** 标注命令方法。方法签名必须严格是
  `ValueTask (CommandContext, CancellationToken)`——框架在启动时用 `CreateDelegate` 把方法绑定成
  强类型委托（热路径上不做反射调用），签名不符会在加载时被跳过并记录日志。

#### 7.3 注册到宿主

插件是**编译期显式登记**的，不做运行时 DLL 扫描——引用关系由编译器校验，
部署时不会出现「插件目录少了一个 dll」这类运行期故障。

在 `src/AnBot.Cli/AnBot.Cli.csproj` 里加项目引用，然后在 `Program.cs` 里登记：

```csharp
using AnBot.Plugins.GroupHelper;

builder.Services.AddPlugin<GroupHelperPlugin>();
```

运行 `dotnet run --project src/AnBot.Cli`，输入 `/ping` 就会回 `pong`。

> `AddPlugin<T>` 只把插件注册成 `IPlugin` 单例，**不额外注册 `T` 自身**——
> 双注册会创建两个实例，破坏「插件是容器单例」的前提。插件之间通信请走事件总线或自有服务接口。

### 8. 命令进阶

#### 8.1 别名、描述与优先级

```csharp
[OnCommand("公告", Aliases = ["announce"], Description = "发布一条 @全体成员 的公告")]
public async ValueTask AnnounceAsync(CommandContext context, CancellationToken cancellationToken)
{
    string text = context.ArgsText.Trim();     // 命令名之后的原始文本，保留内部空白与大小写
    if (text.Length == 0)
    {
        await context.ReplyAsync("用法：/公告 <内容>", cancellationToken).ConfigureAwait(false);
        return;
    }

    // ...
}
```

- `Aliases`：`/公告` 与 `/announce` 都能触发。
- `Priority`：**同一命令名被多个插件注册**时的裁决依据，数值大者胜出。默认 0。
  注意裁决**不保留被压制者**——同一个命令名注册两次，低优先级那个根本不会出现在命令表里。
- `Args`：按空白（含全角空格）切分好的参数列表；`ArgsText` 保留了原始文本。
  两者按需取用，比如 `Args[0]` 取第一个参数。

#### 8.2 命令前缀

默认前缀 `/`，同时允许无前缀（`/echo 你好` 与 `echo 你好` 等价）。
这两项由 `Commands` 配置节控制，属于全局设置，插件不需要关心。

#### 8.3 回复的两种方式

命令里回消息有两条路：

| 方式 | 语义 | 用法 |
| --- | --- | --- |
| `context.ReplyAsync(text, ct)` | 被动回复，语义上引用触发命令的那条消息 | 绝大多数命令 |
| 注入 `IMessageSender` | 主动发送，不引用任何消息 | 公告、定时播报、往别的会话发 |

`ReplyAsync` 是 `ValueTask`，会丢弃发送结果（失败已由框架统一记日志）。
**需要区分失败原因时**（比如平台风控要降级），直接注入 `IMessageSender` 并检查 `SendResult`：

```csharp
SendResult result = await _sender
    .SendAsync(context.Message.Conversation, announcement, cancellationToken)
    .ConfigureAwait(false);

if (!result.IsSuccess)
{
    _logger.LogWarning("公告发送失败：{Kind} {Error}", result.FailureKind, result.ErrorMessage);
    await context.ReplyAsync($"公告发送失败（{result.FailureKind}）。", cancellationToken).ConfigureAwait(false);
}
```

发送失败是聊天机器人的**常规业务分支**（对方被风控、连接断开、目标不存在），
所以用返回值而不是异常表达——插件不必为每次回复写 `try/catch`。
失败分类见 `SendFailureKind`（`NoAdapter` / `NotConnected` / `Rejected` / `Timeout` /
`UnsupportedSegment` / `Unknown` / `Blocked`），其中 `Blocked` 专指**框架主动拦截**
（限流、敏感词等出站中间件所为），与平台拒绝区分开。

### 9. 权限

#### 9.1 声明命令需要的权限

```csharp
[PluginMetadata("GroupHelper", Version = "0.1.0", Description = "群助手示例：公告、报名与名单。")]
public sealed class GroupHelperPlugin : IPlugin
{
    /// <summary>发公告所需的权限节点。必须是 const——特性参数要求编译期常量。</summary>
    public const string AnnouncePermission = "group-helper.announce";

    [OnCommand("公告", Aliases = ["announce"], Description = "发布一条 @全体成员 的公告")]
    [RequirePermission(AnnouncePermission)]
    public async ValueTask AnnounceAsync(CommandContext context, CancellationToken cancellationToken)
    // ...
}
```

命令分发会先查权限，未授权时**插件方法根本不会执行**，用户会收到统一提示（`DeniedMessage`）。

权限节点的命名建议：`插件名.功能名`（小写、点分），例如 `group-helper.announce`。
不要用 `admin`「管理」这类泛化名字——节点一旦对外授予就很难改。

多个节点时默认要求**全部满足**，可以切换：

```csharp
[RequirePermission("group-helper.announce", "group-helper.broadcast",
    Mode = PermissionRequirementMode.Any)]   // 任一满足即可
```

#### 9.2 授予权限的三种途径

| 途径 | 适用场景 | 生效时机 |
| --- | --- | --- |
| `Permissions:SuperAdmins` 配置 | 你自己的账号 | 重启 |
| 后台管理改名单 | 运行时调整 | **立即** |
| `IPermissionService.GrantAsync(...)` | 插件内的授权命令 | 立即 |

第三种在插件里长这样（可以写一条 `/授权 @某人 group-helper.announce` 的管理命令）：

```csharp
await _permissions.GrantAsync(
    PermissionSubject.User(targetUserId),
    GroupHelperPlugin.AnnouncePermission,
    cancellationToken);
```

`IPermissionService` 还提供 `RevokeAsync`、`ListAsync` 与 `IsGrantedAsync`，
日常运维中更推荐用后台管理操作。

### 10. 多轮对话与状态

多轮对话（提问 → 用户回答 → 处理回答）由**交互会话**承载。

#### 10.1 发起提问

```csharp
private const string SignUpPromptOwner = "GroupHelper.报名";

[OnCommand("报名", Description = "报名参加活动（多轮对话）")]
public async ValueTask SignUpAsync(CommandContext context, CancellationToken cancellationToken)
{
    Session session = await _sessions
        .GetOrCreateAsync(SessionKey.From(context.Message), cancellationToken)
        .ConfigureAwait(false);

    // 顺序有意如此：先把问句发出去，再登记等待。
    // 反过来会在发送失败时留下一个永远等不到回答的等待态。
    await context.ReplyAsync("请直接发送你的昵称，我会记进报名名单。", cancellationToken).ConfigureAwait(false);
    session.BeginPrompt(SignUpPromptOwner, HandleNicknameAsync);
}

/// <summary>报名提问的回答处理。</summary>
private async ValueTask HandleNicknameAsync(SessionReplyContext context, CancellationToken cancellationToken)
{
    string nickname = context.Message.PlainText.Trim();
    if (nickname.Length == 0)
    {
        await context.ReplyAsync("昵称不能为空，请重新发送。", cancellationToken).ConfigureAwait(false);
        return;
    }

    _roster.Add(context.Message.Conversation, context.Message.SenderId, nickname);
    await context.ReplyAsync($"已登记：{nickname}", cancellationToken).ConfigureAwait(false);
}
```

几个关键点：

- `SessionKey.From(context.Message)` = **(会话 + 用户)**。群里每人一份、互不干扰，
  私聊与各群之间也彼此隔离——在私聊里说过的内容不会泄漏到群里。
- 用户的下一条消息会被框架（`SessionPromptMiddleware`）截获并转给回调，
  **不会再走命令分发**——所以回答「报名」不会被误当成命令。
- 超时（`Sessions:PromptTimeout`，默认 5 分钟）与取消（`Sessions:CancelCommands`，默认 `/cancel`）
  都由框架接管，插件不用自己写。同一会话同时只保留一次等待，重复 `BeginPrompt` 会覆盖旧的。
- 等待态持有的是**委托**，因此无法序列化——会话默认只存在内存里，进程重启即丢。

#### 10.2 跨命令保存状态

```csharp
session.Set(NicknameStateKey, nickname);                  // 写
if (session.TryGet<string>(NicknameStateKey, out string? name)) { /* 读 */ }
session.Remove(NicknameStateKey);                          // 删
```

`FindAsync`（只读查找，**不存在时返回 null 而不创建**）与 `GetOrCreateAsync`（取或建，顺手丢弃超时会话）按需选择：
事件分发热路径用前者，主动读写用后者。

#### 10.3 ⚠ 状态放哪里：Session 还是自有服务？

这是插件开发里最容易踩的一个坑：

> **`Session` 是「个人状态」——粒度是「会话 + 用户」。
> 任何跨用户共享的数据都不能放这里。**

「一个群里所有人的报名名单」就是典型：如果用 Session 存，
每个用户只能看到自己那一份，`/名单` 永远只显示一个人。

正确做法是让插件拥有自己的领域服务，注册成**单例**：

```csharp
using System.Collections.Concurrent;
using AnBot.Core.Messages;

namespace AnBot.Plugins.GroupHelper;

/// <summary>一条报名记录。</summary>
public sealed record RosterEntry(string UserId, string Nickname);

/// <summary>报名名单存储（跨用户共享，不能放 Session）。</summary>
public interface IRosterStore
{
    void Add(ConversationId conversation, string userId, string nickname);
    IReadOnlyList<RosterEntry> List(ConversationId conversation);
}

/// <summary>进程内实现：换 Redis 或数据库只需替换本类，插件代码不用动。</summary>
public sealed class InMemoryRosterStore : IRosterStore
{
    private readonly ConcurrentDictionary<ConversationId, ConcurrentDictionary<string, string>> _roster = new();

    public void Add(ConversationId conversation, string userId, string nickname)
        => _roster
            .GetOrAdd(conversation, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal))[userId] = nickname;

    public IReadOnlyList<RosterEntry> List(ConversationId conversation)
        => _roster.TryGetValue(conversation, out ConcurrentDictionary<string, string>? members)
            ? [.. members.Select(pair => new RosterEntry(pair.Key, pair.Value))]
            : [];
}
```

配合一条 `/名单` 命令读出它：

```csharp
[OnCommand("名单", Description = "查看本会话的报名名单")]
public async ValueTask ListRosterAsync(CommandContext context, CancellationToken cancellationToken)
{
    IReadOnlyList<RosterEntry> entries = _roster.List(context.Message.Conversation);
    if (entries.Count == 0)
    {
        await context.ReplyAsync("还没有人报名。", cancellationToken).ConfigureAwait(false);
        return;
    }

    StringBuilder builder = new();
    builder.Append("报名名单（").Append(entries.Count).Append(" 人）：");
    for (int i = 0; i < entries.Count; i++)
    {
        builder.Append('\n')
            .Append(i + 1).Append(". ")
            .Append(entries[i].Nickname).Append("（").Append(entries[i].UserId).Append('）');
    }

    await context.ReplyAsync(builder.ToString(), cancellationToken).ConfigureAwait(false);
}
```

服务注册在插件自己的装配扩展里（见第 12.3 节），插件通过构造函数注入：

```csharp
public GroupHelperPlugin(
    IRosterStore roster,
    ISessionStore sessions,
    IMessageSender sender,
    ILogger<GroupHelperPlugin> logger)
```

**规则总结**：

| 数据 | 存放位置 |
| --- | --- |
| 「我正在等这个用户回答」 | `Session` |
| 「这个用户上次说自己叫什么」 | `Session` |
| 「这个群里有哪些人报了名」 | 插件自有单例服务 |
| 「全站统计」 | 插件自有单例服务（或外部存储） |

服务必须是**线程安全**的：`Session` 与插件单例都会被并发访问。

### 11. 消息段

插件操作的是平台无关的 `MessageSegment` 抽象，适配器负责翻译成各平台的真实格式。

| 段类型 | 说明 |
| --- | --- |
| `TextSegment` | 文本 |
| `ImageSegment` | 图片（URL 或平台文件标识） |
| `AtSegment` | @某人（`TargetId`） |
| `MentionAllSegment` | @全体成员 |
| `ReplySegment` | 引用某条消息 |
| `FaceSegment` | 表情 |
| `AudioSegment` / `VideoSegment` / `FileSegment` | 语音 / 视频 / 文件 |
| `LocationSegment` | 位置 |

#### 11.1 构造与拼接

```csharp
// 单段：字符串可以直接赋给 Message（隐式转换）
Message hello = "你好";

// 多段：构造时一次传入
Message announcement = new Message(new MentionAllSegment(), new TextSegment(" 今晚八点开黑"));

// 拼接：用 + 或 Concat
Message full = announcement + Message.Text("（不见不散）");
```

#### 11.2 转成纯文本

`Message` 在需要时会渲染成纯文本（图片等以占位符表示），用于日志、命令解析与纯文本平台降级：

```csharp
string text = message.ToString();      // 渲染为纯文本
string plain = @event.PlainText;       // MessageEvent 上的快捷方式，等价于 Message.ToString()
```

命令解析读的就是 `PlainText`。

#### 11.3 主动发送到别的会话

`IMessageSender.SendAsync(conversation, message, ct)` 可以把消息发到**任意**会话——
构造一个 `ConversationId` 即可，这正是下一章定时播报的基础：

```csharp
var target = new ConversationId("onebot", ConversationType.Group, "123456");
await sender.SendAsync(target, Message.Text("跨会话发送"), cancellationToken);
```

### 12. 定时任务

#### 12.1 写一个任务

任务不必是插件，实现 `IScheduledTask` 即可：

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Core.Adapters;
using AnBot.Core.Messages;
using AnBot.Core.Scheduling;

/// <summary>每天早八点把报名人数播报回群里。</summary>
public sealed class DailyRosterTask : IScheduledTask
{
    private readonly IRosterStore _roster;
    private readonly IMessageSender _sender;
    private readonly GroupHelperOptions _options;
    private readonly ILogger<DailyRosterTask> _logger;

    public DailyRosterTask(
        IRosterStore roster,
        IMessageSender sender,
        IOptions<GroupHelperOptions> options,
        ILogger<DailyRosterTask> logger)
    {
        // ... 空检查后赋值
    }

    /// <summary>每天 08:00 执行一次（标准 5 段 Cron）。</summary>
    public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Cron("0 8 * * *");

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_options.ResolveAnnounceTarget() is not ConversationId target)
        {
            // 没配置播报目标不是错误，是不启用该功能——只记一条日志。
            _logger.LogInformation("未配置播报目标，跳过本次报名播报。");
            return;
        }

        // 与插件注入的是同一个单例，因此这里看得到插件写进去的报名数据。
        int count = _roster.List(target).Count;

        SendResult result = await _sender
            .SendAsync(target, Message.Text($"早上好，目前共有 {count} 人报名。"), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("报名播报发送失败：{Kind} {Error}", result.FailureKind, result.ErrorMessage);
        }
    }
}
```

调度方式两种：

```csharp
public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(30));
public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Cron("0 8 * * *");
```

Cron 是标准 5 段格式（分 时 日 月 周）。**时区建议显式指定**，避免服务器时区与预期不符
（`Cron` 有第二个可选参数接受 `TimeZoneInfo`）。

#### 12.2 任务规则

- `ExecuteAsync` 抛异常会被调度器捕获并记入执行历史，**不会**拖垮调度循环。
- 任务有**分布式锁**（`Scheduling:LockLease`）：多实例部署时同一任务只会被一个实例执行。
- 执行历史保留在内存里（`Scheduling:HistoryCapacity` 条），可在后台查看。
- **同一个类不要既登记成插件又登记成任务**——那是两个独立实例。要共享状态就拆成两个类，
  注入同一份领域服务（例子里插件与任务共享 `IRosterStore` 就是这么做的）。

#### 12.3 注册与配置

把插件、自有服务和任务写进一个装配扩展，调用方一行接入：

```csharp
using Microsoft.Extensions.DependencyInjection;
using AnBot.Core.Plugins;
using AnBot.Core.Scheduling;

/// <summary>群助手插件的装配扩展。</summary>
public static class GroupHelperServiceCollectionExtensions
{
    public static IServiceCollection AddGroupHelperPlugin(this IServiceCollection services)
    {
        // 插件自有服务与插件的注册写在一起：IRosterStore 是单例，
        // 插件与下面的定时任务注入的是同一个实例，状态才共享。
        services.AddPlugin<GroupHelperPlugin>(plugin => plugin.AddSingleton<IRosterStore, InMemoryRosterStore>());

        // 任务不必是插件，单独登记；它复用上面已注册的 IRosterStore。
        services.AddScheduledTask<DailyRosterTask>();

        return services;
    }
}
```

宿主侧只加两行——登记插件、绑定配置：

```csharp
builder.Services.AddGroupHelperPlugin();
builder.Services.Configure<GroupHelperOptions>(
    builder.Configuration.GetSection(GroupHelperOptions.SectionName));
```

配置类（`IOptions<T>` 模式，不直接读 `IConfiguration`）：

```csharp
using AnBot.Core.Messages;

/// <summary>群助手插件的配置节：appsettings.json 里的 "Plugins:GroupHelper"。</summary>
public sealed class GroupHelperOptions
{
    public const string SectionName = "Plugins:GroupHelper";

    /// <summary>播报目标：平台名，需与适配器 IAdapter.Platform 一致（OneBot 适配器为 "onebot"）。</summary>
    public string AnnouncePlatform { get; set; } = "onebot";

    /// <summary>播报目标：会话类型（Private / Group / Channel）。</summary>
    public ConversationType AnnounceType { get; set; } = ConversationType.Group;

    /// <summary>播报目标：群号或用户号。留空表示不做定时播报。</summary>
    public string? AnnounceTargetId { get; set; }

    /// <summary>把三个播报字段合成会话标识；未配置目标时返回 null。</summary>
    public ConversationId? ResolveAnnounceTarget()
        => string.IsNullOrWhiteSpace(AnnounceTargetId)
            ? null
            : new ConversationId(AnnouncePlatform, AnnounceType, AnnounceTargetId);
}
```

对应的配置：

```json
{
  "Plugins": {
    "GroupHelper": {
      "AnnouncePlatform": "onebot",
      "AnnounceType": "Group",
      "AnnounceTargetId": "123456"
    }
  }
}
```

### 13. 中间件与事件处理器

框架有三个中间件挂载点，加上事件处理器，一共四种扩展方式。它们都是**横切**能力：
不用改插件代码，就能给整条管道加行为。

| 扩展点 | 挂在哪 | 典型用途 |
| --- | --- | --- |
| `IEventMiddleware` | 事件分发链 | 限流、审计、来源校验、统计 |
| `IOutboundMiddleware` | 消息出站链 | 敏感词拦截、统一前缀/尾注、发送审计 |
| `IPluginLifecycleMiddleware` | 插件加载 / 卸载 | 加载审计、按配置禁用插件、耗时统计 |
| `IEventHandler<TEvent>` | 事件链的终点 | 「对事件本身做点什么」的业务逻辑 |

三者都是洋葱式管道：**`Priority` 数值大者在外层**（前处理最早、后处理最晚），同优先级按注册顺序。
都由 DI 构造且必须是**单例**——管道在容器构建阶段就绑定成委托，之后不再从容器解析。

#### 13.1 出站中间件：敏感词拦截

```csharp
using AnBot.Core.Adapters;
using AnBot.Core.Messages;

/// <summary>出站敏感词拦截：命中即阻断，消息不会发往平台。</summary>
public sealed class OutboundGuardMiddleware : IOutboundMiddleware
{
    private static readonly string[] BannedWords = ["违禁词", "广告链接"];

    private readonly ILogger<OutboundGuardMiddleware> _logger;

    public OutboundGuardMiddleware(ILogger<OutboundGuardMiddleware> logger) => _logger = logger;

    /// <summary>数值大者在外层：拦截要尽早，先于其它出站中间件生效。</summary>
    public int Priority => 1000;

    public ValueTask<SendResult> InvokeAsync(
        OutboundMessage message,
        OutboundMiddlewareNext next,
        CancellationToken cancellationToken)
    {
        string text = message.Message.ToString();
        foreach (string word in BannedWords)
        {
            if (text.Contains(word, StringComparison.Ordinal))
            {
                _logger.LogWarning("出站消息命中敏感词 {Word}，已阻断；目标 {Conversation}",
                    word, message.Conversation);

                // 不调用 next 即阻断发送。必须自行构造失败结果并返回，
                // 否则调用方会拿到一个没有来源的默认值。
                return ValueTask.FromResult(SendResult.Fail(
                    SendFailureKind.Blocked,
                    $"消息包含受限内容：{word}"));
            }
        }

        // 默认路径：把消息（或改写后的新实例）交给下一环。
        return next(message, cancellationToken);
    }
}
```

两条铁律：

- **默认路径必须是 `await next(...)`**。不调用续延是短路的唯一手段，也是忘记调用时最难排查的故障
  （消息静默消失，没有任何报错）。
- **阻断时必须自己造失败结果**，分类用 `SendFailureKind.Blocked`。
- 中间件抛出的异常由发送器统一捕获并记录，随后返回 `Unknown` 失败结果——消息**不会**发出（fail-closed）。
  要「失败也放行」必须自己 `try/catch`。

#### 13.2 出站中间件：改写消息

```csharp
/// <summary>统一给所有出站消息加尾注：演示「改写」——把新实例传给续延。</summary>
public sealed class SignatureMiddleware : IOutboundMiddleware
{
    /// <summary>数值小者在内层：先让拦截类中间件判断原文，再做改写。</summary>
    public int Priority => -1000;

    public ValueTask<SendResult> InvokeAsync(
        OutboundMessage message,
        OutboundMiddlewareNext next,
        CancellationToken cancellationToken)
    {
        // OutboundMessage 是 record：改写用 with 表达式即可，不必改动原实例。
        OutboundMessage signed = message with
        {
            Message = message.Message.Concat(Message.Text("\n—— 由群助手发送")),
        };

        return next(signed, cancellationToken);
    }
}
```

#### 13.3 插件生命周期中间件

```csharp
using AnBot.Core.Plugins;

/// <summary>插件加载/卸载审计：记录每个插件的耗时与结果。</summary>
public sealed class PluginAuditMiddleware : IPluginLifecycleMiddleware
{
    private readonly ILogger<PluginAuditMiddleware> _logger;
    private readonly TimeProvider _timeProvider;

    public PluginAuditMiddleware(ILogger<PluginAuditMiddleware> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async ValueTask InvokeAsync(
        PluginLifecycleContext context,
        PluginLifecycleNext next,
        CancellationToken cancellationToken)
    {
        long startedAt = _timeProvider.GetTimestamp();

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // PluginManager 本就会逐插件隔离异常（宿主不会崩），
            // 这里捕获只是为了补一条带插件名的失败日志，因此记录后原样抛出。
            _logger.LogError(ex, "插件 {Plugin} 在 {Phase} 阶段失败", context.PluginName, context.Phase);
            throw;
        }

        _logger.LogInformation("插件 {Plugin} {Phase} 完成，耗时 {Elapsed:F0} ms",
            context.PluginName, context.Phase, _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
    }
}
```

不调用续延即**跳过该插件**（加载阶段跳过 = 它的命令不会注册；卸载阶段跳过 = 不执行清理）。
与事件侧刻意不同：这里的异常**不捕获**，交给 `PluginManager` 逐插件隔离——
这样中间件可以用 `try/catch` 包住续延做失败补偿。

#### 13.4 事件处理器

```csharp
using System.Collections.Concurrent;
using AnBot.Core.Dispatching;
using AnBot.Core.Events;
using AnBot.Core.Messages;

/// <summary>统计每个会话收到的普通消息条数。</summary>
/// <remarks>
/// 命令消息在中间件层被消费后不会再流到处理器（命令分发会阻断传播），
/// 因此这里数到的是「没有命中任何命令的普通消息」。
/// 要统计全部消息，应把逻辑写成优先级更大的 IEventMiddleware。
/// </remarks>
public sealed class MessageCountHandler : IEventHandler<MessageEvent>
{
    private readonly ConcurrentDictionary<ConversationId, long> _counts = new();
    private readonly ILogger<MessageCountHandler> _logger;

    public MessageCountHandler(ILogger<MessageCountHandler> logger) => _logger = logger;

    public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        long count = _counts.AddOrUpdate(@event.Conversation, 1, (_, current) => current + 1);
        _logger.LogDebug("会话 {Conversation} 累计收到 {Count} 条普通消息", @event.Conversation, count);
        return ValueTask.CompletedTask;
    }
}
```

处理器是管道终点，可以用 `context.StopPropagation()` 阻断**同批次后续处理器**。

#### 13.5 注册

```csharp
builder.Services.AddOutboundMiddleware<OutboundGuardMiddleware>();
builder.Services.AddOutboundMiddleware<SignatureMiddleware>();
builder.Services.AddPluginLifecycleMiddleware<PluginAuditMiddleware>();
builder.Services.AddEventHandler<MessageEvent, MessageCountHandler>();
builder.Services.AddEventMiddleware<MyOwnMiddleware>();
```

#### 13.6 优先级契约

框架内建的三个事件中间件占用 `int.MaxValue` 起的三档，顺序只由数值决定：

| 中间件 | 优先级 | 作用 |
| --- | --- | --- |
| `AccessControlMiddleware` | `int.MaxValue` | 黑白名单拦截（最外层，最先执行） |
| `SessionPromptMiddleware` | `int.MaxValue - 1` | 多轮对话回答截获 |
| `CommandMiddleware` | `int.MaxValue - 2` | 命令匹配与分发 |

**不要占用这三档**，否则会打乱「先拦截 → 再截获回答 → 再匹配命令」的顺序。
想在最外层做观测，用 `int.MaxValue` 以下、但明显大于 0 的值（如 1000）。

另一条已知边界：事件侧的异常由分发器统一捕获记录、管道自然终止；
出站侧异常则由发送器捕获并返回失败。两侧都是 **fail-closed**。

### 14. 测试

插件必须带测试（新增插件要包含对应测试项目）。下面两种测试形态分别覆盖不同层。

#### 14.1 单元测试：直接驱动命令

不启动宿主、不连平台，直接构造上下文调用插件方法：

```csharp
using AnBot.Core.Adapters;
using AnBot.Core.Commands;
using AnBot.Core.Events;
using AnBot.Core.Messages;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GroupHelperPluginTests
{
    [Fact]
    public async Task Ping_命令回复_pong()
    {
        var sender = new FakeSender();
        var plugin = new GroupHelperPlugin(/* 依赖见下 */);

        MessageEvent message = new()
        {
            Conversation = new ConversationId("test", ConversationType.Private, "u1"),
            Message = Message.Text("/ping"),
            MessageId = "msg-1",
            SenderId = "u1",
            ReplyChannel = sender,          // 装配回复通道，ReplyAsync 才能工作
        };

        var context = new CommandContext(message, "ping", argsText: "", args: []);

        await plugin.PingAsync(context, CancellationToken.None);

        Assert.Equal(["reply:msg-1:pong"], sender.Replies);
    }

    /// <summary>记录发送内容的发送器替身。</summary>
    private sealed class FakeSender : IMessageSender
    {
        public List<string> Replies { get; } = [];

        public ValueTask<SendResult> SendAsync(
            ConversationId conversation, Message message, CancellationToken cancellationToken)
        {
            Replies.Add($"send:{message}");
            return ValueTask.FromResult(SendResult.Ok());
        }

        public ValueTask<SendResult> ReplyAsync(
            ConversationId conversation, string replyToMessageId, Message message, CancellationToken cancellationToken)
        {
            Replies.Add($"reply:{replyToMessageId}:{message}");
            return ValueTask.FromResult(SendResult.Ok());
        }
    }
}
```

要点：`MessageEvent` 是 record，`ReplyChannel` 必须装配，否则 `ReplyAsync` 会抛
`InvalidOperationException`（这正是为了防止「事件没有回复路径却以为发出去了」）。
`CommandContext` 的构造函数是公开的，就是为了让测试能独立调用处理器。

#### 14.2 端到端测试：把管道装起来

分层单测各自验证一环，但**接缝**（谁先执行、谁阻断谁）只有把整条管道装起来才能验证。
仓库里的 `tests/AnBot.Core.Tests/EndToEndFlowTests.cs` 是一个完整范例，
它手工装配了「接入控制 → 命令分发 → 普通监听器」的真实管道：

```csharp
var permissionOptions = new PermissionOptions();
var accessPolicy = new InMemoryAccessPolicy(Options.Create(permissionOptions));
var permissions = new InMemoryPermissionService(accessPolicy, NullLogger<InMemoryPermissionService>.Instance);

var commandDispatcher = new CommandDispatcher(
    Options.Create(new CommandOptions()), permissions, new PluginStateRegistry(),
    NullLogger<CommandDispatcher>.Instance);
commandDispatcher.RegisterPluginCommands(plugin);      // 注册插件命令

var publisher = new EventPublisher(
    new EventDispatcher(
        [new HandlerRegistration<MessageEvent>(handlers)],
        [
            new CommandMiddleware(commandDispatcher),                                  // int.MaxValue - 2
            new AccessControlMiddleware(accessPolicy, NullLogger<AccessControlMiddleware>.Instance),  // int.MaxValue
        ],
        NullLogger<EventDispatcher>.Instance),
    () => sender);

await publisher.PublishAsync(CreateMessage("/ban 某人"), CancellationToken.None);
```

注意中间件是**故意不按优先级顺序传入**的——排序由 `EventDispatcher` 负责，
若它失效这组测试会立刻失败。这正是端到端测试该覆盖的接缝。

测试里常用的替身：

- `NullLogger<T>.Instance` —— 不需要断言的日志。
- 手工实现的 `IMessageSender` 替身 —— 记录每一条发出的消息（见 14.1）。
- `Options.Create(...)` —— 直接构造配置对象，不读文件。

所有单元测试都要标注 `[Trait("Category", "Unit")]`；`dotnet test --filter "Category=Unit"`
可以只跑快速单测。

#### 14.3 提交前

```bash
dotnet build                          # 必须 0 警告 0 错误
dotnet test                           # 全绿
dotnet format                         # 提交前格式化
```

### 15. 下一步

到这里你已经掌握了插件开发的全部要素。继续深入时，值得一读源码的地方：

- `src/AnBot.Plugins.Echo/EchoPlugin.cs` —— 最小完整的插件模板（129 行）。
- `src/AnBot.Core/Dispatching/` —— 事件分发、命令分发的实现与注释，
  尤其是热路径上「缓存委托、不做反射调用」的做法。
- `src/AnBot.Adapter.OneBot/` —— 想接新平台时，这是适配器的参考实现。
- `tests/` —— 每一条设计约束几乎都有对应测试，是理解「为什么这样设计」的最快途径。

写新插件时，建议直接从 `AnBot.Plugins.Echo` 复制一份改名开始——
项目结构、注释风格、测试布局都可以照搬。
