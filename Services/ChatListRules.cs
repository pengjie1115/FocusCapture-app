namespace FocusCapture.Services;

/// <summary>
/// 会话列表的展示规则（2026-10-01 新建）。
///
/// **刻意零项目依赖**（只吃 string / bool），于是能被秒级的快层检查点直接守住 ——
/// 这些规则错了全是**静默**的：列表上冒出一行没有文字的条目、或者按钮点了没反应，两者都不报错。
/// 与 TokenCountParser / ChatSearchMatcher / CloudVerify 同一套路数（能抽成纯函数的一律抽出来单独测）。
/// </summary>
public static class ChatListRules
{
    /// <summary>
    /// 会话列表标题 / 预览（即「有效标题」）：重命名标题 → 首条用户正文前 40 字 → 首条附件名 → 空串。
    ///
    /// 为什么必须有「附件名」这一档（2026-10-01 用户实测）：只贴文件、不打字时，
    /// 首条用户消息的**正文就是空串**，前两档双双落空，列表上会出现一行**没有文字**的条目 ——
    /// 点进去才知道是哪个文件。整条链路（服务层 → 侧边栏 → 分组视图）以前都没有这一档。
    /// </summary>
    /// <param name="title">用户重命名过的标题（空 = 没改过）</param>
    /// <param name="firstUser">首条用户消息的正文</param>
    /// <param name="attachmentNames">首条用户消息的附件文件名（可为 null / 空）</param>
    public static string BuildPreview(string? title, string firstUser, IReadOnlyList<string>? attachmentNames = null)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;
        if (!string.IsNullOrWhiteSpace(firstUser))
            return firstUser.Length > 40 ? firstUser[..40] + "…" : firstUser;

        if (attachmentNames is { Count: > 0 })
        {
            var name = attachmentNames[0];
            if (string.IsNullOrWhiteSpace(name)) name = "未命名文件";
            return attachmentNames.Count > 1
                ? $"文件：{name} 等 {attachmentNames.Count} 个"
                : $"文件：{name}";
        }
        return "";
    }

    /// <summary>
    /// 分组指令按钮的文案（2026-10-01）：无指令 → 「添加指令」；有指令 → 展开时「收起指令」、收起时「查看指令」。
    ///
    /// 抽成纯函数是为了让检查点守住它 —— 三个文案对应三种状态，写错不报错，
    /// 只表现为「按钮上写的和点下去做的事对不上」，用户会以为功能坏了。
    /// </summary>
    public static string InstructionButtonText(bool hasInstruction, bool expanded)
        => !hasInstruction ? "添加指令" : (expanded ? "收起指令" : "查看指令");
}
