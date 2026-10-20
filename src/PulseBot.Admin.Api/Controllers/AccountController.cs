using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PulseBot.Admin.Infrastructure.Accounts;
using PulseBot.Admin.Shared.Accounts;

namespace PulseBot.Admin.Api.Controllers;

/// <summary>账号管理控制器：列表、创建、改自己密码、重置他人密码、启停、强制下线。</summary>
/// <remarks>
/// <para>
/// <b>操作者身份取自访问令牌的声明</b>，而不是请求体里的参数——否则调用方可以伪造成别人，
/// 例如「以 A 的名义改 A 的密码」，那正好绕过了 <see cref="AccountService"/> 里
/// 「重置他人密码」与「改自己密码必须验旧密码」这两条路径的区分。
/// </para>
/// <para>
/// 业务拒绝（用户名重复、不能停用自己等）全部由服务层用
/// <see cref="AccountOperationStatus"/> 表达，本层只做状态码翻译，
/// 不重复实现规则——两处各写一遍，早晚会漂移出「某个入口绕过了不变量」。
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
[Route(AdminApiEndpointRouteBuilderExtensions.RoutePrefix + "/accounts")]
public sealed class AccountController : ControllerBase
{
    /// <summary>账号服务，承载全部业务规则与审计。</summary>
    private readonly IAccountService _accounts;

    /// <summary>构造函数注入账号服务。</summary>
    /// <param name="accounts">账号服务。</param>
    public AccountController(IAccountService accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _accounts = accounts;
    }

    /// <summary>列出全部后台账号。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>账号列表。</returns>
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountInfo> list = await _accounts
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(list);
    }

    /// <summary>创建后台账号。</summary>
    /// <param name="request">用户名与初始密码。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>创建成功的账号；用户名被占用为 409。</returns>
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "用户名与初始密码均不能为空。");
        }

        AccountOperationResult result = await _accounts
            .CreateAsync(actor, request.UserName, request.Password, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    /// <summary>修改自己的密码（需提供当前密码），并使自己的全部刷新令牌失效。</summary>
    /// <param name="request">当前密码与新密码。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的账号信息；当前密码错误为 401。</returns>
    /// <remarks>
    /// 字面量段 "me" 的路由优先级高于参数段，且 <c>{id:int}</c> 本身也匹配不上 "me"，
    /// 因此这条不会被「重置他人密码」那条抢走。
    /// </remarks>
    [HttpPut("me/password")]
    public async Task<IActionResult> ChangeOwnPasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "当前密码与新密码均不能为空。");
        }

        AccountOperationResult result = await _accounts
            .ChangeOwnPasswordAsync(actor, request.CurrentPassword, request.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    /// <summary>重置指定账号的密码（无需旧密码），并使其全部刷新令牌失效。</summary>
    /// <param name="id">目标账号主键。</param>
    /// <param name="request">新密码。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的账号信息；账号不存在为 404。</returns>
    [HttpPut("{id:int}/password")]
    public async Task<IActionResult> ResetPasswordAsync(
        [FromRoute] int id,
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrEmpty(request.NewPassword))
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "新密码不能为空。");
        }

        AccountOperationResult result = await _accounts
            .ResetPasswordAsync(actor, id, request.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    /// <summary>启用或停用指定账号；停用后该账号无法登录且无法刷新令牌。</summary>
    /// <param name="id">目标账号主键。</param>
    /// <param name="request">目标状态。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的账号信息；账号不存在为 404；最后一个启用账号不可停用为 409。</returns>
    [HttpPut("{id:int}/state")]
    public async Task<IActionResult> SetStateAsync(
        [FromRoute] int id,
        [FromBody] SetAccountStateRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null)
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "缺少 Enabled 字段。");
        }

        AccountOperationResult result = await _accounts
            .SetEnabledAsync(actor, id, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    /// <summary>强制下线：撤销指定账号的全部刷新令牌。</summary>
    /// <param name="id">目标账号主键。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的账号信息；账号不存在为 404。</returns>
    [HttpPost("{id:int}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessionsAsync(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        AccountOperationResult result = await _accounts
            .RevokeSessionsAsync(actor, id, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    /// <summary>把服务层的业务结果翻译成 HTTP 响应。</summary>
    /// <remarks>
    /// 404 与 409 的分工：目标不存在是 404；目标存在但与当前状态冲突
    /// （用户名被占用、只剩最后一个启用账号）是 409。前端据此决定
    /// 「刷新列表」还是「提示用户改个名字」。
    /// </remarks>
    private static IActionResult ToResponse(AccountOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            AccountOperationStatus.Success => new OkObjectResult(result.Account),

            AccountOperationStatus.NotFound => ApiResponses.Failure(
                StatusCodes.Status404NotFound, ApiErrorCodes.AccountNotFound, "账号不存在。"),

            // 只可能出现在「改自己密码」路径上：当前密码不对。
            AccountOperationStatus.InvalidCredentials => ApiResponses.Failure(
                StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidCredentials, "当前密码不正确。"),

            AccountOperationStatus.UserNameTaken => ApiResponses.Failure(
                StatusCodes.Status409Conflict, ApiErrorCodes.UserNameTaken, "用户名已被占用。"),

            AccountOperationStatus.LastEnabledAccount => ApiResponses.Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.LastEnabledAccount,
                "这是最后一个启用的账号，停用后将无人能登录后台。请先启用或创建另一个账号。"),

            AccountOperationStatus.WeakPassword => ApiResponses.Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.WeakPassword,
                $"密码长度至少为 {AccountRules.MinimumPasswordLength} 个字符。"),

            AccountOperationStatus.SelfOperation => ApiResponses.Failure(
                StatusCodes.Status400BadRequest, ApiErrorCodes.SelfOperation, "不能对自己执行该操作。"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Status, "未知的账号操作结果：接口层没有为它定义响应。"),
        };
    }
}
