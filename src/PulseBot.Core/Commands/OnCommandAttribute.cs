namespace PulseBot.Core.Commands;

/// <summary>
/// 把一个插件方法标记为命令处理器。
/// </summary>
/// <remarks>
/// <para>
/// 被标记的方法必须满足命令处理器签名约定（见 <see cref="CommandHandler"/>）：
/// <c>ValueTask MethodAsync(CommandContext context, CancellationToken cancellationToken)</c>。
/// 框架在宿主启动时扫描一次本特性，并通过 <see cref="System.Reflection.MethodInfo"/> 的
/// <c>CreateDelegate</c> 把方法编译为强类型委托并缓存；命令匹配热路径上没有任何反射调用。
/// </para>
/// <para>
/// 命令名不包含命令前缀：<c>[OnCommand("echo")]</c> 对应消息 <c>/echo hello</c>（前缀可配置）。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [OnCommand("echo", Aliases = ["重复"], Description = "复读参数内容")]
/// public async ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
/// {
///     await context.ReplyAsync(context.ArgsText, cancellationToken);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OnCommandAttribute : Attribute
{
    /// <summary>
    /// 创建命令标注。
    /// </summary>
    /// <param name="name">主命令名（不含前缀、不含空白字符），例如 <c>"echo"</c> 或 <c>"天气"</c>。</param>
    /// <exception cref="ArgumentException">命令名为空或包含空白字符时抛出。</exception>
    public OnCommandAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 命令名按「首个空白分隔片段」切分定位，因此自身不允许包含空白字符。
        // 在构造期直接失败，避免插件作者写出永远匹配不上的命令名。
        if (name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("命令名不能包含空白字符。", nameof(name));
        }

        Name = name;
    }

    /// <summary>主命令名（不含前缀）。</summary>
    public string Name { get; }

    /// <summary>别名列表；主名与别名指向同一个处理器，都会被注册进命令表。</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>
    /// 命令名冲突（多个插件注册同一名字）时的裁决优先级：数值大者胜。
    /// 优先级相同时保留先注册的插件；冲突只记录 Warning 日志，不会让宿主启动失败。
    /// </summary>
    public int Priority { get; init; }

    /// <summary>命令说明（可选），供后续的帮助命令与后台管理界面展示。</summary>
    public string? Description { get; init; }
}
