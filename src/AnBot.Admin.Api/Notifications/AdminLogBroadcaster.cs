using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Shared;
using AnBot.Admin.Shared.Notifications;

namespace AnBot.Admin.Api.Notifications;

/// <summary>
/// 把宿主的日志复制一份投递给后台的实时流。
/// </summary>
/// <remarks>
/// <para>
/// <b>注册方式</b>：作为 <see cref="ILoggerProvider"/> 注册进容器即可，日志系统会把
/// 容器里所有该接口的实现都接上，不需要宿主显式调用 <c>AddProvider</c>——
/// 那会把「后台是否启用」的判断散到宿主入口，而这里已经能读到配置。
/// </para>
/// <para>
/// <b>为什么不定制消息模板</b>：日志正文交给日志系统自己的格式化器产出，
/// 与写入控制台/文件的是同一份文本。另起一套格式会让同一件事在两处显示得不一样，
/// 排查时得先判断「以哪边为准」。
/// </para>
/// <para>
/// 本类不抛异常、不阻塞：投递走的是有界通道的丢弃模式，最坏情况是丢条目。
/// </para>
/// </remarks>
public sealed class AdminLogBroadcaster : ILoggerProvider
{
    /// <summary>转发的最低级别。</summary>
    /// <remarks>
    /// 取 <see cref="LogLevel.Information"/>：<c>Debug</c> 与 <c>Trace</c> 在框架内部
    /// 极其密集（每个 HTTP 请求、每条 SQL 都有），转发它们会让通道长期处于丢弃状态，
    /// 结果反而是「后台什么也看不清」。
    /// </remarks>
    private const LogLevel MinimumLevel = LogLevel.Information;

    /// <summary>
    /// 不转发的日志类别：推送服务自身。
    /// </summary>
    /// <remarks>
    /// 推送失败时该服务会记一条警告；若不屏蔽，这条警告会被本广播器再次投进通道、
    /// 再次推送失败、再记一条——形成自我维持的循环。旁路组件必须断开这条回头路。
    /// </remarks>
    private static readonly string IgnoredCategory =
        typeof(AdminNotificationService).FullName ?? nameof(AdminNotificationService);

    private readonly AdminNotifications _notifications;
    private readonly TimeProvider _time;
    private readonly bool _enabled;

    /// <summary>构造函数注入。</summary>
    /// <param name="notifications">推送缓冲。</param>
    /// <param name="options">后台配置：关闭后台时完全不转发。</param>
    /// <param name="time">时间源。</param>
    public AdminLogBroadcaster(
        AdminNotifications notifications,
        IOptions<AdminOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _notifications = notifications;
        _time = time;

        // 决定一次即可：配置在宿主启动期读定，日志广播不在热重载的范围内。
        _enabled = options.Value.Enabled;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new Broadcaster(this, categoryName);

    /// <inheritdoc />
    /// <remarks>本提供程序不持有需要释放的资源，通道的生命周期由容器管理。</remarks>
    public void Dispose()
    {
    }

    /// <summary>按类别转发日志的记录器。</summary>
    /// <remarks>
    /// 每个类别一个实例（由日志系统决定何时创建），因此这里只持有不可变的引用与字符串，
    /// 不含任何跨条目状态。
    /// </remarks>
    private sealed class Broadcaster : ILogger
    {
        private readonly AdminLogBroadcaster _owner;
        private readonly string _category;
        private readonly bool _forward;

        public Broadcaster(AdminLogBroadcaster owner, string category)
        {
            _owner = owner;
            _category = category;
            _forward = owner._enabled && !string.Equals(category, IgnoredCategory, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
            => _forward && logLevel >= MinimumLevel && logLevel != LogLevel.None;

        /// <inheritdoc />
        /// <remarks>
        /// 作用域用于给同一段流程的日志附加共享字段，而转发出去的条目已经定型为单行文本，
        /// 没有地方承载它们，因此返回 null。日志系统允许空作用域。
        /// </remarks>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // 格式化器只管消息模板，异常要由提供程序自己接上——控制台提供程序也是这么做的。
            // 不接的话，出错时后台只看得到「处理消息时出错」而看不到原因，
            // 而那恰恰是打开后台想找的东西。
            string message = formatter(state, exception);
            if (exception is not null)
            {
                message = string.Concat(message, Environment.NewLine, exception);
            }

            _owner._notifications.Publish(new AdminLogEntry(
                Timestamp: _owner._time.GetUtcNow(),
                Level: logLevel.ToString(),
                Category: _category,
                Message: message));
        }
    }
}
