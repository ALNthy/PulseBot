namespace PulseBot.Admin.Shared.Accounts;

/// <summary>账号相关的固定规则。</summary>
/// <remarks>
/// 放在共享项目里，使服务层的校验与接口层的提示文案引用同一个值——
/// 否则改了服务端的下限却忘了改提示，界面会给出与实际不符的要求。
/// </remarks>
public static class AccountRules
{
    /// <summary>密码的最短长度。</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>用户名的最大长度，与数据库列宽一致。</summary>
    public const int MaxUserNameLength = 64;
}

/// <summary>账号信息（对外）。不含任何凭据材料。</summary>
/// <param name="Id">账号主键。</param>
/// <param name="UserName">登录名。</param>
/// <param name="Role">角色名。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="CreatedAt">创建时刻。</param>
/// <param name="LastLoginAt">最后一次登录成功时刻；从未登录过为 <c>null</c>。</param>
public sealed record AccountInfo(
    int Id,
    string UserName,
    string Role,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// 发起账号写操作的操作者。
/// </summary>
/// <remarks>
/// 服务层需要它来写审计、判断「是不是在操作自己」以及「是不是在停用自己」，
/// 而这些信息只有接口层（持有当前登录身份的那个地方）知道，因此显式传入。
/// </remarks>
/// <param name="UserId">操作者的账号主键。</param>
/// <param name="UserName">操作者的登录名。</param>
/// <param name="RemoteIp">操作者来源 IP。</param>
public sealed record AccountActor(int UserId, string UserName, string? RemoteIp);

/// <summary>创建账号。</summary>
/// <param name="UserName">登录名。</param>
/// <param name="Password">初始密码明文。</param>
public sealed record CreateAccountRequest(string UserName, string Password);

/// <summary>修改自己的密码。</summary>
/// <param name="CurrentPassword">当前密码，用于证明操作者是账号本人。</param>
/// <param name="NewPassword">新密码明文。</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// 重置他人密码。
/// </summary>
/// <remarks>
/// 不带旧密码：调用方是管理员，这正是「忘记密码」场景下唯一的救援通道。
/// 代价是该接口一旦被滥用即可接管任意账号，因此它必须要求管理员身份并落审计。
/// </remarks>
/// <param name="NewPassword">新密码明文。</param>
public sealed record ResetPasswordRequest(string NewPassword);

/// <summary>启用或停用账号。</summary>
/// <param name="Enabled">目标状态。</param>
public sealed record SetAccountStateRequest(bool Enabled);

/// <summary>
/// 账号写操作的结局。
/// </summary>
/// <remarks>
/// 每种失败都有对应的 HTTP 语义，接口层据此映射状态码：
/// <see cref="NotFound"/> → 404、<see cref="InvalidCredentials"/> → 401、
/// <see cref="UserNameTaken"/> 与 <see cref="LastEnabledAccount"/> → 409、
/// 其余 → 400。
/// </remarks>
public enum AccountOperationStatus
{
    /// <summary>操作成功。</summary>
    Success,

    /// <summary>目标账号不存在。</summary>
    NotFound,

    /// <summary>当前密码错误（仅改自己的密码时可能出现）。</summary>
    InvalidCredentials,

    /// <summary>登录名已被占用。</summary>
    UserNameTaken,

    /// <summary>密码长度不满足 <see cref="AccountRules.MinimumPasswordLength"/>。</summary>
    WeakPassword,

    /// <summary>
    /// 不能停用最后一个启用的账号。
    /// </summary>
    /// <remarks>
    /// 否则会把自己锁在门外，且没有任何界面上的补救手段——只能改数据库文件。
    /// </remarks>
    LastEnabledAccount,

    /// <summary>该操作不允许作用于自己（停用自己、重置自己的密码）。</summary>
    SelfOperation,
}

/// <summary>账号写操作的结果。</summary>
/// <param name="Status">结局。</param>
/// <param name="Account">成功时的账号信息（已反映本次改动）；失败时为 <c>null</c>。</param>
public sealed record AccountOperationResult(AccountOperationStatus Status, AccountInfo? Account)
{
    /// <summary>是否成功。</summary>
    public bool IsSuccess => Status == AccountOperationStatus.Success;

    /// <summary>构造成功结果。</summary>
    /// <param name="account">改动后的账号信息。</param>
    public static AccountOperationResult Ok(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountOperationResult(AccountOperationStatus.Success, account);
    }

    /// <summary>构造失败结果。</summary>
    /// <param name="status">失败原因，不能是 <see cref="AccountOperationStatus.Success"/>。</param>
    public static AccountOperationResult Fail(AccountOperationStatus status)
    {
        if (status == AccountOperationStatus.Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "失败结果不能使用 Success；成功请用 AccountOperationResult.Ok。");
        }

        return new AccountOperationResult(status, null);
    }
}
