using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Sessions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 交互会话存储测试：会话键的提取与隔离、空闲超时的懒重建与主动清理、会话状态的读写。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SessionStoreTests
{
    /// <summary>会话键由消息事件的会话与发送者两部分组成。</summary>
    [Fact]
    public void 会话键从消息事件提取会话与发送者()
    {
        MessageEvent message = CreateMessage("u1", ConversationType.Group, "g1");

        var key = SessionKey.From(message);

        Assert.Equal(message.Conversation, key.Conversation);
        Assert.Equal("u1", key.UserId);
    }

    /// <summary>用户标识为空白属于非法状态，构造会话键时立即失败。</summary>
    [Fact]
    public void 会话键拒绝空白用户标识()
    {
        var conversation = new ConversationId("test", ConversationType.Private, "c1");

        Assert.Throws<ArgumentException>(() => new SessionKey(conversation, "  "));
    }

    /// <summary>同一个会话中的同一个用户，反复获取拿到的永远是同一个实例。</summary>
    [Fact]
    public async Task 相同会话与用户始终得到同一个会话实例()
    {
        var store = CreateStore();
        var key = CreateKey("u1");

        Session first = await store.GetOrCreateAsync(key, CancellationToken.None);
        Session second = await store.GetOrCreateAsync(key, CancellationToken.None);

        Assert.Same(first, second);
    }

    /// <summary>同一会话里，不同用户的状态互不可见。</summary>
    [Fact]
    public async Task 不同用户在同一会话中的状态互不干扰()
    {
        var store = CreateStore();

        Session alice = await store.GetOrCreateAsync(CreateKey("alice"), CancellationToken.None);
        Session bob = await store.GetOrCreateAsync(CreateKey("bob"), CancellationToken.None);

        alice.Set("nickname", "爱丽丝");

        Assert.True(alice.TryGet<string>("nickname", out var nickname));
        Assert.Equal("爱丽丝", nickname);
        Assert.False(bob.TryGet<string>("nickname", out _));
    }

    /// <summary>同一个用户 id 出现在私聊与群里时，是两个彼此独立的会话。</summary>
    [Fact]
    public async Task 私聊与群聊中的同名用户互不干扰()
    {
        var store = CreateStore();

        Session inPrivate = await store.GetOrCreateAsync(
            CreateKey("u1", ConversationType.Private, "u1"),
            CancellationToken.None);

        Session inGroup = await store.GetOrCreateAsync(
            CreateKey("u1", ConversationType.Group, "g1"),
            CancellationToken.None);

        Assert.NotSame(inPrivate, inGroup);
    }

    /// <summary>查找不存在的会话返回空，且不会顺手建出一个（否则每个说话的人都会留下会话对象）。</summary>
    [Fact]
    public async Task 查找不存在的会话不创建实例()
    {
        var time = new FakeTimeProvider(StartTime);
        var store = CreateStore(timeProvider: time);
        var key = CreateKey("u1");

        Assert.Null(await store.FindAsync(key, CancellationToken.None));

        // 若查找时悄悄建了会话，其创建时间会停在 StartTime。
        time.Advance(TimeSpan.FromMinutes(1));

        Session created = await store.GetOrCreateAsync(key, CancellationToken.None);

        Assert.Equal(StartTime.AddMinutes(1), created.CreatedAt);
    }

    /// <summary>空闲超过阈值的会话会被丢弃，下次获取得到全新实例（状态一并作废）。</summary>
    [Fact]
    public async Task 空闲超时后再次获取得到全新实例()
    {
        var time = new FakeTimeProvider(StartTime);
        var store = CreateStore(timeProvider: time);
        var key = CreateKey("u1");

        Session first = await store.GetOrCreateAsync(key, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(31));

        Session second = await store.GetOrCreateAsync(key, CancellationToken.None);

        Assert.NotSame(first, second);
    }

    /// <summary>持续被使用的会话不会因「创建得早」而被判过期。</summary>
    [Fact]
    public async Task 持续使用的会话不会被过期重建()
    {
        var time = new FakeTimeProvider(StartTime);
        var store = CreateStore(timeProvider: time);
        var key = CreateKey("u1");

        Session first = await store.GetOrCreateAsync(key, CancellationToken.None);

        // 每 20 分钟使用一次，始终没累积到 30 分钟的空闲。
        for (var round = 0; round < 3; round++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            first.Set("round", round);
        }

        Assert.Same(first, await store.GetOrCreateAsync(key, CancellationToken.None));
    }

    /// <summary>主动清理只移除空闲超时的会话，并返回移除数量。</summary>
    [Fact]
    public async Task 清理只移除空闲超时的会话并返回数量()
    {
        var time = new FakeTimeProvider(StartTime);
        var store = CreateStore(timeProvider: time);

        var idleKey = CreateKey("idle");
        var activeKey = CreateKey("active");

        await store.GetOrCreateAsync(idleKey, CancellationToken.None);
        Session active = await store.GetOrCreateAsync(activeKey, CancellationToken.None);

        // 先推进 20 分钟让 active 续一次命，再推进 20 分钟：
        // 此时 idle 已空闲 40 分钟，active 只空闲 20 分钟。
        time.Advance(TimeSpan.FromMinutes(20));
        active.Set("touched", true);
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(1, await store.CleanupExpiredAsync(CancellationToken.None));
        Assert.Null(await store.FindAsync(idleKey, CancellationToken.None));
        Assert.NotNull(await store.FindAsync(activeKey, CancellationToken.None));
    }

    /// <summary>把空闲超时配置为非正值即关闭过期策略：再久也不会被清理。</summary>
    [Fact]
    public async Task 关闭空闲超时策略时清理不移除任何会话()
    {
        var time = new FakeTimeProvider(StartTime);
        var store = CreateStore(
            new SessionOptions { IdleTimeout = Timeout.InfiniteTimeSpan },
            time);

        var key = CreateKey("u1");
        Session session = await store.GetOrCreateAsync(key, CancellationToken.None);

        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(0, await store.CleanupExpiredAsync(CancellationToken.None));
        Assert.Same(session, await store.GetOrCreateAsync(key, CancellationToken.None));
    }

    /// <summary>会话状态读不到不存在的键，也读不到类型不匹配的值；移除是幂等的。</summary>
    [Fact]
    public async Task 会话状态按类型读取()
    {
        var store = CreateStore();
        Session session = await store.GetOrCreateAsync(CreateKey("u1"), CancellationToken.None);

        Assert.False(session.TryGet<string>("missing", out _));

        session.Set("age", 42);

        Assert.True(session.TryGet<int>("age", out var age));
        Assert.Equal(42, age);
        Assert.False(session.TryGet<string>("age", out _));
        Assert.True(session.Remove("age"));
        Assert.False(session.Remove("age"));
    }

    /// <summary>
    /// 并发获取同一个会话时，字典里只会有一个实例存活——所有调用者拿到的都是它。
    /// </summary>
    [Fact]
    public async Task 并发获取同一会话只产生一个存活实例()
    {
        var store = CreateStore();
        var key = CreateKey("u1");

        Session[] sessions = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => store.GetOrCreateAsync(key, CancellationToken.None).AsTask()));

        Assert.Single(sessions.Distinct());
    }

    // ---------- 测试基础设施 ----------

    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static InMemorySessionStore CreateStore(
        SessionOptions? options = null,
        TimeProvider? timeProvider = null)
        => new(
            Options.Create(options ?? new SessionOptions()),
            timeProvider ?? new FakeTimeProvider(StartTime),
            NullLogger<InMemorySessionStore>.Instance);

    private static SessionKey CreateKey(
        string userId,
        ConversationType type = ConversationType.Private,
        string conversationId = "conv-1")
        => new(new ConversationId("test", type, conversationId), userId);

    private static MessageEvent CreateMessage(
        string senderId,
        ConversationType type = ConversationType.Private,
        string conversationId = "conv-1") => new()
        {
            Conversation = new ConversationId("test", type, conversationId),
            Message = Message.Text("测试"),
            MessageId = "msg-1",
            SenderId = senderId,
        };

    /// <summary>可控时间源：只覆写 <see cref="GetUtcNow"/>，用 <see cref="Advance"/> 推进时间。</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
