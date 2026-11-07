using System.Net;
using System.Text;

namespace AnBot.Adapter.Email;

/// <summary>
/// 把 HTML 正文降级为纯文本。
/// </summary>
/// <remarks>
/// <para>
/// 首版刻意不引入 HTML 解析库：聊天场景只需要三件事——去掉标签、把块级标签转换为换行、
/// 解码实体；脚本与样式的内容整段丢弃。这不是通用 HTML 渲染器，遇到畸形文本时
/// 宁可多留一些空白，也不抛异常。
/// </para>
/// <para>
/// 只在「邮件没有 text/plain 正文」时作为降级路径调用，不在消息处理热路径上。
/// </para>
/// </remarks>
internal static class HtmlTextConverter
{
    /// <summary>把一段 HTML 转换为紧凑的纯文本；块级标签之间保留一个空行。</summary>
    /// <param name="html">HTML 文本，允许为畸形内容。</param>
    public static string Convert(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            int tagStart = html.IndexOf('<', index);
            if (tagStart < 0)
            {
                // 剩余部分全是文本。
                AppendDecoded(builder, html[index..]);
                break;
            }

            if (tagStart > index)
            {
                AppendDecoded(builder, html[index..tagStart]);
            }

            // 注释可能包含 '<'、'>'，必须先整体跳过，否则注释里的文字会漏进正文。
            if (html.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                int commentEnd = html.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    break;
                }

                index = commentEnd + 3;
                continue;
            }

            int tagEnd = html.IndexOf('>', tagStart);
            if (tagEnd < 0)
            {
                // 未闭合的标签：后面已经没有可靠的文本边界，直接丢弃。
                break;
            }

            string tagName = ReadTagName(html, tagStart, tagEnd);

            if (tagName is "script" or "style")
            {
                // 脚本/样式可能包含 '<'、'>'，不能按普通标签跳过，要找配对的结束标签。
                int closingStart = FindClosingTag(html, tagName, tagEnd + 1);
                if (closingStart < 0)
                {
                    break;
                }

                tagEnd = html.IndexOf('>', closingStart);
                if (tagEnd < 0)
                {
                    break;
                }
            }
            else if (IsBlockTag(tagName))
            {
                builder.Append('\n');
            }

            index = tagEnd + 1;
        }

        return Normalize(builder.ToString());
    }

    /// <summary>解码实体后追加文本；实体不会跨越标签边界，因此按片段解码是安全的。</summary>
    private static void AppendDecoded(StringBuilder builder, string text)
        => builder.Append(WebUtility.HtmlDecode(text));

    /// <summary>读取标签名并转小写；注释、声明等没有标签名的形式返回空串。</summary>
    private static string ReadTagName(string html, int tagStart, int tagEnd)
    {
        int index = tagStart + 1;

        // 结束标签的 '/' 与声明/注释的 '!'、'?' 不属于标签名。
        if (index < tagEnd && html[index] is '/' or '!' or '?')
        {
            index++;
        }

        int start = index;
        while (index < tagEnd && (char.IsLetterOrDigit(html[index]) || html[index] == '-'))
        {
            index++;
        }

        return index > start ? html[start..index].ToLowerInvariant() : string.Empty;
    }

    /// <summary>查找 <c>&lt;/tagName</c> 的起始位置（大小写不敏感）。</summary>
    private static int FindClosingTag(string html, string tagName, int start)
        => html.IndexOf("</" + tagName, start, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否块级标签：在纯文本里用换行表达它的分隔语义。</summary>
    private static bool IsBlockTag(string tagName) => tagName is
        "br" or "p" or "div" or "li" or "tr" or "blockquote" or "section" or "article"
        or "header" or "footer" or "table" or "ul" or "ol" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    /// <summary>
    /// 规范化空白：合并行内连续空白，行首尾去空白，连续空行折叠为一行。
    /// </summary>
    private static string Normalize(string text)
    {
        string[] lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var builder = new StringBuilder(text.Length);
        var blankLines = 0;

        foreach (string line in lines)
        {
            string compact = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (compact.Length == 0)
            {
                blankLines++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');

                // 源文本中的空行（如段落之间的两个标签）折叠为一个空行。
                if (blankLines > 0)
                {
                    builder.Append('\n');
                }
            }

            builder.Append(compact);
            blankLines = 0;
        }

        return builder.ToString();
    }
}
