# PulseBot

事件驱动、**适配器 + 插件**架构的 C# 聊天机器人框架。基于 .NET 10 / C# 14。

插件只接触平台无关的抽象（事件、消息段、命令上下文），完全不感知 QQ、Telegram 之类的协议细节；
适配器把某个平台的原始报文翻译成统一事件与消息段，核心不反向依赖任何适配器。

## 特性

- **事件驱动** — 所有事件实现 `IEvent`，由 `EventDispatcher` 按优先级分发给处理器，支持 `StopPropagation` 阻断传播。
- **中间件管道** — 三处洋葱式挂载点（优先级大者在外层，可前处理、改写载荷、后处理、不调续延即短路）：**事件分发链**（接入控制与会话拦截装在这里，不在处理器列表里参与排序）、**消息发送前后**（限流防刷、敏感词过滤、统一前缀、发送审计）、**插件加载与卸载前后**（加载审计、按配置禁用插件、失败补偿）。
- **命令系统** — `[OnCommand("天气")]` 标注即注册，支持别名、无前缀命令、命令名冲突优先级、命令级权限节点。
- **交互会话** — 插件可提问，用户的下一条消息自动路由回该插件，支持多轮、超时与取消，并带一个可跨命令读写的键值状态袋。粒度为 *(会话 + 用户)*：同一群里每人独立。
- **权限与接入控制** — 用户/群黑白名单、超级管理员、`RequirePermission` 组合规则，接入控制位于事件管道最前端。
- **定时任务** — 任务实现 `IScheduledTask`，用固定间隔或 Cron（5 段 / 含秒 6 段）声明触发方式；调度器串行执行并记录执行历史，带分布式锁，存储与锁都可替换为持久化实现。
- **消息段抽象** — 文本、图片、@、回复引用、表情；基类是刻意不密封的 `abstract record`，适配器可自行派生平台专属段。
- **热路径零反射** — 命令处理器在启动时扫描一次并通过 `CreateDelegate` 编译为强类型委托并缓存，分发路径上没有 `MethodInfo.Invoke`。
- **全异步 + 构造函数注入** — 所有 I/O 返回 `Task`/`ValueTask` 并传播 `CancellationToken`，服务一律通过 DI 构造。

## 当前状态

| 项目 | 状态 |
| --- | --- |
| `PulseBot.Core` | ✅ 已实现 — 事件分发、命令、权限、插件、交互会话、定时任务、消息段 |
| `PulseBot.Cli` | ✅ 已实现 — 宿主入口、DI 装配、生命周期协调 |
| `PulseBot.Adapter.Console` | ✅ 已实现 — 把标准输入输出当作聊天平台，用于本地联调与新适配器的参考实现 |
| `PulseBot.Plugins.Echo` | ✅ 已实现 — 示例插件（单轮命令 + 多轮对话两种形态） |
| `PulseBot.Adapter.OneBotV11` | 🚧 骨架 — 尚无代码 |
| `PulseBot.Admin.*` | 🚧 骨架 — 尚无代码 |

测试：186 个（Core 174 + Console 12），`dotnet test` 全绿。

> 也就是说，**目前只有控制台这一个可用适配器** —— PulseBot 现在是个能在控制台里跑通完整链路的框架，还没有接入真实平台。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（目标框架 `net10.0`，语言版本 C# 14）
- Node.js 仅在开发后台前端时需要（`PulseBot.Admin.Web` 目前是骨架）

```bash
dotnet --version   # 应输出 10.x
```

## 快速开始

```bash
git clone https://github.com/ALNthy/PulseBot.git
cd PulseBot
dotnet run --project src/PulseBot.Cli
```

启动后会出现 `你> ` 提示符，直接输入即可：

```
你> /echo 你好
机器人> 你好

你> /注册
机器人> 请输入你的昵称（发送 /cancel 放弃）
你> 小明
机器人> 你好，小明！
你> /我是谁
机器人> 你是 小明。
```

控制台适配器还有几个**本地指令**（在适配器内部消化，不会发给机器人）：

| 指令 | 作用 |
| --- | --- |
| `:help` | 查看本地指令 |
| `:session` | 显示当前会话 |
| `:switch private` / `:switch group <群号>` | 切换当前会话（用于验证「同一群里每人独立」） |
| `exit` | 结束输入（机器人继续运行，Ctrl+C 退出宿主） |

## 搭一个宿主

框架的全部能力都由 DI 容器拼装，宿主入口只负责「登记」。以下是 Cli 的完整装配（见 [`Program.cs`](src/PulseBot.Cli/Program.cs)），搭自己的宿主时逐段照抄即可：

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// 内核：事件、分发、命令、插件、会话、权限、调度
builder.Services.AddPulseBot();

// 配置绑定：没有这一步，appsettings.json 里对应节的内容不会生效
builder.Services.Configure<PermissionOptions>(builder.Configuration.GetSection(PermissionOptions.SectionName));
builder.Services.Configure<SessionOptions>(builder.Configuration.GetSection(SessionOptions.SectionName));
builder.Services.Configure<SchedulingOptions>(builder.Configuration.GetSection(SchedulingOptions.SectionName));

// 插件与定时任务：逐个显式登记，由 PluginManager / Scheduler 在启动时统一处理
builder.Services.AddPlugin<EchoPlugin>();
// builder.Services.AddScheduledTask<DailyReportTask>();

// 适配器：每个平台登记一个
builder.Services.AddSingleton<IAdapter, OneBotAdapter>();

// 中间件：三处挂载点按需登记（这里以出站为例）
builder.Services.AddOutboundMiddleware<PrefixMiddleware>();

// 宿主服务：把插件与适配器的启停、会话回收、任务调度挂到宿主生命周期上。
// 调度服务注册在最后 → 关闭时最先停止，不再拉起新一轮任务。
builder.Services.AddHostedService<PulseBotLifecycleService>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddHostedService<SchedulerHostedService>();

await builder.Build().RunAsync();
```

三点值得说明：

- **`ContentRootPath = AppContext.BaseDirectory`** —— 默认取当前工作目录，从仓库根执行 `dotnet run` 时就会去仓库根找 `appsettings.json`（不存在），配置被静默忽略。以可执行文件所在目录为准，配置文件才能稳定随程序一起被找到。
- **Core 不含后台服务** —— `PulseBot.Core` 不引用 Hosting 抽象，会话清理与任务调度都只提供「一轮即返回」的逻辑入口（`ISessionStore.CleanupExpiredAsync`、`Scheduler.RunDueTasksAsync`），由宿主的 `BackgroundService` 决定何时调用。可直接照搬 [`SessionCleanupService`](src/PulseBot.Cli/SessionCleanupService.cs) 与 [`SchedulerHostedService`](src/PulseBot.Cli/SchedulerHostedService.cs)。
- **插件与适配器都是显式登记** —— 项目引用 + DI 登记，不做运行时 DLL 扫描：引用关系由编译器校验，部署时也不会有「插件目录里少了一个 dll」这类运行期故障。

## 配置项一览

所有配置节都是可选的：不绑定就用 `Options` 类的默认值，配置写错也不会让机器人启动失败。每个 `Options` 类的 `SectionName` 常量即推荐节名。

**`Commands`** —— 控制命令怎么被识别（Cli 目前用默认值，需要时用 `Configure<CommandOptions>()` 绑定）：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `Prefix` | `"/"` | 命令前缀；设为空字符串则进入「全无前缀」模式 |
| `EnablePrefixlessCommands` | `true` | 是否允许无前缀命令（`天气 北京`）；只认首个空白片段精确相等，普通聊天不会被误判 |
| `CaseSensitive` | `false` | 命令名匹配是否区分大小写 |

**`Permissions`** —— 权限与接入控制：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `SuperAdmins` | 空 | 超管用户 id，拥有全部权限且不受白名单限制；黑名单对所有人生效 |
| `DeniedMessage` | `"你没有权限执行该命令。"` | 权限不足时的提示；设为空字符串则静默拒绝（只记日志） |
| `UserBlacklist` / `UserWhitelist` | 空 | 用户级名单；白名单**非空时**才构成允许列表 |
| `GroupBlacklist` / `GroupWhitelist` | 空 | 群级名单；私聊不受影响 |

**`Sessions`** —— 交互会话：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `IdleTimeout` | `00:30:00` | 空闲超时，按「最后使用时刻」计算而非最后发言；非正值表示不过期 |
| `PromptTimeout` | `00:05:00` | 单次提问的等待超时；非正值表示不超时 |
| `CancelCommands` | `[ "/cancel" ]` | 放弃回答的取消词，精确匹配（忽略大小写，不做前缀匹配） |
| `CancelMessage` | `"已取消。"` | 取消后的提示语 |
| `CleanupInterval` | `00:05:00` | 宿主后台清理的间隔（Core 自身不跑后台任务） |

**`Scheduling`** —— 定时任务：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `PollInterval` | `00:00:01` | 调度器轮询间隔；非正值则整个调度循环关闭 |
| `HistoryCapacity` | `200` | 每个任务保留的最近执行记录条数；非正值不保留历史 |
| `LockLease` | `00:10:00` | 分布式锁租约，应明显大于任务最长执行时间（默认实现不续期） |

**`Adapters:Console`** —— 控制台适配器：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `UserId` | `"console-user"` | 控制台用户的平台 id，即每条消息的 `SenderId`。把它填进 `Permissions:SuperAdmins`，就能在控制台里测试受权限保护的命令 |
| `UserDisplayName` | `"控制台用户"` | 展示名，仅用于日志与提示 |
| `Prompt` | `"你> "` | 输入提示符 |
| `BotPrefix` | `"机器人> "` | 机器人输出前缀 |

## 项目结构

```
src/
  PulseBot.Core/                 内核：事件、分发、命令、权限、插件、会话、调度、消息段
  PulseBot.Cli/                  宿主入口：DI 配置、加载适配器与插件
  PulseBot.Adapter.Console/      控制台适配器（参考实现）
  PulseBot.Adapter.OneBotV11/    OneBot v11 适配器（骨架）
  PulseBot.Plugins.Echo/         示例插件
  PulseBot.Admin.Shared/         后台共享契约（骨架）
  PulseBot.Admin.Infrastructure/ 后台数据访问（骨架）
  PulseBot.Admin.Api/            后台 REST API + SignalR（骨架）
tests/
  PulseBot.Core.Tests/
  PulseBot.Adapter.Console.Tests/
```

## 写一个插件

插件就是实现了 `IPlugin` 的类，所需服务通过构造函数注入：

```csharp
using Microsoft.Extensions.Logging;
using PulseBot.Core.Commands;
using PulseBot.Core.Plugins;

[PluginMetadata("Weather", Version = "0.1.0", Description = "示例：查询天气")]
public sealed class WeatherPlugin : IPlugin
{
    private readonly ILogger<WeatherPlugin> _logger;

    public WeatherPlugin(ILogger<WeatherPlugin> logger) => _logger = logger;

    [OnCommand("天气", Aliases = ["weather"], Description = "查询指定城市的天气")]
    public async ValueTask QueryAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("查询天气：{City}", context.ArgsText);
        await context.ReplyAsync($"{context.ArgsText} 今天晴。", cancellationToken);
    }
}
```

在宿主的 DI 里登记，`PluginManager` 会在启动时统一加载并扫描命令：

```csharp
builder.Services.AddPlugin<WeatherPlugin>();
```

多轮对话（提问 → 用户回答 → 处理回答）见[多轮对话与跨命令状态](#多轮对话与跨命令状态)一节，可运行的完整示例是 [`EchoPlugin`](src/PulseBot.Plugins.Echo/EchoPlugin.cs) 的 `/注册` 与 `/我是谁`。

## 写一个命令

在插件方法上标注 `[OnCommand]` 即可。框架在启动时扫描一次，用 `CreateDelegate` 把方法编译为强类型委托并缓存——分发路径上没有任何反射调用。

```csharp
[OnCommand("ping", Description = "连通性检查")]
public ValueTask PingAsync(CommandContext context, CancellationToken cancellationToken)
    => context.ReplyAsync("pong", cancellationToken);

[OnCommand("天气", Aliases = ["weather", "tianqi"], Priority = 10, Description = "查询天气")]
public async ValueTask WeatherAsync(CommandContext context, CancellationToken cancellationToken)
{
    await context.ReplyAsync($"{context.ArgsText} 今天晴。", cancellationToken);
}
```

**方法签名必须完全一致**：`ValueTask MethodAsync(CommandContext context, CancellationToken cancellationToken)`。参数类型不符的方法会在启动扫描时被拒绝，标注也就不会生效。

| 属性 | 说明 |
| --- | --- |
| `Name` | 主命令名，**不含前缀**，不能包含空白字符 |
| `Aliases` | 别名列表，与主名指向同一处理器 |
| `Priority` | 多个插件注册同名命令时的裁决优先级，数值大者胜；相同则保留先注册的 |
| `Description` | 命令说明，供后续的帮助命令与后台管理界面展示 |

命令名冲突只记录 Warning 日志，不会让宿主启动失败。命令名前缀与无前缀模式的开关见 [`Commands` 配置](#配置项一览)。

`CommandContext` 提供本次调用的全部输入：

| 成员 | 内容 |
| --- | --- |
| `ArgsText` | 命令名之后的原始文本，保留大小写与内部空白 |
| `Args` | 按空白（含全角空格）切分后的参数列表 |
| `CommandName` | 实际命中的命令名，可能是别名 |
| `Message` | 触发本次命令的完整 `MessageEvent`（发送者、会话、消息段都在这里） |
| `ReplyAsync(text, ct)` | 向消息来源回复文本，等价于 `Message.ReplyAsync` |

## 用权限保护命令

给命令加 `[RequirePermission]`，框架会在分发前判定：

```csharp
[OnCommand("ban")]
[RequirePermission("admin.ban")]
public ValueTask BanAsync(CommandContext context, CancellationToken cancellationToken) { /* ... */ }

[OnCommand("mute")]
[RequirePermission("admin.mute", "admin.ban", Mode = PermissionRequirementMode.Any)]  // 两者任有其一
public ValueTask MuteAsync(CommandContext context, CancellationToken cancellationToken) { /* ... */ }
```

未标注的命令不做任何权限检查（默认放行）。判定遵循「就近合并」：用户级、群级、全局三级授权**任一满足**即通过，超级管理员拥有全部权限。权限不足时回复 `DeniedMessage`，设为空字符串则静默拒绝。

黑白名单则由**事件管道最前端**的接入控制处理，在命令分发之前就把整条消息丢掉——名单内的用户发什么都不会传到插件（包括普通聊天）：

```json
{
  "Permissions": {
    "SuperAdmins": [ "123456789" ],
    "GroupWhitelist": [ "111222333" ]
  }
}
```

需要在业务逻辑里自己判定时，注入 `IPermissionService` 调用 `IsGrantedAsync(message, requirement, ct)`；授予与撤销（`GrantAsync` / `RevokeAsync`）面向宿主与后台管理。

## 多轮对话与跨命令状态

会话的粒度是**（会话 + 用户）**：同一群里每人一份互不干扰，同一个人在私聊与不同群里也彼此隔离。完整示例见 [`EchoPlugin`](src/PulseBot.Plugins.Echo/EchoPlugin.cs) 的 `/注册` 与 `/我是谁`。

**发起提问**——顺序有意如此：先把问句发出去，再登记等待；反过来会在发送失败时留下一个永远等不到回答的等待态。

```csharp
[OnCommand("注册", Description = "多轮对话示例：登记昵称")]
public async ValueTask RegisterAsync(CommandContext context, CancellationToken cancellationToken)
{
    Session session = await _sessions.GetOrCreateAsync(SessionKey.From(context.Message), cancellationToken);

    await context.ReplyAsync("请输入你的昵称（发送 /cancel 放弃）", cancellationToken);
    session.BeginPrompt("Echo.注册", HandleNicknameAsync);   // 下一条消息交给这个回调
}

private static async ValueTask HandleNicknameAsync(SessionReplyContext reply, CancellationToken cancellationToken)
{
    string nickname = reply.Message.PlainText.Trim();
    reply.Session.Set("nickname", nickname);
    await reply.ReplyAsync($"你好，{nickname}！", cancellationToken);
}
```

回调登记的是方法组，不参与 `[OnCommand]` 扫描，写成 `static` 即可（除非需要访问插件实例）。`SessionReplyContext` 提供用户那条消息（`Message`）、所属会话（`Session`）与 `ReplyAsync`。

**读取状态**用只读的 `FindAsync`，它**不存在时返回 `null` 而不创建会话**——没登记过的用户不该因为问了一句就被建出一份会话：

```csharp
Session? session = await _sessions.FindAsync(SessionKey.From(context.Message), cancellationToken);
if (session is not null && session.TryGet<string>("nickname", out var nickname)) { /* ... */ }
```

`Session` 同时就是一个跨命令的键值状态袋（`Set` / `TryGet<T>` / `Remove`）。`TryGet` 在类型不匹配时返回 `false` 而不抛异常：同一个键可能被新旧版本的同名命令先后写入，读到自己不认识的内容时当作「没有这个状态」处理更稳妥。

两种超时都由 [`Sessions` 配置](#配置项一览)控制：提问超时后用户的消息会按普通消息处理，**不会**被当成迟到的回答吞掉；空闲超时按「最后使用时刻」计算，用户连发普通消息不会给会话续命。

## 写一个定时任务

任务与插件相互独立：实现 `IScheduledTask`，依赖同样走构造函数注入，只是不与命令系统打交道。

```csharp
public sealed class DailyReportTask(ILogger<DailyReportTask> logger) : IScheduledTask
{
    // 固定间隔，或者 Cron（标准 5 段与含秒的 6 段都支持，时区可显式指定）。
    public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Cron("0 8 * * *");

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("开始生成日报");
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}
```

在宿主的 DI 里登记：

```csharp
builder.Services.AddScheduledTask<DailyReportTask>();
```

再配一个后台服务按 `SchedulingOptions.PollInterval` 驱动 `Scheduler` —— Core 不引用 Hosting 抽象、不自己起定时器，可直接照搬 [`SchedulerHostedService`](src/PulseBot.Cli/SchedulerHostedService.cs)。

## 写一个中间件

三处挂载点的管道形状完全一致：洋葱式，优先级大者在外层（前处理最先执行、后处理最后执行），
同优先级按注册顺序进入；**不调用 `next` 就等于短路**。

| 挂载点 | 接口 | 注册方法 | 典型用途 |
| --- | --- | --- | --- |
| 事件分发链 | `IEventMiddleware` | `AddEventMiddleware<T>()` | 接入控制、来源校验、限流；可改写事件 |
| 消息发送前后 | `IOutboundMiddleware` | `AddOutboundMiddleware<T>()` | 统一前缀、敏感词过滤、出站限流、发送审计；可改写消息、阻断发送 |
| 插件加载与卸载前后 | `IPluginLifecycleMiddleware` | `AddPluginLifecycleMiddleware<T>()` | 加载审计、按配置禁用插件、失败补偿；可跳过插件 |

以出站为例，给所有消息加前缀：

```csharp
using PulseBot.Core.Adapters;
using PulseBot.Core.Messages;

public sealed class PrefixMiddleware : IOutboundMiddleware
{
    public ValueTask<SendResult> InvokeAsync(
        OutboundMessage message,
        OutboundMiddlewareNext next,
        CancellationToken cancellationToken)
        // 改写靠 with 表达式：新实例会传给更内层的中间件与适配器。
        => next(message with { Message = Message.Text($"[PulseBot] {message.Message}") }, cancellationToken);
}
```

```csharp
builder.Services.AddOutboundMiddleware<PrefixMiddleware>();
```

三处的短路后果各不相同，写的时候要清楚：事件短路的是一整条分发链，出站短路的是**这条消息不再发出**
（记得返回失败结果，限流与拦截用 `SendFailureKind.Blocked`），插件生命周期短路的是**这个插件不被加载**。

异常语义也分两种：出站中间件抛异常会被发送器隔离并转成失败结果（fail-closed，消息不发出）；
插件生命周期中间件抛异常则向上传播到逐插件的隔离逻辑——这是刻意留的，好让中间件用 `try/catch` 包住 `next` 做失败补偿。

## 监听事件

插件面向命令，事件处理器面向**所有事件**——「每条消息都记一笔日志」「按要求拦截」这类需求写处理器而不是插件。两者可以并存于同一个程序集。

```csharp
public sealed class ChatLogHandler(ILogger<ChatLogHandler> logger) : IEventHandler<MessageEvent>
{
    public int Priority => 10;   // 数值大者先执行，默认 0；同优先级按注册顺序

    public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Sender} 说：{Text}", @event.SenderId, @event.PlainText);
        return ValueTask.CompletedTask;
    }
}
```

```csharp
builder.Services.AddEventHandler<MessageEvent, ChatLogHandler>();
```

匹配是**运行时类型精确相等**，不做基类/接口向上匹配：注册 `IEventHandler<MessageEvent>` 只会收到 `MessageEvent`。`EventContext.StopPropagation()` 可阻断后续处理器；处理器内部异常由分发器统一隔离，不会中断同批次其它处理器，也不会让宿主崩溃。

框架内建的接入控制、会话拦截、命令分发**不在处理器列表里参与排序**——它们是包在列表之外的中间件，见 [写一个中间件](#写一个中间件)。

## 常用命令

```bash
dotnet build PulseBot.slnx                     # 构建（可空警告是错误级）
dotnet test PulseBot.slnx                      # 全量测试
dotnet test PulseBot.slnx --filter "Category=Unit"
dotnet run --project src/PulseBot.Cli          # 跑起来
dotnet format PulseBot.slnx                    # 提交前格式化
```

## 架构约束

这几条是硬约束，改动时不能绕开：

- `PulseBot.Core` **不得**引用任何 `Adapter.*`、`Plugins.*`、`Admin.*` 项目，只出契约。
- 插件必须实现 `IPlugin`，禁止直接调用 Core 内部服务；插件之间不得互相引用。
- 所有事件统一实现 `IEvent`，由 `EventDispatcher` 分发；插件不得处理原始平台消息。
- 热路径（事件分发、命令匹配）禁止未缓存的反射。
- 所有 I/O 异步，禁止 `.Result` / `.Wait()`。
- 可空引用类型启用，禁止用 `!` 抑制警告。

更完整的开发约定见 [`CLAUDE.md`](CLAUDE.md)。

## 路线图

Core 的地基已完整，尚未实现的主要是：

- **限流、审计与 IP 级黑白名单** — 三处中间件管道（事件、出站、插件生命周期）已经就位，但这些具体策略尚未实现；事件侧目前只内建了接入控制与会话拦截。
- **通知类 / 请求类事件** — 目前只有 `MessageEvent`，群成员变动、戳一戳、加好友请求等尚无事件类型。
- **插件依赖解析与热重载** — `[PluginMetadata]` 的 `Dependencies` 目前仅承载元数据。
- **消息段补全** — 语音、视频、Markdown、按钮。
- **适配器状态契约** — 供后台显示在线/掉线状态。
- **持久化与多实例** — 会话、权限、调度执行历史与分布式锁目前均为内存实现（单机可用）。

按依赖关系，接入真实平台的第一步是**补齐事件类型与消息段，然后实现 `Adapter.OneBotV11`**。

## 许可证

[GNU Affero General Public License v3.0](LICENSE)（AGPL-3.0）。

你可以自由使用、修改、分发本项目，条件是衍生作品同样以 AGPL-3.0 授权并附带完整源代码。

与 GPL 的关键差别在第 13 条：**把修改后的版本部署成网络服务、让用户通过网络与它交互，同样算分发**，必须向这些用户提供获取源代码的途径。
