using System.Text;

namespace PulseBot.Core.Messages;

/// <summary>
/// 消息：由有序 <see cref="MessageSegment"/> 列表构成的不可变容器，是收发两侧的统一载体。
/// </summary>
/// <remarks>
/// 使用 sealed class 而非 record：<see cref="Segments"/> 是集合，record 生成的相等性只比较集合引用，
/// 会给出误导性的「值相等」语义；消息是否相等由调用方按段序列自行判断。
/// 构造时复制段列表——实例一经创建即可安全跨线程共享。
/// </remarks>
public sealed class Message
{
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
    public override string ToString()
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
