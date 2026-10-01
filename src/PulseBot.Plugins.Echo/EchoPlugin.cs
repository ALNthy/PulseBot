using Microsoft.Extensions.Logging;
using PulseBot.Core.Commands;
using PulseBot.Core.Plugins;
using PulseBot.Core.Sessions;

namespace PulseBot.Plugins.Echo;

/// <summary>
/// 示例插件：演示单轮命令与多轮对话两种插件形态，可作为新插件的模板。
/// </summary>
/// <remarks>
/// <para>单轮命令（默认前缀 <c>/</c>，同时允许无前缀）：</para>
/// <code>
/// /echo 你好        → 回复「你好」
/// /复读 你好        → 同上（别名）
/// 复读 你好          → 同上（无前缀模式）
/// /echo             → 回复「（没有参数）」
/// </code>
/// <para>多轮对话（提问 → 用户回答 → 处理回答）：</para>
/// <code>
/// /注册             → 机器人问「请输入你的昵称」并登记等待
/// 小明              → 这条消息被路由回本插件，昵称写入会话状态
/// /我是谁           → 从会话状态读回昵称
/// /注册 后发 /cancel → 放弃本次提问
/// </code>
/// <para>
/// 注意本插件只依赖 Core 的公开契约：注入的是 <c>ILogger</c> 与 <c>ISessionStore</c>，
/// 回复走 <see cref="CommandContext.ReplyAsync"/>，完全不感知任何聊天平台。
/// </para>
/// </remarks>
[PluginMetadata("Echo", Description = "示例插件：演示单轮命令与多轮对话。")]
public sealed class EchoPlugin : IPlugin
{
    /// <summary>会话状态键：当前用户登记的昵称。</summary>
    private const string NicknameStateKey = "nickname";

    private readonly ISessionStore _sessions;
    private readonly ILogger<EchoPlugin> _logger;

    /// <summary>构造函数注入：插件是容器托管的单例，所需服务一律通过构造函数获得。</summary>
    /// <param name="sessions">交互会话存储（多轮对话与跨命令状态）。</param>
    /// <param name="logger">结构化日志。</param>
    public EchoPlugin(ISessionStore sessions, ILogger<EchoPlugin> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
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

    /// <summary>
    /// 发起一轮多轮对话：提问并登记等待，用户的下一条消息交给
    /// <see cref="HandleNicknameAsync"/> 处理。
    /// </summary>
    /// <param name="context">命令上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [OnCommand("注册", Description = "多轮对话示例：登记昵称")]
    public async ValueTask RegisterAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Session session = await _sessions
            .GetOrCreateAsync(SessionKey.From(context.Message), cancellationToken)
            .ConfigureAwait(false);

        // 顺序有意如此：先把问句发出去，再登记等待。
        // 反过来会在发送失败时留下一个永远等不到回答的等待态。
        await context.ReplyAsync("请输入你的昵称（发送 /cancel 放弃）", cancellationToken).ConfigureAwait(false);
        session.BeginPrompt("Echo.注册", HandleNicknameAsync);
    }

    /// <summary>读取会话状态，验证状态能跨命令保持。</summary>
    /// <param name="context">命令上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [OnCommand("我是谁", Description = "多轮对话示例：读回昵称")]
    public async ValueTask WhoAmIAsync(CommandContext context, CancellationToken cancellationToken)
    {
        // 只读查找：没登记过的用户不该因为问了一句就被建出一个会话。
        Session? session = await _sessions
            .FindAsync(SessionKey.From(context.Message), cancellationToken)
            .ConfigureAwait(false);

        var text = session is not null && session.TryGet<string>(NicknameStateKey, out var nickname)
            ? $"你是 {nickname}。"
            : "我还不知道你是谁，先发送 /注册 登记一下。";

        await context.ReplyAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 「注册」提问的回答处理：保存昵称并回复。
    /// </summary>
    /// <remarks>
    /// 本方法是 <c>static</c> 的：它只是被 <see cref="Session.BeginPrompt"/> 登记的回调，
    /// 不参与 <c>[OnCommand]</c> 扫描，也不需要访问插件实例。
    /// </remarks>
    private static async ValueTask HandleNicknameAsync(SessionReplyContext reply, CancellationToken cancellationToken)
    {
        var nickname = reply.Message.PlainText.Trim();
        if (nickname.Length == 0)
        {
            await reply.ReplyAsync("昵称不能为空，注册已放弃。", cancellationToken).ConfigureAwait(false);
            return;
        }

        reply.Session.Set(NicknameStateKey, nickname);

        await reply.ReplyAsync($"你好，{nickname}！", cancellationToken).ConfigureAwait(false);
    }
}
