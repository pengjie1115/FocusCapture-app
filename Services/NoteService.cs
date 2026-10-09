using System;
using System.Diagnostics;
using FocusCapture.Models;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services;

public class NoteService
{
    private readonly AppSettings _settings;
    private readonly DeletedNoteService _deletedService;
    private readonly RecycleBinService _recycleBin;

    // Windows 文件名非法字符
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    /// <summary>静态整文件写锁（v3.5）：UpdateTodo（原地改行）与同步层 AppendLine/RemoveLines 共用，
    /// 防后台同步（SyncEngine）与用户操作并发整文件重写互相覆盖。</summary>
    private static readonly object FileWriteLock = new();

    /// <summary>速览行格式：- [yyyy-MM-dd HH:mm] 内容 — 来源: xxx（兼容旧格式 - [HH:mm]）。
    /// v3.10：行尾可选 `(改: yyyy-MM-dd HH:mm)` 标记最后修改时间（来源组改非贪婪以剥离改标记）。
    /// public 让 NoteImportService 复用同一份正则。</summary>
    public static readonly Regex NoteLineRegex = new(
        @"^- \[(\d{4}-\d{2}-\d{2} )?(\d{2}:\d{2})\] (.+?)(?: — 来源: (.+?))?(?: \(改: (\d{4}-\d{2}-\d{2} \d{2}:\d{2})\))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 待办展示/归类时间（v3，2026-08-28）：有 DueTime → 用 DueTime（待办归类到提醒日期，如"30号出去吃饭"归到 8/30）；
    /// 无 DueTime（纯待办）→ 用创建时间。普通笔记恒为 Timestamp。
    /// 统一原则：写入落盘、按日加载、日历计数、面板排序/显示全部走这里，避免各层口径漂移。
    /// </summary>
    public static DateTime TodoDisplayTime(NoteEntry e)
    {
        // v3.10：今天创建且非未到期的条目被实质性修改后，显示修改时间（优先于创建/提醒时间）。
        // 未到期待办（DueTime 在未来）保持显示 DueTime —— 写入路径不为其记录 ModifiedAt，故走原逻辑。
        if (e.ModifiedAt.HasValue) return e.ModifiedAt.Value;
        return e.Type == NoteType.Todo && e.DueTime.HasValue ? e.DueTime.Value : e.Timestamp;
    }

    /// <summary>本机笔记变更事件（保存/编辑/AI 回填/删除成功后触发）——SyncEngine 订阅后启动 30s 合并窗口推送。</summary>
    public event Action? NotesChanged;

    /// <summary>本机"行消失"事件（v4 2026-09-12 起两处触发）：(相对路径, 消失的原始行)。
    /// ①DeleteNote 成功后（行已进回收站）②UpdateTodo 原地改行后（旧版本行被替换掉，不进回收站）。
    /// SyncEngine 订阅后为其生成删除墓碑随下次同步传播——否则云端会永远留着旧行，被反复投递回本地。</summary>
    public event Action<string, IReadOnlyList<string>>? LinesDeleted;

    /// <summary>回收站服务（公开：SyncEngine 软删落地、UI 层复用同一实例）。</summary>
    public RecycleBinService RecycleBin => _recycleBin;

    /// <summary>外部路径（回收站恢复/清空等，非 SaveNote/AppendEdit/DeleteNote 内部）完成本机变更后调用，触发 NotesChanged。</summary>
    public void RaiseNotesChanged() => NotesChanged?.Invoke();

    /// <summary>
    /// 云同步拉取落地后触发（2026-09-09 新增，独立于 NotesChanged）：
    /// 仅供 UI 刷新（灵感速览/角标），绝不可接到 NotifyLocalChange——那会把拉下来的行当成"本地新变更"反向推回云端。
    /// </summary>
    public event Action? CloudDataLanded;

    /// <summary>云同步拉取落地后由 SyncEngine 调用。</summary>
    public void RaiseCloudDataLanded() => CloudDataLanded?.Invoke();

    /// <summary>
    /// 构造函数。<paramref name="deletedRecordPath"/> 为 null → 删除记录使用默认全局位置（生产行为不变）；
    /// 显式传入 → 该实例使用独立的删除记录，供「双设备模拟」等测试场景（2026-09-11 新增）。
    /// </summary>
    public NoteService(AppSettings settings, string? deletedRecordPath = null)
    {
        _settings = settings;
        _deletedService = new DeletedNoteService(deletedRecordPath);
        _recycleBin = new RecycleBinService(settings.NotesPath);
    }

    /// <summary>软删除服务（暴露给 UI 层调用 MarkDeleted）</summary>
    public DeletedNoteService DeletedService => _deletedService;

    /// <summary>
    /// 保存笔记/待办。type=Todo 时自动做本地规则时间识别（命中且是未来时间 → 设 DueTime；已过/未命中 → 纯待办）。
    /// dueTime 非空（如输入框已弹窗确认过）→ 直接用显式值，跳过自动识别。
    /// </summary>
    public NoteEntry? SaveNote(string content, string? sourceWindow = null, NoteType type = NoteType.Note, DateTime? dueTime = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var entry = new NoteEntry
        {
            Timestamp = DateTime.Now,
            Content = content.Trim(),
            SourceWindow = sourceWindow ?? Win32.GetActiveWindowTitle(),
        };

        // 提取 #标签（取第一个）
        var tagMatch = Regex.Match(content, @"^#(\S+)");
        if (tagMatch.Success)
        {
            var rawTag = tagMatch.Groups[1].Value;
            // 净化标签：移除文件名非法字符，替换路径分隔符为下划线
            entry.Tag = SanitizeFileName(rawTag);
            // 去掉标签前缀存内容
            entry.Content = content[tagMatch.Length..].Trim();
        }

        // v3.5：待办类型 + 规则解析（命中且是未来时间 → 设 DueTime；已过 / 未命中 → 不设）
        // v2：dueTime 显式传入（输入框已弹窗确认过）→ 优先用显式值
        entry.Type = type;
        if (type == NoteType.Todo)
        {
            if (dueTime.HasValue)
                entry.DueTime = dueTime.Value;
            else if (TimeParser.TryParse(entry.Content, out var due))
                entry.DueTime = due;
            // v3.12（2026-09-27 用户拍板）：输入的日期时间只用于识别归属与提醒，不留在正文里 ——
            // 设上提醒后，把正文里被时间识别命中的表达剥掉（没命中 / 剥完为空 → 保留原文，绝不产生空待办）
            if (entry.DueTime.HasValue)
            {
                var stripped = TimeParser.StripTimeExpression(entry.Content);
                if (stripped.Length > 0) entry.Content = stripped;
            }
        }

        // 确定文件名
        string fileName;
        if (!string.IsNullOrEmpty(entry.Tag))
            fileName = $"{entry.Tag}.md";
        else
        {
            // v3（2026-08-28）：待办有提醒日期 → 归类写入该日期文件（如"30号出去吃饭"→ 灵感_2026-08-30.md，
            // 保证 30 号面板能看到；普通笔记仍写当天文件）。entry.DueTime 在上方已赋值（显式 or 规则解析）。
            var fileDate = TodoDisplayTime(entry).Date;
            fileName = $"灵感_{fileDate:yyyy-MM-dd}.md";
        }

        try
        {
            Directory.CreateDirectory(_settings.NotesPath);
            var filePath = Path.Combine(_settings.NotesPath, fileName);
            var line = entry.ToMarkdownLine();
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.Error("Note", "写入笔记失败", ex);
            // 降级：不带标签写入默认文件（保持类型）
            if (!string.IsNullOrEmpty(entry.Tag))
            {
                entry.Tag = null;
                return SaveNote(content, sourceWindow, type);
            }
            return null;
        }

        NotesChanged?.Invoke();
        return entry;
    }

    /// <summary>移除文件名非法字符，截断过长标签</summary>
    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(InvalidFileChars, c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);
        }
        // 限制标签长度，防止路径过长
        var result = sb.ToString().Trim();
        return result.Length > 60 ? result[..60] : result;
    }

    public string? SaveClipboard(string? sourceWindow = null)
    {
        var text = Win32.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return null;

        SaveNote(text, sourceWindow);
        return text;
    }

    /// <summary>编辑保存：追加带标记的新行，原行不动（MD 只增不减）。带 (ref 原笔记时间戳) 精确关联回原笔记。</summary>
    public bool AppendEdit(NoteEntry entry, string newContent)
    {
        if (entry == null || string.IsNullOrWhiteSpace(newContent)) return false;
        if (ImmersiveSessionService.IsLocked(entry.Timestamp)) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        // 多段内容换行转义为单行标记，保持单行存储格式
        var escaped = newContent
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【编辑】{escaped} — 来源: 手动编辑 (ref {entry.Timestamp:yyyy-MM-dd HH:mm})";

        try
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Note", $"编辑保存失败 ({filePath})", ex);
            return false;
        }
    }

    /// <summary>
    /// AI 回填-追加到原笔记：追加一条带标记的新行（MD 只增不减，禁止覆盖原行）。
    /// 行尾带 (ref 原笔记时间戳)，解析时精确关联回原笔记（展示层合并为子条目）。
    /// 受沉浸式锁定约束：锁定时返回 false。
    /// </summary>
    public bool AppendToNote(NoteEntry entry, string fillText)
    {
        if (entry == null || string.IsNullOrWhiteSpace(fillText)) return false;
        if (ImmersiveSessionService.IsLocked(entry.Timestamp)) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        // 新行只含回填内容；多段内容换行转义为单行标记，保持单行存储格式
        var escaped = fillText
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【AI 释义】{escaped} — 来源: AI 回填 (ref {entry.Timestamp:yyyy-MM-dd HH:mm})";

        try
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Note", $"AI 回填失败 ({filePath})", ex);
            return false;
        }
    }

    /// <summary>AI 回填-单独形成一条新笔记（普通行，独立成条）</summary>
    public NoteEntry? SaveAiNote(string text)
        => SaveNote(text, "AI 回填");

    /// <summary>
    /// 定位 entry 所在的 md 文件（v4 2026-09-12：**全库扫行**，不再按文件名猜），找不到返回 null。
    /// 旧实现只查 `灵感_{entry.Timestamp:yyyy-MM-dd}.md` + 标签文件，但待办写入时按**提醒日**归类
    /// （未到期待办躺在 `灵感_{DueTime}.md` 里）→ 文件名与时间戳日对不上 → 删除/编辑一律报
    /// "未在笔记文件中找到该条目"（实测：创建端删不掉自己的未到期待办，而另一端从云端落地时按创建日写，
    /// 反而删得掉——同一行在两台设备落进不同文件造成的"口径分裂"，根因见 SyncNote.ComputeId 注释）。
    /// 匹配优先级：整行精确匹配（IsEntryLine）优先，退而取行首时间戳前缀匹配（兼容用户手工改过的行）。
    /// </summary>
    private string? FindEntryFile(NoteEntry entry)
    {
        if (!Directory.Exists(_settings.NotesPath)) return null;

        // 行前缀：完整日期（新格式）；旧格式 [HH:mm] 行作为兼容回退
        var fullPrefix = $"- [{entry.Timestamp:yyyy-MM-dd HH:mm}]";
        var timePrefix = $"- [{entry.Timestamp:HH:mm}]";
        string? prefixHit = null;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            try
            {
                foreach (var rawLine in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (IsEntryLine(line, entry)) return file;   // 整行精确匹配最可信，直接返回
                    if (prefixHit == null &&
                        (line.StartsWith(fullPrefix, StringComparison.Ordinal) ||
                         line.StartsWith(timePrefix, StringComparison.Ordinal)))
                        prefixHit = file;                        // 记为候选，继续找是否有精确匹配
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Note", $"定位条目所在文件失败 ({file})", ex);
            }
        }
        return prefixHit;
    }

    /// <summary>删除笔记：物理删除 MD 对应行（选项 A）+ 软删除记录；关联的 AI 释义/编辑标记行一并删除。</summary>
    public bool DeleteNote(NoteEntry entry)
    {
        if (entry == null) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        try
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var refStr = entry.Timestamp.ToString("yyyy-MM-dd HH:mm");

            // 分钟精度歧义检测：同分钟存在 >1 条原笔记（非标记行）时，(ref 时间戳) 无法区分归属，
            // 删除时保守不删关联标记行（宁可留孤行，不可误删其他笔记的标记行——2026-08-13 测试发现误删+错误软删）。
            var sameMinuteCount = 0;
            foreach (var rawLine in lines)
            {
                var l = rawLine.TrimEnd('\r');
                if (l.Contains("【编辑】", StringComparison.Ordinal) ||
                    l.Contains("【AI 释义】", StringComparison.Ordinal)) continue;
                var m = NoteLineRegex.Match(l);
                if (!m.Success) continue;
                var day = m.Groups[1].Success
                    ? m.Groups[1].Value.Trim()
                    : DateTime.Today.ToString("yyyy-MM-dd");
                if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts) && ts == entry.Timestamp)
                    sameMinuteCount++;
            }
            var refAmbiguous = sameMinuteCount > 1;

            var keep = new List<string>(lines.Length);
            var removedLines = new List<string>();
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (IsEntryLine(line, entry) || IsAssociatedMarkerLine(line, refStr, entry, refAmbiguous))
                {
                    removedLines.Add(line);
                    continue;
                }
                keep.Add(line);
            }
            if (removedLines.Count == 0) return false;

            // 先写回收站（成功）→ 再删原行（2026-08-13 审查修正：防"行已删但回收站没记"的数据永久丢失，
            // 不再 MarkDeleted，避免 v2.0 软删记录与回收站双轨冲突）
            if (!_recycleBin.Add(Path.GetFileName(filePath), removedLines))
            {
                AppLog.Error("Note", $"删除中止：回收站写入失败，原行保留 ({filePath})");
                return false;
            }
            File.WriteAllLines(filePath, keep, Encoding.UTF8);
            LinesDeleted?.Invoke(Path.GetFileName(filePath), removedLines);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Note", $"删除笔记失败 ({filePath})", ex);
            return false;
        }
    }

    // ── 原地改行（MD「只增不减」的唯一例外，红线 2）：UpdateTodo（v3.5）+ UpdateNote（2026-09-17） ──

    /// <summary>
    /// 待办原地改行：更新内容/状态/提醒时间。用变更前字段定位原行 → 重建行文本 → 整文件重写该行。
    /// 找不到原行返回 false。禁止追加新行；禁止改到非待办行（入口要求 Type=Todo）。
    /// 顺序敏感：先定位后重建——禁止先套用变更再匹配（IsEntryLine 比较 line == entry.ToMarkdownLine()，
    /// 先改字段后新行文本带 (状态:) 等后缀永远匹配不上原行，UpdateTodo 将恒返回 false）。
    /// 与同步层 AppendLine/RemoveLines 共用 FileWriteLock，防后台同步与用户操作并发整文件重写互相覆盖。
    /// 2026-09-17：改行内核抽出为 <see cref="RewriteEntryLine"/>（笔记 <see cref="UpdateNote"/> 共用），
    /// 且**旧行文本先写入回收站**再替换（用户拍板：AI 改错必须能恢复）。
    /// </summary>
    public bool UpdateTodo(NoteEntry entry, string? newContent = null, TodoStatus? status = null,
        DateTime? dueTime = null, bool clearDue = false)
    {
        if (entry == null || entry.Type != NoteType.Todo) return false;

        // 以 entry 原始字段为底套用变更（定位必须用变更前的字段，见上面顺序敏感说明）
        var updated = CloneForUpdate(entry);
        var contentChanged = newContent != null
            && !string.Equals(newContent.Trim(), entry.Content, StringComparison.Ordinal);
        if (newContent != null) updated.Content = newContent.Trim();
        if (clearDue) updated.DueTime = null;
        else if (dueTime.HasValue) updated.DueTime = dueTime;
        if (status.HasValue) updated.TodoStatus = status.Value;
        // v3.10：实质性内容修改（非提醒/状态变更）→ 今天创建且非未到期的条目记录修改时间
        if (contentChanged && IsEligibleForModifiedTime(entry))
            updated.ModifiedAt = DateTime.Now;

        if (!RewriteEntryLine(entry, updated, out var replacedLine, out var fileName)) return false;

        // 成功后把变更回写传入的 entry（防调用方后续再 UpdateTodo 用旧字段定位失败——面板徽标已办/右键设提醒/建议条设提醒
        // 链路都会先后对同一条待办多次 UpdateTodo，定位必须始终用最新字段）
        entry.Content = updated.Content;
        entry.DueTime = updated.DueTime;
        entry.TodoStatus = updated.TodoStatus;
        entry.ModifiedAt = updated.ModifiedAt;

        // v4（2026-09-12 行身份改造）：原地改行 = 旧版本行在本地消失。若不告知同步层，云端那条旧版本行
        // 永不删除，而任何"本机没有该行"的设备都会在每次拉取时把它当新行落回来（**本机自己也算**，
        // 因为改行后本机也认不出旧行了）——这就是"点已完成 → 过一会待办又变回未办、每轮多留一份已办副本"
        // 的根因。这里复用删除事件把旧行作为 Deleted=true 墓碑推给同步层。
        // （2026-09-17 起：旧行还会**先写进回收站**兜底，见 RewriteEntryLine。）
        if (replacedLine != null)
            LinesDeleted?.Invoke(fileName!, new[] { replacedLine });

        NotesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 笔记原地改行（2026-09-17 新增，供 Agent 的 update_note 使用；2026-09-20 起界面「编辑保存」
    /// 也走这条路，见 TodoEditService.SaveEdited —— 用户拍板：编辑=替换，不再追加【编辑】行）。
    ///
    /// **两条路曾刻意不同**（界面走 AppendEdit 追加、这里原地替换），2026-09-20 因跨端同步实测
    /// 双条并存/孤儿卡删不掉而合并为一条：追加【编辑】行 + 展示层合并的语义在跨端场景天然脆弱
    /// （标记行与原行会被拆文件/乱顺序），替换语义的同步模型是"删旧行+加新行"，天然只留一条。
    ///
    /// 代价与对策：原地替换 = 旧文本消失，而行身份是 SHA256(行文本)，改行等于「删旧行 + 加新行」，
    /// 同步层会认不出旧行。所以 ① 旧行先写回收站（改错可恢复）② 成功后抛 LinesDeleted
    /// 让同步层生成删除墓碑，避免云端旧行被反复投递回本机 ③ 清理旧【编辑】痕迹行（它们描述的是
    /// 旧正文，留着会被展示层挂回新行、把新内容盖成旧编辑文本），同样进回收站 + 发墓碑。
    ///
    /// 沉浸式输入锁定中的笔记不许改。
    /// </summary>
    public bool UpdateNote(NoteEntry entry, string newContent)
    {
        if (entry == null || entry.Type != NoteType.Note) return false;
        if (string.IsNullOrWhiteSpace(newContent)) return false;
        if (ImmersiveSessionService.IsLocked(entry.Timestamp)) return false;

        var updated = CloneForUpdate(entry);
        updated.Content = newContent.Trim();
        // v3.10：笔记内容修改 → 今天创建且非未到期的条目记录修改时间（UpdateNote 仅由内容修改路径调用）
        if (IsEligibleForModifiedTime(entry))
            updated.ModifiedAt = DateTime.Now;

        if (!RewriteEntryLine(entry, updated, out var replacedLine, out var fileName)) return false;

        entry.Content = updated.Content;
        entry.ModifiedAt = updated.ModifiedAt;

        if (replacedLine != null)
            LinesDeleted?.Invoke(fileName!, new[] { replacedLine });

        // 旧【编辑】痕迹行清理（进回收站 + 墓碑，见 RemoveStaleEditMarkers）
        RemoveStaleEditMarkers(entry);

        NotesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 原地替换正文后，清理引用原时间戳的【编辑】痕迹行（2026-09-20 随"编辑=替换"语义新增）。
    /// 痕迹行描述的是旧正文的编辑历史；正文被整体替换后若不清，展示层会按 ref 把它们挂回
    /// 新行，把新内容盖成旧编辑文本（EditedContent 优先于 Content 展示）。
    /// 【AI 释义】是子条目语义（不随正文改写失效），**保留不清**。
    /// 防线：① 旧行先进回收站（可恢复）② 同分钟多条原笔记时保守跳过（ref 分钟精度无法区分归属，
    /// 同 DeleteNote 的口径）③ 清掉的行发 LinesDeleted 墓碑，否则云端旧行会被反复投递回本机。
    /// </summary>
    private void RemoveStaleEditMarkers(NoteEntry entry)
    {
        if (!Directory.Exists(_settings.NotesPath)) return;
        var refStr = entry.Timestamp.ToString("yyyy-MM-dd HH:mm");

        try
        {
            // 同分钟歧义检测（全库口径）：>1 条原笔记时 (ref 时间戳) 无法区分归属 → 保守不清
            var sameMinuteCount = 0;
            foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }
                foreach (var raw in lines)
                {
                    var l = raw.TrimEnd('\r');
                    if (l.Contains("【编辑】", StringComparison.Ordinal) ||
                        l.Contains("【AI 释义】", StringComparison.Ordinal)) continue;
                    var m = NoteLineRegex.Match(l);
                    if (!m.Success) continue;
                    var day = m.Groups[1].Success ? m.Groups[1].Value.Trim() : "";
                    if (day.Length == 0) continue;   // 无日期旧格式行不参与计数（无法可靠判定同分钟）
                    if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts) && ts == entry.Timestamp)
                        sameMinuteCount++;
                }
            }
            if (sameMinuteCount > 1) return;

            foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }

                var removed = new List<string>();
                var keep = new List<string>(lines.Length);
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Contains("【编辑】", StringComparison.Ordinal) &&
                        line.Contains($"(ref {refStr})", StringComparison.Ordinal))
                    {
                        removed.Add(line);
                        continue;
                    }
                    keep.Add(line);
                }
                if (removed.Count == 0) continue;

                lock (FileWriteLock)
                {
                    // 先回收站后删行（与 DeleteNote/RewriteEntryLine 同序：防"行已删但回收站没记"）
                    if (!_recycleBin.AddIfAbsent(Path.GetFileName(file), removed))
                    {
                        AppLog.Error("Note", $"清理旧编辑痕迹中止：回收站写入失败，痕迹保留 ({file})");
                        continue;
                    }
                    File.WriteAllLines(file, keep, Encoding.UTF8);
                }
                LinesDeleted?.Invoke(Path.GetFileName(file), removed);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Note", "清理旧编辑痕迹失败（不影响本次改行结果）", ex);
        }
    }

    /// <summary>
    /// 原地改行内核（笔记 / 待办共用，2026-09-17 从 UpdateTodo 抽出）。
    ///
    /// 返回 false = **没有改动任何东西**（定位不到原行 / 回收站写入失败 / 文件写入异常），
    /// 调用方据此如实回复用户，不要谎报成功。
    /// <c>replacedLine</c> 非空 = 确实替换了，调用方需把它当墓碑推给同步层。
    ///
    /// 顺序（不可颠倒）：定位原行 → **旧行写回收站** → 替换 → 带回旧行文本。
    /// 回收站写失败就中止改行：宁可这次改不成（用户还能重试），也不能让旧内容无声消失 ——
    /// 那是"原地改行"唯一的可恢复出口。
    /// </summary>
    private bool RewriteEntryLine(NoteEntry entry, NoteEntry updated, out string? replacedLine, out string? fileName)
    {
        replacedLine = null;
        fileName = null;

        // v3.7 修复：按行精确查找所在文件——FindEntryFile 按 entry 时间戳日期猜文件
        // （只查 灵感_{日期}.md + 标签文件），但存在行内日期与文件名不一致的历史数据
        // （如 8-28 创建的待办行躺在 灵感_2026-08-30.md 里）→ 永远找不到 → "清理失败"。
        // 这里遍历全部 md，哪一行 IsEntryLine 匹配就改哪个文件，天然兜底所有错位情况。
        var filePath = FindEntryFileByLine(entry);
        if (filePath == null) return false;

        var name = Path.GetFileName(filePath);

        lock (FileWriteLock)
        {
            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);

                // 先定位：用变更前字段（调用方传入的 entry 尚未套用变更）精确整行匹配原行
                var matchedIndex = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (IsEntryLine(lines[i].TrimEnd('\r'), entry)) { matchedIndex = i; break; }
                }
                if (matchedIndex < 0) return false;

                var oldLine = lines[matchedIndex].TrimEnd('\r');
                var newLine = updated.ToMarkdownLine();

                // 文本没变：不算失败，也没有墓碑要发
                if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
                {
                    fileName = name;
                    return true;
                }

                // ① 旧行先落回收站（AddIfAbsent：已有同内容记录时不重复写）
                if (!_recycleBin.AddIfAbsent(name, new[] { oldLine }))
                {
                    AppLog.Error("Note", $"改行中止：回收站写入失败，原行保留 ({filePath})");
                    return false;
                }

                // ② 再整行替换
                lines[matchedIndex] = newLine;
                File.WriteAllLines(filePath, lines, Encoding.UTF8);

                replacedLine = oldLine;
                fileName = name;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Note", $"原地改行失败 ({filePath})", ex);
                return false;
            }
        }
    }

    /// <summary>浅克隆 NoteEntry（仅携带存储相关字段），供 UpdateTodo 以原始字段为底重建新行。变更不污染调用方传入的 entry。</summary>
    private static NoteEntry CloneForUpdate(NoteEntry e) => new()
    {
        Timestamp = e.Timestamp,
        Content = e.Content,
        SourceWindow = e.SourceWindow,
        Tag = e.Tag,
        Type = e.Type,
        DueTime = e.DueTime,
        TodoStatus = e.TodoStatus,
        ModifiedAt = e.ModifiedAt
    };

    /// <summary>v3.10：判定该条目修改后是否记录修改时间。
    /// 规则（用户拍板）：仅今天创建的条目适用；未到期待办（DueTime 在未来）保持原规则不记录。
    /// 用 entry 原始字段判定（创建时间、原始 DueTime）——内容修改不改这两项。</summary>
    private static bool IsEligibleForModifiedTime(NoteEntry entry)
    {
        if (entry.Timestamp.Date != DateTime.Today) return false;
        if (entry.Type == NoteType.Todo && entry.DueTime.HasValue && entry.DueTime.Value.Date > DateTime.Today)
            return false;
        return true;
    }

    /// <summary>精确匹配 entry 对应的存储行（新格式整行 / v3.5 旧待办格式无秒 / v2.0 旧格式 [HH:mm] 兼容）。
    /// v3.7 回退分支：9月2日秒级落盘改动前的待办行 (提醒: yyyy-MM-dd HH:mm) 无秒，
    /// 解析侧兼容（补 :00）但 ToMarkdownLine 生成带秒行 → 整行匹配恒失败 → UpdateTodo/编辑保存全部报"找不到条目"。
    /// 此处仅加匹配分支定位旧行，不产生/不修改任何时间值；匹配成功后行会被重写为带秒新格式（旧数据自然迁移）。</summary>
    private static bool IsEntryLine(string line, NoteEntry entry)
    {
        if (line == entry.ToMarkdownLine()) return true;

        // v5（2026-09-20）：RawLine 精确匹配 —— 解析时记录的原始物理行，最高优先级的身份证据。
        // 标记行孤儿卡（Content 是剥掉前缀/ref 的展示文本）靠它才能定位/删除/改行
        //（否则重建行永远匹配不上带【AI 释义】/【编辑】前缀和 (ref …) 尾巴的物理行）。
        if (!string.IsNullOrEmpty(entry.RawLine) && line == entry.RawLine) return true;

        // v3.5 旧待办格式回退：同 FormatTodoLine 但提醒时间到分钟（秒为 :00 时与新版同串，天然去重）
        if (entry.Type == NoteType.Todo && line == NoteEntry.FormatTodoLine(entry, withSeconds: false)) return true;

        var escaped = entry.Content
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var old = $"- [{entry.Timestamp:HH:mm}] {escaped}";
        return line == old || line == old + $" — 来源: {entry.SourceWindow}";
    }

    /// <summary>按行精确查找 entry 所在 md 文件：遍历全部 .md，任一行 IsEntryLine 匹配即返回该文件；找不到返回 null。
    /// 与 FindEntryFile（按前缀猜文件）的区别：不依赖"行内日期 == 文件名日期"假设，供 UpdateTodo 定位历史错位数据。</summary>
    private string? FindEntryFileByLine(NoteEntry entry)
    {
        if (!Directory.Exists(_settings.NotesPath)) return null;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (IsEntryLine(raw.TrimEnd('\r'), entry)) return file;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Note", $"按行查找待办失败 ({file})", ex);
            }
        }
        return null;
    }

    /// <summary>
    /// 判断是否为该条笔记关联的标记行（AI 释义 / 编辑）。
    /// - (ref) 精确匹配：refAmbiguous=true（同分钟有多条原笔记，ref 分钟精度无法区分归属）时**保守不删**，防误删其他笔记的标记行；
    /// - 有 (ref ...) 但匹配不上 → 属于其他笔记，不删；
    /// - 无 ref 的 v2.0 旧数据按 ±60s 回退（2026-08-13 测试发现并修正）。
    /// </summary>
    private static bool IsAssociatedMarkerLine(string line, string refStr, NoteEntry entry, bool refAmbiguous)
    {
        if (!line.Contains("【AI 释义】", StringComparison.Ordinal) &&
            !line.Contains("【编辑】", StringComparison.Ordinal))
            return false;

        // ref 精确匹配：歧义时保守不删
        if (line.Contains($"(ref {refStr})", StringComparison.Ordinal)) return !refAmbiguous;

        // 有 ref 但匹配不上 → 属于其他笔记，不误删
        if (line.Contains("(ref ", StringComparison.Ordinal)) return false;

        var match = NoteLineRegex.Match(line);
        if (!match.Success) return false;
        var day = match.Groups[1].Success
            ? match.Groups[1].Value.Trim()
            : DateTime.Today.ToString("yyyy-MM-dd");
        if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) return false;
        return Math.Abs((ts - entry.Timestamp).TotalMinutes) < 1;
    }

    /// <summary>
    /// 按指定日期加载笔记（v5 2026-09-20：加载内核统一走 <see cref="LoadEntriesCore"/> ——
    /// 两遍扫描 + **跨文件 ref 挂靠**，修复标记行跨端同步后"原始+编辑"双条并存/孤儿卡删不掉）。
    /// 归类口径不变：<see cref="TodoDisplayTime"/>（待办=提醒日/创建日、笔记=行时间戳），
    /// 与日历计数（LoadNoteCounts）、面板排序一致。
    /// </summary>
    public List<NoteEntry> LoadNotes(DateTime date)
        => LoadEntriesCore(date);

    /// <summary>
    /// 加载内核（v5 2026-09-20 重构：**两遍扫描 + 跨文件 ref 挂靠**，取代旧的"逐文件边解析边挂靠"）。
    ///
    /// 修的两个实测 Bug（同一根因：旧版标记行合并是「同文件 + 原行先解析」的启发式，跨端同步落地
    /// 按各行自己的时间戳算落地文件、落地顺序也不保证创建序，两个前提都会被打破）：
    /// ① 【编辑】/【AI 释义】标记行与原笔记被拆进不同文件，或标记行物理位置先于原行 → 挂靠失败
    ///    → 标记行独立成卡（来源"手动编辑/AI 回填"）→ 面板出现"原始笔记 + 编辑笔记"两条；
    /// ② 孤儿卡删除必失败：孤儿卡的 Content 是剥掉前缀/ref 的展示文本，拿它重建行永远匹配不上
    ///    带前缀/ref 的物理行 → 报"未在笔记文件中找到该条目"。
    ///
    /// 新逻辑：第一遍收集全部原笔记行（含 <see cref="NoteEntry.RawLine"/> 原始物理行）与全部标记行；
    /// 第二遍把标记行按 ref **挂靠到全库任意文件**的原笔记（不再受文件边界与解析顺序限制），
    /// 挂不上的才独立成条且带 RawLine（删除/改行按它精确定位物理行）。
    /// 多条标记行挂同一目标时按标记行自身时间戳升序逐条套用 → "最新编辑内容赢"与物理顺序解耦。
    /// <paramref name="dateFilter"/> 非空时按 <see cref="TodoDisplayTime"/> 过滤（LoadNotes 语义）。
    /// </summary>
    private List<NoteEntry> LoadEntriesCore(DateTime? dateFilter)
    {
        var entries = new List<NoteEntry>();
        var markers = new List<DeferredMarker>();
        if (!Directory.Exists(_settings.NotesPath)) return entries;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var isDayFile = fileName.StartsWith("灵感_", StringComparison.Ordinal);
            var tag = isDayFile ? null : fileName;
            // 旧格式 [HH:mm] 行无日期：灵感文件按文件名日期归属，标签文件按"今天"归属（与 LoadNoteCounts 一致）
            var dateContext = isDayFile && DateTime.TryParse(fileName["灵感_".Length..], out var fileDate)
                ? fileDate
                : DateTime.Today;

            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 读取笔记失败 ({file}): {ex.Message}");
                continue;
            }

            foreach (var rawLine in lines)
            {
                // 新格式: - [yyyy-MM-dd HH:mm] 内容 — 来源: xxx
                // 旧格式: - [HH:mm] 内容 — 来源: xxx（日期取 dateContext）
                var line = rawLine.TrimEnd('\r');
                var match = NoteLineRegex.Match(line);
                if (!match.Success) continue;

                var day = match.Groups[1].Success
                    ? match.Groups[1].Value.Trim()
                    : dateContext.ToString("yyyy-MM-dd");
                if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) continue;

                var rawContent = match.Groups[3].Value.Replace("\u23CE", "\n");
                var source = match.Groups[4].Success ? match.Groups[4].Value : "";
                // v3.10：行尾改标记 → ModifiedAt（旧行无此标记则保持 null，走原显示规则）
                DateTime? modifiedAt = null;
                if (match.Groups[5].Success && DateTime.TryParse(match.Groups[5].Value, out var modTs))
                    modifiedAt = modTs;

                // 标记行（AI 释义 / 编辑）：第一遍只暂存，第二遍跨文件挂靠
                var marker = ParseMarkerLine(rawContent, source, out var markerText, out var refTs);
                if (marker != null)
                {
                    markers.Add(new DeferredMarker(marker, markerText, ts, refTs, line));
                    continue;
                }

                // 普通行（v3.5 待办解析：必须放在 ParseMarkerLine 判定之后，否则待办正文以【编辑】/【AI 释义】开头会被误判成标记行）
                NoteEntry entry;
                if (rawContent.StartsWith("【待办】", StringComparison.Ordinal))
                {
                    entry = new NoteEntry
                    {
                        Timestamp = ts,
                        Type = NoteType.Todo,
                        Content = rawContent,
                        SourceWindow = source,
                        Tag = tag
                    };
                    // 剥离 (提醒: …[, 状态: …]) 与独立 (状态: …) 属性（兼容组合/独立两种括号写法）
                    // v3.7：提醒时间兼容带秒（新）与不带秒（旧，按 :00 补齐）两种落盘格式
                    var body = rawContent["【待办】".Length..].Trim();
                    var attrMatch = Regex.Match(body, @"\(提醒: (\d{4}-\d{2}-\d{2} \d{2}:\d{2}(?::\d{2})?)(?:, 状态: (已办|已读))?\)");
                    if (attrMatch.Success)
                    {
                        if (DateTime.TryParse(attrMatch.Groups[1].Value, out var due))
                            entry.DueTime = due;
                        if (attrMatch.Groups[2].Success)
                            entry.TodoStatus = attrMatch.Groups[2].Value == "已办" ? TodoStatus.Done : TodoStatus.Read;
                        body = body.Remove(attrMatch.Index, attrMatch.Length).Trim();
                    }
                    var stMatch = Regex.Match(body, @"\(状态: (已办|已读)\)");
                    if (stMatch.Success)
                    {
                        entry.TodoStatus = stMatch.Groups[1].Value == "已办" ? TodoStatus.Done : TodoStatus.Read;
                        body = body.Remove(stMatch.Index, stMatch.Length).Trim();
                    }
                    entry.Content = body;
                }
                else
                {
                    entry = new NoteEntry
                    {
                        Timestamp = ts,
                        Content = rawContent,
                        SourceWindow = source,
                        Tag = tag
                    };
                }
                entry.ModifiedAt = modifiedAt;
                entry.RawLine = line;
                entries.Add(entry);
            }
        }

        // 第二遍：标记行跨文件挂靠（按标记行自身时间戳升序 → 最新编辑内容最后套用，与物理顺序解耦）
        markers.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        foreach (var m in markers)
        {
            NoteEntry? target = null;
            // 有 ref → 全库精确关联；无 ref → 全库时间相近（±60s）的最近原笔记（口径同旧版，只是不再受文件边界限制）
            if (m.RefTs.HasValue)
                target = entries.LastOrDefault(e => Math.Abs((e.Timestamp - m.RefTs.Value).TotalMinutes) < 1);
            if (target == null)
                target = entries.LastOrDefault(e => Math.Abs((e.Timestamp - m.Timestamp).TotalMinutes) < 1);

            if (target != null)
            {
                if (m.Marker == "AI") target.AiFills.Add(m.Text);
                else target.EditedContent = m.Text;
                continue; // 关联成功，不生成独立条目
            }

            // 找不到相近原笔记 → 独立成条（内容去掉标记前缀，Tag 置空；**必须带 RawLine**，
            // 否则删除/改行永远定位不到物理行 —— 2026-09-20 "同步来的笔记删不掉"的根因）
            entries.Add(new NoteEntry
            {
                Timestamp = m.Timestamp,
                Content = m.Text,
                SourceWindow = m.Marker == "AI" ? "AI 回填" : "手动编辑",
                Tag = null,
                RawLine = m.RawLine
            });
        }

        var q = entries.Where(e => !_deletedService.IsDeleted(e));
        if (dateFilter.HasValue)
            q = q.Where(e => TodoDisplayTime(e).Date == dateFilter.Value.Date);
        return q.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>暂存的标记行（第一遍收集、第二遍跨文件挂靠）。</summary>
    private sealed record DeferredMarker(string Marker, string Text, DateTime Timestamp, DateTime? RefTs, string RawLine);

    /// <summary>识别标记行：返回 "AI"/"Edit" 与内容、可选 (ref 时间戳)；普通行返回 null。ref 可写在内容尾部或来源区。</summary>
    private static string? ParseMarkerLine(string content, string source, out string text, out DateTime? refTs)
    {
        text = "";
        refTs = null;

        string marker;
        string body;
        if (content.StartsWith("【AI 释义】", StringComparison.Ordinal))
        {
            marker = "AI";
            body = content["【AI 释义】".Length..];
        }
        else if (content.StartsWith("【编辑】", StringComparison.Ordinal))
        {
            marker = "Edit";
            body = content["【编辑】".Length..];
        }
        else return null;

        text = body.Trim();

        // 解析 (ref yyyy-MM-dd HH:mm)：优先内容尾部，其次来源区
        var refMatch = Regex.Match($"{text} {source}", @"\(ref (\d{4}-\d{2}-\d{2} \d{2}:\d{2})\)");
        if (refMatch.Success && DateTime.TryParse(refMatch.Groups[1].Value, out var rt))
        {
            refTs = rt;
            // ref 写在内容尾部时，从展示内容中移除
            var idx = text.IndexOf($"(ref {refMatch.Groups[1].Value})", StringComparison.Ordinal);
            if (idx >= 0) text = text[..idx].TrimEnd();
        }
        return marker;
    }

    /// <summary>统计指定月份每天笔记数（含标签文件与当天灵感文件，排除软删除与标记行）</summary>
    public Dictionary<DateTime, int> LoadNoteCounts(int year, int month)
    {
        var counts = new Dictionary<DateTime, int>();
        if (!Directory.Exists(_settings.NotesPath)) return counts;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            // 旧格式行无日期：灵感文件按文件名日期归属，标签文件按"今天"归属（与 LoadNotes 一致）
            DateTime dateContext = DateTime.Today;
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("灵感_") && DateTime.TryParse(fileName["灵感_".Length..], out var fileDate))
                dateContext = fileDate;
            var tag = fileName.StartsWith("灵感_") ? null : fileName;

            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                foreach (var line in lines)
                {
                    var match = NoteLineRegex.Match(line);
                    if (!match.Success) continue;

                    var rawContent = match.Groups[3].Value.Replace("\u23CE", "\n");
                    // 标记行（AI 释义 / 编辑）不是独立笔记，不参与热力统计
                    if (rawContent.StartsWith("【AI 释义】") || rawContent.StartsWith("【编辑】")) continue;

                    var day = match.Groups[1].Success
                        ? match.Groups[1].Value.Trim()
                        : dateContext.ToString("yyyy-MM-dd");
                    if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) continue;
                    if (ts.Year != year || ts.Month != month) continue;

                    // 排除已软删除
                    var entry = new NoteEntry
                    {
                        Timestamp = ts,
                        Content = rawContent,
                        SourceWindow = match.Groups[4].Success ? match.Groups[4].Value : "",
                        Tag = tag
                    };
                    if (_deletedService.IsDeleted(entry)) continue;

                    // v3（2026-08-28）：待办行按 DueTime 归类记红点（日历热力与面板归类同口径）
                    DateTime? due = null;
                    if (rawContent.StartsWith("【待办】", StringComparison.Ordinal))
                    {
                        var attr = Regex.Match(rawContent, @"\(提醒: (\d{4}-\d{2}-\d{2} \d{2}:\d{2}(?::\d{2})?)(?:, 状态: (已办|已读))?\)");
                        if (attr.Success && DateTime.TryParse(attr.Groups[1].Value, out var d)) due = d;
                    }
                    var key = (due.HasValue ? due.Value.Date : ts.Date);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 日历统计失败 ({file}): {ex.Message}");
            }
        }
        return counts;
    }

    /// <summary>
    /// 统计指定月份「有未办待办（Open/Read）」的日期集合（按 DueTime 归类，排除软删除与已办）。
    /// v3.7：供日历热力图给未来有待办的日期画角标——只标未办，已办不再占用注意力。
    /// </summary>
    public HashSet<DateTime> LoadTodoDueDates(int year, int month)
    {
        var dates = new HashSet<DateTime>();
        if (!Directory.Exists(_settings.NotesPath)) return dates;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            var tag = Path.GetFileNameWithoutExtension(file);
            // 旧格式行无日期：灵感文件按文件名日期归属，标签文件按"今天"归属（与 LoadNoteCounts 一致）
            var dateContext = tag.StartsWith("灵感_")
                ? DateTime.TryParseExact(tag["灵感_".Length..], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var fd) ? fd : DateTime.Today
                : DateTime.Today;

            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                foreach (var line in lines)
                {
                    var match = NoteLineRegex.Match(line);
                    if (!match.Success) continue;

                    var rawContent = match.Groups[3].Value.Replace("\u23CE", "\n");
                    if (!rawContent.StartsWith("【待办】", StringComparison.Ordinal)) continue; // 只看待办行

                    var day = match.Groups[1].Success
                        ? match.Groups[1].Value.Trim()
                        : dateContext.ToString("yyyy-MM-dd");
                    if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) continue;

                    var attr = Regex.Match(rawContent, @"\(提醒: (\d{4}-\d{2}-\d{2} \d{2}:\d{2}(?::\d{2})?)(?:, 状态: (已办|已读))?\)");
                    if (!attr.Success || !DateTime.TryParse(attr.Groups[1].Value, out var due)) continue;
                    if (attr.Groups[2].Value == "已办") continue; // 已办不标角标
                    if (due.Year != year || due.Month != month) continue;

                    // 排除已软删除
                    var entry = new NoteEntry { Timestamp = ts, Content = rawContent };
                    if (_deletedService.IsDeleted(entry)) continue;

                    dates.Add(due.Date);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 待办日期统计失败 ({file}): {ex.Message}");
            }
        }
        return dates;
    }

    // ── 同步层行级读写扩展：只新增方法，不改现有方法 ──

    /// <summary>
    /// 遍历 NotesPath 下全部 .md，逐行解析（不合并标记行，同步层按纯行级处理），
    /// 返回 (相对路径, 原始行, 解析结果)。相对路径 = 文件名（双机路径一致 → 确定性 ID 一致）。
    /// </summary>
    public List<(string RelativePath, string Line, NoteEntry Entry)> ReadAllLines()
    {
        var result = new List<(string, string, NoteEntry)>();
        if (!Directory.Exists(_settings.NotesPath)) return result;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            var relativePath = Path.GetFileName(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            var tag = fileName.StartsWith("灵感_", StringComparison.Ordinal) ? null : fileName;

            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch { continue; }

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                var m = NoteLineRegex.Match(line);
                if (!m.Success) continue;

                var day = m.Groups[1].Success ? m.Groups[1].Value.Trim() : DateTime.Today.ToString("yyyy-MM-dd");
                if (!DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts)) continue;

                var entry = new NoteEntry
                {
                    Timestamp = ts,
                    Content = m.Groups[3].Value.Replace("\u23CE", "\n"),
                    SourceWindow = m.Groups[4].Success ? m.Groups[4].Value : "",
                    Tag = tag
                };
                result.Add((relativePath, line, entry));
            }
        }
        return result;
    }

    /// <summary>向指定文件追加一行（目录不存在自动创建）。line 必须是单行格式（含 \u23CE 转义）。共享 FileWriteLock 防并发覆盖。</summary>
    public void AppendLine(string relativePath, string line)
    {
        var safeName = Path.GetFileName(relativePath); // 防御路径穿越
        if (string.IsNullOrEmpty(safeName)) return;
        Directory.CreateDirectory(_settings.NotesPath);
        var filePath = Path.Combine(_settings.NotesPath, safeName);
        lock (FileWriteLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    /// <summary>从指定文件移除指定的行（按整行内容精确匹配），供"清空回收站后同步软删"与"冲突替换"用。共享 FileWriteLock。</summary>
    public void RemoveLines(string relativePath, HashSet<string> lineContents)
    {
        var safeName = Path.GetFileName(relativePath);
        if (string.IsNullOrEmpty(safeName)) return;
        var filePath = Path.Combine(_settings.NotesPath, safeName);
        if (!File.Exists(filePath)) return;

        lock (FileWriteLock)
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var keep = new List<string>(lines.Length);
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (lineContents.Contains(line)) continue;
                keep.Add(line);
            }
            File.WriteAllLines(filePath, keep, Encoding.UTF8);
        }
    }

    /// <summary>MD 行本地时间戳 → ISO 8601 UTC 字符串（同步层 CreatedAt/UpdatedAt 约定，分钟精度、DateTimeKind 处理）。</summary>
    public static string ToUtcIsoString(DateTime localTime)
        => DateTime.SpecifyKind(localTime, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ── 导入 / 区间 / 查找 扩展（2026-08-15 新增，灵感速览面板功能） ──

    /// <summary>
    /// 导入笔记：写入对应 MD 文件 + Content 去重（与目标日期已有笔记按 Content 完全相同去重，
    /// 重复项默认保留原笔记跳过导入）。调用者负责已勾选过滤。
    /// <b>目标日期权威（2026-10-01 用户拍板）：所有条目一律落到 targetDate</b>——
    /// 此前只有无时间戳条目跟 targetDate 走，解析层给旧格式行兜底填的「今天」和文件自带日期都会
    /// 绕过选定日期（导入永远落不到选的那天，2026-10-01 用户报告）。时分保留条目原值；
    /// 无时间戳条目按 targetDate + 当前时分起逐分钟递增。
    /// 全部处理后触发一次 NotesChanged。
    /// </summary>
    public int ImportNotes(List<NoteEntry> entries, DateTime targetDate)
    {
        if (entries == null || entries.Count == 0) return 0;

        // 收集目标日期所有 MD 文件（含 tag 文件）的现有 Content 用于去重
        var existingContents = LoadAllContents();

        var written = 0;
        var now = DateTime.Now;
        // 无时间戳条目按 targetDate.Date + 递增时分分配（避免同分钟冲突）
        var fallbackCursor = new DateTime(targetDate.Year, targetDate.Month, targetDate.Day, now.Hour, now.Minute, 0);

        try
        {
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Content)) continue;

                // 去重：Content 已存在则跳过
                var contentKey = entry.Content.Trim();
                if (existingContents.Contains(contentKey)) continue;

                // 重新分配日期：无时间戳条目 → targetDate + 递增时分；带时间戳条目 → 日期改到
                // targetDate、时分保留原值（秒位清零，落盘口径本就只有分钟）
                if (entry.Timestamp == DateTime.MinValue)
                {
                    entry.Timestamp = fallbackCursor;
                    fallbackCursor = fallbackCursor.AddMinutes(1);
                }
                else
                {
                    var tod = entry.Timestamp.TimeOfDay;
                    entry.Timestamp = targetDate.Date + new TimeSpan(tod.Hours, tod.Minutes, 0);
                }

                // 决定写入哪个 MD 文件：tag 文件 vs 当天灵感文件
                var fileName = !string.IsNullOrEmpty(entry.Tag)
                    ? $"{entry.Tag}.md"
                    : $"灵感_{entry.Timestamp:yyyy-MM-dd}.md";

                try
                {
                    AppendLine(fileName, entry.ToMarkdownLine());
                    existingContents.Add(contentKey);   // 防同一批次内重复
                    written++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FocusCapture] 导入写入失败 ({fileName}): {ex.Message}");
                }
            }
        }
        finally
        {
            if (written > 0) NotesChanged?.Invoke();
        }
        return written;
    }

    /// <summary>收集所有 MD 文件的现有 Content 集合（用于导入去重）</summary>
    private HashSet<string> LoadAllContents()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_settings.NotesPath)) return set;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            try
            {
                foreach (var rawLine in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var m = NoteLineRegex.Match(rawLine.TrimEnd('\r'));
                    if (!m.Success) continue;
                    var c = m.Groups[3].Value.Replace("\u23CE", "\n").Trim();
                    if (!string.IsNullOrEmpty(c)) set.Add(c);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 读取已有笔记失败 ({file}): {ex.Message}");
            }
        }
        return set;
    }

    /// <summary>加载所有笔记（v5 2026-09-20：统一走 <see cref="LoadEntriesCore"/> 两遍扫描 + 跨文件 ref 挂靠；
    /// 合并 AI 释义/编辑标记行），用于区间筛选和全局查找的基础集。</summary>
    public List<NoteEntry> LoadAllEntries()
        => LoadEntriesCore(null);

    /// <summary>区间加载：start..end（含两端）的所有笔记，按时间戳倒序。</summary>
    public List<NoteEntry> LoadNotesRange(DateTime start, DateTime end)
    {
        var s = start.Date;
        var e = end.Date;
        if (e < s) (s, e) = (e, s);   // 防御性交换

        return LoadAllEntries()
            .Where(x => x.Timestamp.Date >= s && x.Timestamp.Date <= e)
            .ToList();
    }

    /// <summary>全局关键字查找：Content / EditedContent / SourceWindow 任一包含 keyword（不区分大小写）。</summary>
    public List<NoteEntry> LoadNotesSearch(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<NoteEntry>();

        return LoadAllEntries()
            .Where(x =>
                x.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(x.EditedContent) && x.EditedContent.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                x.SourceWindow.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>查找内容完全相同的已有条目（添加前重复预检用）。
    /// 口径与 SaveNote 写入一致：剥离 #标签前缀 + Trim 后比较 Content；type 必须相同。
    /// 返回首个匹配条目（含其时间戳供弹窗展示），无匹配返回 null。</summary>
    public NoteEntry? FindDuplicate(string content, NoteType type)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var normalized = NormalizeContentForCompare(content);
        if (string.IsNullOrEmpty(normalized)) return null;
        foreach (var e in LoadAllEntries())
        {
            if (e.Type == type && string.Equals(e.Content?.Trim(), normalized, StringComparison.Ordinal))
                return e;
        }
        return null;
    }

    /// <summary>剥离 #标签前缀 + Trim（与 SaveNote 写入口径一致），供重复比较。</summary>
    private static string NormalizeContentForCompare(string content)
    {
        var tagMatch = Regex.Match(content, @"^#(\S+)");
        var c = tagMatch.Success ? content[tagMatch.Length..] : content;
        return c.Trim();
    }
}
