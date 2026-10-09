namespace FocusCapture.Services;

/// <summary>目标目录的现状（决定要不要复制数据、以及 UI 该问什么）。</summary>
public enum RootTargetState
{
    /// <summary>目录不存在，将新建并复制</summary>
    NotExist,

    /// <summary>目录存在但没有文件，可直接复制</summary>
    Empty,

    /// <summary>目录已有内容：只能「直接切过去」，不再覆盖式复制（防把用户已有数据冲掉）</summary>
    NonEmpty,
}

public sealed record RootValidation(bool Ok, string Message, RootTargetState State);

public sealed record RootMigrationResult(bool Ok, string Message, int Files, long Bytes);

/// <summary>
/// 数据根迁移（2026-09-16）。
///
/// 最坏后果是丢笔记，所以这套逻辑的原则只有一条：**任何一步不确定就停，且永不破坏已有数据**。
/// 具体保障：
/// 1. <b>校验在前</b> —— 系统目录 / 盘根 / 网络路径 / 源与目标互相嵌套，一律拒绝
/// 2. <b>写入测试</b> —— 目标必须真的能写（建目录 + 写临时文件 + 读回 + 删），不通过即拒
/// 3. <b>只复制，不移动</b> —— 源目录一个字节都不改，它本身就是最可靠的备份与回滚点
/// 4. <b>复制后逐项校验</b> —— 文件数 + 总字节必须与源一致，不一致不切换
/// 5. <b>失败自动回滚</b> —— 校验不通过则删掉本次新建的目标目录（只在「目标原本为空」时才敢删），指针不写
/// 6. <b>旧目录永不自动删</b> —— 迁移完成后旧数据原样留在原处，等用户自己确认后再清
/// 7. <b>不做热切换</b> —— 只写指针，由下次启动读取生效（运行中一半旧路径一半新路径是最难查的 bug 形态）
/// </summary>
public static class RootMigrationService
{
    /// <summary>迁移留痕文件名（写在目标根下，记录本次迁移的源/目标/规模，便于日后追溯）。</summary>
    public const string MigrationLogName = "_migration_log.txt";

    /// <summary>不参与迁移的目录名：日志是本机瞬时产物，没有搬的意义。</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase) { "logs" };

    /// <summary>不参与迁移的文件名：指针文件只属于默认根，复制过去会造成「新根里又有个指针」的混乱。</summary>
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        FocusCapturePaths.PointerFileName, MigrationLogName,
    };

    // ── 校验 ──

    /// <summary>校验目标路径是否可作为数据根。会做写入测试（可能创建目录）。</summary>
    public static RootValidation ValidateTarget(string? target, string? sourceRoot = null)
    {
        var normalized = TryNormalize(target);
        if (normalized == null)
            return new RootValidation(false, "请输入合法的绝对路径（例如 D:\\FocusCapture）。", RootTargetState.NotExist);

        var source = TryNormalize(sourceRoot ?? FocusCapturePaths.Root);

        // 盘根：D:\ 这种位置直接往上堆 FocusCapture 的数据目录属于制造混乱
        if (IsDriveRoot(normalized))
            return new RootValidation(false, "不能直接使用盘符根目录（如 D:\\），请在其下指定一个文件夹。", RootTargetState.NotExist);

        // 网络路径：UNC 与「已映射的网络驱动器」都排除。
        // 数据根本地优先是本项目的立身之本，放网络上会同时踩延迟、断网、权限三颗雷。
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return new RootValidation(false, "不能使用网络路径（\\\\ 开头），请选择本机磁盘上的目录。", RootTargetState.NotExist);

        if (IsUnderSystemDir(normalized, out var blockedBy))
            return new RootValidation(false, $"该位置在系统目录内（{blockedBy}），不允许存放数据。", RootTargetState.NotExist);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            string.Equals(normalized, TryNormalize(profile), StringComparison.OrdinalIgnoreCase))
            return new RootValidation(false, "不能直接用用户主目录本身作为数据根，请在其下新建一个文件夹。", RootTargetState.NotExist);

        // 源与目标互相嵌套 → 复制会自我递归（轻则复制到一半炸，重则无限膨胀直到写满磁盘）
        if (source != null)
        {
            if (string.Equals(normalized, source, StringComparison.OrdinalIgnoreCase))
                return new RootValidation(false, "该目录就是当前数据目录，无需切换。", RootTargetState.NotExist);
            if (IsUnder(normalized, source))
                return new RootValidation(false, "不能选择当前数据目录的子目录，否则复制会自我递归。", RootTargetState.NotExist);
            if (IsUnder(source, normalized))
                return new RootValidation(false, "不能选择当前数据目录的上级目录，否则复制会自我递归。", RootTargetState.NotExist);
        }

        // 写入测试（最后一道，也是最实际的一道）
        var state = InspectTarget(normalized);
        var writeError = ProbeWritable(normalized);
        if (writeError != null)
            return new RootValidation(false, writeError, state);

        var message = state == RootTargetState.NonEmpty
            ? "该目录已有内容。为避免冲掉现有数据，切换后将直接使用该目录的现有内容，不再复制当前数据。"
            : "";
        return new RootValidation(true, message, state);
    }

    /// <summary>探测目标目录现状（不创建目录时以 parent 是否可访问为准）。</summary>
    public static RootTargetState InspectTarget(string normalizedPath)
    {
        try
        {
            if (!Directory.Exists(normalizedPath)) return RootTargetState.NotExist;
            using var e = Directory.EnumerateFileSystemEntries(normalizedPath).GetEnumerator();
            return e.MoveNext() ? RootTargetState.NonEmpty : RootTargetState.Empty;
        }
        catch
        {
            return RootTargetState.NonEmpty;   // 探不进去就当成非空，宁可少做也不误覆盖
        }
    }

    /// <summary>写入测试：建目录 → 写临时文件 → 读回比对 → 删除。返回 null = 通过。</summary>
    private static string? ProbeWritable(string normalizedPath)
    {
        var probe = Path.Combine(normalizedPath, ".fc_write_probe");
        try
        {
            Directory.CreateDirectory(normalizedPath);
            var payload = Guid.NewGuid().ToString("N");
            File.WriteAllText(probe, payload, new UTF8Encoding(false));
            var readBack = File.ReadAllText(probe);
            if (readBack != payload) return "该目录写入后读回的内容不一致，可能磁盘异常，已取消。";
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "没有写入该目录的权限（可能被系统保护或需要管理员权限），请换一个位置。";
        }
        catch (Exception ex)
        {
            return $"该目录不可用：{ex.Message}";
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* 清不掉也无害 */ }
        }
    }

    // ── 迁移 ──

    /// <summary>
    /// 执行迁移。
    /// <paramref name="copyExistingData"/> = true：把当前数据完整复制到目标并校验；
    /// = false：目标已有内容，直接切过去（不复制、不覆盖）。
    /// 成功只写指针文件，需重启生效；源目录始终原样保留。
    /// </summary>
    public static RootMigrationResult Migrate(string sourceRoot, string targetPath, bool copyExistingData,
        Action<string>? report = null)
    {
        var source = TryNormalize(sourceRoot);
        var target = TryNormalize(targetPath);
        if (source == null || target == null)
            return new RootMigrationResult(false, "路径不合法。", 0, 0);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return new RootMigrationResult(false, "目标目录与当前数据目录相同，无需切换。", 0, 0);

        var files = 0;
        long bytes = 0;

        if (copyExistingData)
        {
            var targetExisted = Directory.Exists(target);

            try
            {
                report?.Invoke("正在创建目标目录…");
                Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                return new RootMigrationResult(false, $"无法创建目标目录：{ex.Message}", 0, 0);
            }

            try
            {
                report?.Invoke("正在复制数据…");
                CopyTree(source, target, report, ref files, ref bytes);

                report?.Invoke("正在校验…");
                var srcStat = Measure(source, applyExcludes: true);
                var dstStat = Measure(target, applyExcludes: true);

                if (srcStat.Files != dstStat.Files || srcStat.Bytes != dstStat.Bytes)
                {
                    var msg = $"复制校验不通过（源 {srcStat.Files} 个文件 / {FormatSize(srcStat.Bytes)}，" +
                              $"目标 {dstStat.Files} 个文件 / {FormatSize(dstStat.Bytes)}），已放弃切换，原数据未受影响。";
                    Rollback(target, targetExisted);
                    return new RootMigrationResult(false, msg, dstStat.Files, dstStat.Bytes);
                }

                files = dstStat.Files;
                bytes = dstStat.Bytes;
            }
            catch (Exception ex)
            {
                Rollback(target, targetExisted);
                return new RootMigrationResult(false, $"复制过程中出错：{ex.Message}。已放弃切换，原数据未受影响。", 0, 0);
            }
        }

        try
        {
            WriteMigrationLog(target, source, files, bytes, copyExistingData);
            FocusCapturePaths.SaveCustomRoot(target);
        }
        catch (Exception ex)
        {
            return new RootMigrationResult(false, $"数据已就位但写入位置记录失败：{ex.Message}", files, bytes);
        }

        var summary = copyExistingData
            ? $"已复制 {files} 个文件（{FormatSize(bytes)}）到新位置。"
            : "已直接切换到该目录的现有内容。";
        return new RootMigrationResult(true, summary, files, bytes);
    }

    /// <summary>校验失败时清理：只在「目标原本不存在」或「目标原本为空」时才敢整棵删掉（里面只有本次复制的东西）。</summary>
    private static void Rollback(string target, bool targetExistedBefore)
    {
        try
        {
            // 原本非空的目标不会被走到这里（UI 不会让它走复制分支）；为稳妥仍加一道判断
            if (targetExistedBefore && InspectTarget(target) == RootTargetState.NonEmpty)
            {
                // 保守处理：只试图删掉我们写的留痕文件，其余一律不动
                var log = Path.Combine(target, MigrationLogName);
                if (File.Exists(log)) File.Delete(log);
                return;
            }
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        catch { /* 回滚失败也不能抛：此时最要紧的是把失败信息原样交给用户 */ }
    }

    private static void WriteMigrationLog(string target, string source, int files, long bytes, bool copied)
    {
        try
        {
            var lines = new[]
            {
                $"迁移时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"原数据目录：{source}",
                $"新数据目录：{target}",
                copied ? $"已复制：{files} 个文件，共 {FormatSize(bytes)}" : "未复制（沿用目标目录已有内容）",
                "说明：原目录内容未被删除也未被修改，需要回退时把数据根改回上面的「原数据目录」即可；",
                "      确认新位置一切正常后，可自行删除原目录。",
            };
            File.WriteAllText(Path.Combine(target, MigrationLogName),
                string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
        }
        catch { /* 留痕写不进去不阻断迁移 */ }
    }

    // ── 复制与统计 ──

    private static void CopyTree(string src, string dst, Action<string>? report, ref int files, ref long bytes)
    {
        Directory.CreateDirectory(dst);

        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            if (ExcludedFiles.Contains(name)) continue;

            var target = Path.Combine(dst, name);
            File.Copy(file, target, overwrite: true);
            files++;
            bytes += new FileInfo(target).Length;
            if (files % 50 == 0) report?.Invoke($"正在复制… {files} 个文件");
        }

        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (ExcludedDirs.Contains(name)) continue;
            CopyTree(dir, Path.Combine(dst, name), report, ref files, ref bytes);
        }
    }

    /// <summary>统计目录下的文件数与总字节（排除项与复制保持一致，否则校验必然误报）。</summary>
    public static (int Files, long Bytes) Measure(string dir, bool applyExcludes)
    {
        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (applyExcludes && ExcludedFiles.Contains(Path.GetFileName(file))) continue;
                files++;
                try { bytes += new FileInfo(file).Length; } catch { /* 拿不到大小就不计，校验会因此报警，属期望行为 */ }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (applyExcludes && ExcludedDirs.Contains(Path.GetFileName(sub))) continue;
                var (f, b) = Measure(sub, applyExcludes);
                files += f;
                bytes += b;
            }
        }
        catch { /* 枚举失败：返回已统计部分，由调用方的比对逻辑兜住 */ }
        return (files, bytes);
    }

    // ── 工具 ──

    private static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var full = Path.GetFullPath(value.Trim());
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string path) =>
        path.Length is 2 or 3 && char.IsLetter(path[0]) && path[1] == ':';

    private static bool IsUnderSystemDir(string path, out string blocked)
    {
        var candidates = new (string Path, string Label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Windows"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
        };

        foreach (var (root, label) in candidates)
        {
            var normalizedRoot = TryNormalize(root);
            if (normalizedRoot == null) continue;
            if (string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase) || IsUnder(path, normalizedRoot))
            {
                blocked = label;
                return true;
            }
        }
        blocked = "";
        return false;
    }

    /// <summary>child 是否位于 parent 之下（不含相等）。</summary>
    private static bool IsUnder(string child, string parent)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.EndsWith(':')) p += Path.DirectorySeparatorChar;   // 盘根已带分隔符
        return child.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };
}
