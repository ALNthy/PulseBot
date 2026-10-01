namespace PulseBot.Core.Sessions;

/// <summary>
/// 交互会话配置。宿主绑定配置节后通过 <c>IOptions&lt;SessionOptions&gt;</c> 注入使用；
/// 未绑定时使用本类默认值。
/// </summary>
/// <remarks>
/// 两个超时都允许用 <see cref="Timeout.InfiniteTimeSpan"/>（或任何非正值）表示「不过期」，
/// 与命令域的宽容风格一致：配置写错不应让机器人启动失败。
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>推荐配置节名，宿主形如 <c>services.Configure&lt;SessionOptions&gt;(config.GetSection(SessionOptions.SectionName))</c>。</summary>
    public const string SectionName = "Sessions";

    /// <summary>
    /// 会话空闲超时，默认 30 分钟：超过这段时间没有被真正使用（读写状态、发起提问、收到回答），
    /// 会话连同其中的状态一起作废。
    /// </summary>
    /// <remarks>
    /// 注意「空闲」的判定依据是最后使用时刻而非最后发言时刻：
    /// 用户连发普通消息不会给会话续命，插件显式读写状态才会。见 <see cref="ISessionStore.FindAsync"/>。
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 单次提问的等待超时，默认 5 分钟：插件提问后超过这段时间仍未收到回答，
    /// 等待态作废，用户此后的消息按普通消息处理（而不是被当成迟到的回答吞掉）。
    /// </summary>
    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 等待回答期间用于放弃回答的取消词，默认 <c>"/cancel"</c>。
    /// 对消息纯文本去除首尾空白后**精确匹配**（忽略大小写），不做前缀匹配——
    /// 否则 <c>/cancel foo</c> 这类输入会被误吞。
    /// </summary>
    public string[] CancelCommands { get; set; } = ["/cancel"];

    /// <summary>用户发送取消词后回复的提示语，默认「已取消。」。</summary>
    public string CancelMessage { get; set; } = "已取消。";

    /// <summary>
    /// 后台清理的间隔，默认 5 分钟。
    /// Core 不引用 Hosting 抽象、自身不跑后台任务，本项仅供宿主侧的清理服务使用
    /// （见 <see cref="ISessionStore.CleanupExpiredAsync"/>）。
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
