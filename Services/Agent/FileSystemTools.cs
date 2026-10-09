using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.Agent;

/// <summary>路径校验结果。SuggestDir = 该路径所属目录（加白弹窗用它，让用户知道授权的是什么范围）。</summary>
public record FsGuardResult(bool Ok, string FullPath, string Error, string SuggestDir);

/// <summary>
/// 本地文件工具组（2026-10-01 新增，授权目录制）。
///
/// <para>
/// <b>这是对 B-16 红线①「路径参数绝不让 AI 生成」的一次有意且受控的突破</b> ——
/// 既有工具（FileTools/DocumentTools）的可达范围由用户亲手签发的牌号决定；本组工具相反，
/// 模型直接给路径，边界由三道闸守住：
/// </para>
/// <list type="number">
/// <item><b>授权目录白名单</b>（AppSettings.AiAllowedDirs，规范化绝对路径前缀校验，防 ../ 逃逸）；
///       默认空 = 没有任何目录可访问。总开关 AiFsToolsEnabled 默认关，关着时这组工具根本不注册、模型看不见。</item>
/// <item><b>白名单外访问自动弹窗</b>（用户需求：设置手动添加之外的第二路添加方式）——
///       经注入的 <c>askAllowDir</c> 委托弹确认，**必须经 UiThread 封送**（B-16 红线④：工具体跑在线程池线程上），
///       用户拒绝即取消；弹窗由 <see cref="FsGuard"/> 触发，独立于写确认弹窗开关，**不可被设置关掉**。</item>
/// <item><b>删除一律进 Windows 回收站</b>（SHFileOperation + FOF_ALLOWUNDO），绝不物理删除；
///       覆盖写先自动备份 .bak —— 回收站只兜「删除」，兜不了「覆盖」，备份补的就是这个洞。</item>
/// </list>
///
/// <para><b>隐私前提（红线 12 的边界）</b>：read_file 读到的内容会进入对话上下文、随请求发给大模型提供商 ——
/// 该边界已在设置页文案与本工具描述中写明，由用户授权目录的动作背书。</para>
/// </summary>
public static class FsGuard
{
    public const int MaxReadBytes = 1_000_000;   // read_file 上限 1MB
    public const int MaxListEntries = 1000;      // list_dir 单次条目上限

    /// <summary>
    /// 把路径规范化并校验是否落在授权目录内。
    /// 路径本身非法 / 网络路径 → Error 给出具体原因（**不可加白**，调用方直接返回）；
    /// 白名单命中 → Ok=true，SuggestDir=命中目录；
    /// 未命中 → Ok=false、Error 为空、SuggestDir=该路径所属目录（调用方走「弹窗加白」流程）。
    /// 存在性校验不在 Check 里 —— 路径可能还没创建（write_file），由各工具执行时自查。
    /// </summary>
    public static FsGuardResult Check(string path, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FsGuardResult(false, "", "错误：缺少路径参数。", "");

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex)
        {
            return new FsGuardResult(false, "", $"错误：路径「{path}」无法解析（{ex.Message}）。", "");
        }

        // UNC / 网络盘：删除进不了回收站（会变物理删除），读写也常出怪问题 —— 一律拒绝
        if (full.StartsWith(@"\\"))
            return new FsGuardResult(false, full, "错误：网络路径（UNC）不支持，AI 文件工具只作用于本机磁盘。", "");
        try
        {
            var root = Path.GetPathRoot(full);
            if (root != null && new DriveInfo(root).DriveType == DriveType.Network)
                return new FsGuardResult(false, full, "错误：网络驱动器不支持，AI 文件工具只作用于本机磁盘。", "");
        }
        catch { /* 盘符取不到时交给白名单校验 */ }

        var dirs = settings.AiAllowedDirs ?? new List<string>();
        foreach (var raw in dirs)
        {
            string dir;
            try { dir = Path.GetFullPath(raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { continue; }
            if (IsInside(full, dir))
                return new FsGuardResult(true, full, "", dir);
        }

        var suggest = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
        return new FsGuardResult(false, full, "", suggest);
    }

    /// <summary>full 是否等于 dir 或位于 dir 之下（分隔符边界对齐，防 "C:\a" 放行 "C:\ab"）。</summary>
    public static bool IsInside(string full, string dir) =>
        string.Equals(full, dir, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>把目录加入白名单并持久化（去重、规范化）。</summary>
    public static void AllowDir(string dir, AppSettings settings)
    {
        var full = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        settings.AiAllowedDirs ??= new List<string>();
        if (!settings.AiAllowedDirs.Any(d => string.Equals(
                Path.GetFullPath(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                full, StringComparison.OrdinalIgnoreCase)))
            settings.AiAllowedDirs.Add(full);
        settings.Save();
        AppLog.Info("Agent", $"AI 文件访问白名单已加入目录：{full}");
    }

    /// <summary>
    /// 路径门禁（从 FsToolBase.ExecuteAsync 抽出的公共流程，文档工具的 path 来源同走这一道，
    /// 2026-10-01）：FsGuard 校验 → 白名单外弹窗问用户（拒绝即取消）→ 加白后复查。
    /// 返回 Guard = 通过后的校验结果；Error 非空 = 直接返回给模型的错误文案。
    /// </summary>
    public static async Task<(FsGuardResult Guard, string? Error)> GateAsync(
        string path, AppSettings settings, Func<string, Task<bool>> askAllowDir)
    {
        var guard = Check(path, settings);
        if (!guard.Ok && guard.Error.Length > 0) return (guard, guard.Error);   // 路径非法 / 网络路径：弹窗无意义

        if (!guard.Ok)
        {
            // 目录不在白名单：按用户拍板的第二路添加方式，弹窗问是否加入（拒绝即取消，绝不绕过）
            bool allowed;
            try { allowed = await askAllowDir(guard.SuggestDir).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Agent", "目录授权弹窗异常（按拒绝处理）：" + ex.Message); allowed = false; }
            if (!allowed)
                return (guard, $"错误：用户拒绝将「{guard.SuggestDir}」加入 AI 文件访问白名单，操作已取消。不要对同一目录反复请求。");
            AllowDir(guard.SuggestDir, settings);
            guard = Check(path, settings);
            if (!guard.Ok)
                return (guard, $"错误：目录「{guard.SuggestDir}」加入白名单后仍未通过校验，操作取消。");
        }

        return (guard, null);
    }
}

    /// <summary>本组工具共用骨架：参数取路径 → FsGuard 校验 → 白名单外弹窗问用户 → 校验通过后执行。</summary>
public abstract class FsToolBase : AgentTool
{
    protected readonly AppSettings Settings;
    /// <summary>白名单外访问的授权确认（生产实现经 UiThread.AskAsync 弹窗 + FsGuard.AllowDir 落盘）。</summary>
    protected readonly Func<string, Task<bool>> AskAllowDir;

    protected FsToolBase(AppSettings settings, Func<string, Task<bool>> askAllowDir)
    {
        Settings = settings;
        AskAllowDir = askAllowDir;
    }

    /// <summary>校验 + 弹窗加白之后的执行体。</summary>
    protected abstract Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct);

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "path", out var path))
            return "错误：缺少参数 path。";

        var (guard, gateError) = await FsGuard.GateAsync(path, Settings, AskAllowDir).ConfigureAwait(false);
        if (gateError != null) return gateError;

        return await RunAsync(guard, argumentsJson, ct).ConfigureAwait(false);
    }

    protected static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / 1024.0 / 1024.0:F1} MB";
}

// ══════════════════ 1. list_dir ══════════════════

public class ListDirTool : FsToolBase
{
    public ListDirTool(AppSettings settings, Func<string, Task<bool>> askAllowDir)
        : base(settings, askAllowDir) { }

    public override string Name => "list_dir";
    public override bool IsReadOnly => true;

    public override string Description =>
        "列出本机目录内容（授权目录制：只能访问用户授权过的目录；首次访问新目录会弹窗请用户确认）。" +
        "返回名称、类型、大小、修改时间。用户说「看看 D:\\工作 里有什么」「这个文件夹下有哪些文件」时使用。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"path":{"type":"string","description":"目录完整路径"},"depth":{"type":"number","description":"可选，列几层，默认 1，最大 3"},"limit":{"type":"number","description":"可选，最多返回几条，默认 200"}},"required":["path"]}""";

    public override string DescribeAction(string argumentsJson) =>
        ToolArgs.TryGetString(argumentsJson, "path", out var p) ? $"列出目录 {p}" : Name;

    protected override async Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct)
    {
        ToolArgs.TryGetInt(argumentsJson, "depth", out var depth);
        depth = Math.Clamp(depth is < 1 or 0 ? 1 : depth, 1, 3);
        ToolArgs.TryGetInt(argumentsJson, "limit", out var limit);
        limit = limit is < 1 or 0 ? 200 : Math.Min(limit, FsGuard.MaxListEntries);

        return await Task.Run(() =>
        {
            var di = new DirectoryInfo(guard.FullPath);
            if (!di.Exists) return $"错误：目录不存在：{guard.FullPath}";

            var lines = new List<string>();
            var truncated = false;
            void Walk(DirectoryInfo dir, int level)
            {
                if (lines.Count >= limit) { truncated = true; return; }
                try
                {
                    foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (lines.Count >= limit) { truncated = true; return; }
                        lines.Add($"{Indent(level)}[目录] {sub.Name}");
                        if (level + 1 < depth) Walk(sub, level + 1);
                    }
                    foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (lines.Count >= limit) { truncated = true; return; }
                        lines.Add($"{Indent(level)}{f.Name}（{FormatSize(f.Length)}，改于 {f.LastWriteTime:yyyy-MM-dd HH:mm}）");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    lines.Add($"{Indent(level)}（无权限访问，已跳过）");
                }
            }
            static string Indent(int level) => level <= 0 ? "" : new string(' ', level * 4);

            Walk(di, 0);
            if (lines.Count == 0) return $"目录 {guard.FullPath} 是空的。";
            return $"目录 {guard.FullPath} 内容（{lines.Count} 条{(truncated ? "，已截断 —— 可用更深的子路径缩小范围" : "")}）：\n"
                   + string.Join("\n", lines);
        }).ConfigureAwait(false);
    }
}

// ══════════════════ 2. read_file ══════════════════

public class ReadFileTool : FsToolBase
{
    public ReadFileTool(AppSettings settings, Func<string, Task<bool>> askAllowDir)
        : base(settings, askAllowDir) { }

    public override string Name => "read_file";
    public override bool IsReadOnly => true;

    public override string Description =>
        "读取本机文本文件内容（授权目录制；上限 1MB）。二进制文件（图片/压缩包/exe 等）拒绝读取，只报基本信息。" +
        "⚠ 读到的内容会随对话发送给大模型服务 —— 不要让用户在不知情的情况下读取含密码/密钥的文件，" +
        "此类文件应先提醒用户并征得同意。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"path":{"type":"string","description":"文件完整路径"}},"required":["path"]}""";

    public override string DescribeAction(string argumentsJson) =>
        ToolArgs.TryGetString(argumentsJson, "path", out var p) ? $"读取文件 {p}" : Name;

    protected override async Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var fi = new FileInfo(guard.FullPath);
            if (!fi.Exists) return $"错误：文件不存在：{guard.FullPath}";

            // 二进制探测：前 8KB 出现 NUL 字节视为二进制，只报元信息不读内容
            using (var fs = fi.OpenRead())
            {
                Span<byte> probe = stackalloc byte[8192];
                var n = fs.Read(probe);
                if (probe.Slice(0, n).Contains((byte)0))
                    return $"错误：{guard.FullPath} 是二进制文件（{FormatSize(fi.Length)}），无法按文本读取。" +
                           "图片可用「添加附件」让用户发进对话；压缩包/程序等请说明只支持列目录与基本元信息。";
            }

            if (fi.Length > FsGuard.MaxReadBytes)
                return $"错误：文件 {FormatSize(fi.Length)}，超过 1MB 上限。请让用户拆分文件，或只让 AI 处理其中较小的部分。";

            var content = File.ReadAllText(guard.FullPath, Encoding.UTF8);
            AppLog.Info("Agent", $"read_file：{guard.FullPath}（{content.Length} 字符）");
            return $"文件 {guard.FullPath}（{FormatSize(fi.Length)}）：\n{content}";
        }).ConfigureAwait(false);
    }
}

// ══════════════════ 3. write_file ══════════════════

public class WriteFileTool : FsToolBase
{
    public WriteFileTool(AppSettings settings, Func<string, Task<bool>> askAllowDir)
        : base(settings, askAllowDir) { }

    public override string Name => "write_file";
    public override bool IsReadOnly => false;

    public override string Description =>
        "写入本机文本文件（授权目录制）。mode=\"new\"（默认）只能新建，文件已存在则报错；" +
        "mode=\"overwrite\" 覆盖已有文件（**覆盖前自动备份原文件为 .bak-时间戳**）；mode=\"append\" 在文件末尾追加。" +
        "只支持文本，不支持二进制。目录不存在会自动创建。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"path":{"type":"string","description":"文件完整路径"},"content":{"type":"string","description":"要写入的文本内容"},"mode":{"type":"string","enum":["new","overwrite","append"],"description":"new=新建（默认，已存在报错）；overwrite=覆盖（自动备份 .bak）；append=追加"}},"required":["path","content"]}""";

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "path", out var p);
        ToolArgs.TryGetString(argumentsJson, "mode", out var mode);
        return mode switch
        {
            "overwrite" => $"覆盖文件 {p}（原文件自动备份为 .bak）",
            "append" => $"追加内容到 {p}",
            _ => $"新建文件 {p}",
        };
    }

    protected override async Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "content", out var content))
            return "错误：缺少参数 content。";
        ToolArgs.TryGetString(argumentsJson, "mode", out var mode);
        mode = mode is "overwrite" or "append" ? mode : "new";

        return await Task.Run(() =>
        {
            var full = guard.FullPath;
            var exists = File.Exists(full);

            string? backupPath = null;
            if (mode == "overwrite" && exists)
            {
                // 回收站只兜删除、兜不了覆盖 —— 原文件先备份（AppSettings.NotesPath 之外的独立保险）
                backupPath = $"{full}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(full, backupPath, overwrite: false);
            }
            if (mode == "new" && exists)
                return $"错误：{full} 已存在。要覆盖请明确说明并改用 mode=\"overwrite\"（会先自动备份原文件），或改用 append 追加。";

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            switch (mode)
            {
                case "append":
                    File.AppendAllText(full, content, Encoding.UTF8);
                    break;
                case "overwrite":
                    File.WriteAllText(full, content, Encoding.UTF8);
                    break;
                default:
                    File.WriteAllText(full, content, Encoding.UTF8);
                    break;
            }

            AppLog.Info("Agent", $"write_file：{mode} {full}（{content.Length} 字符" +
                       (backupPath != null ? $"，原文件已备份 {backupPath}" : "") + "）");
            return backupPath != null
                ? $"已覆盖 {full}（{content.Length} 字符）。原文件已备份为：{backupPath}"
                : $"已{(mode == "append" ? "追加到" : "写入")} {full}（{content.Length} 字符）。";
        }).ConfigureAwait(false);
    }
}

// ══════════════════ 4. move_path ══════════════════

public class MovePathTool : FsToolBase
{
    public MovePathTool(AppSettings settings, Func<string, Task<bool>> askAllowDir)
        : base(settings, askAllowDir) { }

    public override string Name => "move_path";
    public override bool IsReadOnly => false;

    public override string Description =>
        "移动或重命名本机文件/目录（授权目录制；源路径和目标路径都必须在授权目录内）。" +
        "同目录不同名 = 重命名。目标已存在时报错（不覆盖）。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"path":{"type":"string","description":"源路径（文件或目录）"},"to":{"type":"string","description":"目标路径（已存在则报错，不覆盖）"}},"required":["path","to"]}""";

    public override string DescribeAction(string argumentsJson) =>
        ToolArgs.TryGetString(argumentsJson, "path", out var p) && ToolArgs.TryGetString(argumentsJson, "to", out var to)
            ? $"把 {p} 移动/重命名为 {to}" : Name;

    protected override async Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "to", out var to))
            return "错误：缺少参数 to（目标路径）。";

        var toGuard = FsGuard.Check(to, Settings);
        if (!toGuard.Ok && toGuard.Error.Length > 0) return toGuard.Error;

        // 目标也要在白名单内（与源同样的弹窗加白流程），否则白名单形同虚设（能"移出去"就等于能改任意位置）
        if (!toGuard.Ok)
        {
            var allowed = false;
            try { allowed = await AskAllowDir(toGuard.SuggestDir).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Agent", "目录授权弹窗异常（按拒绝处理）：" + ex.Message); }
            if (!allowed)
                return $"错误：用户拒绝将「{toGuard.SuggestDir}」加入 AI 文件访问白名单，操作已取消。";
            FsGuard.AllowDir(toGuard.SuggestDir, Settings);
        }

        return await Task.Run(() =>
        {
            var from = guard.FullPath;
            var dest = toGuard.FullPath;
            var isFile = File.Exists(from);
            var isDir = Directory.Exists(from);
            if (!isFile && !isDir) return $"错误：源路径不存在：{from}";
            if (File.Exists(dest) || Directory.Exists(dest)) return $"错误：目标已存在：{dest}（不覆盖）。请换一个目标路径。";

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                return $"错误：目标目录不存在：{destDir}（请先用 write_file 或让用户创建）。";

            if (isFile) File.Move(from, dest);
            else Directory.Move(from, dest);

            AppLog.Info("Agent", $"move_path：{from} → {dest}");
            return $"已{(Path.GetDirectoryName(from) == Path.GetDirectoryName(dest) ? "重命名" : "移动")}：{from} → {dest}";
        }).ConfigureAwait(false);
    }
}

// ══════════════════ 5. delete_path（删除一律进 Windows 回收站） ══════════════════

public class DeletePathTool : FsToolBase
{
    public DeletePathTool(AppSettings settings, Func<string, Task<bool>> askAllowDir)
        : base(settings, askAllowDir) { }

    public override string Name => "delete_path";
    public override bool IsReadOnly => false;

    public override string Description =>
        "把本机文件或目录移入 **Windows 回收站**（授权目录制；绝不物理删除，用户可在回收站还原）。" +
        "这是破坏性动作：必须先向用户复述要删的路径并得到明确同意，再调用。confirm 参数必须为 true。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"path":{"type":"string","description":"要删除的文件或目录完整路径"},"confirm":{"type":"boolean","description":"必须显式传 true（防误调用）"}},"required":["path","confirm"]}""";

    public override string DescribeAction(string argumentsJson) =>
        ToolArgs.TryGetString(argumentsJson, "path", out var p)
            ? $"把 {p} 移入 Windows 回收站" : Name;

    protected override async Task<string> RunAsync(FsGuardResult guard, string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetBool(argumentsJson, "confirm"))
            return "错误：删除是破坏性动作，必须先征得用户同意，并把 confirm 参数显式设为 true 再调用。";

        return await Task.Run(() =>
        {
            var full = guard.FullPath;
            var isFile = File.Exists(full);
            var isDir = Directory.Exists(full);
            if (!isFile && !isDir) return $"错误：路径不存在：{full}";

            var (result, aborted) = RecycleBin.Delete(full);
            if (aborted) return $"错误：系统中止了对 {full} 的删除（未移入回收站）。";
            if (result != 0)
            {
                AppLog.Warn("Agent", $"delete_path SHFileOperation 失败：{full}，code=0x{result:X}");
                return $"错误：删除失败（系统错误码 0x{result:X}）。可能原因：文件被占用 / 回收站被禁用。" +
                       "不会退化为物理删除 —— 请告知用户手动处理。";
            }

            AppLog.Info("Agent", $"delete_path：{full} → Windows 回收站");
            return $"已把 {full} 移入 Windows 回收站（可在回收站还原）。";
        }).ConfigureAwait(false);
    }
}

/// <summary>SHFileOperation 封装：FOF_ALLOWUNDO = 进回收站。必须在 STA 线程上调（Shell 操作要求）。</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;      // 进回收站
    private const ushort FOF_NOCONFIRMATION = 0x10; // 不弹系统确认框（授权弹窗已在应用内完成）
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;     // 不弹系统错误框，错误码回给我们

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;   // BOOL
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    /// <summary>删除到回收站。返回 (系统错误码，0=成功；是否被中止)。内部开 STA 线程执行。</summary>
    public static (uint Result, bool Aborted) Delete(string path)
    {
        uint result = unchecked(0xFFFFFFFF);
        var aborted = false;
        var t = new Thread(() =>
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0",   // 双 null 结尾（marshal 已带一个）
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            };
            result = (uint)SHFileOperationW(ref op);
            aborted = op.fAnyOperationsAborted != 0;
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return (result, aborted);
    }
}
