namespace AnBot.Core.Plugins;

/// <summary>
/// 插件入口接口。插件是容器托管的单例：通过构造函数注入所需服务
/// （<c>ILogger&lt;T&gt;</c>、<c>IOptions&lt;T&gt;</c>、插件自有的领域服务等），
/// 命令用 <see cref="OnCommandAttribute"/> 标注在公开方法上。
/// 框架只向插件暴露 Core 的公开契约，插件不得直接调用 Core 内部服务。
/// </summary>
/// <remarks>
/// <para>装配方式（宿主 CLI 中，编译期项目引用 + 显式注册，不做运行时 DLL 扫描）：</para>
/// <code>
/// builder.Services.AddAnBotPlugins();       // 插件管理器（并确保命令分发可用）
/// builder.Services.AddPlugin&lt;EchoPlugin&gt;();  // 注册具体插件
/// </code>
/// <para>
/// 生命周期：容器构建完成后，由 <see cref="PluginManager.LoadAllAsync"/> 统一加载——
/// 先调用 <see cref="OnLoadAsync"/> 完成插件初始化，成功后再扫描并注册 [OnCommand] 命令
/// （加载失败的插件不会对外提供命令）；
/// 宿主优雅关闭时逆序调用 <see cref="OnUnloadAsync"/>。
/// 两个钩子都提供默认空实现，插件只实现自己关心的部分即可。
/// </para>
/// <para>
/// 为什么没有 <c>ConfigureServices(IServiceCollection)</c> 钩子：
/// 该钩子只能在容器构建前按实例调用，而实例需要容器才能构造，二者互相矛盾；
/// 强行保留会迫使插件放弃构造函数注入。因此服务注册采用
/// <c>AddPlugin&lt;TPlugin&gt;(configurePluginServices)</c> 回调（或插件项目封装自己的
/// <c>AddXxxPlugin()</c> 扩展方法），构造注入全程可用。
/// </para>
/// </remarks>
public interface IPlugin
{
    /// <summary>
    /// 插件加载钩子：容器就绪后、命令扫描之前调用一次，用于初始化插件自身状态
    /// （读取配置、预热缓存等）。
    /// </summary>
    /// <param name="cancellationToken">启动期取消令牌。</param>
    /// <returns>表示异步初始化完成的任务。</returns>
    ValueTask OnLoadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// 插件卸载钩子：宿主优雅关闭时逆序调用一次，用于释放资源。
    /// 实现应尽力完成清理，不依赖取消令牌是否已触发。
    /// </summary>
    /// <param name="cancellationToken">关闭期取消令牌。</param>
    /// <returns>表示异步清理完成的任务。</returns>
    ValueTask OnUnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
