namespace FocusCapture.Services.Skills;

/// <summary>恢复内置 Skill 的结果（措辞由调用方给，这里只搬事实）</summary>
public sealed record BuiltinSkillRestoreResult(bool Ok, string BackupPath, string Detail);

/// <summary>
/// 内置 Skill 的首次落地与恢复（2026-09-21，授权闭环步骤 4）。
///
/// <para>
/// <b>为什么要有这一层：</b>官方 lark-cli 把 28 个技能内嵌在二进制里，业务知识随之同步更新。
/// 所以我们要做的不是"写飞书知识"，而是造一条从 Agent 到 lark-cli 的通路 ——
/// 也就是随包分发一个只讲"怎么调用"的桥接 Skill。它得先落到用户能看见、也能改的地方。
/// </para>
/// <para>
/// <b>为什么落到数据目录而不是直接用应用目录里的那份：</b>
/// ① 用户可能要改（往 SKILL.md 里补自己的约定）；② Skill 扫描器只认数据根下的
/// <c>Skills\</c>（跟着自定义数据根走），应用目录那份它看不到。
/// </para>
/// <para>
/// <b>刻意零项目依赖</b>（不引 AppLog、不碰 WPF、目录全部由参数注入）：
/// 快层检查点要能直接链接它编译。落地逻辑错了的后果是**静默**的 ——
/// 用户改过的版本被悄悄覆盖、或者该出现的技能压根没出现，两种都很难被发现，
/// 所以这一层必须有防线。
/// </para>
/// <para>
/// <b>永不抛</b>：所有失败都通过返回值与 <see cref="Directory"/> 的存在性表达，
/// 由调用方决定措辞。启动期调用它，抛异常就等于拖垮启动。
/// </para>
/// </summary>
public static class BuiltinSkills
{
    /// <summary>随包分发的内置 Skill 目录名（<c>&lt;应用目录&gt;\builtin-skills</c>）</summary>
    public const string SourceFolderName = "builtin-skills";

    /// <summary>备份目录名：刻意与 <c>Skills</c> **同级**，不能放在它里面</summary>
    private const string BackupFolderName = "Skills_backup";

    /// <summary>落地点所在目录的名字（默认 <c>Skills</c>）—— 只用于推备份位置，不参与复制</summary>
    private const string FallbackBackupFolderName = ".backup";

    /// <summary>内置 Skill 的源目录（&lt;应用目录&gt;\builtin-skills）</summary>
    public static string SourceRoot(string? baseDir) =>
        string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir!, SourceFolderName);

    /// <summary>
    /// 随包分发的内置技能名（目录名）。设置页用它决定"恢复内置技能"能不能点，
    /// 也用来在确认框里如实列出即将被覆盖的名字。找不到就返回空表，**不抛**。
    /// </summary>
    public static IReadOnlyList<string> ListBuiltin(string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot)) return Array.Empty<string>();
        return ListSourceSkills(sourceRoot!)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>
    /// 首次落地：把内置 Skill 逐个复制进 <paramref name="skillsRoot"/>。
    /// 返回本次**真的新落地**了哪些（技能目录名）。
    ///
    /// <para>
    /// <b>判据用「目标里有没有 SKILL.md」，不是「目标目录在不在」</b> ——
    /// 扫描器本来就只认 SKILL.md，一个空目录或杂目录本来不算 Skill；
    /// 拿它去挡住内置技能，等于"用一个不算 Skill 的东西挡住一个真 Skill"，
    /// 用户看到的现象是"这个技能永远装不上"，且没有任何提示。
    /// </para>
    /// <para>反过来，只要目标里已有 SKILL.md，就**一个字节都不动** —— 用户改过的版本优先。</para>
    /// </summary>
    public static IReadOnlyList<string> Deploy(string? sourceRoot, string skillsRoot)
    {
        var deployed = new List<string>();
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(skillsRoot)) return deployed;
        if (!Directory.Exists(sourceRoot)) return deployed;   // 精简包/开发机没带内置技能：功能性降级，不是错误

        foreach (var skill in ListSourceSkills(sourceRoot!))
        {
            var name = Path.GetFileName(skill);
            var target = Path.Combine(skillsRoot!, name);
            if (File.Exists(Path.Combine(target, "SKILL.md"))) continue;   // 已存在 → 不覆盖

            try
            {
                CopyTree(skill, target);
                deployed.Add(name);
            }
            catch { /* 单个技能复制失败不影响其余；调用方从返回值就知道没落地 */ }
        }
        return deployed;
    }

    /// <summary>
    /// 把某个内置 Skill 恢复成出厂版本（设置页的「恢复内置技能」）。
    ///
    /// <para>
    /// <b>必须由用户点击发起</b>：它会覆盖用户自己的改动，属破坏性动作（<c>AGENTS.md</c> 红线 7）。
    /// 覆盖前先把现存目录整体改名搬走当备份 —— 不删除、只挪位置，
    /// 用户改的东西还在磁盘上，这是他唯一的后悔药。
    /// </para>
    /// <para>
    /// <b>备份位置刻意放在 <c>Skills</c> 同级</b>（<c>&lt;数据根&gt;\Skills_backup\&lt;时间戳&gt;\</c>）：
    /// 放进 <c>Skills\</c> 里面的话，那份备份里也有 SKILL.md →
    /// 会被扫描器当成第二个同名 Skill，清单里出现两行一样的名字。
    /// </para>
    /// </summary>
    public static BuiltinSkillRestoreResult Restore(string? sourceRoot, string skillsRoot, string? skillName)
    {
        var name = (skillName ?? "").Trim();
        if (name.Length == 0) return new BuiltinSkillRestoreResult(false, "", "没有指定要恢复的技能名。");

        // 只接受单层目录名 —— 与执行器的纪律一致：绝不把外部传来的字符串直接拼进路径
        if (name.Contains("..") || name.Contains('\\') || name.Contains('/') ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            return new BuiltinSkillRestoreResult(false, "", "技能名不合法。");

        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return new BuiltinSkillRestoreResult(false, "", "应用里没有可供恢复的内置技能目录。");

        var source = Path.Combine(sourceRoot!, name);
        if (!File.Exists(Path.Combine(source, "SKILL.md")))
            return new BuiltinSkillRestoreResult(false, "", $"内置技能里没有「{name}」，无法恢复。");

        if (string.IsNullOrWhiteSpace(skillsRoot))
            return new BuiltinSkillRestoreResult(false, "", "没有拿到技能目录位置。");

        var target = Path.Combine(skillsRoot!, name);
        var backupPath = "";

        try
        {
            if (Directory.Exists(target))
            {
                backupPath = MakeBackupPath(skillsRoot!, name);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                Directory.Move(target, backupPath);          // 挪走，不删除
            }

            CopyTree(source, target);
            return new BuiltinSkillRestoreResult(true, backupPath, "");
        }
        catch (Exception ex)
        {
            return new BuiltinSkillRestoreResult(false, backupPath, ex.Message);
        }
    }

    private static string MakeBackupPath(string skillsRoot, string skillName)
    {
        var parent = Path.GetDirectoryName(skillsRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // 正常情况：<数据根>\Skills_backup\<时间戳>\<技能名>（与 Skills 同级，扫描器看不到）
        var root = string.IsNullOrEmpty(parent)
            ? Path.Combine(skillsRoot, FallbackBackupFolderName)   // 退路：藏在 Skills 下的隐藏目录，同样扫不到
            : Path.Combine(parent!, BackupFolderName);

        // 时间戳只到秒：同一秒里点两次会给到同一个路径，而 Directory.Move 遇到已存在的目标会直接抛。
        // 与其让第二次恢复失败（用户完全看不懂），不如顺延一个序号。
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(root, stamp, skillName);
        for (var i = 2; Directory.Exists(path) && i < 100; i++)
            path = Path.Combine(root, $"{stamp}-{i}", skillName);

        return path;
    }

    /// <summary>列出源目录里真的是 Skill 的那些子目录（有 SKILL.md 才算）</summary>
    private static List<string> ListSourceSkills(string sourceRoot)
    {
        var list = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(sourceRoot))
            {
                if (File.Exists(Path.Combine(dir, "SKILL.md"))) list.Add(dir);
            }
        }
        catch { /* 枚举失败就当没有 */ }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>递归复制目录（覆盖同名文件，但不删除目标里多出来的文件）</summary>
    private static void CopyTree(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyTree(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}
