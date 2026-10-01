using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Commands;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Tests;

/// <summary>
/// 插件生命周期测试：加载顺序、失败隔离、幂等性与逆序卸载。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginManagerTests
{
    /// <summary>全部插件加载成功，命令随之进入命令表。</summary>
    [Fact]
    public async Task 加载全部插件并注册它们的命令()
    {
        var plugin = new CommandPlugin();
        var log = new List<string>();
        CommandDispatcher dispatcher = CreateDispatcher();
        PluginManager manager = CreateManager([plugin], dispatcher);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal([plugin], manager.LoadedPlugins);
        Assert.Equal(["ping"], dispatcher.Commands.Select(c => c.Name));
    }

    /// <summary>按注册顺序依次加载。</summary>
    [Fact]
    public async Task 按注册顺序依次加载插件()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "first"), new LifecyclePlugin(log, "second")],
            CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["load:first", "load:second"], log);
    }

    /// <summary>单个插件加载失败被隔离：其余插件照常加载，失败插件的命令不会进入命令表。</summary>
    [Fact]
    public async Task 加载失败的插件被跳过且不注册命令()
    {
        var healthy = new CommandPlugin();
        var broken = new FailingLoadPlugin();
        CommandDispatcher dispatcher = CreateDispatcher();
        PluginManager manager = CreateManager([broken, healthy], dispatcher);

        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal([healthy], manager.LoadedPlugins);
        Assert.Equal(["ping"], dispatcher.Commands.Select(c => c.Name));
    }

    /// <summary>重复调用加载是幂等的（第二次直接返回，不重复执行 OnLoadAsync）。</summary>
    [Fact]
    public async Task 重复加载是幂等的()
    {
        var log = new List<string>();
        var plugin = new LifecyclePlugin(log, "only");
        PluginManager manager = CreateManager([plugin], CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["load:only"], log);
        Assert.Equal([plugin], manager.LoadedPlugins);
    }

    /// <summary>卸载按加载的逆序执行。</summary>
    [Fact]
    public async Task 卸载按加载的逆序执行()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "first"), new LifecyclePlugin(log, "second"), new LifecyclePlugin(log, "third")],
            CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        Assert.Equal(
            ["load:first", "load:second", "load:third", "unload:third", "unload:second", "unload:first"],
            log);
    }

    /// <summary>卸载时单个插件抛异常被隔离，其余插件仍被卸载。</summary>
    [Fact]
    public async Task 卸载异常被隔离且其余插件继续卸载()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager(
            [new LifecyclePlugin(log, "first"), new FailingUnloadPlugin(log, "broken"), new LifecyclePlugin(log, "third")],
            CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        // broken 的加载是成功的（只有卸载会失败），因此加载阶段三项都记录；
        // 卸载逆序：third → broken（抛异常但被隔离）→ first 仍然执行。
        Assert.Equal(
            ["load:first", "load:broken", "load:third", "unload:third", "unload:broken", "unload:first"],
            log);
        Assert.Empty(manager.LoadedPlugins);
    }

    /// <summary>卸载后可以重新加载（状态被重置）。</summary>
    [Fact]
    public async Task 卸载后可以重新加载()
    {
        var log = new List<string>();
        PluginManager manager = CreateManager([new LifecyclePlugin(log, "only")], CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);
        await manager.LoadAllAsync(CancellationToken.None);

        Assert.Equal(["load:only", "unload:only", "load:only"], log);
        Assert.Single(manager.LoadedPlugins);
    }

    /// <summary>没有任何插件时加载与卸载都是安全的空操作。</summary>
    [Fact]
    public async Task 无插件时加载与卸载不抛异常()
    {
        PluginManager manager = CreateManager([], CreateDispatcher());

        await manager.LoadAllAsync(CancellationToken.None);
        await manager.UnloadAllAsync(CancellationToken.None);

        Assert.Empty(manager.LoadedPlugins);
    }

    // ---------- 测试基础设施 ----------

    private static CommandDispatcher CreateDispatcher() =>
        new(
            Options.Create(new CommandOptions()),
            new InMemoryPermissionService(
                Options.Create(new PermissionOptions()),
                NullLogger<InMemoryPermissionService>.Instance),
            NullLogger<CommandDispatcher>.Instance);

    private static PluginManager CreateManager(
        IEnumerable<IPlugin> plugins,
        CommandDispatcher dispatcher,
        IEnumerable<IPluginLifecycleMiddleware>? middlewares = null) =>
        new(plugins, middlewares ?? [], dispatcher, NullLogger<PluginManager>.Instance);

    /// <summary>提供一条 ping 命令的插件，用于验证命令注册时机。</summary>
    private sealed class CommandPlugin : IPlugin
    {
        [OnCommand("ping")]
        public ValueTask PingAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
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

    /// <summary>初始化即失败的插件；它同时声明了命令，用于验证「加载失败不留僵尸命令」。</summary>
    private sealed class FailingLoadPlugin : IPlugin
    {
        [OnCommand("never")]
        public ValueTask NeverAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask OnLoadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("模拟插件初始化失败");
    }

    /// <summary>卸载时抛异常的插件。</summary>
    private sealed class FailingUnloadPlugin(List<string> log, string name) : IPlugin
    {
        public ValueTask OnLoadAsync(CancellationToken cancellationToken)
        {
            log.Add($"load:{name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnUnloadAsync(CancellationToken cancellationToken)
        {
            log.Add($"unload:{name}");
            throw new InvalidOperationException($"插件 {name} 卸载失败");
        }
    }
}
