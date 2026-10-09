namespace FocusCapture.Services;

/// <summary>
/// 灵感速览标题栏「可组装工具栏」的功能目录与布局预算（v3.9）。
/// 纯数据 + 纯逻辑：不得引用任何 WPF 类型 —— tests/FocusCapture.Tests.csproj 按文件直链编译本类，
/// 引入依赖会破坏快层测试的秒级编译约定（见该 csproj 注释）。
/// 按钮的构建/样式/事件绑定在 QuickViewWindow.xaml.cs（CreateToolbarButton / RebuildToolbar）。
/// </summary>
public static class QuickViewToolbarCatalog
{
    /// <summary>
    /// 一个可放进标题栏的功能按钮定义。EstimatedWidth 含按钮间距，用于设置页的像素预算。
    ///
    /// Label 与 Glyph 的分工（2026-09-13 拆分）：
    /// - <see cref="Label"/> 人类可读名称，用于设置页列表/下拉的展示（"上传" 比 "↑" 好懂）；
    /// - <see cref="Glyph"/> 标题栏图标字符，取自 Segoe MDL2 Assets 私有区（如 "\uE721"）。
    ///   空串 = 该功能是文字按钮，直接用 Label 当文案。
    /// 必须拆开的原因：图标字符拿到设置页会变成豆腐块，而设置页恰恰需要看得懂的说明。
    /// </summary>
    public sealed record FuncDef(
        string Id,
        string Label,           // 人类可读名称（设置页展示）
        string Glyph,           // 标题栏图标字符（Segoe MDL2 Assets）；空 = 文字按钮
        string ToolTip,
        double EstimatedWidth,
        bool IsTextButton,      // true = 宽度随内容自适应（时间筛选/AI 问答/导出文案是动态的）；false = 固定 28px 图标钮
        double FontSize,
        string Foreground,      // hex，如 "#4CAF50"
        string BorderBrush);

    /// <summary>
    /// 可配置功能全表（id 稳定不变，settings.json 按此存储）。
    ///
    /// 前 8 项 = 重构前的原始按钮集合，宽度值与 v3.9 完全一致 —— 勿随意改动：
    /// tests/Program.cs 断言默认布局占用恰为 396px，改宽度会同时坑设置页与面板。
    /// 后 5 项 = 2026-09-13 扩展，均为图标钮；点击后不自行开窗，而是把 id 交给主程序统一处理
    /// （缘由见 QuickViewWindow.ExternalActionRequested：设置需热键服务、每日总结需悬浮球坐标、
    ///   导入需预览构造逻辑，且待办汇总已有单例管理逻辑，就地实现就会出现两份会漂移的逻辑）。
    /// </summary>
    public static readonly IReadOnlyList<FuncDef> All = new FuncDef[]
    {
        new("Calendar",     "今天",         "",       "选择时间范围：预设周期或自定义区间", 74, true,  11, "#CCCCCC", "#555555"),
        new("SyncUpload",   "上传",         "\uE74A", "上传笔记到云端（沿用全量同步机制）", 34, false, 14, "#4CAF50", "#4CAF50"),
        new("SyncDownload", "下载",         "\uE74B", "从云端拉取笔记到本地（仅拉不推）",   34, false, 14, "#4CAF50", "#4CAF50"),
        new("Search",       "查找",         "\uE721", "全局查找（所有笔记）",               34, false, 14, "#CCCCCC", "#555555"),
        new("Refresh",      "刷新",         "\uE72C", "刷新",                               34, false, 14, "#CCCCCC", "#555555"),
        new("AiAsk",        "AI 问答",      "",       "",                                   78, true,  11, "#4CAF50", "#4CAF50"), // 文案随 AiAssistantName 动态
        new("Export",       "▼ 导出",       "",       "导出当前/已选笔记",                  74, true,  11, "#888888", "#555555"),
        new("GetNote",      "存到得到大脑", "\uE74E", "存到得到大脑（勾选条目优先；未勾选推送当前列表）", 34, false, 14, "#2196F3", "#2196F3"),

        // ── 2026-09-13 扩展：点击后由主程序统一打开对应窗口（与菜单/热键入口共用同一份逻辑）──
        new("TodoSummary",  "待办汇总",     "\uE8FD", "打开待办汇总面板（与全局热键同一入口）", 34, false, 14, "#4CAF50", "#555555"),
        new("RecycleBin",   "回收站",       "\uE74D", "打开回收站（恢复或清空已删除的笔记）",  34, false, 14, "#CCCCCC", "#555555"),
        new("Settings",     "设置",         "\uE713", "打开设置",                              34, false, 14, "#CCCCCC", "#555555"),
        new("Import",       "导入笔记",     "\uE8B5", "从文本文件导入笔记",                    34, false, 14, "#CCCCCC", "#555555"),
        new("DailySummary", "每日总结",     "\uE9D9", "查看今日总结",                          34, false, 14, "#CCCCCC", "#555555"),
    };

    public static FuncDef? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(f => f.Id == id);

    /// <summary>升级默认布局 = 重构前的按钮集合（老用户 settings.json 无此字段时反序列化即得，无感迁移）。</summary>
    public static readonly IReadOnlyList<string> DefaultLeft = new[] { "Calendar", "SyncUpload", "SyncDownload" };
    public static readonly IReadOnlyList<string> DefaultRight = new[] { "Search", "Refresh", "AiAsk", "Export", "GetNote" };

    /// <summary>
    /// 清洗用户配置：未知 id 过滤、去重、保持顺序；整列无效/为空回退默认（防手改 settings.json 把工具栏清空后找不到入口）。
    /// </summary>
    public static (List<string> left, List<string> right) Sanitize(List<string>? left, List<string>? right)
    {
        return (CleanSide(left, DefaultLeft), CleanSide(right, DefaultRight));
    }

    private static List<string> CleanSide(List<string>? ids, IReadOnlyList<string> fallback)
    {
        if (ids == null || ids.Count == 0) return fallback.ToList();
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (Find(id) == null) continue;      // 未知 id 丢弃
            if (result.Contains(id, StringComparer.Ordinal)) continue;   // 去重
            result.Add(id);
        }
        return result.Count == 0 ? fallback.ToList() : result;
    }

    // ── 标题栏像素预算 ──
    // 左右两组按钮共享标题行同一行空间，约束按像素而非按个数（图标钮 34px vs 文字钮 ~78px，差一倍多）。

    /// <summary>标题行固定开销：左右边距 28 + 标题文字 ~62 + 铬区（最小化/最大化/关闭 + 分隔线）~90。</summary>
    public const double FixedTitleBarOverhead = 180;

    /// <summary>面板宽度下，标题栏按钮区可用像素。</summary>
    public static double AvailableBudget(double panelWidth) => Math.Max(0, panelWidth - FixedTitleBarOverhead);

    /// <summary>当前左右配置实际占用的估算像素（AiAsk 按默认名「AI 问答」估，自定义更长名称时实际略宽）。</summary>
    public static double UsedBudget(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        double Sum(IEnumerable<string?> ids) => ids
            .Select(Find)
            .Where(f => f != null)
            .Sum(f => f!.EstimatedWidth);
        return Sum(left) + Sum(right);
    }

    /// <summary>在预算内还能否放下指定功能（设置页「添加」的准入判断）。</summary>
    public static bool CanAdd(double panelWidth, IReadOnlyList<string> left, IReadOnlyList<string> right, string idToAdd)
    {
        var def = Find(idToAdd);
        if (def == null) return false;
        return UsedBudget(left, right) + def.EstimatedWidth <= AvailableBudget(panelWidth) + 0.01;
    }
}
