using System.Collections.Frozen;

namespace PulseBot.Core.Permissions;

/// <summary>
/// 某一时刻生效的接入控制策略快照：黑白名单、超级管理员名单与权限不足提示语。
/// </summary>
/// <remarks>
/// <para>
/// 快照一经构造即不可变——所有名单在构造期归一化并冻结为 <see cref="FrozenSet{T}"/>，
/// 因此读路径可以无锁并发访问；变更方以「整体替换」的方式发布新版本
/// （见 <see cref="IAccessPolicy.Update"/>）。
/// </para>
/// <para>
/// 这正是名单能热更新而不牺牲热路径性能的关键：改名单的代价是构造一份新快照，
/// 而每条消息判定时的代价仍然只是一次引用读加一次哈希查找，
/// 与「构造期冻结、运行期只读」的开销完全相同。
/// </para>
/// <para>
/// 空值语义与 <see cref="PermissionOptions"/> 保持一致：
/// 白名单为空表示不限制，黑名单为空表示不封禁任何人。
/// </para>
/// </remarks>
public sealed class AccessPolicySnapshot
{
    /// <summary>创建快照；名单在此刻归一化（去首尾空白、丢弃空项、去重）并冻结。</summary>
    /// <param name="superAdmins">超级管理员用户 id 集合。</param>
    /// <param name="userBlacklist">用户黑名单。</param>
    /// <param name="userWhitelist">用户白名单。</param>
    /// <param name="groupBlacklist">群黑名单。</param>
    /// <param name="groupWhitelist">群白名单。</param>
    /// <param name="deniedMessage">权限不足提示语；<c>null</c> 视为空串（静默拒绝）。</param>
    /// <remarks>
    /// 归一化不是洁癖：白名单非空即构成允许列表，一个空串混进去会让
    /// 「名单非空」成立却匹配不到任何人——那等于把所有人关在门外，
    /// 而这类脏数据正是后台表单最容易产生的。
    /// </remarks>
    public AccessPolicySnapshot(
        IEnumerable<string> superAdmins,
        IEnumerable<string> userBlacklist,
        IEnumerable<string> userWhitelist,
        IEnumerable<string> groupBlacklist,
        IEnumerable<string> groupWhitelist,
        string? deniedMessage = null)
    {
        ArgumentNullException.ThrowIfNull(superAdmins);
        ArgumentNullException.ThrowIfNull(userBlacklist);
        ArgumentNullException.ThrowIfNull(userWhitelist);
        ArgumentNullException.ThrowIfNull(groupBlacklist);
        ArgumentNullException.ThrowIfNull(groupWhitelist);

        SuperAdmins = Freeze(superAdmins);
        UserBlacklist = Freeze(userBlacklist);
        UserWhitelist = Freeze(userWhitelist);
        GroupBlacklist = Freeze(groupBlacklist);
        GroupWhitelist = Freeze(groupWhitelist);
        DeniedMessage = deniedMessage ?? string.Empty;
    }

    /// <summary>超级管理员用户 id 集合：拥有全部权限，且不受白名单限制。</summary>
    public FrozenSet<string> SuperAdmins { get; }

    /// <summary>用户黑名单：名单内的用户发送的消息一律被丢弃，优先级高于白名单。</summary>
    public FrozenSet<string> UserBlacklist { get; }

    /// <summary>用户白名单：非空时构成允许列表，只有名单内的用户能使用机器人。</summary>
    public FrozenSet<string> UserWhitelist { get; }

    /// <summary>群黑名单：名单内的群消息一律被丢弃，私聊不受影响。</summary>
    public FrozenSet<string> GroupBlacklist { get; }

    /// <summary>群白名单：非空时只有在名单内的群能使用机器人，私聊不受影响。</summary>
    public FrozenSet<string> GroupWhitelist { get; }

    /// <summary>权限不足时向用户回复的提示文本；空字符串表示静默拒绝。</summary>
    public string DeniedMessage { get; }

    /// <summary>
    /// 从配置对象构造快照。这是宿主的播种来源——不接后台管理时，
    /// 名单就永远停在这一份上，行为与「名单只存在于配置文件里」完全一致。
    /// </summary>
    /// <param name="options">权限配置。</param>
    /// <returns>不可变的策略快照。</returns>
    public static AccessPolicySnapshot From(PermissionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AccessPolicySnapshot(
            options.SuperAdmins,
            options.UserBlacklist,
            options.UserWhitelist,
            options.GroupBlacklist,
            options.GroupWhitelist,
            options.DeniedMessage);
    }

    /// <summary>把一份名单归一化并冻结成查找结构：每个名单在消息热路径上都要查，这里换最省的形式。</summary>
    private static FrozenSet<string> Freeze(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToFrozenSet(StringComparer.Ordinal);
}
