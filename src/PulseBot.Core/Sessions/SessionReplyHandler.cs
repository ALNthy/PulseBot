namespace PulseBot.Core.Sessions;

/// <summary>
/// 多轮对话的回调：用户在插件提问之后发来下一条消息时被调用。
/// </summary>
/// <remarks>
/// <para>
/// 与拦截器 <see cref="SessionPromptHandler"/> 名字相近但职责完全不同，勿混淆：
/// 本委托是**插件提供的业务处理逻辑**，由 <see cref="Session.BeginPrompt"/> 登记；
/// <see cref="SessionPromptHandler"/> 是框架内建的事件拦截器，负责按会话键把消息路由到本委托。
/// </para>
/// <para>
/// 回调内可以再次调用 <see cref="Session.BeginPrompt"/> 继续下一轮问答，
/// 也可以用 <see cref="Session.Set{T}"/> 把这一轮的答案存进会话状态供后续命令读取。
/// </para>
/// </remarks>
/// <param name="context">本次回答的上下文（用户消息 + 所属会话 + 回复能力）。</param>
/// <param name="cancellationToken">取消令牌（宿主关闭时触发），必须向下传播。</param>
public delegate ValueTask SessionReplyHandler(SessionReplyContext context, CancellationToken cancellationToken);
