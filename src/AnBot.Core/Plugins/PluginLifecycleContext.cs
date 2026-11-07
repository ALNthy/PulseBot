namespace AnBot.Core.Plugins;

/// <summary>
/// 插件生命周期阶段：中间件在两个阶段共用同一批注册项。
/// </summary>
public enum PluginLifecyclePhase
{
    /// <summary>加载：调用 <see cref="IPlugin.OnLoadAsync"/> 并随后注册该插件的命令。</summary>
    Load = 0,

    /// <summary>卸载：宿主关闭时逆序调用 <see cref="IPlugin.OnUnloadAsync"/>。</summary>
    Unload = 1,
}

/// <summary>
/// 插件生命周期中间件的上下文：本次是哪个插件、处于哪个阶段、它的显示名与版本。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="PluginManager"/> 在每个插件进入管道前构造，一次生命周期调用中全程共享同一实例。
/// 全部属性都是 <c>init</c> 只读：插件实例是容器托管的单例、元数据在其生命周期内不会变化，
/// 中间件不应也无法改写它们——这正是本管道的续延不接收载荷参数的原因。
/// </para>
/// <para>
/// <see cref="PluginName"/> 与 <see cref="PluginVersion"/> 取自 <see cref="PluginMetadataAttribute"/>，
/// 缺少该特性时分别回退到类型全名与「未知」，与加载完成日志同源——
/// 审计中间件无需重复实现这段回退逻辑。
/// </para>
/// </remarks>
public sealed class PluginLifecycleContext
{
    /// <summary>本次生命周期调用针对的插件实例（容器单例，不可替换）。</summary>
    public required IPlugin Plugin { get; init; }

    /// <summary>当前阶段，中间件可据此区分加载与卸载。</summary>
    public required PluginLifecyclePhase Phase { get; init; }

    /// <summary>插件显示名：优先取 <see cref="PluginMetadataAttribute.Name"/>，缺省回退到类型全名。</summary>
    public required string PluginName { get; init; }

    /// <summary>插件版本：优先取 <see cref="PluginMetadataAttribute.Version"/>，缺省为「未知」。</summary>
    public required string PluginVersion { get; init; }
}
