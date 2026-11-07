namespace AnBot.Admin.Shared.Control;

/// <summary>一份权限名单。</summary>
/// <param name="Key">
/// 名单标识，取值与 <c>PUT /admin/api/access-policy/{listKey}</c> 的路径参数完全一致。
/// 由读取接口一并返回，而不是让调用方从属性名去猜标识的拼写——
/// 两者一旦不一致，界面上每一份名单都会改不动，而错误只表现为一个笼统的 404。
/// </param>
/// <param name="Values">名单条目。</param>
public sealed record PermissionListInfo(string Key, IReadOnlyList<string> Values);

/// <summary>
/// 当前生效的接入策略。
/// </summary>
/// <remarks>
/// 返回的是内核**正在使用**的那份名单（内存快照），而不是数据库里的备份——
/// 两者在正常流程下始终一致，一旦写入失败，这里显示的才是系统真实的判定依据。
/// </remarks>
/// <param name="Lists">
/// 全部名单，顺序固定（与后台展示顺序一致）。
/// 用列表而不是逐份命名属性，是为了让每份名单自带标识，读写两侧的标识只有一个来源。
/// </param>
/// <param name="DeniedMessage">权限不足时回复给用户的提示。</param>
public sealed record AccessPolicyInfo(
    IReadOnlyList<PermissionListInfo> Lists,
    string DeniedMessage);

/// <summary>
/// 整体替换一份名单。
/// </summary>
/// <remarks>
/// 用整体替换而不是增删单条：名单是「一组 id」的整体，编辑器里也是整份提交，
/// 逐条增删接口反而要在并发编辑时处理中间态。
/// </remarks>
/// <param name="Values">新的完整名单。</param>
public sealed record UpdatePermissionListRequest(IReadOnlyList<string> Values);

/// <summary>启用或停用一个插件。</summary>
/// <param name="Enabled">目标状态。</param>
public sealed record SetPluginStateRequest(bool Enabled);

/// <summary>
/// 运行时控制写操作的结局。
/// </summary>
/// <remarks>
/// 接口层据此映射状态码：<see cref="Success"/> → 200、<see cref="UnknownList"/> 与
/// <see cref="PluginNotFound"/> → 404。
/// </remarks>
public enum RuntimeControlStatus
{
    /// <summary>操作成功，且已立即生效。</summary>
    Success,

    /// <summary>名单标识不存在。</summary>
    UnknownList,

    /// <summary>宿主中没有登记该类型名的插件。</summary>
    PluginNotFound,
}
