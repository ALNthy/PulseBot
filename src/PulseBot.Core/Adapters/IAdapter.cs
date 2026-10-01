using PulseBot.Core.Messages;

namespace PulseBot.Core.Adapters;

/// <summary>
/// 适配器契约：某个聊天平台接入 PulseBot 的唯一边界。
/// </summary>
/// <remarks>
/// 按反向 WebSocket 形状设计（宿主启动监听、平台侧主动连入；参考实现见 PulseBot.Adapter.OneBot）：
/// 1. 宿主调用 <see cref="StartAsync"/>，适配器自行启动监听（端口/路径来自其 IOptions 配置），
///    等待平台侧（NapCat / Lagrange 等）主动连入；停止时 <see cref="StopAsync"/> 关闭监听并断开连接；
/// 2. 收到平台报文后，适配器把原始消息转换为 Core 的 IEvent（典型为 MessageEvent），
///    通过构造函数注入的事件入口（跨域约定：PulseBot.Core.Dispatching 的 IEventPublisher.PublishAsync）
///    交给 Core —— 适配器不直接依赖 IEventDispatcher 内部，也不得调用插件或处理命令；
/// 3. 插件发送时调用 Core 的 IMessageSender，由 AdapterMessageSender 路由回本接口的 <see cref="SendAsync"/>。
/// 因此适配器只依赖 Core 的契约类型，Core 不反向依赖任何 Adapter.* 项目。
/// </remarks>
public interface IAdapter
{
    /// <summary>适配器实例名（如 "onebot-main"），用于日志与诊断；同平台实例名应唯一。</summary>
    string Name { get; }

    /// <summary>平台名（如 "onebot"），必须与 <see cref="ConversationId.Platform"/> 一致，发送时据此路由。</summary>
    string Platform { get; }

    /// <summary>
    /// 启动适配器：建立监听/连接，开始把平台事件送入 Core。宿主启动阶段调用。
    /// 重复调用应由实现幂等处理，或抛出明确异常。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 停止适配器：停止接收新事件，断开连接并等待进行中的收发结束（优雅关闭）。宿主关闭阶段调用。
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 发送消息到目标会话。失败不抛异常，以 <see cref="SendResult"/> 返回；
    /// 实现内部应捕获平台 I/O 异常并转为失败结果，避免异常穿透到插件调用点。
    /// </summary>
    ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}
