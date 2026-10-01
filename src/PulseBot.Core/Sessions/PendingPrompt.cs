namespace PulseBot.Core.Sessions;

/// <summary>
/// 一次尚未得到回答的提问：谁在等、等什么、等到什么时候。
/// </summary>
/// <remarks>
/// 仅框架内部使用。不对外公开是因为「取走等待态」必须由拦截器独占完成——
/// 插件若能从别人的会话里取走等待态，多轮对话的归属就无从保证。
/// </remarks>
/// <param name="Owner">发起提问的插件标识，仅用于日志与诊断（如 <c>"Echo.注册"</c>）。</param>
/// <param name="Handler">收到回答时调用的回调。</param>
/// <param name="ExpiresAt">等待截止时刻（UTC）；为 <see cref="DateTimeOffset.MaxValue"/> 表示不超时。</param>
internal sealed record PendingPrompt(string Owner, SessionReplyHandler Handler, DateTimeOffset ExpiresAt);
