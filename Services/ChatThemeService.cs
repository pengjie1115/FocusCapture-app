using System.Windows;
using System.Windows.Media;

namespace FocusCapture.Services;

/// <summary>
/// AI 问答界面明暗主题（2026-09-27，用户需求「两极可调」）。
///
/// 形态：shade ∈ [0,1]，0 = 浅色（白底深字），1 = 深色（现状配色，默认），
/// 中间值把色板里每个角色在浅/深两端色之间线性插值 —— 拖滑块整窗实时跟随。
/// 这是「背景明暗」不是窗口透明度：窗口始终不透明（与 InputWindow 等的 Opacity 调节是两回事）。
///
/// 机制：AIDialogWindow / ChatSidebar 的 XAML 色值全部改成
/// {DynamicResource Chat_xxxxxx}（key 后六位 = 深色端原色值，深色端=改造前逐处原值，
/// 保证 shade=1 时渲染与改造前逐像素一致）；本类把插值后的 SolidColorBrush
/// 写进 Application.Current.Resources，DynamicResource 引用自动全窗刷新。
///
/// 刻意不进色板的（两端恒定、XAML 保留字面量）：品牌绿 #4CAF50、红底白字 #C0392B 与
/// 其上的 #FFFFFF、遮罩 #66000000、查找高亮黄 #FFD54F —— 这些在浅色端依然可读。
/// 已知妥协（浅色端仍是深色外观，与 DatePicker 日历同类）：全局滚动条 / ContextMenu /
/// ToolTip 的深色模板在 App.xaml，属全局改动，本次不动。
/// </summary>
public static class ChatThemeService
{
    /// <summary>深色 = 现状默认；滑块右端。</summary>
    public const double DefaultShade = 0.0;

    /// <summary>key = 资源 key（Chat_ + 深色端色值），value = (浅色端, 深色端=改造前原值)。
    /// 浅色端按「保持明度层级」手工映射：底色越深浅端越白、文字越亮浅端越黑，彩字浅端加深保对比。
    /// public 只读暴露给检查点（tests/sync）：断言「深色端 == key 后六位」防现状漂移。</summary>
    public static readonly Dictionary<string, (Color Light, Color Dark)> Palette = new()
    {
        // ── 底色系 ──
        ["Chat_1E1E1E"] = (FromHex("#F6F6F6"), FromHex("#1E1E1E")),   // 窗体/标题栏/输入板底
        ["Chat_1A1A1A"] = (FromHex("#E4E4E4"), FromHex("#1A1A1A")),   // 最深 inset（分组搜索框）
        ["Chat_232323"] = (FromHex("#EDEDED"), FromHex("#232323")),   // 附件 chip 底
        ["Chat_252525"] = (FromHex("#EFEFEF"), FromHex("#252525")),   // AI 气泡底
        ["Chat_2A2A2A"] = (FromHex("#E9E9E9"), FromHex("#2A2A2A")),   // 次级面板/图标按钮底
        ["Chat_2A2A1E"] = (FromHex("#F2EEDC"), FromHex("#2A2A1E")),   // 金色警示条底
        ["Chat_2D2D2D"] = (FromHex("#FFFFFF"), FromHex("#2D2D2D")),   // TextBox/附件卡底
        ["Chat_2E2E2E"] = (FromHex("#E8E8E8"), FromHex("#2E2E2E")),   // 侧栏条目/次面板
        ["Chat_2E3A2E"] = (FromHex("#E0EBE0"), FromHex("#2E3A2E")),   // 侧栏绿 tint
        ["Chat_33422E"] = (FromHex("#DFE9D8"), FromHex("#33422E")),   // 绿 tint（深）
        ["Chat_2A3A2A"] = (FromHex("#E1EFE1"), FromHex("#2A3A2A")),   // 用户气泡绿底
        ["Chat_3A3A3A"] = (FromHex("#DADADA"), FromHex("#3A3A3A")),   // 边框/按钮底
        ["Chat_505050"] = (FromHex("#DEDEDE"), FromHex("#505050")),   // 按钮 hover
        ["Chat_555555"] = (FromHex("#C2C2C2"), FromHex("#555555")),   // 强边框

        // ── 文字系（越亮 → 浅端越深）──
        ["Chat_E8E8E8"] = (FromHex("#262626"), FromHex("#E8E8E8")),   // 分组标题
        ["Chat_E0E0E0"] = (FromHex("#202020"), FromHex("#E0E0E0")),   // 主文字
        ["Chat_DDDDDD"] = (FromHex("#3A3A3A"), FromHex("#DDDDDD")),   // 金条/提示条内文字
        ["Chat_D8D8D8"] = (FromHex("#404040"), FromHex("#D8D8D8")),   // 欢迎语
        ["Chat_D0D0D0"] = (FromHex("#4A4A4A"), FromHex("#D0D0D0")),   // 侧栏次文字
        ["Chat_CCCCCC"] = (FromHex("#444444"), FromHex("#CCCCCC")),   // 次标题/附件名
        ["Chat_C8C8C8"] = (FromHex("#4A4A4A"), FromHex("#C8C8C8")),   // 次文字
        ["Chat_BBBBBB"] = (FromHex("#565656"), FromHex("#BBBBBB")),   // 输入框加号图标（2026-10-01 补：主题化时漏加色板条目导致加号不渲染）
        ["Chat_B8B8B8"] = (FromHex("#585858"), FromHex("#B8B8B8")),   // 图标/侧栏文字
        ["Chat_AAAAAA"] = (FromHex("#6A6A6A"), FromHex("#AAAAAA")),
        ["Chat_A8A8A8"] = (FromHex("#6C6C6C"), FromHex("#A8A8A8")),
        ["Chat_9E9E9E"] = (FromHex("#747474"), FromHex("#9E9E9E")),
        ["Chat_9A9A9A"] = (FromHex("#767676"), FromHex("#9A9A9A")),
        ["Chat_999999"] = (FromHex("#777777"), FromHex("#999999")),
        ["Chat_8A8A8A"] = (FromHex("#808080"), FromHex("#8A8A8A")),
        ["Chat_888888"] = (FromHex("#828282"), FromHex("#888888")),
        ["Chat_7A7A7A"] = (FromHex("#8A8A8A"), FromHex("#7A7A7A")),   // 主输入框占位
        ["Chat_777777"] = (FromHex("#8C8C8C"), FromHex("#777777")),
        ["Chat_6E6E6E"] = (FromHex("#929292"), FromHex("#6E6E6E")),
        ["Chat_6A6A6A"] = (FromHex("#969696"), FromHex("#6A6A6A")),   // 占位/弱文字
        ["Chat_5A5A5A"] = (FromHex("#9E9E9E"), FromHex("#5A5A5A")),

        // ── 彩字/彩边（浅端加深保对比）──
        ["Chat_8A9E8A"] = (FromHex("#3E7A46"), FromHex("#8A9E8A")),   // 分组入口绿字
        ["Chat_7A9E7A"] = (FromHex("#42854D"), FromHex("#7A9E7A")),   // 侧栏绿字
        ["Chat_6E8B6E"] = (FromHex("#43744B"), FromHex("#6E8B6E")),
        ["Chat_C9A227"] = (FromHex("#9A7B14"), FromHex("#C9A227")),   // 金（星标/警示边）
        ["Chat_E08585"] = (FromHex("#B84040"), FromHex("#E08585")),   // 错误红字
        ["Chat_E08080"] = (FromHex("#B84040"), FromHex("#E08080")),   // 右键菜单「删除对话」红字（2026-10-01 补：与加号同批漏网 —— Brush() 直接索引，缺失会抛 KeyNotFoundException）
        ["Chat_C08080"] = (FromHex("#A85858"), FromHex("#C08080")),
        ["Chat_8A3A3A"] = (FromHex("#D9A0A0"), FromHex("#8A3A3A")),   // 错误边框
        ["Chat_378ADD"] = (FromHex("#2467B8"), FromHex("#378ADD")),   // 附件图片边框蓝
        ["Chat_3A5A8A"] = (FromHex("#8FB0D9"), FromHex("#3A5A8A")),   // 输入框 SelectionBrush
        ["Chat_3A4A6A"] = (FromHex("#DCE6F5"), FromHex("#3A4A6A")),   // 侧栏蓝底
        ["Chat_D8E0F0"] = (FromHex("#33517E"), FromHex("#D8E0F0")),   // 侧栏蓝字
        ["Chat_888780"] = (FromHex("#A89B78"), FromHex("#888780")),   // 附件 chip 边框棕
    };

    /// <summary>把当前 shade 折算成所有角色色写进应用级资源。幂等，可随滑块反复调用。
    /// Application.Current 为 null（检查点沙箱等非 WPF 宿主环境）时静默跳过。</summary>
    public static void Apply(double shade)
    {
        if (Application.Current == null) return;
        shade = ClampShade(shade);
        var dict = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Palette)
        {
            var brush = new SolidColorBrush(Lerp(light, dark, shade));
            brush.Freeze();
            dict[key] = brush;
        }
    }

    /// <summary>code-behind 取当前 shade 下某角色色的画刷（XAML 走 DynamicResource，代码里走这里）。</summary>
    public static SolidColorBrush Brush(string key, double shade)
    {
        shade = ClampShade(shade);
        var (light, dark) = Palette[key];
        var brush = new SolidColorBrush(Lerp(light, dark, shade));
        brush.Freeze();
        return brush;
    }

    public static double ClampShade(double v) => Math.Clamp(v, 0.0, 1.0);

    /// <summary>t=0 取 light，t=1 取 dark。</summary>
    private static Color Lerp(Color light, Color dark, double t) => new()
    {
        R = (byte)Math.Round(light.R + (dark.R - light.R) * t),
        G = (byte)Math.Round(light.G + (dark.G - light.G) * t),
        B = (byte)Math.Round(light.B + (dark.B - light.B) * t),
        A = (byte)Math.Round(light.A + (dark.A - light.A) * t),
    };

    private static Color FromHex(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#' &&
            System.Windows.Media.ColorConverter.ConvertFromString(hex) is Color c) return c;
        throw new ArgumentException($"非法色值：{hex}", nameof(hex));
    }
}
