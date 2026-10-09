using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.AI;

/// <summary>
/// 「AI 整理」的服务层（2026-09-26 新增）：<b>模型解析 → provider 选择 → 一次调用 → 结果清洗</b>。
///
/// <para><b>为什么单独一层而不是让三个窗口各自调 provider</b>：这个功能有三个入口
/// （灵感速览面板 / 待办汇总面板 / 全屏编辑窗），三处若各写一遍"选模型 + 判长度 + 判 Key + 清洗"，
/// 迟早漂移成三套行为（本项目在导入流程上正是这么栽过，见 Windows/ImportFlow.cs 的来历）。
/// 提示词、长度闸门与结果清洗的纯逻辑在 <see cref="NoteTidyPrompt"/>（可被快层直接测）。</para>
///
/// <para><b>失败一律不抛</b>（沿用本项目通则：界面动作不因外部成败崩掉），
/// 统一用 <see cref="TidyOutcome"/> 把"为什么没做成"带回界面，由界面照实告诉用户。</para>
/// </summary>
public static class NoteTidyService
{
    /// <summary>整理结果：成功带正文；失败带一句人话（可直接弹给用户看）。</summary>
    public sealed record TidyOutcome(bool Ok, string Text, string? Error)
    {
        public static TidyOutcome Fail(string message) => new(false, "", message);
        public static TidyOutcome Success(string text) => new(true, text, null);
    }

    /// <summary>
    /// 本次整理实际要用的模型：<b>设置 → AI 模型 → AI 整理模型</b>显式选的优先；
    /// 没选（空）或那个键已失效（供应商/模型被删）→ 回落到当前活跃模型，不让一个过期键把功能锁死。
    /// </summary>
    public static ResolvedAiModel? ResolveModel(AppSettings? s)
        => s == null ? null : (AiModelResolver.TryResolveExact(s, s.AiTidyModelKey) ?? AiModelResolver.ResolveActive(s));

    /// <summary>
    /// 按「AI 整理模型」设置造 provider。
    ///
    /// <para>返回 <paramref name="activeProvider"/>（调用方窗口持有的活跃 provider）的两种情况：
    /// ① 用户没显式指定整理模型 → 跟随当前活跃模型，与翻译/搜索/时间识别一致；
    /// ② 指定了但那家供应商没填 Key → 回落活跃模型，免得用户点了按钮只得到一句"没有 Key"
    /// （他刚配好的活跃模型明明能用）。</para>
    /// </summary>
    public static IChatProvider? ResolveProvider(IChatProvider? activeProvider, AppSettings? s)
    {
        if (s == null) return activeProvider;
        var exact = AiModelResolver.TryResolveExact(s, s.AiTidyModelKey);
        if (exact == null || string.IsNullOrWhiteSpace(exact.ApiKey)) return activeProvider;
        return new OpenAICompatibleProvider(exact.BaseUrl, exact.ApiKey, exact.ModelId,
            exact.MaxOutputTokens, exact.ContextWindow);
    }

    /// <summary>能不能真的发请求 —— 界面据此决定是"点了没反应"还是走正常流程。</summary>
    public static bool IsReady(IChatProvider? provider)
        => provider != null && !string.IsNullOrWhiteSpace(provider.ApiKey);

    /// <summary>没配模型时给用户的话（与 AI 问答的提示口径一致，指到同一个地方）。</summary>
    public const string NotConfiguredMessage =
        "还没有配置可用的 AI 模型。\n\n请到 设置 → AI 模型 添加供应商并填好 Key；" +
        "想让它用某个特定模型，再到「AI 整理模型」里点选一个（不选就跟随当前模型）。";

    /// <summary>按规则解析本次请求的系统提示词（预置 = 纪律 + 指令；自定义 = 整段替换；空 = 默认）。
    /// 提取待办额外注入今天日期 —— 「10月1日补哪年」模型自己判断不了，必须给它今天几号（2026-09-27 phase2）。</summary>
    private static string ResolveSystemPrompt(TidyRule? rule)
    {
        if (rule == null) return NoteTidyPrompt.DefaultSystem;
        if (!rule.IsBuiltin) return (rule.Prompt ?? "").Trim();
        var instruction = TidyRuleCatalog.Find(rule.Id)?.Instruction ?? NoteTidyPrompt.TidyInstruction;
        var system = NoteTidyPrompt.OutputDiscipline + "\n" + instruction;
        if (TidyRuleCatalog.KindOf(rule) == TidyRuleKind.ExtractTodo)
            system += "\n补充事实：今天是 " + DateTime.Now.ToString("yyyy-MM-dd") + "。";
        return system;
    }

    /// <summary>
    /// 整理一次（2026-09-27 起支持指定规则）。<b>顺序敏感</b>：先判空 → 再判超长 → 再判 Key（未配 Key 短路，不发必失败的请求）→ 才发请求。
    /// 超长判定放在 Key 判定之前：内容太长是用户当场能改的事，先告诉他能改的那条。
    /// </summary>
    /// <param name="rule">
    /// 本次用的整理规则：null = 默认（逻辑梳理，与旧版行为对齐）；预置 = 输出纪律 + 指令段拼接；
    /// <b>自定义 = 用户提示词整段替换，不拼任何硬约束</b>（2026-09-27 用户拍板）——
    /// 但 <see cref="NoteTidyPrompt.CleanResult"/> 仍无条件执行（纯机械清洗，与提示词无关）。
    /// </param>
    public static async Task<TidyOutcome> TidyAsync(IChatProvider? provider, string? text, TidyRule? rule = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TidyOutcome.Fail("这条内容是空的，没什么可整理的。");

        if (NoteTidyPrompt.IsTooLong(text))
            return TidyOutcome.Fail(NoteTidyPrompt.TooLongMessage(NoteTidyPrompt.CharCount(text)));

        if (!IsReady(provider)) return TidyOutcome.Fail(NotConfiguredMessage);

        try
        {
            var (system, user) = NoteTidyPrompt.BuildMessages(text, ResolveSystemPrompt(rule));
            var messages = new[]
            {
                new ChatMessage(ChatRoles.System, system),
                new ChatMessage(ChatRoles.User, user),
            };
            var raw = await provider!.CompleteAsync(messages, ct).ConfigureAwait(false);
            var cleaned = NoteTidyPrompt.CleanResult(raw);
            // 预置规则再加一道排版机械清洗（去 ** 、行首 -/* 转「· 」，2026-09-27 phase2 拍板「彻底放弃 * 与 -」）；
            // 自定义规则不做 —— 提示词整段替换是用户的完全主权，他要的 Markdown 不吃掉
            if (rule == null || rule.IsBuiltin)
                cleaned = NoteTidyPrompt.NormalizePlainFormatting(cleaned);
            if (cleaned.Length == 0) return TidyOutcome.Fail("模型这次没有返回内容，请重试一次。");
            return TidyOutcome.Success(cleaned);
        }
        catch (OperationCanceledException)
        {
            return TidyOutcome.Fail("整理已取消。");
        }
        catch (Exception ex)
        {
            // 网络失败 / 超时 / 4xx（Key 失效、余额不足、模型名不对）→ 照实回报，不弹模态框、不冒泡
            return TidyOutcome.Fail($"整理失败：{ex.Message}");
        }
    }
}
