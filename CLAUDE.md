# CLAUDE.md — AnBot 开发指南

减少常见 LLM 编码错误的行为准则。可按需与项目特定指令合并。

**权衡：** 这些准则偏向谨慎而非速度。对于琐碎任务，请自行判断。

## 1. 编码前先思考

**不要假设。不要隐藏困惑。把权衡摆出来。**

在实现之前：
- 明确陈述你的假设。如果不确定，就问。
- 如果存在多种解释，把它们列出来——不要默默选择一种。
- 如果有更简单的方法，就说出来。该反对时就反对。
- 如果有不清楚的地方，停下来。指出哪里令人困惑。提问。

## 2. 简单优先

**用最少的代码解决问题。不做任何推测性工作。**

- 不实现超出要求的功能。
- 不为一次性代码创建抽象。
- 不添加未被要求的“灵活性”或“可配置性”。
- 不为不可能发生的场景编写错误处理。
- 如果你写了 200 行，而它本可以只有 50 行，重写它。

问自己：“一位资深工程师会说这过度复杂吗？”如果会，就简化。

## 3. 外科手术式修改

**只触碰你必须触碰的。只清理你自己制造的混乱。**

编辑现有代码时：
- 不要“改进”相邻代码、注释或格式。
- 不要重构没有坏掉的东西。
- 匹配现有风格，即使你会用不同方式做。
- 如果注意到无关的死代码，提出来——不要删除它。

当你的改动产生孤儿时：
- 移除因你的改动而不再使用的导入/变量/函数。
- 不要移除预先存在的死代码，除非被要求。

检验标准：每一行被修改的代码都应能直接追溯到用户的请求。

## 4. 目标驱动执行

**定义成功标准。循环直到验证通过。**

将任务转化为可验证的目标：
- “添加验证” → “为无效输入编写测试，然后让它们通过”
- “修复 bug” → “编写一个能复现它的测试，然后让它通过”
- “重构 X” → “确保重构前后测试都通过”

对于多步骤任务，简要说明计划：
```
1. [步骤] → 验证：[检查]
2. [步骤] → 验证：[检查]
3. [步骤] → 验证：[检查]
```

强有力的成功标准让你能独立循环。弱标准（“让它能工作”）需要不断澄清。

---

**如果出现以下情况，说明这些准则正在起作用：** diff 中不必要的改动更少，因过度复杂而重写更少，以及澄清性问题出现在实现之前而不是犯错之后。

## 项目概览
AnBot 是一个 C# 版、事件驱动的聊天机器人框架。基于 **.NET 10 (LTS)** + Microsoft.Extensions.DependencyInjection，
采用“适配器 + 插件”架构，支持多平台事件接入、后台管理、热重载与水平扩展。

## 架构约束（最高优先级）
**冲突裁决：** 上方通用准则与本文件的项目约束冲突时，以项目约束为准；实现范围以用户当次请求为准。架构歧义必须先问再动手；实现细节自行决定，并在回复中说明所作假设。

- `AnBot.Core` 不得引用任何 `Adapter.*`、`Plugins.*`、`Admin.*` 项目。
- 插件必须实现 `IPlugin` 接口，禁止直接调用 Core 内部服务。
- 所有事件统一实现 `IEvent`，由 `EventDispatcher` 分发，插件不得处理原始消息。
- 后台管理通过 `IManagementService` 与核心交互，不得侵入消息处理热路径。
- 热路径（事件分发、命令匹配）禁止使用未缓存的反射；必须使用缓存的委托或源生成器。
- 所有 I/O 操作必须异步，禁止 `.Result` / `.Wait()`。
- 目标框架统一为 `net10.0`，语言版本使用 C# 14。

## 解决方案结构
- `src/AnBot.Core/` — 事件、插件管理、适配器接口、命令分发、会话、权限、中间件
- `src/AnBot.Adapter.OneBot/` — OneBot 协议实现
- `src/AnBot.Cli/` — 宿主入口，DI 配置，加载适配器与插件
- `src/AnBot.Plugins.*/` — 可独立分发的插件项目
- `src/AnBot.Admin.Shared/` — 后台共享 DTO、枚举
- `src/AnBot.Admin.Infrastructure/` — EF Core 10、仓储、HybridCache
- `src/AnBot.Admin.Api/` — 后台 REST API（类库，由 `AnBot.Cli` 同进程挂载）
- `src/AnBot.Admin.Web/` — 管理界面：Blazor WebAssembly + MudBlazor（独立构建，发布时复制到宿主 `wwwroot`）
- `tests/` — 对应测试项目

## 常用命令
```bash
dotnet build                     # 必须通过才算完成
dotnet test                      # 全量测试
dotnet test --filter "Category=Unit"  # 快速单元测试
dotnet test --filter "Category=Integration"  # 后台 API 集成测试
dotnet run --project src/AnBot.Cli   # 宿主；后台管理随它一同启动
cd docker && docker compose up -d --build   # 容器部署（首次先 cp .env.example .env）
dotnet run --project src/AnBot.Admin.Web   # 管理界面开发服务器（http://localhost:5242）
./scripts/start-dev.sh                     # 一键启动宿主 + 管理界面（Windows 用 scripts\start-dev.cmd）
dotnet format                    # 提交前格式化
```

## C# / .NET 10 编码规范

### 异步与性能
- 所有 I/O 方法返回 `Task<T>` 并使用 `await`，禁止 `.Result` / `.Wait()`。
- 所有异步方法必须传播 `CancellationToken` 参数。
- 热路径避免分配：优先使用 `Span<T>`、`ValueTask`、对象池。
- 事件分发、命令调用使用缓存的委托或源生成器，禁止 `MethodInfo.Invoke`。
- 优先使用 .NET 10 内置异步可枚举支持，避免引入 `System.Linq.Async`。
- 后台服务使用 `BackgroundService`，其 `ExecuteAsync` 全部内容已在后台线程运行。

### 依赖注入
- 所有服务通过构造函数注入，禁止在方法内 `new` 服务实例。
- 配置统一使用 `IOptions<T>` 模式，禁止直接读 `IConfiguration` 字典。
- 插件通过 `IServiceCollection` 注册自身服务，由框架统一构建容器。
- 缓存优先使用 `HybridCache`，统一 L1 内存 + L2 分布式缓存。

### 命名与风格
- 类、方法、属性：`PascalCase`；参数、局部变量：`camelCase`；私有字段：`_camelCase`。
- 异步方法必须以 `Async` 结尾；接口以 `I` 开头。
- 可空引用类型必须启用，禁止使用 `!` 抑制警告，除非有明确理由。
- 使用 `ILogger<T>` 结构化日志，禁止 `Console.WriteLine`。
- 可使用 C# 14 的 `field` 关键字、空条件赋值、扩展成员简化代码。

### 异常处理
- 禁止捕获通用 `Exception` 后静默吞掉。
- 插件内部异常由框架统一捕获并记录，不得导致宿主崩溃。
- 对外 API 返回统一错误模型，禁止泄露堆栈。

## 核心模式

### 事件分发
- 所有事件实现 `IEvent`，由 `EventDispatcher` 路由。
- 支持优先级与阻断：多个处理器匹配时按优先级执行，可调用 `StopPropagation()`。
- 中间件管道：事件预处理/后处理、插件加载前后、消息发送前后。

### 插件开发
- 插件入口：`class XxxPlugin : IPlugin`。
- 命令标注：`[OnCommand("天气")]`，支持规则与权限组合。
- 元数据：使用 `[PluginMetadata]` 声明名称、版本、描述、依赖。
- 配置：定义 `XxxConfig` 并使用 `IOptions<XxxConfig>` 注入。
- 插件间通信：通过事件总线或暴露服务，禁止直接引用其他插件项目。
- 源生成器必须实现 `IIncrementalGenerator`，禁止非增量式生成器。

### 会话与权限
- 会话管理：多轮对话、超时销毁、状态持久化（内存/Redis/DB）。
- 权限系统：命令级、群级、用户级权限节点，支持 `Require` 组合规则。
- 黑名单/白名单：用户、群级别已实现，统一在中间件中处理；IP 级别暂无数据来源（OneBot 12 报文不含来源 IP，只能做连接级准入，见 README 路线图）。

### 消息段
- 统一 `MessageSegment` 抽象；已有文本、图片、@、@全体成员、回复引用、表情、语音、视频、文件、位置。
- Markdown、按钮、云文档等段类型待补——OneBot 12 无标准对应段，只能由支持的平台结构化翻译，其余平台降级为纯文本。
- 适配器负责将平台消息转换为 `MessageSegment`，插件只操作抽象类型。
- WebSocket 通信优先使用 .NET 10 的 `WebSocketStream`。

### 定时任务
- 内置 Cron/间隔任务，支持持久化与分布式锁。
- 任务实现 `IScheduledTask`，由调度器统一管理，可在后台查看执行历史。

## 后台管理
- `AnBot.Admin.Api` 由 `AnBot.Cli` 同进程挂载（两者共用同一个 DI 容器），通过 `IManagementService` 调用核心。
- REST 接口使用 MVC 控制器（`ControllerBase` + `[ApiController]`），在类库中通过 `AddApplicationPart` 显式注册；错误响应统一为 `ApiError`。
- 管理界面使用 Blazor WebAssembly + MudBlazor：开发期由 DevServer 独立运行（5242），生产期发布到宿主 `wwwroot` 同源托管。
- 实时推送使用 SignalR，前端通过 WebSocket 接收日志、事件、状态。
- 轻量级实时流可使用 `TypedResults.ServerSentEvents`。
- 配置修改写入数据库，通过 Redis Pub/Sub 通知核心热重载。
- 所有敏感操作记录到 `AuditLogs`，不可篡改。
- 认证：JWT + Refresh Token；授权：RBAC，接口用 `[Authorize(Policy = "...")]`。
- EF Core 10 仅支持 .NET 10，使用原生 JSON 类型与 `SqlVector<float>` 时需确认数据库版本。

## 测试
- 使用 xUnit + Moq + Testcontainers。
- 单元测试覆盖 Core 的事件分发、插件加载、权限规则。
- 集成测试覆盖适配器与后台 API；后台 API 使用 `Microsoft.AspNetCore.TestHost`（进程内 TestServer + 临时 SQLite），标注 `[Trait("Category", "Integration")]`。
- 新增插件必须包含对应测试项目。
- 提交前必须 `dotnet test` 全部通过。

## 安全与运维
- 敏感配置使用 ASP.NET Core Data Protection 加密。
- 全局限流与防刷，基于用户/群/全局。
- 健康检查：`/health`、`/ready`；指标：Prometheus；追踪：OpenTelemetry。
- 优雅关闭：处理完进行中事件再退出。
- 多实例：Redis 背板、状态共享、分布式锁。
- Native AOT 可用于 CLI 与冷启动敏感场景，但需验证反射与源生成器兼容性。

## .NET 10 迁移注意
- EF Core 10 仅限 .NET 10，无法在 .NET 8/9 运行。
- `System.Linq.Async` 被内置异步可枚举取代，逐步移除该依赖。
- 非增量式源生成器已淘汰，统一使用 `IIncrementalGenerator`。
- 配置绑定中 `IEnumerable` record 与可空参数行为有变更，需回归验证。
- 后台服务 `ExecuteAsync` 全部在后台线程运行，检查启动顺序依赖。

## AI 行为指令
- 修改代码后必须运行 `dotnet build` 和 `dotnet test`，失败则继续修复。
- 不要修改生成文件（如 `Migrations/`、`obj/`、`bin/`）。
- 遵循现有模式，新增功能需同步更新测试与文档。
- 提交前检查格式：`dotnet format`。
- 不确定时先阅读相邻代码，保持风格一致。

## 其他要求

- 思考时使用中文，不要使用英语
- 代码要有详细注释