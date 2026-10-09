using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 把笔记/待办导出成本机文件（写，需确认）。2026-09-17 新增。
///
/// 落点 = 设置里的「默认导出文件夹」（<see cref="AppSettings.ExportFolderPath"/>），与界面导出同一处，
/// **不进网盘**（用户明确要求：导出就是本机拿一份，不要外流）。
///
/// 未设置过导出文件夹时**不弹文件夹选择框** —— 工具跑在后台线程，弹框会打断对话；
/// 改为明确告诉用户去界面上选一次。
///
/// 2026-09-17 二次调整（用户反馈「只能导整天，包容度不够」）：原来只有日期/区间一条路，
/// 用户说「只导那两条」时 AI 只能整段导出（夹带当天其它内容）或宣告做不到。
/// 现补两条精选路 —— <c>ref_times</c>（按时间戳精确指定）与 <c>query</c>（按关键词筛选），
/// 底层直接复用既有的 <see cref="NoteService.LoadAllEntries"/> 与 <see cref="NoteService.LoadNotesSearch"/>，
/// 没有新造检索能力。
/// </summary>
public class ExportNotesTool : AgentTool
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;

    public ExportNotesTool(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
    }

    public override string Name => "export_notes";

    public override string Description =>
        "把笔记/待办导出成本机文件（Markdown / 文本 / Word / JSON）。" +
        "用户说「把今天记的导出来」「导一份给我」「只要那两条」时使用。三种用法（优先级从高到低）：\n" +
        "1) **ref_times** —— 精选导出：传 list_notes_by_date / search_notes 输出中方括号里的时间戳（yyyy-MM-dd HH:mm），" +
        "只导这几条，不夹带当天其它内容。用户说「只导某几条」「我只要这两条」时必须用这个，不要整段导。\n" +
        "   注意：条目时间戳**只精确到分钟**（同一分钟记的多条时间戳完全相同）。若某一项报「命中了 N 条」，" +
        "把该项改成 {\"time\":\"时间戳\",\"hint\":\"内容关键词\"} 再试即可消歧。\n" +
        "2) **query** —— 按关键词筛选导出（匹配内容/编辑内容/来源窗口）；配 date/区间 就是「那天含某关键词的几条」。\n" +
        "3) **date / from_date+to_date** —— 该日期/区间的**全部**记录（不填默认今天）。\n" +
        "文件存到用户设置好的默认导出文件夹，**不联网、不上传网盘**。\n" +
        "若返回「还没设置导出文件夹」，请让用户先在界面上做一次导出（那里会让他选文件夹），之后就能直接导了。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"ref_times":{"type":"array","description":"可选，精选导出：要导出的条目。每项可以是时间戳字符串 \"yyyy-MM-dd HH:mm\"（取自列表/搜索输出中方括号），或对象 {\"time\":\"yyyy-MM-dd HH:mm\",\"hint\":\"内容关键词\"}（同一分钟多条时用 hint 消歧）。给了它就只导这几条，忽略 query 与日期区间"},"query":{"type":"string","description":"可选，按关键词筛选导出（匹配内容/编辑内容/来源窗口，不区分大小写）。不配日期则在全部记录里找"},"date":{"type":"string","description":"可选，某一天 yyyy-MM-dd；不填且未给区间时默认今天"},"from_date":{"type":"string","description":"可选，起始日 yyyy-MM-dd（与 to_date 配对）"},"to_date":{"type":"string","description":"可选，结束日 yyyy-MM-dd"},"format":{"type":"string","description":"可选：md（默认）/ txt / docx / json"}},"required":[]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "format", out var fmt);
        var f = string.IsNullOrWhiteSpace(fmt) ? "md" : fmt.Trim().ToLowerInvariant();

        if (TryReadRefTimes(argumentsJson, out var refs, out _) && refs.Count > 0)
            return $"把指定的 {refs.Count} 条记录导出为 {f} 文件（存到本机导出文件夹）";

        if (ToolArgs.TryGetString(argumentsJson, "query", out var q))
            return $"把含「{q}」的记录导出为 {f} 文件（存到本机导出文件夹）";

        var (start, end, _) = ResolveRange(argumentsJson);
        var scope = start == end
            ? $"{start:yyyy-MM-dd} 的记录"
            : $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 的记录";
        return $"把{scope}导出为 {f} 文件（存到本机导出文件夹）";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
            return "错误：还没有设置导出文件夹。请让用户先打开「灵感速览 → 导出」选一次文件夹，" +
                   "之后就能直接导出了（工具不会自己弹文件夹选择框，那会打断对话）。";

        var formatStr = "md";
        if (ToolArgs.TryGetString(argumentsJson, "format", out var fmtRaw)) formatStr = fmtRaw.Trim().ToLowerInvariant();

        ExportFormat? format = formatStr switch
        {
            "md" or "markdown" => ExportFormat.Markdown,
            "txt" or "text" => ExportFormat.Txt,
            "docx" or "word" => ExportFormat.Word,
            "json" => ExportFormat.Json,
            _ => null,
        };
        if (format == null)
            return $"错误：format「{formatStr}」不支持，可用：md / txt / docx / json。";

        var hasRefTimes = TryReadRefTimes(argumentsJson, out var refTimes, out var refParseError);
        if (refParseError != null) return refParseError;

        ToolArgs.TryGetString(argumentsJson, "query", out var queryRaw);
        var query = (queryRaw ?? "").Trim();
        var hasQuery = query.Length > 0;

        List<NoteEntry> entries;
        string baseName;
        string scopeText;

        if (hasRefTimes)
        {
            var (picked, error) = await PickByRefTimesAsync(refTimes, ct);
            if (error != null) return error;

            var d0 = picked[0].Timestamp.Date;
            var d1 = picked[^1].Timestamp.Date;
            entries = picked;
            baseName = $"灵感_精选{picked.Count}条_{d0:yyyy-MM-dd}" + (d0 == d1 ? "" : $"_{d1:yyyy-MM-dd}");
            scopeText = $"精选 {picked.Count} 条，{d0:yyyy-MM-dd}{(d0 == d1 ? "" : $" 至 {d1:yyyy-MM-dd}")}";
        }
        else if (hasQuery)
        {
            var hasRange = HasExplicitRange(argumentsJson);
            var (start, end, rangeError) = ResolveRange(argumentsJson);
            if (rangeError != null) return rangeError;

            entries = await Task.Run(() =>
            {
                // 有日期：在该区间内按关键词过滤（与 list_notes_by_date 同一口径，都是 Timestamp.Date）。
                // 无日期：全库按关键词找（复用既有 LoadNotesSearch，不另写一套匹配规则）。
                if (hasRange)
                {
                    return _noteService.LoadNotesRange(start, end)
                        .Where(e => MatchesQuery(e, query))
                        .OrderBy(e => e.Timestamp)
                        .ToList();
                }
                return _noteService.LoadNotesSearch(query).OrderBy(e => e.Timestamp).ToList();
            }, ct);

            if (entries.Count == 0)
                return $"没有找到含「{query}」的记录{(hasRange ? $"（{start:yyyy-MM-dd}{(start == end ? "" : $" 至 {end:yyyy-MM-dd}")}）" : "")}，无需导出。";

            var q0 = entries[0].Timestamp.Date;
            var q1 = entries[^1].Timestamp.Date;
            var shortQuery = query.Length > 16 ? query[..16] : query;
            baseName = $"灵感_{q0:yyyy-MM-dd}" + (q0 == q1 ? "" : $"_{q1:yyyy-MM-dd}") + $"_含{shortQuery}";
            scopeText = $"含「{query}」的 {entries.Count} 条";
        }
        else
        {
            var (start, end, rangeError) = ResolveRange(argumentsJson);
            if (rangeError != null) return rangeError;

            entries = await Task.Run(() => _noteService.LoadNotesRange(start, end), ct).ConfigureAwait(false);
            if (entries.Count == 0)
                return start == end
                    ? $"{start:yyyy-MM-dd} 没有记录，无需导出。"
                    : $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 没有记录，无需导出。";

            baseName = start == end
                ? $"灵感_{start:yyyy-MM-dd}"
                : $"灵感_{start:yyyy-MM-dd}_{end:yyyy-MM-dd}";
            scopeText = $"{start:yyyy-MM-dd}{(start == end ? "" : $" 至 {end:yyyy-MM-dd}")}";
        }

        var svc = new NoteExportService();
        var config = new ExportConfig
        {
            IncludeTime = true,
            IncludeSource = true,
            IncludeTag = true,
            IncludeContent = true,
            Format = format.Value,
        };

        var fileName = NoteExportService.SanitizeFileName(baseName) + svc.GetFileExtension(format.Value);

        try
        {
            Directory.CreateDirectory(_settings.ExportFolderPath);
            var path = NoteExportService.GetUniquePath(Path.Combine(_settings.ExportFolderPath, fileName));

            if (format.Value == ExportFormat.Word)
                File.WriteAllBytes(path, svc.BuildWord(entries, config));
            else
                File.WriteAllText(path, svc.BuildExport(entries, config), new UTF8Encoding(false));

            var size = RootMigrationService.FormatSize(new FileInfo(path).Length);
            return $"已导出 {entries.Count} 条（{scopeText}）到：{path}（{size}）。" +
                   "文件在本机，未上传网盘。";
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", "导出笔记失败", ex);
            return "错误：导出失败 —— " + ex.Message;
        }
    }

    /// <summary>ref_times 的一项：时间戳 + 可选内容关键词（同一分钟多条时用于消歧）。</summary>
    private sealed record NoteRef(DateTime Time, string Hint);

    /// <summary>
    /// 读取 ref_times（同时也接受逗号分隔的单个字符串，模型两种写法都会给）。
    /// 元素支持两种形态：
    ///   "2026-09-01 09:10"                           —— 纯时间戳
    ///   {"time":"2026-09-01 09:10","hint":"关键词"}   —— 带消歧关键词
    /// 为什么必须有第二种：条目落盘的时间戳**只到分钟**（<see cref="NoteEntry.ToMarkdownLine"/> 用
    /// yyyy-MM-dd HH:mm 写盘），同一分钟记的多条在时间戳上完全一样 —— 没有内容线索就只能报错。
    /// 返回 false = 参数没给；error 非空 = 给了但用不了。
    /// </summary>
    private static bool TryReadRefTimes(string json, out List<NoteRef> refs, out string? error)
    {
        refs = new List<NoteRef>();
        error = null;

        var rawItems = new List<(string Time, string Hint)>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("ref_times", out var prop))
                return false;

            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        rawItems.Add(((item.GetString() ?? "").Trim(), ""));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var t = item.TryGetProperty("time", out var te) && te.ValueKind == JsonValueKind.String
                            ? (te.GetString() ?? "").Trim() : "";
                        var h = item.TryGetProperty("hint", out var he) && he.ValueKind == JsonValueKind.String
                            ? (he.GetString() ?? "").Trim() : "";
                        rawItems.Add((t, h));
                    }
                    else
                    {
                        rawItems.Add(("", ""));
                    }
                }
            }
            else if (prop.ValueKind == JsonValueKind.String)
            {
                // 兜底：模型偶尔给 "a,b" 这种复合字符串
                foreach (var part in (prop.GetString() ?? "").Split(
                             new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Trim().Length > 0) rawItems.Add((part.Trim(), ""));
                }
            }
            else
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var (timeStr, hint) in rawItems)
        {
            if (timeStr.Length == 0)
            {
                error = "错误：ref_times 里有一项缺少时间戳。每项应为 \"yyyy-MM-dd HH:mm\" 或 {\"time\":\"yyyy-MM-dd HH:mm\",\"hint\":\"内容关键词\"}。";
                return true;
            }

            if (!DateTime.TryParseExact(timeStr, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var want))
            {
                error = $"错误：ref_times 里的「{timeStr}」格式不对，应为 yyyy-MM-dd HH:mm（取自列表输出方括号）。";
                return true;
            }

            refs.Add(new NoteRef(want, hint));
        }

        return refs.Count > 0;
    }

    /// <summary>
    /// 按 ref_times 精确挑条目。返回 (命中列表, 错误消息)。
    /// **错误消息非空时一个文件都不写** —— 精选导出要么全对、要么不动，避免用户拿到半份还得自己去核。
    /// 命中 0 条 / 命中多条都会报错并要求模型先核实；多条时给出候选项与**可执行的下一步**（补 hint）。
    /// </summary>
    private async Task<(List<NoteEntry> Picked, string? Error)> PickByRefTimesAsync(
        List<NoteRef> refs, CancellationToken ct)
    {
        var all = await Task.Run(() => _noteService.LoadAllEntries(), ct).ConfigureAwait(false);

        var picked = new List<NoteEntry>();
        var problems = new List<string>();

        foreach (var r in refs)
        {
            var key = r.Time.ToString("yyyy-MM-dd HH:mm");
            var hits = all.Where(e => e.Timestamp.ToString("yyyy-MM-dd HH:mm") == key).ToList();

            if (r.Hint.Length > 0)
                hits = hits.Where(e => SearchNotesTool.FormatContent(e).Contains(r.Hint, StringComparison.OrdinalIgnoreCase))
                           .ToList();

            if (hits.Count == 0)
            {
                problems.Add(r.Hint.Length == 0
                    ? $"{key} 找不到对应条目"
                    : $"{key} 里没有内容含「{r.Hint}」的条目");
            }
            else if (hits.Count > 1)
            {
                var listed = string.Join("\n", hits.Take(5).Select(h => "  - " + SearchNotesTool.FormatContent(h)));
                problems.Add($"{key} 命中了 {hits.Count} 条，无法确定是哪一条：\n" + listed +
                             $"\n  → 该分钟有多条，请把这一项改成 {{\"time\":\"{key}\",\"hint\":\"内容关键词\"}} 再试，" +
                             "或改用 query 参数按内容导出");
            }
            else
            {
                if (!picked.Any(p => p.Timestamp == hits[0].Timestamp && p.Content == hits[0].Content))
                    picked.Add(hits[0]);
            }
        }

        if (problems.Count > 0)
            return (picked,
                "错误：精选导出未执行（没有写任何文件）——\n- " + string.Join("\n- ", problems) +
                "\n请先用 list_notes_by_date / search_notes 核实时间戳后再试。");

        if (picked.Count == 0)
            return (picked, "错误：ref_times 里没有有效的条目，未执行导出。");

        picked = picked.OrderBy(e => e.Timestamp).ToList();
        return (picked, null);
    }

    /// <summary>关键词匹配口径与 NoteService.LoadNotesSearch 完全一致（内容 / 编辑内容 / 来源窗口）。</summary>
    private static bool MatchesQuery(NoteEntry e, string query) =>
        e.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(e.EditedContent) && e.EditedContent.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        e.SourceWindow.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>模型是否显式给了日期参数（用于区分「今天」是默认值还是用户本意）。</summary>
    private static bool HasExplicitRange(string argumentsJson) =>
        ToolArgs.TryGetString(argumentsJson, "date", out _) ||
        ToolArgs.TryGetString(argumentsJson, "from_date", out _) ||
        ToolArgs.TryGetString(argumentsJson, "to_date", out _);

    /// <summary>解析导出范围；返回 (起, 止, 错误消息)。错误消息非空时调用方直接返回它。</summary>
    private static (DateTime Start, DateTime End, string? Error) ResolveRange(string argumentsJson)
    {
        DateTime from = default, to = default;
        var hasFrom = ToolArgs.TryGetString(argumentsJson, "from_date", out var fromStr)
                      && DateTime.TryParse(fromStr, out from);
        var hasTo = ToolArgs.TryGetString(argumentsJson, "to_date", out var toStr)
                    && DateTime.TryParse(toStr, out to);

        if (hasFrom || hasTo)
        {
            var start = hasFrom ? from.Date : (hasTo ? to.Date : DateTime.Today);
            var end = hasTo ? to.Date : (hasFrom ? from.Date : DateTime.Today);
            if (end < start) (start, end) = (end, start);
            return (start, end, null);
        }

        if (ToolArgs.TryGetString(argumentsJson, "date", out var dateStr))
        {
            if (!DateTime.TryParse(dateStr, out var d))
                return (DateTime.Today, DateTime.Today, $"错误：date「{dateStr}」格式不对，请用 yyyy-MM-dd。");
            return (d.Date, d.Date, null);
        }

        return (DateTime.Today, DateTime.Today, null);
    }
}
