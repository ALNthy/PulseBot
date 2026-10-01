namespace PulseBot.Admin.Shared;

/// <summary>
/// 后台管理配置。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> 为 <c>false</c> 时宿主完全不挂载后台：不注册服务、不开端口、不建数据库。
/// 这是「只跑机器人」的部署形态。
/// </para>
/// <para>
/// 启用后台时 <see cref="AdminAuthOptions.SigningKey"/> 必须配置：签名密钥缺失属于配置错误，
/// 宿主会在启动期直接失败，而不是退化成一个没有认证的后台。
/// </para>
/// </remarks>
public sealed class AdminOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Admin";

    /// <summary>是否在宿主内挂载后台服务。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 后台监听地址，形如 <c>http://localhost:5187</c>。
    /// 需要对外提供服务时改为 <c>http://0.0.0.0:5187</c>，并自行置于反向代理与 TLS 之后。
    /// </summary>
    public string Url { get; set; } = "http://localhost:5187";

    /// <summary>
    /// SQLite 数据库文件路径。相对路径以宿主内容根目录（可执行文件所在目录）为基准。
    /// </summary>
    public string DatabasePath { get; set; } = "pulsebot-admin.db";

    /// <summary>认证与令牌配置。</summary>
    public AdminAuthOptions Auth { get; set; } = new();

    /// <summary>登录限流配置。</summary>
    public AdminThrottleOptions Throttle { get; set; } = new();
}

/// <summary>
/// 登录限流配置。
/// </summary>
/// <remarks>
/// <para>
/// 计数保存在进程内存里：重启即清零，多实例之间不共享。这是有意的取舍——后台管理是
/// 单实例部署的运维界面，把每次失败都写库会带来写放大与清理任务，收益与复杂度不成比例。
/// 失败事实本身已经由审计日志落库，可追溯。
/// </para>
/// <para>
/// 锁定时长固定而非指数增长：攻击者可以用已知用户名故意触发锁定（账号锁定型 DoS），
/// 固定窗口让这种伤害有界且可预期。
/// </para>
/// </remarks>
public sealed class AdminThrottleOptions
{
    /// <summary>失败统计的时间窗口。超出窗口的失败不累计。</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>单个账号在窗口内允许的失败次数；达到该值即锁定。</summary>
    public int MaxFailuresPerUser { get; set; } = 5;

    /// <summary>单个来源 IP 在窗口内允许的失败次数；达到该值即锁定。取值应明显大于账号阈值。</summary>
    public int MaxFailuresPerIp { get; set; } = 20;

    /// <summary>触发后的锁定时长。</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>后台认证配置。</summary>
public sealed class AdminAuthOptions
{
    /// <summary>
    /// JWT 签名密钥（HMAC-SHA256）。启用后台时必填，且长度不少于 32 字节。
    /// </summary>
    /// <remarks>
    /// 不要提交进版本库：用环境变量 <c>Admin__Auth__SigningKey</c>
    /// 或用户机密（user-secrets）注入。
    /// </remarks>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>令牌签发者（<c>iss</c> 声明）。</summary>
    public string Issuer { get; set; } = "pulsebot-admin";

    /// <summary>令牌受众（<c>aud</c> 声明）。</summary>
    public string Audience { get; set; } = "pulsebot-admin";

    /// <summary>访问令牌有效期。</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>刷新令牌有效期。</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>首个管理员用户名；仅在账号表为空时用于创建引导账号。</summary>
    public string BootstrapUser { get; set; } = "admin";

    /// <summary>
    /// 首个管理员的初始密码；仅在账号表为空时使用。
    /// 为空则不创建引导账号——后台仍会启动，但无人能登录，宿主会记录一条警告。
    /// </summary>
    public string BootstrapPassword { get; set; } = string.Empty;
}
