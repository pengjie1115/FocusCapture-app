using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using FocusCapture.Models;
using FocusCapture.Services.AI;
using FocusCapture.Windows;

namespace FocusCapture.Services;

/// <summary>
/// 编辑待办公共服务（v3.5 → v2 时间识别）：两条保存路径（行内 SaveEditNote / 全屏 NoteEditWindow.Save）共用，
/// 防逻辑漂移。
/// - SaveEdited：待办/笔记都**原地替换**（待办 UpdateTodo、笔记 UpdateNote，红线 2 例外）。
///   2026-09-20 用户拍板：笔记编辑从 AppendEdit 追加【编辑】行改为原地替换 —— 追加语义跨端同步
///   会双条并存/孤儿卡删不掉（标记行与原行被拆文件/乱顺序），替换语义同步模型是"删旧+加新"，
///   天然只留一条。旧内容进回收站可恢复，旧【编辑】痕迹随替换清理（见 UpdateNote）。
/// - ResolveDueAsync：时间识别统一入口。本地规则（TimeParser）优先，按结果分派：
///   ① 命中且未来 → 直接返回
///   ② 纯日期（无时刻，如"30号"）→ 弹"设置提醒"窗问几点（预填识别日期 09:00）
///   ③ 裸时钟已过（如下午输"8点"）→ 弹三选一确认（今晚/明早/取消）
///   ④ 带时段/日期的已过 → 不弹不设（现状）
///   ⑤ 规则未命中 → LLM 兜底（空 Key 短路不发请求；JSON 解析宽容剥代码块）
/// </summary>
public static class TodoEditService
{
    /// <summary>编辑保存：待办/笔记原地替换改行。返回是否保存成功。</summary>
    public static bool SaveEdited(NoteService notes, NoteEntry entry, string newContent)
    {
        var content = newContent.Trim();
        if (string.IsNullOrEmpty(content)) return false;
        if (entry.Type == NoteType.Todo)
            return notes.UpdateTodo(entry, newContent: content);
        return notes.UpdateNote(entry, content);
    }

    /// <summary>
    /// 时间识别统一入口（v2）。owner 为弹窗宿主窗口（可 null，此时弹窗居中无宿主）。
    /// settings 可 null：null 时内部 Load()（用于创建路径等未持有设置实例的调用方）。
    /// 返回最终确认的提醒时间；无时间/取消/识别失败 → null。
    /// </summary>
    public static async Task<DateTime?> ResolveDueAsync(Window? owner, string text, IChatProvider? llm, AppSettings? settings = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parse = TimeParser.Parse(text);
        if (parse.Matched)
        {
            var now = DateTime.Now;

            // ① 命中且未来（含带日期/时段）→ 直接用
            if (!parse.IsDateOnly && parse.Time > now) return parse.Time;

            // ② 纯日期（无时刻）→ 设置开=弹"设置提醒"窗问几点（预填识别日期 09:00）；关=直接默认当天 09:00
            if (parse.IsDateOnly)
            {
                var s = settings ?? AppSettings.Load();
                if (!s.AskTimeForDateOnly)
                    return parse.Time.Date.AddHours(9);
                var dlg = new DueTimeDialog(parse.Time.Date.AddHours(9),
                    $"识别到日期 {parse.Time:yyyy-MM-dd}，几点提醒？");
                if (owner != null) dlg.Owner = owner;
                return dlg.ShowDialog() == true ? dlg.DueTime : null;
            }

            // ③ 裸时钟已过 → 三选一确认（今晚/明早/取消）
            if (parse.IsBareClock && parse.Time <= now)
            {
                var dlg = new TimeConfirmDialog(parse.Time);
                if (owner != null) dlg.Owner = owner;
                return dlg.ShowDialog() == true ? dlg.ConfirmedTime : null;
            }

            // ④ 带时段/日期的已过 → 不弹不设（用户决策：只有裸时钟才确认）
            return null;
        }

        // ⑤ 规则未命中 → LLM 兜底
        return await LlmFallbackAsync(text, llm, ct);
    }

    /// <summary>LLM 兜底：空 Key 短路（不发必失败的请求）；JSON 宽容解析（剥 markdown 代码块再取字段）。</summary>
    private static async Task<DateTime?> LlmFallbackAsync(string text, IChatProvider? llm, CancellationToken ct)
    {
        if (llm == null || string.IsNullOrWhiteSpace(llm.ApiKey)) return null;
        try
        {
            // 带上当前时间：相对时间表达（"明天中午 12 点"）没有基准点就只能靠猜（2026-09-17）
            var messages = PromptBuilder.BuildTimeParseMessages(text, DateTime.Now);
            var result = await llm.CompleteAsync(messages, ct);
            var json = result.Trim();
            // 宽容：剥 ```json ... ``` 代码块（部分模型不遵守"只输出 JSON"）
            var fence = Regex.Match(json, @"```(?:json)?\s*([\s\S]*?)```");
            if (fence.Success) json = fence.Groups[1].Value.Trim();
            // 只认 has_time=true 的输出；time 字段解析失败/已过 → null
            if (!Regex.IsMatch(json, "\"has_time\"\\s*:\\s*true", RegexOptions.IgnoreCase)) return null;
            var m = Regex.Match(json, "\"time\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;
            if (DateTime.TryParse(m.Groups[1].Value, out var t) && t > DateTime.Now) return t;
            return null;
        }
        catch
        {
            // 未配 Key（空 key）/ 网络失败 / 超时 → 降级不弹建议，编辑保存不受影响
            return null;
        }
    }
}
