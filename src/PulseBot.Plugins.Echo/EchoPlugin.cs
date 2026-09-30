using Microsoft.Extensions.Logging;
using PulseBot.Core.Commands;
using PulseBot.Core.Plugins;

namespace PulseBot.Plugins.Echo;

/// <summary>
/// 示例插件：把命令参数原样回显，演示 <see cref="IPlugin"/> + <see cref="PluginMetadataAttribute"/>
/// + <see cref="OnCommandAttribute"/> 的最小可用写法，可作为新插件的模板。
/// </summary>
/// <remarks>
/// <para>命令用法（默认前缀 <c>/</c>，同时允许无前缀）：</para>
/// <code>
/// /echo 你好        → 回复「你好」
/// /复读 你好        → 同上（别名）
/// 复读 你好          → 同上（无前缀模式）
/// /echo             → 回复「（没有参数）」
/// </code>
/// <para>
/// 注意本插件只依赖 Core 的公开契约：它注入的是 <c>ILogger</c>，回复走
/// <see cref="CommandContext.ReplyAsync"/>，完全不感知任何聊天平台。
/// </para>
/// </remarks>
[PluginMetadata("Echo", Description = "示例插件：把命令参数原样回显。")]
public sealed class EchoPlugin : IPlugin
{
    private readonly ILogger<EchoPlugin> _logger;

    /// <summary>构造函数注入：插件是容器托管的单例，所需服务一律通过构造函数获得。</summary>
    /// <param name="logger">结构化日志。</param>
    public EchoPlugin(ILogger<EchoPlugin> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// 回显命令。方法签名必须与 <see cref="CommandHandler"/> 约定完全一致，
    /// 框架才能在启动时用 <c>CreateDelegate</c> 把它绑定为强类型委托。
    /// </summary>
    /// <param name="context">命令上下文，承载原始消息与参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [OnCommand("echo", Aliases = ["复读"], Description = "复读参数内容")]
    public async ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var text = context.ArgsText.Length > 0 ? context.ArgsText : "（没有参数）";

        _logger.LogInformation(
            "Echo 命令被触发：会话 {Conversation}，发送者 {SenderId}，参数 {ArgsText}",
            context.Message.Conversation,
            context.Message.SenderId,
            context.ArgsText);

        await context.ReplyAsync(text, cancellationToken).ConfigureAwait(false);
    }
}
