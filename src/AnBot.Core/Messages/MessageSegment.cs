namespace AnBot.Core.Messages;

/// <summary>
/// 消息段基类型：一条消息由若干有序片段组成。适配器负责把平台原始报文
/// 转换为消息段，插件只操作本抽象（插件不得处理原始平台消息）。
/// </summary>
/// <remarks>
/// 选用 <c>abstract record</c> 而非 <c>abstract class</c>：
/// 1) 消息段是纯数据载体，record 自带值相等与 <c>with</c> 表达式，便于测试断言与复制修改；
/// 2) 基类型刻意不密封：适配器可在自己的程序集内派生平台专属段（语音、Markdown、按钮等），
///    Core 无需为每个平台扩展枚举值；插件用模式匹配处理已知段，
///    未知段走 <see cref="ToPlainText"/> 回退，不会因平台扩展而破坏 Core；
/// 3) 具体段类型必须 sealed：record 继续派生会触发相等性契约告警，密封可从源头杜绝。
/// </remarks>
public abstract record MessageSegment
{
    /// <summary>
    /// 渲染为人类可读的纯文本。用途：日志、平台不支持该段时的降级回退、
    /// 以及 <see cref="Message.ToString"/> 的整体呈现。
    /// 无可见文本表现的段（如回复引用）返回空字符串。
    /// </summary>
    public abstract string ToPlainText();
}
