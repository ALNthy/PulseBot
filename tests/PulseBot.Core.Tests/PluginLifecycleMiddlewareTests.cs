using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Commands;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Tests;

/// <summary>
/// 插件生命周期中间件测试：洋葱顺序、跳过插件、阶段区分、元数据可见性、
/// 异常隔离与失败补偿，以及注册幂等。
/// </summary>
/// <remarks>
/// 插件自身的加载/卸载隔离仍由 <see cref="PluginManagerTests"/> 覆盖；
/// 本文件只钉中间件这一层与 <see cref="PluginManager"/> 的接缝。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class PluginLifecycleMiddlewareTests
{
    /// <summary>中间件按优先级由外到内包裹插件钩子：前处理正序进入，后处理逆序返回。</summary>
    [Fact]
    public async Task 中间件按洋葱顺序包裹插件钩子()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "p1")],
            [
                new RecordingMiddleware(log, "inner"),                      // 优先级 0
                new RecordingMiddleware(log, "outer") { Priority = 10 },
            ]);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(
            ["enter:outer:Load", "enter:inner:Load", "load:p1", "exit:inner:Load", "exit:outer:Load"],
            log);
    }

    /// <summary>同优先级的中间件按注册顺序由外到内进入。</summary>
    [Fact]
    public async Task 同优先级的中间件按注册顺序进入()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "p1")],
            [
                new RecordingMiddleware(log, "first"),
                new RecordingMiddleware(log, "second"),
            ]);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(
            ["enter:first:Load", "enter:second:Load", "load:p1", "exit:second:Load", "exit:first:Load"],
            log);
    }

    /// <summary>不调用续延即跳过：插件不初始化、命令不注册、也不计入已加载列表。</summary>
    [Fact]
    public async Task 中间件不调用续延则插件不被加载()
    {
        var log = new List<string>();
        CommandDispatcher dispatcher = CreateDispatcher();
        PluginManager manager = CreateManager(
            [new CommandPlugin()],
            [new RecordingMiddleware(log, "skipper") { Priority = 10, Skip = true }],
            dispatcher);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["enter:skipper:Load", "skip:skipper"], log);
        Assert.Empty(manager.LoadedPlugins);
        Assert.Empty(dispatcher.Commands);
    }

    /// <summary>加载与卸载是同一批中间件的两个阶段，由上下文里的 Phase 区分。</summary>
    [Fact]
    public async Task 卸载阶段同样经过管道并标记阶段()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "p1")],
            [new RecordingMiddleware(log, "auditor")]);

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        Assert.Equal(
            [
                "enter:auditor:Load", "load:p1", "exit:auditor:Load",
                "enter:auditor:Unload", "unload:p1", "exit:auditor:Unload",
            ],
            log);
    }

    /// <summary>中间件从上下文里直接读到插件的显示名与版本（与加载日志同源的回退逻辑）。</summary>
    [Fact]
    public async Task 中间件能读到插件显示名与版本()
    {
        var log = new List<string>();
        var middleware = new RecordingMiddleware(log, "auditor");
        PluginManager manager = CreateManager([new DescribedPlugin()], [middleware]);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.NotNull(middleware.CapturedContext);
        Assert.Equal("审计目标", middleware.CapturedContext.PluginName);
        Assert.Equal("1.2.3", middleware.CapturedContext.PluginVersion);
    }

    /// <summary>中间件抛出的异常被逐插件隔离：只影响当前插件，其余插件照常加载。</summary>
    [Fact]
    public async Task 中间件异常只影响当前插件()
    {
        var log = new List<string>();
        var victim = new LifecyclePlugin(log, "victim");
        var healthy = new LifecyclePlugin(log, "healthy");
        PluginManager manager = CreateManager(
            [victim, healthy],
            [new RecordingMiddleware(log, "thrower") { Priority = 10, ThrowOnInvoke = true, ThrowFor = victim }]);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal([healthy], manager.LoadedPlugins);
    }

    /// <summary>中间件可以用 try/catch 包住续延做失败补偿——插件侧管道刻意不自行捕获异常。</summary>
    [Fact]
    public async Task 中间件能捕获下游失败做补偿()
    {
        var observed = new List<string>();
        PluginManager manager = CreateManager(
            [new FailingPlugin()],
            [new FailureWatchingMiddleware(observed)]);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["加载失败：插件初始化失败"], observed);
        Assert.Empty(manager.LoadedPlugins);
    }

    /// <summary>卸载阶段的中间件异常同样被隔离，其余插件继续卸载。</summary>
    [Fact]
    public async Task 卸载阶段的中间件异常不阻断其余插件()
    {
        var log = new List<string>();
        var victim = new LifecyclePlugin(log, "victim");
        var healthy = new LifecyclePlugin(log, "healthy");
        PluginManager manager = CreateManager(
            [victim, healthy],
            [
                new RecordingMiddleware(log, "thrower")
                {
                    ThrowOnInvoke = true,
                    ThrowFor = victim,
                    ThrowPhase = PluginLifecyclePhase.Unload,
                },
            ]);

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        // 卸载逆序：healthy 先卸载成功，victim 的中间件抛异常被隔离后循环收尾（不中断）。
        Assert.Contains("unload:healthy", log);
        Assert.DoesNotContain("unload:victim", log);
        Assert.Empty(manager.LoadedPlugins);
    }

    /// <summary>没有注册中间件时插件照常加载与卸载（快路径）。</summary>
    [Fact]
    public async Task 未注册中间件时插件正常加载与卸载()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager([new LifecyclePlugin(log, "only")], []);

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        Assert.Equal(["load:only", "unload:only"], log);
        Assert.Empty(manager.LoadedPlugins);
    }

    /// <summary>重复注册同一个中间件是幂等的，它只会被执行一次。</summary>
    [Fact]
    public void 重复注册中间件是幂等的()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddPluginLifecycleMiddleware<PassthroughMiddleware>();
        services.AddPluginLifecycleMiddleware<PassthroughMiddleware>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IPluginLifecycleMiddleware>().OfType<PassthroughMiddleware>());
    }

    // ---------- 测试基础设施 ----------

    private static CommandDispatcher CreateDispatcher() =>
        new(
            Options.Create(new CommandOptions()),
            new InMemoryPermissionService(
                new InMemoryAccessPolicy(Options.Create(new PermissionOptions())),
                NullLogger<InMemoryPermissionService>.Instance),
            new PluginStateRegistry(),
            NullLogger<CommandDispatcher>.Instance);

    private static PluginManager CreateManager(
        IEnumerable<IPlugin> plugins,
        IEnumerable<IPluginLifecycleMiddleware> middlewares,
        CommandDispatcher? dispatcher = null) =>
        new(
            plugins,
            middlewares,
            dispatcher ?? CreateDispatcher(),
            new PluginStateRegistry(),
            NullLogger<PluginManager>.Instance);

    /// <summary>记录进入/退出的中间件；可跳过某个插件、或对指定插件抛异常。</summary>
    private sealed class RecordingMiddleware(List<string> log, string name) : IPluginLifecycleMiddleware
    {
        public int Priority { get; init; }

        public bool Skip { get; init; }

        /// <summary>是否抛异常。</summary>
        public bool ThrowOnInvoke { get; init; }

        /// <summary>不为 null 时只对该插件抛；用于验证异常隔离的作用范围。</summary>
        public IPlugin? ThrowFor { get; init; }

        /// <summary>不为 null 时只在该阶段抛。</summary>
        public PluginLifecyclePhase? ThrowPhase { get; init; }

        public PluginLifecycleContext? CapturedContext { get; private set; }

        public async ValueTask InvokeAsync(
            PluginLifecycleContext context,
            PluginLifecycleNext next,
            CancellationToken cancellationToken)
        {
            log.Add($"enter:{name}:{context.Phase}");
            CapturedContext = context;

            bool shouldThrow = ThrowOnInvoke
                && (ThrowFor is null || ReferenceEquals(context.Plugin, ThrowFor))
                && (ThrowPhase is null || ThrowPhase == context.Phase);

            if (shouldThrow)
            {
                throw new InvalidOperationException($"中间件 {name} 故意失败");
            }

            if (Skip)
            {
                log.Add($"skip:{name}");
                return;
            }

            await next(context, cancellationToken).ConfigureAwait(false);
            log.Add($"exit:{name}:{context.Phase}");
        }
    }

    /// <summary>用 try/catch 包住续延、把下游失败记录下来后继续的中间件（失败补偿）。</summary>
    private sealed class FailureWatchingMiddleware(List<string> observed) : IPluginLifecycleMiddleware
    {
        public async ValueTask InvokeAsync(
            PluginLifecycleContext context,
            PluginLifecycleNext next,
            CancellationToken cancellationToken)
        {
            try
            {
                await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                observed.Add($"加载失败：{ex.Message}");
            }
        }
    }

    /// <summary>原样放行的最小中间件；仅用于验证 DI 注册的幂等性。</summary>
    private sealed class PassthroughMiddleware : IPluginLifecycleMiddleware
    {
        public ValueTask InvokeAsync(
            PluginLifecycleContext context,
            PluginLifecycleNext next,
            CancellationToken cancellationToken)
            => next(context, cancellationToken);
    }

    /// <summary>把加载/卸载时机记录到共享列表的插件。</summary>
    private sealed class LifecyclePlugin(List<string> log, string name) : IPlugin
    {
        public ValueTask OnLoadAsync(CancellationToken cancellationToken)
        {
            log.Add($"load:{name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnUnloadAsync(CancellationToken cancellationToken)
        {
            log.Add($"unload:{name}");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>带元数据的插件；用于验证中间件能直接读到显示名与版本。</summary>
    [PluginMetadata("审计目标", Version = "1.2.3")]
    private sealed class DescribedPlugin : IPlugin;

    /// <summary>提供一条 ping 命令的插件，用于验证「被跳过时不注册命令」。</summary>
    private sealed class CommandPlugin : IPlugin
    {
        [OnCommand("ping")]
        public ValueTask PingAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    /// <summary>初始化即失败的插件。</summary>
    private sealed class FailingPlugin : IPlugin
    {
        public ValueTask OnLoadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("插件初始化失败");
    }
}
