namespace PulseBot.Core.Plugins;

/// <summary>
/// 插件生命周期中间件的续延：调用它才会继续走到下一个中间件，最终调用插件自身的钩子。
/// </summary>
/// <remarks>
/// <para>
/// 与事件、出站两侧不同，这里没有可改写的载荷：插件实例是容器单例，
/// <see cref="PluginLifecycleContext"/> 也是全只读的，因此续延不接收任何载荷参数——
/// 上下文全程共享同一个实例。
/// </para>
/// <para>
/// 一次生命周期调用中只应调用它一次：重复调用会让插件的钩子被执行多遍
/// （对被跳过的插件反复「继续」会让它在已加载列表与命令表里出现两次）。
/// </para>
/// </remarks>
/// <param name="context">本次生命周期调用的上下文，全程共享同一个实例。</param>
/// <param name="cancellationToken">取消令牌，必须向下传播。</param>
public delegate ValueTask PluginLifecycleNext(
    PluginLifecycleContext context,
    CancellationToken cancellationToken);

/// <summary>
/// 插件生命周期中间件契约：包裹单个插件的加载与卸载，可做前处理、跳过该插件、包裹失败补偿与后处理。
/// </summary>
/// <remarks>
/// <para>
/// 管道形状与 <see cref="Dispatching.IEventMiddleware"/>、<see cref="Adapters.IOutboundMiddleware"/> 一致：
/// 洋葱式，<see cref="Priority"/> 大者在外层（前处理最先执行、后处理最后执行），同优先级按 DI 注册顺序进入。
/// </para>
/// <para>
/// 粒度为<b>逐插件</b>：每个插件的加载、每个插件的卸载各自过一遍管道；
/// 两个阶段共用同一批中间件，由 <see cref="PluginLifecycleContext.Phase"/> 区分——
/// 因此审计类中间件注册一次即可覆盖完整的插件生命周期。
/// </para>
/// <para>
/// <b>不调用续延即跳过该插件</b>：加载阶段跳过意味着它的钩子不会执行，也不会被计入已加载插件、
/// 命令表里不会出现它的命令；卸载阶段跳过意味着不再执行它的清理。
/// 被跳过的插件不会打印加载/卸载完成日志——那是终端的职责，需要留痕的中间件请自行记录。
/// </para>
/// <para>
/// <b>异常语义与事件侧刻意不同：这里不捕获中间件异常。</b>异常会向上传播到
/// <see cref="PluginManager"/> 的逐插件隔离——加载阶段记录为「该插件加载失败，已跳过」并继续加载其余插件，
/// 卸载阶段记录后继续卸载其余插件，宿主都不会崩溃。不做捕获是为了让失败补偿有意义：
/// 中间件可以 <c>try/catch</c> 包住 <c>next</c>，观测到下游失败后自行决定重试、降级或告警。
/// </para>
/// <para>
/// 中间件由 DI 构造（构造函数注入可用）且必须是单例。加载审计、按配置禁用插件、
/// 加载失败告警、加载耗时统计都装在这一层。
/// </para>
/// </remarks>
public interface IPluginLifecycleMiddleware
{
    /// <summary>
    /// 优先级：数值大者在外层（前处理最早、后处理最晚），默认为 0。
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// 处理一次插件生命周期调用。不调用 <paramref name="next"/> 即跳过该插件。
    /// </summary>
    /// <param name="context">本次生命周期调用的上下文（插件实例、阶段、显示名与版本）。</param>
    /// <param name="next">续延；调用它才会继续走到下一个中间件与插件自身的钩子。</param>
    /// <param name="cancellationToken">取消令牌，必须向下传播。</param>
    ValueTask InvokeAsync(
        PluginLifecycleContext context,
        PluginLifecycleNext next,
        CancellationToken cancellationToken);
}
