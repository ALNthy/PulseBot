namespace AnBot.Core.Adapters;

/// <summary>发送失败分类：供插件决定重试 / 降级 / 提示，不暴露平台错误码。</summary>
public enum SendFailureKind
{
    /// <summary>没有注册目标平台的适配器（宿主漏注册或平台名写错）。</summary>
    NoAdapter = 0,

    /// <summary>适配器存在但当前未连接 / 未启动（如反向 WebSocket 尚无客户端连入）。</summary>
    NotConnected = 1,

    /// <summary>平台明确拒绝（权限不足、被风控、目标不存在等）。</summary>
    Rejected = 2,

    /// <summary>等待平台响应超时。</summary>
    Timeout = 3,

    /// <summary>消息包含目标平台不支持的段类型。</summary>
    UnsupportedSegment = 4,

    /// <summary>其他未归类失败（适配器已记录详细日志）。</summary>
    Unknown = 5,

    /// <summary>
    /// 被框架主动拦截，消息根本未发往平台（限流、黑白名单、敏感词等出站中间件所为）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Rejected"/> 的区别：那是平台侧明确拒绝（权限不足、被风控、目标不存在），
    /// 这是框架侧在消息出网之前就拦下了。插件可据此决定是稍后重试还是改文案。
    /// </remarks>
    Blocked = 6,
}

/// <summary>
/// 发送结果。发送失败（对方被风控、连接断开、目标不存在）是聊天机器人的常规业务分支，
/// 因此以返回值而非异常表达：插件不必为每次回复写 try/catch，
/// 同时可按 <see cref="FailureKind"/> 决定重试或降级。
/// 仅当传入参数为 null 等编程错误时才抛 <see cref="ArgumentNullException"/>。
/// </summary>
/// <remarks>用记录类而非结构体：避免 <c>default(SendResult)</c> 这种「既非成功又无失败原因」的非法状态。</remarks>
public sealed record SendResult
{
    private SendResult(bool isSuccess, string? messageId, SendFailureKind? failureKind, string? errorMessage)
    {
        IsSuccess = isSuccess;
        MessageId = messageId;
        FailureKind = failureKind;
        ErrorMessage = errorMessage;
    }

    /// <summary>是否发送成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时平台返回的消息 id（供后续撤回 / 编辑 / 被动回复）；平台不返回时为 null。</summary>
    public string? MessageId { get; }

    /// <summary>失败分类；成功时为 null。</summary>
    public SendFailureKind? FailureKind { get; }

    /// <summary>失败描述，面向日志，不面向终端用户，不含堆栈。</summary>
    public string? ErrorMessage { get; }

    /// <summary>构造成功结果。</summary>
    public static SendResult Ok(string? messageId = null) => new(true, messageId, null, null);

    /// <summary>构造失败结果。</summary>
    public static SendResult Fail(SendFailureKind kind, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new(false, null, kind, errorMessage);
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString()
        => IsSuccess ? $"成功(MessageId={MessageId ?? "-"})" : $"失败({FailureKind}): {ErrorMessage}";
}
