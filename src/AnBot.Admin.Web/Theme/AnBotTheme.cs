using MudBlazor;

namespace AnBot.Admin.Web.Theme;

/// <summary>
/// 后台界面的统一主题。
/// </summary>
/// <remarks>
/// 只定制调色板与圆角，不改组件默认行为：升级 MudBlazor 时不必跟随大量样式覆写。
/// 暗色模式使用同一套语义色，保证图表、状态 chip 在两套主题下都清晰可读。
/// </remarks>
public static class AnBotTheme
{
    /// <summary>创建主题实例。</summary>
    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2563EB",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#7C3AED",
            Info = "#0EA5E9",
            Success = "#16A34A",
            Warning = "#D97706",
            Error = "#DC2626",

            Background = "#F4F6FB",
            Surface = "#FFFFFF",
            TextPrimary = "#0F172A",
            TextSecondary = "#475569",
            TextDisabled = "#94A3B8",

            AppbarBackground = "#0F172A",
            AppbarText = "#F8FAFC",

            DrawerBackground = "#0F172A",
            DrawerText = "#E2E8F0",
            DrawerIcon = "#94A3B8",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#60A5FA",
            PrimaryContrastText = "#0B1120",
            Secondary = "#A78BFA",
            Info = "#38BDF8",
            Success = "#4ADE80",
            Warning = "#FBBF24",
            Error = "#F87171",

            Background = "#0B1120",
            Surface = "#111827",
            TextPrimary = "#E5E7EB",
            TextSecondary = "#94A3B8",

            AppbarBackground = "#0B1120",
            AppbarText = "#E5E7EB",

            DrawerBackground = "#0B1120",
            DrawerText = "#E5E7EB",
            DrawerIcon = "#94A3B8",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "14px",
        },
    };
}
