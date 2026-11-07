using AnBot.Core.Messages;

namespace AnBot.Core.Tests;

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

    /// <summary>
    /// 多段消息的纯文本渲染结果被缓存：反复读取返回同一个字符串引用，拼接只发生一次。
    /// 一条消息在事件管道里会被多个处理器依次读取，重复拼接是热路径上的真实开销。
    /// </summary>
    [Fact]
    public void 多段消息的渲染结果被缓存()
    {
        var message = new Message(new AtSegment("123", "某人"), new TextSegment(" 你好"));

        string first = message.ToString();

        Assert.Same(first, message.ToString());
        Assert.Equal("@某人 你好", first);
    }

    /// <summary>
    /// 并发首次读取同一实例的纯文本结果一致：缓存写入免锁，最坏情况只是多渲染一份等价结果。
    /// </summary>
    [Fact]
    public async Task 并发首次读取纯文本结果一致()
    {
        var message = new Message(new TextSegment("A"), new TextSegment("B"), new TextSegment("C"));

        string[] results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => message.ToString())));

        Assert.All(results, text => Assert.Equal("ABC", text));
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

    /// <summary>
    /// 新增段类型的纯文本渲染：平台不支持时靠它回退，因此每种段都必须有可读的占位文本。
    /// </summary>
    [Fact]
    public void 新增段类型渲染为可读的纯文本()
    {
        var message = new Message(
            new MentionAllSegment(),
            new AudioSegment("file:///a.silk"),
            new VideoSegment("file:///c.mp4"),
            new FileSegment("file:///d.zip", "报告.zip"),
            new LocationSegment(31.2, 121.5, "上海"));

        Assert.Equal("@全体成员[语音][视频][文件:报告.zip][位置:上海]", message.ToString());
    }

    /// <summary>可选信息缺失（文件名、地点名）时退化为更短的占位符。</summary>
    [Fact]
    public void 缺少可选信息的段渲染为短占位符()
    {
        var message = new Message(
            new FileSegment("file:///d.zip"),
            new LocationSegment(31.2, 121.5));

        Assert.Equal("[文件][位置]", message.ToString());
    }
}
