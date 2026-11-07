using System.Text;

namespace AnBot.Core.Messages;

/// <summary>
/// 消息：由有序 <see cref="MessageSegment"/> 列表构成的不可变容器，是收发两侧的统一载体。
/// </summary>
/// <remarks>
/// <para>
/// 使用 sealed class 而非 record：<see cref="Segments"/> 是集合，record 生成的相等性只比较集合引用，
/// 会给出误导性的「值相等」语义；消息是否相等由调用方按段序列自行判断。
/// 构造时复制段列表——实例一经创建即可安全跨线程共享。
/// </para>
/// <para>
/// 纯文本渲染结果在首次 <see cref="ToString"/> 时算好并缓存：一条消息在事件管道里会被多个
/// 处理器依次读取（会话拦截判取消词、命令分发做命令匹配、插件再读一次），
/// 缓存让多段消息的拼接最多只发生一次。缓存字段是纯派生值、对外不可见，
/// 不改变本类型「构造后只读」的语义，也无损线程安全。
/// </para>
/// </remarks>
public sealed class Message
{
    /// <summary>
    /// 纯文本渲染结果的缓存；null 表示尚未渲染过。
    /// </summary>
    /// <remarks>
    /// 免锁写入：并发首次渲染时最坏情况是各自渲染一份等价结果，写回的是不可变字符串引用，
    /// 不存在撕裂，也无须同步。
    /// </remarks>
    private string? _plainText;

    /// <summary>空消息哨兵（无任何段），避免重复分配。</summary>
    public static Message Empty { get; } = new(Array.Empty<MessageSegment>());

    /// <summary>有序消息段列表；构造时复制，外部不可变。</summary>
    public IReadOnlyList<MessageSegment> Segments { get; }

    /// <summary>是否不含任何段。</summary>
    public bool IsEmpty => Segments.Count == 0;

    /// <summary>由段序列构造消息（段列表会被复制冻结）。</summary>
    public Message(IEnumerable<MessageSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments.ToArray();
    }

    /// <summary>由段数组构造消息，支持 <c>new Message(seg1, seg2)</c> 写法。</summary>
    public Message(params MessageSegment[] segments)
        : this((IEnumerable<MessageSegment>)segments)
    {
    }

    /// <summary>构造只含一个文本段的消息。</summary>
    public static Message Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Message(new TextSegment(text));
    }

    /// <summary>字符串到消息的隐式转换，便于插件写 <c>sender.SendAsync(conversation, "你好", ct)</c>。</summary>
    public static implicit operator Message(string text) => Text(text);

    /// <summary>单个消息段到消息的隐式转换。</summary>
    public static implicit operator Message(MessageSegment segment) => new(segment);

    /// <summary>顺序拼接两条消息，返回新实例（原实例不变）。</summary>
    public Message Concat(Message other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Message(Segments.Concat(other.Segments));
    }

    /// <summary>拼接运算符，等价于 <see cref="Concat"/>。</summary>
    public static Message operator +(Message left, Message right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Concat(right);
    }

    /// <summary>按段顺序渲染为纯文本：文本直出，图片/表情用占位符，回复引用不输出。</summary>
    /// <remarks>
    /// 结果是纯派生值，首次调用后缓存复用——同一实例反复调用返回同一个字符串引用。
    /// </remarks>
    public override string ToString() => _plainText ??= RenderPlainText();

    /// <summary>执行一次实际渲染，仅供 <see cref="ToString"/> 在缓存未命中时调用。</summary>
    private string RenderPlainText()
    {
        if (Segments.Count == 0)
        {
            return string.Empty;
        }

        if (Segments.Count == 1)
        {
            return Segments[0].ToPlainText();
        }

        var builder = new StringBuilder();
        foreach (MessageSegment segment in Segments)
        {
            builder.Append(segment.ToPlainText());
        }

        return builder.ToString();
    }
}
