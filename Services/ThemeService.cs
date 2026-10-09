namespace FocusCapture.Services;

/// <summary>
/// 主题切换抽象层。
/// 当前支持 Dark / Light，先只对 VoiceInputWindow 生效，后续其他窗口可复用。
/// </summary>
public class ThemeService
{
    public string CurrentTheme { get; private set; } = "Dark";

    /// <summary>设置主题并返回变更后的颜色方案</summary>
    public ThemeColors SetTheme(string theme)
    {
        CurrentTheme = theme;
        return GetColors();
    }

    public ThemeColors GetColors()
    {
        return CurrentTheme switch
        {
            "Light" => ThemeColors.Light,
            _ => ThemeColors.Dark
        };
    }
}

/// <summary>窗口级主题颜色方案（窗口背景/标题栏/边框 + 日历热力图 4 档色）</summary>
public record ThemeColors(
    string WindowBg,
    string TitleBarBg,
    string BorderColor,
    string TextColor,
    string SecondaryText,
    string Accent,
    string SplitterColor,
    string BodyBg,
    string Heat0Bg,   // 日历热力：0 条（无笔记格背景）
    string Heat0Fg,   // 日历热力：0 条（文字）
    string Heat1Bg,   // 1-2 条（浅绿）
    string Heat1Fg,
    string Heat2Bg,   // 3-5 条（中绿）
    string Heat2Fg,
    string Heat3Bg,   // 6+ 条（深绿）
    string Heat3Fg
)
{
    public static readonly ThemeColors Dark = new(
        WindowBg: "#2D2D2D",
        TitleBarBg: "#333333",
        BorderColor: "#3A3A3A",
        TextColor: "#D0D0D0",
        SecondaryText: "#888888",
        Accent: "#4CAF50",
        SplitterColor: "#3A3A3A",
        BodyBg: "#262626",
        Heat0Bg: "#262626",
        Heat0Fg: "#CCCCCC",
        Heat1Bg: "#C8E6C9",
        Heat1Fg: "#1B5E20",
        Heat2Bg: "#81C784",
        Heat2Fg: "#103D14",
        Heat3Bg: "#388E3C",
        Heat3Fg: "#FFFFFF"
    );

    public static readonly ThemeColors Light = new(
        WindowBg: "#EEEEEE",
        TitleBarBg: "#D8D8D8",
        BorderColor: "#C0C0C0",
        TextColor: "#333333",
        SecondaryText: "#666666",
        Accent: "#2E7D32",
        SplitterColor: "#C0C0C0",
        BodyBg: "#F5F5F5",
        Heat0Bg: "#F5F5F5",
        Heat0Fg: "#333333",
        Heat1Bg: "#C8E6C9",
        Heat1Fg: "#1B5E20",
        Heat2Bg: "#81C784",
        Heat2Fg: "#103D14",
        Heat3Bg: "#388E3C",
        Heat3Fg: "#FFFFFF"
    );
}
