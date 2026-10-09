namespace FocusCapture.Models;

/// <summary>
/// 「AI 整理」的一条整理规则（2026-09-27 规则化改造）。
///
/// <para><b>预置与自定义统一存一份列表</b>（<see cref="AppSettings.TidyRules"/>）：
/// 预置条目只存 Id / 名称 / 显隐 / 顺序，指令正文在代码里（<c>TidyRuleCatalog</c>）——
/// 这样升级内置规则的提示词不需要迁移用户数据；自定义条目存完整提示词。</para>
///
/// <para><b>可见性与默认规则互不干涉</b>（用户拍板）：底栏隐藏某条规则不影响它作为默认规则运行
/// —— 默认规则由 <see cref="AppSettings.TidyDefaultRuleId"/> 单独决定，哪怕底栏一条不剩，预览窗照样按默认规则整理。</para>
/// </summary>
public class TidyRule
{
    /// <summary>稳定标识。预置 = "builtin-*"（见 TidyRuleCatalog）；自定义 = Guid。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（底栏 chip 与设置列表都用它）。预置条目的名称随版本由代码刷新（Normalize），settings 里的旧名不滞留。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 适用场景 / 用法的一句话说明（2026-09-27 phase2）：预览窗规则按钮与设置列表的悬停提示都显示它。
    /// 预置条目由代码随版本刷新；自定义规则在 设置 → 整理规则 的编辑器里填写，留空时悬停只提示「未填写说明」。
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// 自定义规则的提示词（<b>整段替换</b>系统提示词，不拼硬约束 —— 2026-09-27 用户拍板：
    /// 自定义完全交给用户）。预置规则此字段为空，指令从 TidyRuleCatalog 取。
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>是否预置规则（预置不可编辑 / 不可删，可复制成自定义底稿）。</summary>
    public bool IsBuiltin { get; set; }

    /// <summary>是否显示在预览窗底部规则条（隐藏不等于禁用：默认规则隐藏了照样运行）。</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>预置规则的行为分档：决定预览窗结果区形态与底栏出口（<b>只对预置规则有意义</b>，自定义一律 Rewrite）。</summary>
public enum TidyRuleKind
{
    /// <summary>重写原文（理顺 / 总结 / 解释）—— 文本流结果，四个出口全给</summary>
    Rewrite,

    /// <summary>提取待办 —— 结果区切成「待办行列表 + 行内日期时间」，多一个「创建待办」出口</summary>
    ExtractTodo,
}
