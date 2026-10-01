using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PulseBot.Admin.Infrastructure.Accounts;
using PulseBot.Admin.Shared.Accounts;

namespace PulseBot.Admin.Api.Endpoints;

/// <summary>账号管理端点：列表、创建、改自己密码、重置他人密码、启停、强制下线。</summary>
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
internal static class AccountEndpoints
{
    /// <summary>把账号端点挂到分组上。</summary>
    /// <param name="group">后台 API 分组（前缀 <see cref="AdminApiEndpointRouteBuilderExtensions.RoutePrefix"/>）。</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/accounts", ListAsync)
            .WithSummary("列出全部后台账号")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPost("/accounts", CreateAsync)
            .WithSummary("创建后台账号")
            .RequireAuthorization(AdminPolicies.Admin);

        // 字面量段 "me" 的路由优先级高于参数段，且 {id:int} 本身也匹配不上 "me"，
        // 因此这条不会被下一条抢走；放在前面只是为了让「自己」与「他人」在读代码时更容易分辨。
        group.MapPut("/accounts/me/password", ChangeOwnPasswordAsync)
            .WithSummary("修改自己的密码（需提供当前密码），并使自己的全部刷新令牌失效")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPut("/accounts/{id:int}/password", ResetPasswordAsync)
            .WithSummary("重置指定账号的密码（无需旧密码），并使其全部刷新令牌失效")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPut("/accounts/{id:int}/state", SetStateAsync)
            .WithSummary("启用或停用指定账号；停用后该账号无法登录且无法刷新令牌")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPost("/accounts/{id:int}/revoke-sessions", RevokeSessionsAsync)
            .WithSummary("强制下线：撤销指定账号的全部刷新令牌")
            .RequireAuthorization(AdminPolicies.Admin);
    }

    private static async Task<IResult> ListAsync(
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountInfo> list = await accounts
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        CreateAccountRequest request,
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "用户名与初始密码均不能为空。"));
        }

        AccountOperationResult result = await accounts
            .CreateAsync(actor, request.UserName, request.Password, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "当前密码与新密码均不能为空。"));
        }

        AccountOperationResult result = await accounts
            .ChangeOwnPasswordAsync(actor, request.CurrentPassword, request.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    private static async Task<IResult> ResetPasswordAsync(
        int id,
        ResetPasswordRequest request,
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || string.IsNullOrEmpty(request.NewPassword))
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "新密码不能为空。"));
        }

        AccountOperationResult result = await accounts
            .ResetPasswordAsync(actor, id, request.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    private static async Task<IResult> SetStateAsync(
        int id,
        SetAccountStateRequest request,
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null)
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "缺少 Enabled 字段。"));
        }

        AccountOperationResult result = await accounts
            .SetEnabledAsync(actor, id, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    private static async Task<IResult> RevokeSessionsAsync(
        int id,
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        AccountOperationResult result = await accounts
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
    private static IResult ToResponse(AccountOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            AccountOperationStatus.Success => Results.Ok(result.Account),

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
