using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>「AI 整理」预览窗的出口（2026-09-27 增「创建待办」：仅提取待办规则下出现）。</summary>
public enum TidyChoice
{
    /// <summary>没选（关窗 / Esc）—— 什么都不做</summary>
    None,
    /// <summary>只把整理结果复制到剪贴板，笔记不动</summary>
    Copy,
    /// <summary>原文不动，整理结果另存成一条新笔记</summary>
    SaveAsNew,
    /// <summary>用整理结果原地替换这条内容（旧文本先进回收站，可恢复）</summary>
    Replace,
    /// <summary>把提取出的待办逐条落成真正的待办（原文不动）—— 仅提取待办规则的列表形态下可用。
    /// 2026-09-27 phase2 起不再经关窗触发：预览窗内点「创建待办」原地走这条出口（经 CreateHandler），窗口保持打开。</summary>
    CreateTodos,
}

/// <summary>「创建待办」出口的一条草稿：待办文字 + 可空截止时间（行内日期框/时间框填了才有）。</summary>
public sealed record TidyTodoDraft(string Text, DateTime? Due);

/// <summary>
/// 「AI 整理」的共用流程（2026-09-26 新增；2026-09-27 规则化 + 预览窗改<b>非模态</b>；同日 phase2 改先开窗后整理）：
/// 整理 → 预览对照 → 按出口落库，三个入口共用这一份。
///
/// <para><b>为什么抽成一处</b>：灵感速览面板 / 待办汇总面板 / 全屏编辑窗三处都要这个功能，
/// 各写一遍必然漂移成三套行为（本项目在导入流程上已经栽过一次 —— 见 Windows/ImportFlow.cs 的来历）。
/// 各入口的差异只在"落库后怎么刷新自己"，所以刷新留给调用方的回调，其余全部收在这里。</para>
///
/// <para><b>2026-09-27 phase2：先开窗后整理（用户拍板：点 AI 按钮立即弹窗，不等整理完 —— 等着像卡住了）</b>：
/// 预览窗以「整理中」态 Show 出来后才开始调模型，结果经 <see cref="AiTidyPreviewWindow.CompleteInitialTidy"/>
/// 落进窗口；空内容 / 超长 / 未配模型这三件开窗前就能判定的事仍然提前拦下弹框，不开白窗。
/// 落库发生在预览窗关闭时（或「创建待办」原地创建时），结果经 <paramref name="onDone"/> 回调交还调用方刷新自己。
/// 同一时间可以开着多个预览窗（对不同条目），各窗互不影响。</para>
///
/// <para><b>失败一律不抛</b>：网络失败在预览窗状态行报人话（不再弹框）；开窗前的判定失败弹一句人话，预览窗不开 ——
/// 调用方无需刷新。</para>
/// </summary>
public static class AiTidyFlow
{
    /// <summary>流程结果：用户选了哪个出口、最终文本、以及**数据是否真的变了**（调用方据此决定要不要刷新列表）。
    /// CreatedCount 仅「创建待办」出口 > 0（原地创建后窗口不关，列表要当场刷新）。</summary>
    public sealed record FlowResult(TidyChoice Choice, string Text, bool DataChanged, int CreatedCount = 0);

    /// <summary>
    /// 开预览窗（非模态）→ 按默认规则整理 → 结果落窗。方法在窗口打开后即返回；
    /// 用户选出口关窗（或窗内原地创建待办）后按需落库并回调。
    /// </summary>
    /// <param name="entry">被整理的那条（用于预览窗标题与落库定位）。</param>
    /// <param name="text">送给模型的正文（行内编辑态下应是编辑框里正在改的内容，而不是已落盘的旧文本）。</param>
    /// <param name="onDone">落库后的回调（UI 线程上触发；用户没选任何出口则不回调）。各入口刷新自己的界面用。</param>
    public static async Task RunAsync(Window? owner, NoteService notes, AppSettings? settings,
        IChatProvider? activeProvider, NoteEntry entry, string? text, Action<FlowResult>? onDone = null)
    {
        // 门卫（2026-10-08）：AI 整理属高级档。加在**最前面** ——
        // 这里是三个入口（灵感速览 / 待办汇总 / 全屏编辑窗）唯一的汇聚点，
        // 收在这一处就不必去三处调用点各加一遍（各加必然漂移，本项目在导入流程上栽过）。
        // 拦截发生在开预览窗之前：未解锁时连「整理中」的白窗都不该出现。
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureAiTidy, "AI 整理", owner)) return;

        var provider = NoteTidyService.ResolveProvider(activeProvider, settings);
        var rule = TidyRuleCatalog.ResolveDefaultRule(settings);

        // 开窗前就能判定的失败：拦下弹框，不开白窗（窗内的失败 = 模型 / 网络这一类，走窗口状态行）
        if (string.IsNullOrWhiteSpace(text))
        {
            Warn(owner, "这条内容是空的，没什么可整理的。");
            return;
        }
        if (NoteTidyPrompt.IsTooLong(text))
        {
            Warn(owner, NoteTidyPrompt.TooLongMessage(NoteTidyPrompt.CharCount(text)));
            return;
        }
        if (!NoteTidyService.IsReady(provider))
        {
            Warn(owner, NoteTidyService.NotConfiguredMessage);
            return;
        }

        var modelLabel = ResolveModelLabel(settings, provider);

        var preview = new AiTidyPreviewWindow(new AiTidyPreviewWindow.TidyPreviewRequest
        {
            Original = (text ?? "").Trim(),
            InitialTidied = "",
            InitialRule = rule,
            Rules = TidyRuleCatalog.ResolveVisible(settings),
            ShowCustomEntry = settings?.TidyShowCustomEntry ?? true,
            ModelLabel = modelLabel,
            Provider = provider,
            Settings = settings,
            PendingFirstTidy = true,
            // 窗内「创建待办」的落库入口：复用 Apply 的创建分支，创建成功立刻回调刷新列表（窗口不关，2026-09-27 phase2 拍板）
            CreateHandler = drafts =>
            {
                try
                {
                    var result = Apply(notes, entry, TidyChoice.CreateTodos, "", drafts);
                    if (result != null) onDone?.Invoke(result);
                    return result?.CreatedCount ?? 0;
                }
                catch (Exception ex)
                {
                    AppLog.Error("AiTidy", "窗内创建待办失败", ex);
                    return 0;
                }
            },
        })
        { Owner = owner };

        TidyChoice choice = TidyChoice.None;
        string finalText = "";
        List<TidyTodoDraft> todoDrafts = new();
        preview.Closed += (_, _) =>
        {
            // 非模态下落库搬到关窗时刻。整个回调包 try：owner 已关闭等边缘情况宁可什么都不做，
            // 也不能把异常抛到全局变成崩溃框
            try
            {
                choice = preview.Choice;
                if (choice == TidyChoice.None) return;
                finalText = preview.ResultText;
                todoDrafts = preview.TodoItems;
                var result = Apply(notes, entry, choice, finalText, todoDrafts);
                if (result != null) onDone?.Invoke(result);
                else if (choice == TidyChoice.Copy)
                    Warn(owner, "复制失败：剪贴板可能正被其他程序占用（如远程工具），请稍后重试。");
            }
            catch (Exception ex)
            {
                AppLog.Error("AiTidy", "预览窗关闭后落库失败", ex);
            }
        };

        try
        {
            preview.Show();   // 非模态：灵感速览等宿主窗口保持可操作（2026-09-27 用户拍板）
        }
        catch (Exception ex)
        {
            AppLog.Error("AiTidy", "预览窗打开失败", ex);
            return;
        }

        var outcome = await NoteTidyService.TidyAsync(provider, text, rule).ConfigureAwait(true);
        preview.CompleteInitialTidy(outcome);   // 窗已关 = 用户不等了，方法内部直接丢弃
    }

    /// <summary>
    /// 按出口落库（不含界面刷新）：
    /// <list type="bullet">
    /// <item><b>复制</b> —— 只写剪贴板（走 SafeClipboard，被占用时退避重试、绝不抛，红线 9）。</item>
    /// <item><b>另存为新笔记</b> —— 原文不动，新增一条独立笔记（来源标「AI 整理」，与「AI 回填」区分开）。</item>
    /// <item><b>替换原文</b> —— 原地改行（TodoEditService.SaveEdited）：旧行进回收站可恢复、发删除墓碑防云端旧行回灌。</item>
    /// <item><b>创建待办</b>（2026-09-27 新增）—— 把提取出的每条待办落成真正的待办（可带行内选的截止时间），原文不动。</item>
    /// </list>
    /// </summary>
    public static FlowResult? Apply(NoteService notes, NoteEntry entry, TidyChoice choice, string text,
        List<TidyTodoDraft>? todos = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        switch (choice)
        {
            case TidyChoice.Copy:
                // 双程加固写入：OLE 失败 → Win32 固化退避重试（2026-10-01，远程工具占用场景）。
                // 失败返回 null，由 RunAsync 的关窗回调弹可见提示 —— 此前静默，用户感知为「点了复制没反应」
                if (SafeClipboard.TrySetTextHardened(text, WpfClipboard.SetText, Win32.WriteClipboardText))
                {
                    ClipboardHookService.MarkSelfCopy();
                    return new FlowResult(choice, text, DataChanged: false);
                }
                AppLog.Error("AiTidy", "AI 整理复制失败：剪贴板被持续占用（OLE + Win32 双程均已退避重试）");
                return null;

            case TidyChoice.SaveAsNew:
                return notes.SaveNote(text, "AI 整理") != null
                    ? new FlowResult(choice, text, DataChanged: true)
                    : null;

            case TidyChoice.Replace:
                if (!TodoEditService.SaveEdited(notes, entry, text)) return null;
                // 存储层状态同步（与 QuickViewWindow.SaveEditNote 同口径）：
                // 旧 EditedContent（历史编辑痕迹的合并结果）必须清掉，否则展示层会拿旧文本盖住新正文
                entry.Content = text;
                if (entry.Type != NoteType.Todo) entry.EditedContent = null;
                return new FlowResult(choice, text, DataChanged: true);

            case TidyChoice.CreateTodos:
                var created = 0;
                foreach (var t in todos ?? new List<TidyTodoDraft>())
                {
                    if (string.IsNullOrWhiteSpace(t.Text)) continue;
                    if (notes.SaveNote(t.Text.Trim(), "AI 整理", NoteType.Todo, t.Due) != null) created++;
                }
                return created > 0 ? new FlowResult(choice, text, DataChanged: true, CreatedCount: created) : null;

            default:
                return null;
        }
    }

    /// <summary>预览窗右上角显示的模型名 —— 用户能当场核对"我选的整理模型真的生效了"。</summary>
    private static string ResolveModelLabel(AppSettings? settings, IChatProvider? provider)
    {
        if (provider == null) return "";
        if (settings == null) return provider.Model;
        // 显式选了整理模型 → 显示它（与设置页同一套展示口径：以用户填的显示名为准，重名才补供应商）；
        // 没选（跟随活跃模型）→ 显示实际在用的那个模型的 ID
        var picked = AiModelResolver.TryResolveExact(settings, settings.AiTidyModelKey);
        return picked != null ? AiModelResolver.DisplayNameFor(settings, picked) : provider.Model;
    }

    /// <summary>弹提示。owner 可能已关闭（面板被收掉），此时退回无宿主的弹框，不抛。</summary>
    private static void Warn(Window? owner, string message)
    {
        try
        {
            if (owner != null && owner.IsLoaded)
                System.Windows.MessageBox.Show(owner, message, "AI 整理", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(message, "AI 整理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("AiTidy", "提示弹框失败", ex);
        }
    }
}
