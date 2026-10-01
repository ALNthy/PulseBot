using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Commands;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Tests;

/// <summary>
/// 命令分发行为测试：前缀解析、别名、参数切分、冲突裁决与异常隔离。
/// </summary>
[Trait("Category", "Unit")]
public sealed class CommandDispatcherTests
{
    /// <summary>带前缀的消息命中命令，参数为命令名之后的原始文本。</summary>
    [Fact]
    public async Task 带前缀的命令被正确解析()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        var hit = await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo 你好 世界"), CancellationToken.None);

        Assert.True(hit);
        Assert.Equal(["你好 世界"], plugin.Invocations);
    }

    /// <summary>没有参数时 ArgsText 为空字符串，命令依然命中。</summary>
    [Fact]
    public async Task 无参数的命令命中且参数为空()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        var hit = await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo"), CancellationToken.None);

        Assert.True(hit);
        Assert.Equal([string.Empty], plugin.Invocations);
    }

    /// <summary>默认允许无前缀命令（中文机器人惯例），且命令名匹配不区分大小写。</summary>
    [Fact]
    public async Task 无前缀命令与大小写不敏感均被支持()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("复读 中文参数"), CancellationToken.None));
        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("ECHO hi"), CancellationToken.None));

        Assert.Equal(["中文参数", "hi"], plugin.Invocations);
    }

    /// <summary>普通聊天（首片段不匹配任何命令名）不会被当作命令。</summary>
    [Fact]
    public async Task 非命令消息不命中()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("今天天气不错"), CancellationToken.None));
        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/unknown"), CancellationToken.None));
        Assert.Empty(plugin.Invocations);
    }

    /// <summary>消息只有前缀没有命令名时不命中。</summary>
    [Fact]
    public async Task 只有前缀的消息不命中()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/"), CancellationToken.None));
        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/   "), CancellationToken.None));
    }

    /// <summary>禁止无前缀时，未带前缀的消息不再作为命令解析。</summary>
    [Fact]
    public async Task 关闭无前缀模式后只接受带前缀的命令()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(
            plugin,
            new CommandOptions { EnablePrefixlessCommands = false });

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("echo hi"), CancellationToken.None));
        Assert.True(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo hi"), CancellationToken.None));

        Assert.Equal(["hi"], plugin.Invocations);
    }

    /// <summary>参数按空白切分（含全角空格），并去除空项。</summary>
    [Fact]
    public async Task 参数按空白切分()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo a  b　c"), CancellationToken.None);

        Assert.Equal(["a", "b", "c"], plugin.LastArgs);
    }

    /// <summary>命中别名时，CommandName 保留调用者书写形式，但主名仍是声明的命令名。</summary>
    [Fact]
    public async Task 别名命中时保留调用者书写的命令名()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/复读 内容"), CancellationToken.None);

        Assert.Equal(["复读"], plugin.CommandNames);
    }

    /// <summary>命令名冲突时高优先级者胜出（后注册的高优先级可覆盖先注册的低优先级）。</summary>
    [Fact]
    public async Task 命令名冲突时高优先级插件胜出()
    {
        var low = new LowPriorityEchoPlugin();
        var high = new HighPriorityEchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(low, high);

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo x"), CancellationToken.None);

        Assert.Empty(low.Invocations);
        Assert.Equal(["x"], high.Invocations);
    }

    /// <summary>命令名冲突时低优先级不会覆盖已注册的高优先级。</summary>
    [Fact]
    public async Task 低优先级注册不会覆盖已有的高优先级()
    {
        var high = new HighPriorityEchoPlugin();
        var low = new LowPriorityEchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(high, low);

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo x"), CancellationToken.None);

        Assert.Equal(["x"], high.Invocations);
        Assert.Empty(low.Invocations);
    }

    /// <summary>签名不合法（返回 Task 而非 ValueTask）的 [OnCommand] 方法被跳过，不影响启动。</summary>
    [Fact]
    public void 签名不合法的命令方法被跳过()
    {
        var plugin = new InvalidSignaturePlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        Assert.Empty(dispatcher.Commands);
    }

    /// <summary>声明了含空白字符的非法别名时，只忽略该别名，主名照常注册。</summary>
    [Fact]
    public void 非法别名被忽略而主名照常注册()
    {
        var plugin = new InvalidAliasPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        Assert.Equal(["ok"], dispatcher.Commands.Select(c => c.Name));
    }

    /// <summary>命令表快照同时包含主名与别名，并带上声明主名与所属插件。</summary>
    [Fact]
    public void 命令表包含主名与别名()
    {
        var plugin = new EchoPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        var names = dispatcher.Commands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(["echo", "复读"], names);
        Assert.All(dispatcher.Commands, c => Assert.Equal("echo", c.PrimaryName));
        Assert.All(dispatcher.Commands, c => Assert.False(string.IsNullOrEmpty(c.PluginName)));
    }

    /// <summary>处理器内部抛异常时命令视为已消费（返回 true），异常不外泄到事件管道。</summary>
    [Fact]
    public async Task 处理器异常被隔离且命令视为已消费()
    {
        var plugin = new ThrowingPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin);

        var hit = await dispatcher.TryDispatchAsync(CreateMessageEvent("/boom"), CancellationToken.None);

        Assert.True(hit);
    }

    /// <summary>命令表为空时，任何消息都不会被解析为命令。</summary>
    [Fact]
    public async Task 命令表为空时不命中任何消息()
    {
        CommandDispatcher dispatcher = CreateDispatcher();

        Assert.False(await dispatcher.TryDispatchAsync(CreateMessageEvent("/echo hi"), CancellationToken.None));
    }

    // ---------- 测试基础设施 ----------

    private static CommandDispatcher CreateDispatcher(params IPlugin[] plugins) =>
        CreateDispatcher(plugins, new CommandOptions());

    private static CommandDispatcher CreateDispatcher(IPlugin plugin, CommandOptions options) =>
        CreateDispatcher([plugin], options);

    private static CommandDispatcher CreateDispatcher(
        IEnumerable<IPlugin> plugins,
        CommandOptions options,
        IPermissionService? permissionService = null,
        PluginStateRegistry? pluginState = null)
    {
        var dispatcher = new CommandDispatcher(
            Options.Create(options),
            permissionService ?? CreatePermissionService(),
            pluginState ?? new PluginStateRegistry(),
            NullLogger<CommandDispatcher>.Instance);

        foreach (IPlugin plugin in plugins)
        {
            dispatcher.RegisterPluginCommands(plugin);
        }

        return dispatcher;
    }

    /// <summary>创建使用默认权限配置（无超管、无黑白名单）的权限服务。</summary>
    private static InMemoryPermissionService CreatePermissionService() =>
        new(
            new InMemoryAccessPolicy(Options.Create(new PermissionOptions())),
            NullLogger<InMemoryPermissionService>.Instance);

    private static MessageEvent CreateMessageEvent(string text) => new()
    {
        Conversation = new ConversationId("test", ConversationType.Group, "group-1"),
        Message = Message.Text(text),
        MessageId = "msg-1",
        SenderId = "user-1",
    };

    /// <summary>记录调用参数的插件（主名与别名均指向同一处理器）。</summary>
    private sealed class EchoPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        public IReadOnlyList<string> LastArgs { get; private set; } = [];

        public List<string> CommandNames { get; } = [];

        [OnCommand("echo", Aliases = ["复读"], Priority = 0)]
        public ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.ArgsText);
            CommandNames.Add(context.CommandName);
            LastArgs = context.Args;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>注册 echo 但优先级较低（0）的插件。</summary>
    private sealed class LowPriorityEchoPlugin : IPlugin
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

    /// <summary>签名不符合约定（返回 Task）的插件，用于验证签名校验。</summary>
    private sealed class InvalidSignaturePlugin : IPlugin
    {
        [OnCommand("bad")]
        public Task BadAsync(CommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>声明了含空白字符的非法别名的插件。</summary>
    private sealed class InvalidAliasPlugin : IPlugin
    {
        [OnCommand("ok", Aliases = ["有两个 词"])]
        public ValueTask OkAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    /// <summary>处理器抛异常的插件。</summary>
    private sealed class ThrowingPlugin : IPlugin
    {
        [OnCommand("boom")]
        public ValueTask BoomAsync(CommandContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("模拟插件缺陷");
    }
}
