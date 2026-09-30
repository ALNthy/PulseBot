using PulseBot.Core.Messages;

namespace PulseBot.Core.Tests;

/// <summary>
/// 消息模型测试：纯文本渲染、拼接、隐式转换与不可变性。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MessageTests
{
    /// <summary>纯文本消息渲染为原文。</summary>
    [Fact]
    public void 文本消息渲染为原文()
    {
        Assert.Equal("你好", Message.Text("你好").ToString());
    }

    /// <summary>空消息渲染为空字符串，且 IsEmpty 为真。</summary>
    [Fact]
    public void 空消息渲染为空字符串()
    {
        Assert.True(Message.Empty.IsEmpty);
        Assert.Equal(string.Empty, Message.Empty.ToString());
    }

    /// <summary>多段消息按段顺序拼接渲染。</summary>
    [Fact]
    public void 多段消息按顺序渲染()
    {
        var message = new Message(new TextSegment("前缀-"), new TextSegment("后缀"));

        Assert.Equal("前缀-后缀", message.ToString());
        Assert.Equal(2, message.Segments.Count);
    }

    /// <summary>隐式转换：字符串可直接当作消息使用。</summary>
    [Fact]
    public void 字符串隐式转换为消息()
    {
        Message message = "直接赋值";

        Assert.Equal("直接赋值", message.ToString());
    }

    /// <summary>隐式转换：单个消息段可直接当作消息使用。</summary>
    [Fact]
    public void 消息段隐式转换为消息()
    {
        Message message = new TextSegment("单段");

        Assert.Equal("单段", message.ToString());
        Assert.Single(message.Segments);
    }

    /// <summary>Concat 与 + 运算符都返回按顺序拼接的新实例，原实例不变。</summary>
    [Fact]
    public void 拼接返回新实例且不影响原实例()
    {
        var left = Message.Text("左");
        var right = Message.Text("右");

        Assert.Equal("左右", left.Concat(right).ToString());
        Assert.Equal("左右", (left + right).ToString());

        // 原实例保持不变（不可变语义）。
        Assert.Equal("左", left.ToString());
        Assert.Equal("右", right.ToString());
    }

    /// <summary>构造时复制段列表：外部修改源集合不会影响已创建的消息。</summary>
    [Fact]
    public void 构造时复制段列表()
    {
        var source = new List<MessageSegment> { new TextSegment("初始") };

        var message = new Message(source);
        source.Add(new TextSegment("追加"));

        Assert.Single(message.Segments);
        Assert.Equal("初始", message.ToString());
    }

    /// <summary>构造函数与工厂方法对 null 参数抛出参数异常。</summary>
    [Fact]
    public void 空参数抛出参数异常()
    {
        // 本测试专门验证参数校验，此处必须有 null；显式抑制可空警告。
        Assert.Throws<ArgumentNullException>(() => Message.Text(null!));
        Assert.Throws<ArgumentNullException>(() => new Message((IEnumerable<MessageSegment>)null!));
        Assert.Throws<ArgumentNullException>(() => Message.Text("x").Concat(null!));
    }

    /// <summary>会话标识按平台、类型与 id 三者区分（record 值相等性）。</summary>
    [Fact]
    public void 会话标识按值比较()
    {
        var group = new ConversationId("onebot", ConversationType.Group, "123");
        var sameGroup = new ConversationId("onebot", ConversationType.Group, "123");
        var privateChat = new ConversationId("onebot", ConversationType.Private, "123");
        var otherPlatform = new ConversationId("discord", ConversationType.Group, "123");

        Assert.Equal(group, sameGroup);
        Assert.NotEqual(group, privateChat);
        Assert.NotEqual(group, otherPlatform);
    }
}
