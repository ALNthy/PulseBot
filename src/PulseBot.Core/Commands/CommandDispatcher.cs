using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Core.Events;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Commands;

/// <summary>
/// <see cref="ICommandDispatcher"/> 的默认实现。
/// </summary>
/// <remarks>
/// <para>命令表的生命周期：</para>
/// <list type="number">
///   <item><description>宿主启动时由 <see cref="PluginManager"/> 调用 <see cref="RegisterPluginCommands"/>，
///   扫描插件的 <see cref="OnCommandAttribute"/> 方法；反射只发生这一次，
///   随后 <c>MethodInfo.CreateDelegate</c> 把方法编译成 <see cref="CommandHandler"/> 强类型委托并缓存。</description></item>
///   <item><description>运行期 <see cref="TryDispatchAsync"/> 只做字符串解析、字典查找与委托调用，零反射、零 <c>MethodInfo.Invoke</c>。</description></item>
/// </list>
/// <para>
/// 线程安全：命令表在宿主启动阶段由单线程写入，启动完成后只读访问，因此使用普通字典。
/// 插件启停不写命令表——被停用的插件依旧登记着它的命令，只是由
/// <see cref="PluginStateRegistry"/> 在查找命中后把触发请求挡回去，因此运行期无并发写入问题。
/// </para>
/// </remarks>
public sealed class CommandDispatcher : ICommandDispatcher
{
    /// <summary>命令表：命令名/别名 → 注册记录。Comparer 由 <see cref="CommandOptions.CaseSensitive"/> 决定。</summary>
    private readonly Dictionary<string, CommandRegistration> _commands;

    private readonly CommandOptions _options;

    /// <summary>前缀与命令名解析使用的字符串比较方式（与字典 Comparer 保持一致）。</summary>
    private readonly StringComparison _nameComparison;

    /// <summary>规范化后的命令前缀；空字符串表示「全无前缀」模式。</summary>
    private readonly string _prefix;

    /// <summary>权限判定入口：只有声明了 <c>[RequirePermission]</c> 的命令才会用到它。</summary>
    private readonly IPermissionService _permissionService;

    /// <summary>插件启用状态：被后台停用的插件，其命令对外如同不存在。</summary>
    private readonly PluginStateRegistry _pluginState;

    private readonly ILogger<CommandDispatcher> _logger;

    /// <summary>
    /// 创建命令分发器。配置缺失时 <see cref="CommandOptions"/> 默认值生效（前缀 <c>"/"</c>、允许无前缀命令、忽略大小写）。
    /// </summary>
    /// <param name="options">命令配置。</param>
    /// <param name="permissionService">权限判定服务（未声明要求的命令不会调用它）。</param>
    /// <param name="pluginState">插件启用状态（未停用任何插件时判定近乎零开销）。</param>
    /// <param name="logger">结构化日志。</param>
    public CommandDispatcher(
        IOptions<CommandOptions> options,
        IPermissionService permissionService,
        PluginStateRegistry pluginState,
        ILogger<CommandDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionService);
        ArgumentNullException.ThrowIfNull(pluginState);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _permissionService = permissionService;
        _pluginState = pluginState;
        _logger = logger;
        _nameComparison = _options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _commands = new Dictionary<string, CommandRegistration>(
            _options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        // 容忍配置缺失/空白：空字符串统一表示「不启用前缀匹配」。
        _prefix = string.IsNullOrWhiteSpace(_options.Prefix) ? string.Empty : _options.Prefix.Trim();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<CommandDescriptor> Commands =>
        _commands
            .Select(pair => new CommandDescriptor(
                pair.Key,
                pair.Value.PrimaryName,
                pair.Value.PluginName,
                pair.Value.Description,
                pair.Value.Priority))
            .ToArray();

    /// <summary>
    /// 扫描插件的全部公开方法，把带 <see cref="OnCommandAttribute"/> 且签名合法的方法注册进命令表。
    /// 由 <see cref="PluginManager"/> 在宿主启动阶段调用，插件代码不应直接调用本方法。
    /// </summary>
    /// <param name="plugin">要扫描的插件实例（实例方法将绑定到该实例）。</param>
    public void RegisterPluginCommands(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var pluginType = plugin.GetType();
        var pluginName = pluginType.FullName ?? pluginType.Name;

        foreach (var method in pluginType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<OnCommandAttribute>(inherit: true);
            if (attribute is null)
            {
                continue;
            }

            if (!TryCreateHandler(plugin, method, out var handler))
            {
                // 签名不合法属于插件作者的编码错误：给出明确日志后跳过该方法，不影响其它插件与宿主启动。
                _logger.LogError(
                    "插件 {Plugin} 的方法 {Method} 标注了 [OnCommand]，但签名不合法，已跳过。期望签名：ValueTask MethodAsync(CommandContext context, CancellationToken cancellationToken)",
                    pluginName,
                    method.Name);
                continue;
            }

            // 权限要求在启动扫描时解析一次并缓存，运行期只做判定，不在热路径上读特性。
            var requirement = method.GetCustomAttribute<RequirePermissionAttribute>(inherit: true)?.ToRequirement();

            AddRegistration(
                attribute.Name,
                CreateRegistration(attribute.Name, attribute, pluginName, handler, requirement));

            foreach (var alias in attribute.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || alias.Any(char.IsWhiteSpace))
                {
                    _logger.LogWarning(
                        "插件 {Plugin} 的命令 {Command} 声明了无效别名 {Alias}（不能为空或包含空白字符），已忽略。",
                        pluginName,
                        attribute.Name,
                        alias);
                    continue;
                }

                AddRegistration(
                    alias,
                    CreateRegistration(alias, attribute, pluginName, handler, requirement));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryDispatchAsync(MessageEvent messageEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageEvent);

        if (_commands.Count == 0)
        {
            return ValueTask.FromResult(false);
        }

        var text = messageEvent.PlainText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.FromResult(false);
        }

        text = text.Trim();

        // 步骤 1：前缀解析。带前缀（如 "/echo 你好"）与无前缀（如 "天气 北京"）两条路径。
        var hasPrefix = _prefix.Length > 0 && text.StartsWith(_prefix, _nameComparison);
        if (!hasPrefix && _prefix.Length > 0 && !_options.EnablePrefixlessCommands)
        {
            // 已配置前缀且禁止无前缀命令：不以该前缀开头的消息一律不作为命令解析。
            return ValueTask.FromResult(false);
        }

        var remainder = hasPrefix ? text[_prefix.Length..].TrimStart() : text;
        if (remainder.Length == 0)
        {
            return ValueTask.FromResult(false);
        }

        // 步骤 2：参数切分。命令名是首个空白分隔片段，其余为原始参数文本。
        var separatorIndex = IndexOfWhitespace(remainder);
        var commandName = separatorIndex < 0 ? remainder : remainder[..separatorIndex];
        var argsText = separatorIndex < 0 ? string.Empty : remainder[(separatorIndex + 1)..].Trim();

        // 步骤 3：命令表查找（大小写策略由字典 Comparer 决定；无前缀模式同样要求命令名精确相等）。
        if (!_commands.TryGetValue(commandName, out var registration))
        {
            return ValueTask.FromResult(false);
        }

        // 步骤 4：插件门控。放在字典命中之后，未命中命令的消息（绝大多数）完全不付这次判定。
        // 返回 false 表示「未消费」：被停用插件的命令对外如同不存在，消息继续流向普通监听器。
        // 命令表里不保留被压制者的裁决结果，因此停用后该名字不会回退到低优先级的注册。
        if (_pluginState.IsDisabled(registration.PluginName))
        {
            return ValueTask.FromResult(false);
        }

        var args = SplitArgs(argsText);
        var context = new CommandContext(messageEvent, commandName, argsText, args);

        // 未声明权限要求的命令（默认放行）走原路径，不引入额外的异步状态机分配。
        if (registration.Requirement is not { IsEmpty: false } requirement)
        {
            return InvokeAsync(registration, context, cancellationToken);
        }

        return InvokeWithPermissionAsync(registration, requirement, messageEvent, context, cancellationToken);
    }

    /// <summary>
    /// 带权限校验的命令调用：先判定权限，不通过则回复提示并结束。
    /// 无论通过与否命令都视为「已消费」——权限不足的消息不该再被下游插件当作普通聊天处理。
    /// </summary>
    private async ValueTask<bool> InvokeWithPermissionAsync(
        CommandRegistration registration,
        PermissionRequirement requirement,
        MessageEvent messageEvent,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!await _permissionService
                .IsGrantedAsync(messageEvent, requirement, cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogWarning(
                "用户 {SenderId} 在会话 {Conversation} 触发命令 {Command} 时权限不足（要求 {Mode}：{Nodes}），已拒绝。",
                messageEvent.SenderId,
                messageEvent.Conversation,
                context.CommandName,
                requirement.Mode,
                string.Join(", ", requirement.Nodes));

            await NotifyDeniedAsync(messageEvent, cancellationToken).ConfigureAwait(false);

            return true;
        }

        return await InvokeAsync(registration, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 回复「权限不足」提示。提示语为空、或事件没有装配回复通道（如测试中直接构造的事件）时静默跳过；
    /// 提示本身发送失败也不影响「命令已被拒绝」这一结果。
    /// </summary>
    private async ValueTask NotifyDeniedAsync(MessageEvent messageEvent, CancellationToken cancellationToken)
    {
        var message = _permissionService.DeniedMessage;
        if (message.Length == 0 || messageEvent.ReplyChannel is null)
        {
            return;
        }

        try
        {
            await messageEvent.ReplyAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正在关闭：取消属于正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // 提示发送失败（如平台断连）不改变命令被拒绝的事实，记录后继续。
            _logger.LogWarning(ex, "回复权限不足提示失败，用户 {SenderId}。", messageEvent.SenderId);
        }
    }

    /// <summary>
    /// 调用处理器并做异常隔离：插件内部异常被记录后视为「命令已消费」，不向事件管道传播，
    /// 其它插件与宿主不受影响。
    /// </summary>
    private async ValueTask<bool> InvokeAsync(
        CommandRegistration registration,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await registration.Handler(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正在关闭：取消属于正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // 插件异常隔离：记录插件、命令与异常详情；命令视为已消费，事件管道继续。
            _logger.LogError(
                ex,
                "插件 {Plugin} 的命令 {Command} 执行时抛出未处理异常，已隔离。",
                registration.PluginName,
                context.CommandName);
        }

        return true;
    }

    /// <summary>
    /// 为单个触发名（主名或别名）创建注册记录。每个名字一条独立记录，
    /// 冲突裁决以「名字」为粒度，互不牵连。
    /// </summary>
    private static CommandRegistration CreateRegistration(
        string triggerName,
        OnCommandAttribute attribute,
        string pluginName,
        CommandHandler handler,
        PermissionRequirement? requirement)
        => new()
        {
            TriggerName = triggerName,
            PrimaryName = attribute.Name,
            Priority = attribute.Priority,
            PluginName = pluginName,
            Description = attribute.Description,
            Handler = handler,
            Requirement = requirement,
        };

    /// <summary>
    /// 把一条注册记录写入命令表；命令名冲突时按优先级裁决（高者胜，同优先级先注册者胜）并记录 Warning，
    /// 不做静默覆盖、也不中断宿主启动。同类冲突只影响该名字，不影响同一命令的其它名字。
    /// </summary>
    private void AddRegistration(string name, CommandRegistration registration)
    {
        if (_commands.TryGetValue(name, out var existing))
        {
            if (registration.Priority > existing.Priority)
            {
                _commands[name] = registration;
                _logger.LogWarning(
                    "命令 {Command} 被多个插件注册：已有 {ExistingPlugin}（优先级 {ExistingPriority}），新注册来自 {NewPlugin}（优先级 {NewPriority}）；因新注册优先级更高而采用新注册。",
                    name,
                    existing.PluginName,
                    existing.Priority,
                    registration.PluginName,
                    registration.Priority);
            }
            else
            {
                _logger.LogWarning(
                    "命令 {Command} 被多个插件注册：已有 {ExistingPlugin}（优先级 {ExistingPriority}），新注册来自 {NewPlugin}（优先级 {NewPriority}）；保留已有注册。",
                    name,
                    existing.PluginName,
                    existing.Priority,
                    registration.PluginName,
                    registration.Priority);
            }

            return;
        }

        _commands.Add(name, registration);
    }

    /// <summary>
    /// 校验方法签名并把它编译为强类型委托。签名不匹配时返回 <c>false</c>（不抛异常，由调用方记录日志）。
    /// </summary>
    private static bool TryCreateHandler(
        IPlugin plugin,
        MethodInfo method,
        [NotNullWhen(true)] out CommandHandler? handler)
    {
        handler = null;

        // 开放泛型方法无法绑定委托，直接判为非法签名。
        if (method.ContainsGenericParameters)
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (method.ReturnType != typeof(ValueTask)
            || parameters.Length != 2
            || parameters[0].ParameterType != typeof(CommandContext)
            || parameters[1].ParameterType != typeof(CancellationToken))
        {
            return false;
        }

        try
        {
            // 实例方法绑定到插件实例；静态方法不需要 target。
            // 这一步是热路径零反射的关键：绑定只做一次，之后只调用委托。
            handler = method.IsStatic
                ? method.CreateDelegate<CommandHandler>()
                : method.CreateDelegate<CommandHandler>(plugin);
            return true;
        }
        catch (ArgumentException)
        {
            // 签名校验已覆盖绝大多数情况，此处兜底处理绑定器拒绝的边缘情况。
            return false;
        }
    }

    /// <summary>返回字符串中第一个空白字符（含全角空格）的下标；没有空白时返回 -1。</summary>
    private static int IndexOfWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>按空白字符切分参数文本并去除空项；空文本返回空数组。</summary>
    private static string[] SplitArgs(string argsText)
        => string.IsNullOrEmpty(argsText)
            ? []
            : argsText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
