using Microsoft.Extensions.Logging;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Adapters;

/// <summary>
/// <see cref="IMessageSender"/> 的默认实现：按平台路由到已注册适配器。
/// </summary>
/// <remarks>
/// 路由表在构造函数中一次性建立（启动后不再变化），查找零反射；
/// 同一平台注册多个适配器属于配置错误，构造时立即失败（fail fast），
/// 避免运行期出现「消息随机发往某个账号」的不可预期行为。
/// 后续若需同平台多实例（多个机器人账号），扩展方向是让事件额外携带来源适配器实例名并按实例路由，
/// 第一步不引入该复杂度。
/// </remarks>
public sealed class AdapterMessageSender : IMessageSender
{
    private readonly IReadOnlyDictionary<string, IAdapter> _adaptersByPlatform;

    /// <summary>中间件链的入口；未注册任何中间件时为 null，发送走与引入中间件前完全一致的路径。</summary>
    private readonly OutboundMiddlewareNext? _pipeline;

    private readonly ILogger<AdapterMessageSender> _logger;

    /// <summary>
    /// 构造函数注入，<paramref name="adapters"/> 与 <paramref name="middlewares"/> 由 DI 以
    /// <c>IEnumerable&lt;T&gt;</c> 提供（容器即适配器注册表）。
    /// </summary>
    public AdapterMessageSender(
        IEnumerable<IAdapter> adapters,
        IEnumerable<IOutboundMiddleware> middlewares,
        ILogger<AdapterMessageSender> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(middlewares);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var map = new Dictionary<string, IAdapter>(StringComparer.Ordinal);
        foreach (IAdapter adapter in adapters)
        {
            if (!map.TryAdd(adapter.Platform, adapter))
            {
                throw new InvalidOperationException(
                    $"平台 '{adapter.Platform}' 已由适配器 '{map[adapter.Platform].Name}' 注册，不能重复注册适配器 '{adapter.Name}'。");
            }
        }

        _adaptersByPlatform = map;
        _pipeline = BuildPipeline(middlewares);
    }

    /// <inheritdoc />
    public ValueTask<SendResult> SendAsync(ConversationId conversation, Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);

        return DispatchAsync(
            new OutboundMessage { Conversation = conversation, Message = message },
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SendResult> ReplyAsync(ConversationId conversation, string replyToMessageId, Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToMessageId);
        ArgumentNullException.ThrowIfNull(message);

        return DispatchAsync(
            new OutboundMessage { Conversation = conversation, Message = message, ReplyToMessageId = replyToMessageId },
            cancellationToken);
    }

    /// <summary>出站入口：按「有无中间件」在两条路径之间分流。</summary>
    /// <remarks>
    /// 未装中间件时直接走 <see cref="DispatchCoreAsync"/>，与引入中间件之前完全一致（不多一次委托调用）。
    /// </remarks>
    private ValueTask<SendResult> DispatchAsync(OutboundMessage outbound, CancellationToken cancellationToken)
        => _pipeline is null
            ? DispatchCoreAsync(outbound, cancellationToken)
            : _pipeline(outbound, cancellationToken);

    /// <summary>查找适配器并调用，把「无适配器」与适配器异常统一转换为失败结果。本方法同时是管道的内层终端。</summary>
    private ValueTask<SendResult> DispatchCoreAsync(OutboundMessage outbound, CancellationToken cancellationToken)
    {
        if (!_adaptersByPlatform.TryGetValue(outbound.Conversation.Platform, out IAdapter? adapter))
        {
            // 通常是插件写了错误的平台名或宿主漏注册：仍以失败结果返回，保持调用点单一的成功/失败分支。
            _logger.LogWarning(
                "没有注册平台 {Platform} 的适配器，消息无法发送到会话 {Conversation}。",
                outbound.Conversation.Platform,
                outbound.Conversation);

            return ValueTask.FromResult(SendResult.Fail(
                SendFailureKind.NoAdapter,
                $"没有注册平台 '{outbound.Conversation.Platform}' 的适配器。"));
        }

        return SendGuardedAsync(adapter, outbound, cancellationToken);
    }

    /// <summary>
    /// 适配器是外部边界，其异常不得穿透到插件/命令热路径：统一捕获、记录并转为失败结果。
    /// 调用方自身的取消（cancellationToken 已触发）仍然向上传播，与「超时」区分开。
    /// </summary>
    private async ValueTask<SendResult> SendGuardedAsync(IAdapter adapter, OutboundMessage outbound, CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.SendAsync(outbound, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // 非调用方取消：通常是适配器内部超时（自己的 CTS 触发）。
            _logger.LogWarning(ex, "适配器 {Adapter} 发送消息到会话 {Conversation} 超时。", adapter.Name, outbound.Conversation);
            return SendResult.Fail(SendFailureKind.Timeout, $"适配器 '{adapter.Name}' 发送超时。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "适配器 {Adapter} 发送消息到会话 {Conversation} 时抛出异常。", adapter.Name, outbound.Conversation);
            return SendResult.Fail(SendFailureKind.Unknown, $"适配器 '{adapter.Name}' 发送异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 把中间件按优先级由外到内包裹成一条链；未注册任何中间件时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 与事件管道相同：全部包装在构造期完成，发送时只剩纯委托调用；
    /// 终端是「查表 + 调适配器」，因此同一条链服务所有平台。
    /// </remarks>
    private OutboundMiddlewareNext? BuildPipeline(IEnumerable<IOutboundMiddleware> middlewares)
    {
        IOutboundMiddleware[] ordered = middlewares
            .OrderByDescending(static middleware => middleware.Priority)
            .ToArray();

        if (ordered.Length == 0)
        {
            return null;
        }

        // 终端：实际发送。方法组转换即委托，不产生额外的闭包。
        OutboundMiddlewareNext next = DispatchCoreAsync;

        // 反向包裹：优先级最低的先成为内层，循环结束时 next 即优先级最高者的入口。
        for (var i = ordered.Length - 1; i >= 0; i--)
        {
            next = WrapMiddleware(ordered[i], next);
        }

        return next;
    }

    /// <summary>把单个中间件包成一层，并附上异常隔离（fail-closed：中间件异常即不发送）。</summary>
    private OutboundMiddlewareNext WrapMiddleware(IOutboundMiddleware middleware, OutboundMiddlewareNext next)
    {
        return async (message, cancellationToken) =>
        {
            try
            {
                return await middleware.InvokeAsync(message, next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方自身的取消：正常控制流，向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 中间件异常隔离：消息不发出。出站是外部边界，异常不得穿透到插件热路径；
                // 对限流、脱敏这类中间件而言，故障时少发一次也远好过漏发。
                _logger.LogError(
                    ex,
                    "会话 {Conversation} 的出站中间件 {Middleware} 执行时抛出未处理异常，消息未发送。",
                    message.Conversation,
                    middleware.GetType().Name);

                return SendResult.Fail(
                    SendFailureKind.Unknown,
                    $"出站中间件 '{middleware.GetType().Name}' 执行异常，消息未发送。");
            }
        };
    }
}
