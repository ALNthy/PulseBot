using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;

namespace PulseBot.Core.Sessions;

/// <summary>
/// 内建处理器：把「正在等待回答」的消息从正常管道里截获，路由回提问的插件。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>AddPulseBotSessions()</c> 自动注册，插件与宿主都无需手动登记。
/// </para>
/// <para>
/// 优先级取 <c>int.MaxValue - 1</c>，恰好是三档里的中间一档：
/// **后于**接入控制（<c>AccessControlHandler</c> 取 <see cref="int.MaxValue"/>）——
/// 被拉黑的人不该有机会与机器人对话；
/// **先于**命令分发（<c>CommandMessageHandler</c> 取 <c>int.MaxValue - 2</c>）——
/// 等待回答期间的「回答」必须优先于「命令」。否则在无前缀命令默认开启的情况下，
/// 像「复读 你好」这种恰好以命令名开头的正常回答会被命令分发夺走，
/// 插件永远收不到答案，而且没有任何报错。
/// </para>
/// <para>
/// 三档取值互不相同，正确性由优先级数值保证，与本扩展方法的调用顺序无关。
/// </para>
/// <para>
/// 本类型是公开的，因为 DI 容器需要以反射方式实例化它；它属于框架内部机制，
/// 宿主既不需要注册它，也不应直接依赖它。
/// </para>
/// </remarks>
public sealed class SessionPromptHandler : IEventHandler<MessageEvent>
{
    private readonly ISessionStore _sessions;
    private readonly FrozenSet<string> _cancelCommands;
    private readonly string _cancelMessage;
    private readonly ILogger<SessionPromptHandler> _logger;

    /// <summary>创建等待态拦截器。</summary>
    /// <param name="sessions">会话存储。</param>
    /// <param name="options">会话配置（取消词与取消提示语）。</param>
    /// <param name="logger">结构化日志。</param>
    public SessionPromptHandler(
        ISessionStore sessions,
        IOptions<SessionOptions> options,
        ILogger<SessionPromptHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _cancelMessage = options.Value.CancelMessage;

        // 冻结成大小写不敏感的集合：取消词是精确匹配（不是前缀匹配），
        // 否则 "/cancel foo" 这类输入会被误吞。
        _cancelCommands = options.Value.CancelCommands
            .Where(static command => !string.IsNullOrWhiteSpace(command))
            .Select(static command => command.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        _logger = logger;
    }

    /// <inheritdoc />
    public int Priority => int.MaxValue - 1;

    /// <inheritdoc />
    public async ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        // 热路径：每条消息都要经过这里。只读查找，查不到会话就一定没有等待中的提问，
        // 绝不能改用 GetOrCreateAsync——那会让每个说过话的用户都在存储里留下一个会话对象。
        Session? session = await _sessions
            .FindAsync(SessionKey.From(@event), cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return;
        }

        // 原子取走等待态（内部已完成超时判定）。取不到有两种可能：本来就没有等待，
        // 或等待已超时并被清除——两种情况都意味着这条消息该交还给正常管道处理。
        if (!session.TryConsumePrompt(out PendingPrompt? prompt))
        {
            return;
        }

        // 先阻断传播，再执行后续逻辑：等待态此刻已被消费，这条消息只属于这个回调。
        // 与 CommandMessageHandler「成功处理才阻断」的语义不同——这里消息的归属已经确定，
        // 即便回调抛异常也不应让它回流到命令管道被执行第二遍。
        context.StopPropagation();

        var plainText = @event.PlainText.Trim();
        if (_cancelCommands.Contains(plainText))
        {
            _logger.LogDebug(
                "用户 {SenderId} 在会话 {Conversation} 中取消了 {Owner} 的提问。",
                @event.SenderId,
                @event.Conversation,
                prompt.Owner);

            await @event.ReplyAsync(_cancelMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug(
            "已把会话 {Conversation} 中用户 {SenderId} 的回答路由给 {Owner}。",
            @event.Conversation,
            @event.SenderId,
            prompt.Owner);

        // 回调内部可以再次 BeginPrompt 继续下一轮——等待态在上一步就已清空，
        // 因此这里登记的新等待态不会被覆盖掉。
        // 回调抛出的异常由 EventDispatcher 统一捕获并记录，不会影响后续消息。
        await prompt.Handler(new SessionReplyContext(@event, session), cancellationToken).ConfigureAwait(false);
    }
}
