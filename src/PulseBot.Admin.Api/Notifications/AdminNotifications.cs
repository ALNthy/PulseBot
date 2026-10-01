using System.Threading.Channels;
using PulseBot.Admin.Shared.Notifications;

namespace PulseBot.Admin.Api.Notifications;

/// <summary>
/// 后台实时推送的缓冲：日志与事件各一条有界通道。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有界</b>：写入方在宿主的日志链路与事件管道上，读取方是要经过
/// SignalR 网络发送的后台客户端。没有客户端连接、或客户端网络卡住时，
/// 无界通道会一直堆积直到耗尽内存——后台把宿主拖垮，这恰恰是后台最不该造成的事。
/// 有界 + <see cref="BoundedChannelFullMode.DropOldest"/> 把最坏情况限定为
/// 「后台看到的事件流有空洞」，而宿主完全不受影响。
/// </para>
/// <para>
/// <see cref="Publish(AdminLogEntry)"/> 用的是 <c>TryWrite</c>，在丢弃模式下必然立即返回，
/// 因此这两条通道都不会在热路径上产生等待。
/// </para>
/// </remarks>
public sealed class AdminNotifications
{
    /// <summary>单条通道的容量。按「后台打开页面时能立刻看到最近一批」估计，不需要更大。</summary>
    private const int Capacity = 1000;

    private readonly Channel<AdminLogEntry> _logs;
    private readonly Channel<AdminEventEntry> _events;

    /// <summary>构造函数：建立两条有界通道。</summary>
    public AdminNotifications()
    {
        BoundedChannelOptions options = new(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,

            // 写入方确实可能并发（多个线程同时记日志），读取方只有一个推送循环。
            SingleReader = true,
            SingleWriter = false,
        };

        _logs = Channel.CreateBounded<AdminLogEntry>(options);
        _events = Channel.CreateBounded<AdminEventEntry>(options);
    }

    /// <summary>日志通道的读取端，由推送服务独占。</summary>
    public ChannelReader<AdminLogEntry> Logs => _logs.Reader;

    /// <summary>事件通道的读取端，由推送服务独占。</summary>
    public ChannelReader<AdminEventEntry> Events => _events.Reader;

    /// <summary>投递一条日志；通道已满时丢弃最旧的一条。</summary>
    /// <param name="entry">日志条目。</param>
    public void Publish(AdminLogEntry entry) => _logs.Writer.TryWrite(entry);

    /// <summary>投递一条事件；通道已满时丢弃最旧的一条。</summary>
    /// <param name="entry">事件条目。</param>
    public void Publish(AdminEventEntry entry) => _events.Writer.TryWrite(entry);
}
