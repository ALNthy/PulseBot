# PulseBot

事件驱动、**适配器 + 插件**架构的 C# 聊天机器人框架。基于 .NET 10 / C# 14。

插件只接触平台无关的抽象（事件、消息段、命令上下文），完全不感知 QQ、Telegram 之类的协议细节；
适配器把某个平台的原始报文翻译成统一事件与消息段，核心不反向依赖任何适配器。

## 特性

- **事件驱动** — 所有事件实现 `IEvent`，由 `EventDispatcher` 按优先级分发给处理器，支持 `StopPropagation` 阻断传播。
- **命令系统** — `[OnCommand("天气")]` 标注即注册，支持别名、无前缀命令、命令名冲突优先级、命令级权限节点。
- **交互会话** — 插件可提问，用户的下一条消息自动路由回该插件，支持多轮、超时与取消，并带一个可跨命令读写的键值状态袋。粒度为 *(会话 + 用户)*：同一群里每人独立。
- **权限与接入控制** — 用户/群黑白名单、超级管理员、`RequirePermission` 组合规则，接入控制位于事件管道最前端。
- **消息段抽象** — 文本、图片、@、回复引用、表情；基类是刻意不密封的 `abstract record`，适配器可自行派生平台专属段。
- **热路径零反射** — 命令处理器在启动时扫描一次并通过 `CreateDelegate` 编译为强类型委托并缓存，分发路径上没有 `MethodInfo.Invoke`。
- **全异步 + 构造函数注入** — 所有 I/O 返回 `Task`/`ValueTask` 并传播 `CancellationToken`，服务一律通过 DI 构造。

## 当前状态

| 项目 | 状态 |
| --- | --- |
| `PulseBot.Core` | ✅ 已实现 — 事件分发、命令、权限、插件、交互会话、消息段 |
| `PulseBot.Cli` | ✅ 已实现 — 宿主入口、DI 装配、生命周期协调 |
| `PulseBot.Adapter.Console` | ✅ 已实现 — 把标准输入输出当作聊天平台，用于本地联调与新适配器的参考实现 |
| `PulseBot.Plugins.Echo` | ✅ 已实现 — 示例插件（单轮命令 + 多轮对话两种形态） |
| `PulseBot.Adapter.OneBotV11` | 🚧 骨架 — 尚无代码 |
| `PulseBot.Admin.*` | 🚧 骨架 — 尚无代码 |

测试：130 个（Core 118 + Console 12），`dotnet test` 全绿。

> 也就是说，**目前只有控制台这一个可用适配器** —— PulseBot 现在是个能在控制台里跑通完整链路的框架，还没有接入真实平台。

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

## 项目结构

```
src/
  PulseBot.Core/                 内核：事件、分发、命令、权限、插件、会话、消息段
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

多轮对话（提问 → 用户回答 → 处理回答）见 [`EchoPlugin`](src/PulseBot.Plugins.Echo/EchoPlugin.cs) 的 `/注册` 与 `/我是谁`。

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

- **中间件管道** — 事件预处理/后处理、插件加载前后、消息发送前后（限流、审计、IP 级黑白名单都要挂在这里）。
- **通知类 / 请求类事件** — 目前只有 `MessageEvent`，群成员变动、戳一戳、加好友请求等尚无事件类型。
- **定时任务调度器** — `IScheduledTask` + 调度器，支持持久化与分布式锁。
- **插件依赖解析与热重载** — `[PluginMetadata]` 的 `Dependencies` 目前仅承载元数据。
- **消息段补全** — 语音、视频、Markdown、按钮。
- **适配器状态契约** — 供后台显示在线/掉线状态。
- **持久化与多实例** — 会话、权限目前均为内存实现。

按依赖关系，接入真实平台的第一步是**补齐事件类型与消息段，然后实现 `Adapter.OneBotV11`**。

## 许可证

尚未指定。
