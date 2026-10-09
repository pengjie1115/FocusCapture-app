namespace FocusCapture.Services;

/// <summary>拖到悬浮球上的内容类型。</summary>
public enum DragPayloadKind
{
    /// <summary>读不出可用内容（只是窗口被划过，或格式不认识）。</summary>
    None,

    /// <summary>一段文字。</summary>
    Text,

    /// <summary>一批有磁盘路径的文件。</summary>
    Files,
}

/// <summary>
/// 一次拖放的解析结果。**不含任何 UI 与副作用** —— 纯数据，便于单测。
/// </summary>
/// <param name="Kind">内容类型。</param>
/// <param name="Text">文字内容（仅 <see cref="DragPayloadKind.Text"/> 有值）。</param>
/// <param name="Paths">文件路径列表（仅 <see cref="DragPayloadKind.Files"/> 有值）。</param>
/// <param name="SourceApp">识别出的来源程序显示名（如「微信」），识别不出为「拖动」。</param>
public sealed record DragPayload(
    DragPayloadKind Kind,
    string Text,
    IReadOnlyList<string> Paths,
    string SourceApp)
{
    public static readonly DragPayload Empty =
        new(DragPayloadKind.None, "", Array.Empty<string>(), DragDropSaveService.FallbackSourceApp);
}

/// <summary>
/// 悬浮球拖放保存的**判定与取值逻辑**。
/// 下面每条规则的取值口径都来自真机拖放探针的实测记录（微信 / 浏览器 / 文件管理器 / WPS 四类源都跑过）。
///
/// 本类刻意做成静态纯函数集合（除前台窗口查询外无 IO、无 UI），
/// 目的是让「判定顺序」「取值口径」「命名规则」这些**踩过坑的规则**能被检查点直接断言。
/// 这里每一条规则背后都有一次真机事故，改之前先读注释里的"为什么"。
/// </summary>
public static class DragDropSaveService
{
    /// <summary>识别不出来源程序时的兜底来源名。</summary>
    public const string FallbackSourceApp = "拖动";

    /// <summary>得到大脑标题的取字上限（**推定规则，未与用户确认**）。</summary>
    public const int GetNoteTitleMaxChars = 30;

    /// <summary>
    /// 可当文本读的扩展名（卡片「发到得到大脑」只对这类显示，用户拍板）。
    /// 用显式白名单而非"非二进制即文本"：猜错的后果是拿一堆乱码去建笔记。
    /// </summary>
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".text", ".log", ".json", ".xml", ".csv", ".tsv",
        ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".kt", ".cpp", ".cc", ".c", ".h", ".hpp",
        ".go", ".rs", ".php", ".rb", ".swift", ".m", ".mm", ".sql", ".yml", ".yaml", ".toml",
        ".ini", ".cfg", ".conf", ".properties", ".gradle", ".sh", ".bash", ".bat", ".cmd", ".ps1",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".srt", ".vtt",
    };

    // ══════════════════ 内容判定与取值 ══════════════════

    /// <summary>
    /// 解析拖放数据。**判定顺序不可颠倒**：
    /// ① 有 FileDrop 且路径非空 → 按【文件】
    /// ② 否则有 UnicodeText 且去空白后非空 → 按【文字】
    /// ③ 都没有 → <see cref="DragPayloadKind.None"/>
    ///
    /// <b>为什么 FileDrop 必须排在前面</b>：微信拖 xlsx 时，<c>Text</c> 格式里装的是**文件名**
    /// （实测值是 <c>JD-260911.xlsx</c>）。如果把"有文字就当文字"写在前面，
    /// 就会把文件名当成正文存成一条笔记 —— 用户看到的是莫名其妙的一条"笔记"。
    /// </summary>
    public static DragPayload Parse(IDataObject? data)
    {
        if (data == null) return DragPayload.Empty;

        var source = DescribeSourceApp();

        // ① 文件优先
        var paths = TryGetFileDrop(data);
        if (paths.Count > 0)
            return new DragPayload(DragPayloadKind.Files, "", paths, source);

        // ② 文字
        var text = TryGetUnicodeText(data);
        if (!string.IsNullOrWhiteSpace(text))
            return new DragPayload(DragPayloadKind.Text, text, Array.Empty<string>(), source);

        // ③ 读不到
        return new DragPayload(DragPayloadKind.None, "", Array.Empty<string>(), source);
    }

    /// <summary>取 FileDrop 的真实磁盘路径（过滤空项与重复项）。</summary>
    public static List<string> TryGetFileDrop(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
            if (data.GetData(DataFormats.FileDrop) is not string[] arr) return new List<string>();

            var list = new List<string>();
            foreach (var p in arr)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            }
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 取文字。**铁律：一律从 <see cref="DataFormats.UnicodeText"/> 取，绝不从 <see cref="DataFormats.Text"/> 取。**
    ///
    /// 实测证据（浏览器拖「应用凭证」四个字）：<c>Text</c>（CF_TEXT / ANSI）取到的是
    /// <c>搴旂敤鍑瘉</c> —— 乱码；<c>UnicodeText</c>（CF_UNICODETEXT）取到的才是 <c>应用凭证</c>。
    /// 同一份数据，Chromium 给的 CF_TEXT 是坏的（微信给的 CF_TEXT 恰好是好的，但**不能依赖**），
    /// 两者长度都不同（6 vs 4），说明在编码解码层面就已经错了。
    /// </summary>
    public static string TryGetUnicodeText(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.UnicodeText)) return "";
            return data.GetData(DataFormats.UnicodeText) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ══════════════════ 来源程序识别 ══════════════════

    /// <summary>
    /// 用前台窗口反查来源程序。**拖放期间返回的就是源程序**（探针四轮实测稳定：
    /// <c>Weixin.exe</c> / <c>Tabbit Browser.exe</c> / <c>360FileBrowser64.exe</c> / <c>wps.exe</c>）。
    /// 识别不出时返回 <see cref="FallbackSourceApp"/>。
    /// </summary>
    public static string DescribeSourceApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return FallbackSourceApp;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return FallbackSourceApp;

            using var proc = Process.GetProcessById((int)pid);
            return MapProcessName(proc.ProcessName);
        }
        catch
        {
            // 进程可能已退出（拖完就关窗），查不到不算异常，统一兜底
            return FallbackSourceApp;
        }
    }

    /// <summary>进程名 → 显示名（表外一律去掉 <c>.exe</c> 原样显示）。</summary>
    public static string MapProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return FallbackSourceApp;

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return name.ToLowerInvariant() switch
        {
            "weixin" or "wechat" => "微信",
            "tabbit browser" or "chrome" or "msedge" or "firefox" or "iexplore" => "浏览器",
            "360filebrowser64" or "360filebrowser" or "explorer" => "文件管理器",
            "wps" or "wpp" or "et" => "WPS",
            _ => name,
        };
    }

    // ══════════════════ 文件名与卡片头部 ══════════════════

    /// <summary>
    /// 生成用于显示/登记的**显示名**。
    ///
    /// 微信图片是唯一的特殊分支：它在 <c>temp\RWTemp\</c> 下**只有哈希名**（如
    /// <c>697a54b2….jpg</c>），实测 <c>FileNameW</c> / <c>FileName</c> / <c>FileDrop</c>
    /// 三者返回的是同一个哈希路径，原始名根本拿不到 → 只能自动生成
    /// <c>微信图片_yyyyMMdd_HHmmss.ext</c>。
    /// </summary>
    public static string BuildDisplayName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return name;

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        return IsHashLikeName(stem)
            ? $"微信图片_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
            : name;
    }

    /// <summary>
    /// 是否是"哈希名"（≥32 位纯十六进制）。用于识别微信图片那种拿不到原始名的临时文件。
    /// 用长度 + 字符集判定而不写死目录名：目录口径变过一次（RWTemp），写死会静默失效。
    /// </summary>
    public static bool IsHashLikeName(string? stem)
    {
        if (string.IsNullOrEmpty(stem) || stem.Length < 32) return false;
        foreach (var ch in stem)
        {
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    /// <summary>
    /// 卡片头部：单文件 → 真实文件名（微信图片为生成名）；多文件 → 「N 个文件」。
    /// 副行统一 <c>{大小} · 来自{来源程序}</c>（**不是**旧版的"已复制到本机" —— 新逻辑下拖入不复制）。
    /// </summary>
    public static (string Title, string Subtitle) BuildCardHeader(IReadOnlyList<string> paths, string sourceApp)
    {
        var source = string.IsNullOrWhiteSpace(sourceApp) ? FallbackSourceApp : sourceApp;

        if (paths.Count == 0) return ("", $"来自{source}");

        if (paths.Count == 1)
        {
            var p = paths[0];
            var size = TryGetSize(p);
            var sub = size.HasValue
                ? $"{FormatSize(size.Value)} · 来自{source}"
                : $"来自{source}";
            return (BuildDisplayName(p), sub);
        }

        long total = 0;
        var anySize = false;
        foreach (var p in paths)
        {
            var size = TryGetSize(p);
            if (size.HasValue) { total += size.Value; anySize = true; }
        }
        var subtitle = anySize ? $"{FormatSize(total)} · 来自{source}" : $"来自{source}";
        return ($"{paths.Count} 个文件", subtitle);
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>人类可读的大小（1.8 MB / 320 KB / 940 字节）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} 字节";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB";
    }

    // ══════════════════ 各动作的前置判定 ══════════════════

    /// <summary>这批文件里有没有**文本类**文件（决定卡片是否显示「发到得到大脑」）。</summary>
    public static bool HasAnyTextFile(IReadOnlyList<string> paths) => paths.Any(IsTextFile);

    /// <summary>可当文本读的扩展名（「仅文本类文件显示」，用户拍板）。</summary>
    public static bool IsTextFile(string path)
    {
        try { return TextExts.Contains(Path.GetExtension(path)); }
        catch { return false; }
    }

    // ── 图片直传（2026-10-01 用户拍板：拖图也出「发到得到大脑」，直接上传为图片笔记）──
    // 四种扩展名与得到大脑开放平台图片笔记支持的真实格式一致（jpg/png/gif/webp，官方 CLI 同口径）。

    /// <summary>可直传的图片扩展名。</summary>
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
    };

    /// <summary>这批文件里有没有**图片类**文件（与文本类并集决定卡片是否显示「发到得到大脑」）。</summary>
    public static bool HasAnyImageFile(IReadOnlyList<string> paths) => paths.Any(IsImageFile);

    /// <summary>图片类判定（扩展名白名单；真实格式校验在上传前的文件头检查，猜错会明确报错而不是传坏图）。</summary>
    public static bool IsImageFile(string path)
    {
        try { return ImageExts.Contains(Path.GetExtension(path)); }
        catch { return false; }
    }

    /// <summary>
    /// 从这批文件里挑出**当前还真实存在**的那些。
    ///
    /// 存在的意义：微信图片躺在 <c>temp\RWTemp\</c> 里，**微信随时会清**。
    /// 用户拖进来之后如果隔很久才点动作，文件可能已经不在 ——
    /// 那种情况下必须给出明确提示，不能静默失败（硬要求）。
    /// </summary>
    public static List<string> ExistingFiles(IReadOnlyList<string> paths) =>
        paths.Where(p => { try { return File.Exists(p); } catch { return false; } }).ToList();

    /// <summary>"文件已不在"的提示文案（实施时定稿）。</summary>
    public static string BuildMissingFileMessage(IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return "";
        if (missing.Count == 1)
            return $"文件已不在：{Path.GetFileName(missing[0])}\n\n" +
                   "源文件可能已被来源程序清理（微信的临时图片会被定期清空）。请重新拖入一次。";

        return $"有 {missing.Count} 个文件已不在：\n" +
               string.Join("\n", missing.Take(5).Select(p => "· " + Path.GetFileName(p))) +
               (missing.Count > 5 ? $"\n…（共 {missing.Count} 个）" : "") +
               "\n\n源文件可能已被来源程序清理，请重新拖入一次。";
    }

    /// <summary>
    /// 得到大脑的标题策略（**推定规则，未与用户确认**）：取正文首行前 30 字；无换行则取前 30 字。
    /// 首行为空则往下找到第一个非空行 —— 空标题建不出笔记。
    /// </summary>
    public static string MakeGetNoteTitle(string text)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = normalized
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";

        return firstLine.Length <= GetNoteTitleMaxChars
            ? firstLine
            : firstLine[..GetNoteTitleMaxChars];
    }

    /// <summary>文本内容上限（字符）。一次拖入把几十 MB 日志塞进云端笔记既慢又没意义。</summary>
    public const int MaxTextChars = 100_000;

    /// <summary>
    /// 严格按 UTF-8 读取文本文件（BOM 自动剥离）。**不做编码嗅探**，与本项目既有口径一致
    /// （ChatAttachmentService.ExtractPlainText）：宁可能力边界清晰，也不猜错编码把乱码发出去。
    /// 返回 Error 非空时调用方必须提示用户，不能静默传空内容。
    /// </summary>
    public static (string Text, string? Error) ReadTextFileStrict(string path, int maxChars = MaxTextChars)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            string text;
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                text = strict.GetString(StripBom(bytes));
            }
            catch (DecoderFallbackException)
            {
                return ("", "不是 UTF-8 编码的文本（可能是 ANSI/GBK）。没有自动猜编码 —— 猜错会把乱码传上云端。");
            }

            if (text.Length > maxChars)
                text = text[..maxChars] + "\n\n（内容过长，已截断）";

            return (text, null);
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    // ── Win32 ──

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
