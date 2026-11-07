namespace AnBot.Core.Commands;

/// <summary>
/// 命令处理器的固定签名约定。
/// 插件中带 <see cref="OnCommandAttribute"/> 的实例方法（或静态方法）必须与此签名匹配，
/// 框架才能用 <c>MethodInfo.CreateDelegate</c> 直接绑定为强类型委托，热路径零反射。
/// </summary>
/// <remarks>
/// <para>插件作者的典型写法（注意参数顺序与返回类型必须完全一致）：</para>
/// <code>
/// [OnCommand("echo")]
/// public async ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
/// {
///     await context.ReplyAsync(context.ArgsText, cancellationToken);
/// }
/// </code>
/// <para>
/// 为什么返回 <see cref="ValueTask"/> 而不是 <see cref="Task"/>：
/// 命令处理属于热路径，且简单命令的回复可以同步完成（如适配器写缓冲），
/// ValueTask 使同步完成路径零分配；同时 async ValueTask 是插件作者自然的书写方式。
/// </para>
/// <para>
/// 为什么带 <see cref="CancellationToken"/>：
/// 宿主优雅关闭时需要取消进行中的命令，与 CLAUDE.md「所有异步方法必须传播 CancellationToken」一致。
/// </para>
/// </remarks>
/// <param name="context">命令执行上下文，承载原始消息事件、命令名与参数。</param>
/// <param name="cancellationToken">取消令牌（宿主关闭时触发）。</param>
public delegate ValueTask CommandHandler(CommandContext context, CancellationToken cancellationToken);
