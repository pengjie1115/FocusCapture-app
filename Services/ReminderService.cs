using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// v3.5 待办提醒服务（Phase 3）：定时器 + 弹窗调度 + 角标统计。
/// DispatcherTimer 每 30 秒 tick（禁止 Thread.Sleep 阻塞），tick 内读文件放后台线程避免 UI 卡顿，
/// 结果回到 UI 线程调用弹窗/汇总/角标回调。
/// v3.7 精确到点：tick 只负责兜底补弹（已过点未弹的）+ 预排精确定时器；未来 30 秒内到点的待办
/// 按「到点秒」分组挂一次性 DispatcherTimer，到秒触发弹窗（误差远小于 1 秒），不再受 30 秒采样间隔拖累。
/// 睡眠/唤醒：唤醒后 30 秒内的兜底 tick 会补弹过点未弹条目（含睡眠期间错过的精确定时器）。
/// </summary>
public class ReminderService
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;
    private readonly Action<List<NoteEntry>> _showDuePopups;  // 到点弹窗（同秒合并传入）
    private readonly Action _showDailySummary;                // 每日汇总弹窗
    private readonly Action<int, bool> _updateBadge;          // 角标(count, hasRead)

    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _dueShown = new();       // 防重复：key 用 DueTime.ToString("yyyy-MM-dd HH:mm:ss")|content
    private readonly HashSet<string> _dailyShownAt = new();   // 每日汇总已弹的触发分钟（yyyy-MM-dd HH:mm）
    private readonly HashSet<DateTime> _scheduledDue = new(); // 已预排精确定时器的到点秒（去重，防同一秒重复挂表）
    private readonly List<DispatcherTimer> _preciseTimers = new();

    private bool _stopped;

    /// <summary>单次 tick 兜底补弹的回看窗口；精确定时器漏掉（睡眠/卡顿）的条目在此窗口内补弹。</summary>
    private static readonly TimeSpan BackfillWindow = TimeSpan.FromSeconds(30);
    /// <summary>精确定时器触发时向前看的时间窗（毫秒级容差，覆盖同秒稍晚的条目）。</summary>
    private static readonly TimeSpan PreciseWindow = TimeSpan.FromSeconds(1);

    public ReminderService(NoteService notes, AppSettings settings,
        Action<List<NoteEntry>> showDuePopups,
        Action showDailySummary,
        Action<int, bool> updateBadge)
    {
        _notes = notes;
        _settings = settings;
        _showDuePopups = showDuePopups;
        _showDailySummary = showDailySummary;
        _updateBadge = updateBadge;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await OnTickAsync();
    }

    public void Start()
    {
        _stopped = false;
        _timer.Start();
        Refresh(); // 启动时立刻算一次角标（Phase 3 验收 9：启动即初始化，无需等第一个 tick）
    }

    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
        StopPreciseTimers();
    }

    /// <summary>数据变更后刷新角标（NotesChanged 订阅；到点检查由定时的 tick 负责）。</summary>
    public void Refresh() => RefreshBadgeDirect();

    /// <summary>
    /// 每 30 秒 tick：后台线程全量读 md → 过滤刚过点未弹的（兜底补弹）→ 为未来 30 秒内到点的预排精确定时器；
    /// 另做每日汇总触发与角标刷新。
    /// </summary>
    private async Task OnTickAsync()
    {
        if (_stopped) return;

        // 读文件放后台线程，避免 UI 线程每 30 秒全量读 md 文件卡顿（文件多时明显）
        var entries = await Task.Run(() =>
        {
            try { return _notes.LoadAllEntries(); }
            catch (Exception ex) { Debug.WriteLine($"[FocusCapture] Reminder 后台载入失败: {ex.Message}"); return new List<NoteEntry>(); }
        });

        if (_stopped) return;
        var now = DateTime.Now;
        var today = now.Date;

        // 兜底补弹：DueTime ∈ (now-30s, now] 且进程内未弹过（覆盖精确定时器因睡眠/卡顿漏掉的情况）。
        // v3.7 修复：判定上限为 now，严格不早于设定时间弹窗。
        var due = entries.Where(e =>
            e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open &&
            e.DueTime.HasValue && e.DueTime.Value > now - BackfillWindow && e.DueTime.Value <= now)
            .ToList();
        ShowUnshown(due);

        // v3.7 精确到点：未来 30 秒内到点的条目，按到点秒分组预排一次性定时器，到秒触发（误差 < 1 秒）
        SchedulePrecise(entries, now);

        // 每日汇总：启用且命中 DailySummaryTime 且该触发分钟当日未弹过。
        // v3.7：DailySummaryEmptyPopup=false 时，当天无未办待办则跳过空态弹窗。
        if (_settings.DailySummaryEnabled && TryMatchHhmm(now, _settings.DailySummaryTime))
        {
            var mark = now.ToString("yyyy-MM-dd HH:mm");
            bool hasOpenToday = entries.Any(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open
                && (!e.DueTime.HasValue || e.DueTime.Value.Date <= today));
            if (_dailyShownAt.Add(mark) && (_settings.DailySummaryEmptyPopup || hasOpenToday))
                _showDailySummary?.Invoke();
        }

        // 角标刷新：复用后台线程已载入的 entries，避免 UI 线程每 30s 再读一次文件
        UpdateBadgeFrom(entries);
    }

    /// <summary>
    /// 为未来 30 秒内到点的条目预排精确定时器：按「到点秒」分组，每组一个一次性 DispatcherTimer，
    /// 在到点时刻 +250ms 触发（留毫秒级容差），触发时重新载入文件并只弹该秒窗口内未弹的条目。
    /// 触发时重载文件可自然过滤掉已被 snooze/完成的条目（其 DueTime 已变，不再命中窗口）。
    /// </summary>
    private void SchedulePrecise(List<NoteEntry> entries, DateTime now)
    {
        var upcoming = entries.Where(e =>
            e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open &&
            e.DueTime.HasValue && e.DueTime.Value > now && e.DueTime.Value <= now + BackfillWindow);

        foreach (var g in upcoming.GroupBy(e => e.DueTime!.Value))
        {
            var dueSec = g.Key;
            if (!_scheduledDue.Add(dueSec)) continue; // 该秒已挂过表

            var t = new DispatcherTimer { Interval = dueSec - now + TimeSpan.FromMilliseconds(250) };
            if (t.Interval <= TimeSpan.Zero) { _scheduledDue.Remove(dueSec); continue; }
            t.Tick += (_, _) =>
            {
                t.Stop();
                _scheduledDue.Remove(dueSec);
                _preciseTimers.Remove(t);
                if (_stopped) return;

                // 触发时重新载入，过滤 snooze/完成导致的过期计划
                List<NoteEntry> fresh;
                try { fresh = _notes.LoadAllEntries(); }
                catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 精确提醒载入失败: {ex.Message}"); return; }

                var fireAt = DateTime.Now;
                var hit = fresh.Where(e =>
                    e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open &&
                    e.DueTime.HasValue && e.DueTime.Value > fireAt - PreciseWindow && e.DueTime.Value <= fireAt)
                    .ToList();
                ShowUnshown(hit);
            };
            _preciseTimers.Add(t);
            t.Start();
        }
    }

    /// <summary>过滤掉本进程已弹过的条目，剩余的按分钟分组弹窗；并登记防重复 key。</summary>
    private void ShowUnshown(List<NoteEntry> due)
    {
        List<NoteEntry> toShow = new();
        foreach (var e in due)
        {
            var key = $"{e.DueTime:yyyy-MM-dd HH:mm:ss}|{e.EditedContent ?? e.Content}";
            if (_dueShown.Add(key)) toShow.Add(e);
        }
        if (toShow.Count == 0) return;

        // 同秒到点的合并成一次弹窗列表（v3.7 起按秒合并，不再按分钟）
        var groups = toShow.GroupBy(e => e.DueTime!.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        foreach (var g in groups) _showDuePopups?.Invoke(g.ToList());
    }

    private void StopPreciseTimers()
    {
        foreach (var t in _preciseTimers) t.Stop();
        _preciseTimers.Clear();
        _scheduledDue.Clear();
    }

    /// <summary>Refresh()（NotesChanged）单独触发时重新载入并刷新角标，不做到点弹窗检查。</summary>
    private void RefreshBadgeDirect()
    {
        try { UpdateBadgeFrom(_notes.LoadAllEntries()); }
        catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 角标刷新失败: {ex.Message}"); }
    }

    /// <summary>count=「今天及以前」的未办总数（Open+Read；无 DueTime 的纯待办也算，随时要做；明天及以后的不计），hasRead=是否存在 Read。</summary>
    private void UpdateBadgeFrom(List<NoteEntry> entries)
    {
        var today = DateTime.Today;
        var relevant = entries.Where(e => e.Type == NoteType.Todo
            && (!e.DueTime.HasValue || e.DueTime.Value.Date <= today)).ToList();
        var open = relevant.Count(e => e.TodoStatus == TodoStatus.Open);
        var read = relevant.Count(e => e.TodoStatus == TodoStatus.Read);
        _updateBadge?.Invoke(open + read, read > 0);
    }

    /// <summary>当前时刻是否命中 "HH:mm"（DailSummaryTime，00:00~23:59）。</summary>
    private static bool TryMatchHhmm(DateTime now, string hhmm)
    {
        var m = System.Text.RegularExpressions.Regex.Match(hhmm.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!m.Success) return false;
        int hh = int.Parse(m.Groups[1].Value);
        int mm = int.Parse(m.Groups[2].Value);
        if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return false;
        return now.Hour == hh && now.Minute == mm;
    }
}
