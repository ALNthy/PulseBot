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
    private readonly ILogger<AdapterMessageSender> _logger;

    /// <summary>构造函数注入，<paramref name="adapters"/> 由 DI 以 <c>IEnumerable&lt;IAdapter&gt;</c> 提供（容器即适配器注册表）。</summary>
    public AdapterMessageSender(IEnumerable<IAdapter> adapters, ILogger<AdapterMessageSender> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
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

    /// <summary>查找适配器并调用，把「无适配器」与适配器异常统一转换为失败结果。</summary>
    private ValueTask<SendResult> DispatchAsync(OutboundMessage outbound, CancellationToken cancellationToken)
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
}
