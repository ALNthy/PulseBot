using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnBot.Core.Commands;
using AnBot.Core.Events;
using AnBot.Core.Messages;
using AnBot.Core.Permissions;
using AnBot.Core.Plugins;

namespace AnBot.Core.Tests;

/// <summary>
/// 插件启停测试：停用后该插件注册的命令不再可触发，启用后立即恢复；
/// 门控只作用于命令，不触碰插件生命周期。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginStateTests
{
    /// <summary>初始状态下没有任何插件被停用。</summary>
    [Fact]
    public void 默认全部启用()
    {
        var state = new PluginStateRegistry();

        Assert.False(state.IsDisabled("Some.Plugin"));
        Assert.Empty(state.DisabledPlugins);
    }

    /// <summary>停用与启用都返回「状态是否真的变了」，重复设置同一状态不算变化。</summary>
    [Fact]
    public void 重复设置同一状态不算变化()
    {
        var state = new PluginStateRegistry();

        Assert.True(state.SetEnabled("A.Plugin", enabled: false));
        Assert.False(state.SetEnabled("A.Plugin", enabled: false));
        Assert.True(state.IsDisabled("A.Plugin"));

        Assert.True(state.SetEnabled("A.Plugin", enabled: true));
        Assert.False(state.SetEnabled("A.Plugin", enabled: true));
        Assert.False(state.IsDisabled("A.Plugin"));
    }

    /// <summary>整体替换会覆盖旧集合，并丢弃空白项（启动时从持久化状态水合）。</summary>
    [Fact]
    public void 整体替换覆盖旧集合()
    {
        var state = new PluginStateRegistry();
        state.SetEnabled("A.Plugin", enabled: false);

        state.Replace(["B.Plugin", "   ", "C.Plugin"]);

        Assert.False(state.IsDisabled("A.Plugin"));
        Assert.True(state.IsDisabled("B.Plugin"));
        Assert.True(state.IsDisabled("C.Plugin"));
        Assert.Equal(2, state.DisabledPlugins.Count);
    }

    /// <summary>停用插件后，它注册的命令不再命中，处理器不被调用。</summary>
    [Fact]
    public async Task 停用后命令不再触发()
    {
        var state = new PluginStateRegistry();
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher([plugin], state);

        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.Single(plugin.Invocations);

        state.SetEnabled(TypeNameOf(plugin), enabled: false);

        // 返回 false 表示「未消费」：消息会继续流向普通监听器，如同这个命令不存在。
        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.Single(plugin.Invocations);
    }

    /// <summary>重新启用后命令立即恢复触发。</summary>
    [Fact]
    public async Task 重新启用后命令恢复触发()
    {
        var state = new PluginStateRegistry();
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher([plugin], state);

        state.SetEnabled(TypeNameOf(plugin), enabled: false);
        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));

        state.SetEnabled(TypeNameOf(plugin), enabled: true);

        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.Single(plugin.Invocations);
    }

    /// <summary>停用只影响该插件的命令，其它插件的命令照常工作。</summary>
    [Fact]
    public async Task 停用不影响其它插件的命令()
    {
        var state = new PluginStateRegistry();
        var echo = new EchoPlugin();
        var ping = new PingPlugin();
        CommandDispatcher dispatcher = CreateDispatcher([echo, ping], state);

        state.SetEnabled(TypeNameOf(echo), enabled: false);

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("/ping"), CancellationToken.None));
        Assert.Equal(1, ping.Invocations);
    }

    /// <summary>
    /// 停用胜出的注册后不会回退到被压制的同名注册：冲突裁决不保留落败者，
    /// 因此该命令的结果只能是「彻底不可触发」，而不是「换一个插件执行」。
    /// </summary>
    [Fact]
    public async Task 停用后不回退到低优先级注册()
    {
        var state = new PluginStateRegistry();
        var low = new EchoPlugin();
        var high = new HighPriorityEchoPlugin();

        // 先注册低优先级、再注册高优先级：高者胜出并占据命令名。
        CommandDispatcher dispatcher = CreateDispatcher([low, high], state);

        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.Single(high.Invocations);
        Assert.Empty(low.Invocations);

        state.SetEnabled(TypeNameOf(high), enabled: false);

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好"), CancellationToken.None));
        Assert.Empty(low.Invocations);
    }

    /// <summary>
    /// 停用不触碰生命周期：插件仍在已加载列表里，<c>OnUnloadAsync</c> 不会被调用。
    /// 这是刻意的——Core 无法保证插件能被安全地热卸载，因此只承诺「命令不可触发」。
    /// </summary>
    [Fact]
    public async Task 停用不改变生命周期状态()
    {
        var state = new PluginStateRegistry();
        var plugin = new LifecyclePlugin();
        CommandDispatcher dispatcher = CreateDispatcher([plugin], state);
        PluginManager manager = CreateManager([plugin], dispatcher, state);

        await manager.LoadAllAsync(CancellationToken.None);
        Assert.Single(manager.LoadedPlugins);

        Assert.True(manager.SetPluginEnabled(TypeNameOf(plugin), enabled: false));

        Assert.False(manager.IsEnabled(TypeNameOf(plugin)));
        Assert.Single(manager.LoadedPlugins);
        Assert.Equal(1, plugin.LoadCount);
        Assert.Equal(0, plugin.UnloadCount);
    }

    /// <summary>类型名对不上任何已登记插件时返回 false，后台据此回 404。</summary>
    [Fact]
    public void 未知插件类型名返回false()
    {
        var plugin = new LifecyclePlugin();
        CommandDispatcher dispatcher = CreateDispatcher([plugin], new PluginStateRegistry());
        PluginManager manager = CreateManager([plugin], dispatcher, new PluginStateRegistry());

        Assert.False(manager.SetPluginEnabled("Not.A.Plugin", enabled: false));
        Assert.True(manager.SetPluginEnabled(TypeNameOf(plugin), enabled: false));
    }

    // ---------- 测试基础设施 ----------

    /// <summary>按给定顺序注册插件的命令分发器（顺序决定同名命令的冲突裁决）。</summary>
    private static CommandDispatcher CreateDispatcher(IEnumerable<IPlugin> plugins, PluginStateRegistry state)
    {
        var dispatcher = new CommandDispatcher(
            Options.Create(new CommandOptions()),
            new InMemoryPermissionService(
                new InMemoryAccessPolicy(Options.Create(new PermissionOptions())),
                NullLogger<InMemoryPermissionService>.Instance),
            state,
            NullLogger<CommandDispatcher>.Instance);

        foreach (IPlugin plugin in plugins)
        {
            dispatcher.RegisterPluginCommands(plugin);
        }

        return dispatcher;
    }

    private static PluginManager CreateManager(
        IEnumerable<IPlugin> plugins,
        CommandDispatcher dispatcher,
        PluginStateRegistry state) =>
        new(plugins, [], dispatcher, state, NullLogger<PluginManager>.Instance);

    /// <summary>
    /// 取插件类型全名，口径与 <see cref="CommandDispatcher.RegisterPluginCommands"/> 一致——
    /// 门控的键必须与命令注册时写入的插件名分毫不差。
    /// </summary>
    private static string TypeNameOf(IPlugin plugin)
    {
        var pluginType = plugin.GetType();
        return pluginType.FullName ?? pluginType.Name;
    }

    private static MessageEvent CreateMessageEvent(string text) => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "conv-1"),
        Message = Message.Text(text),
        MessageId = "msg-1",
        SenderId = "user-1",
    };

    /// <summary>注册 echo 命令（优先级 0）的插件。</summary>
    private sealed class EchoPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("echo", Priority = 0)]
        public ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.ArgsText);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>注册同名命令 echo 但优先级更高（10）的插件。</summary>
    private sealed class HighPriorityEchoPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("echo", Priority = 10)]
        public ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.ArgsText);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>注册另一个命令名的插件，用于验证停用的隔离性。</summary>
    private sealed class PingPlugin : IPlugin
    {
        public int Invocations { get; private set; }

        [OnCommand("ping")]
        public ValueTask PingAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>记录加载与卸载次数的插件，用于验证停用不触碰生命周期。</summary>
    private sealed class LifecyclePlugin : IPlugin
    {
        public int LoadCount { get; private set; }

        public int UnloadCount { get; private set; }

        [OnCommand("lifecycle")]
        public ValueTask RunAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask OnLoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnUnloadAsync(CancellationToken cancellationToken)
        {
            UnloadCount++;
            return ValueTask.CompletedTask;
        }
    }
}
