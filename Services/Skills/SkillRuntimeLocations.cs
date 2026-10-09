namespace FocusCapture.Services.Skills;

/// <summary>
/// 运行时部件（外部 CLI / 内置 Python）的**候选目录唯一入口**（2026-09-21）。
///
/// <para>
/// <b>为什么要有这个类：</b>运行时可以从两个地方来 —— 随包分发的「自带」那一份，
/// 和使用者按需下载后落在数据目录的那一份。改造前每处消费方都各自拼一遍
/// <c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c>（<c>SkillDependency</c> / <c>SkillRuntime</c> /
/// <c>SkillScriptRunner</c> 各一份），于是「按需下载」一落地就会有地方找不到 —— 这正是
/// 设计稿里的 R2/R3。路径规则收在这里，只有一处会错。
/// </para>
/// <para>
/// <b>候选顺序（刻意的，不可随意调换）：</b>
/// </para>
/// <list type="number">
/// <item><b>自带</b> <c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c> —— 确定、可随包分发，优先</item>
/// <item><b>数据目录</b> <c>&lt;数据根&gt;\runtime\&lt;id&gt;\</c> —— 使用者点击按需下载后落这里。
///   取的是 <see cref="FocusCapturePaths.Root"/>，因此**天然跟随测试隔离（RootOverride）与
///   使用者自定义的数据根** —— 不写死 <c>%AppData%</c>，否则改了数据根就会出现「下载到了 A、使用找的是 B」</item>
/// <item><b>系统 PATH</b> —— 兼容使用者自己装过的情况。**它不在候选目录里**：PATH 是「查找来源」不是「某个目录」，
///   由 <see cref="FindOnPath"/> 兜底；执行器前置 PATH 时也只前置上面两档</item>
/// </list>
/// <para>
/// <b>刻意不认识「别的应用安装目录里恰好有一份」</b>：那既不可移植，也让宿主的行为取决于别人装了什么。
/// </para>
/// <para>
/// <b>永不抛</b>：候选目录只是「去哪儿找」的清单，拼路径失败、PATH 里有畸形项都属常态，一律跳过。
/// 找不到就是找不到，由调用方决定怎么措辞。
/// </para>
/// </summary>
public static class SkillRuntimeLocations
{
    /// <summary>运行时部件所在的一级目录名（自带与数据目录同名，只是根不同）</summary>
    public const string RuntimeFolderName = "runtime";

    /// <summary>
    /// 自带目录：<c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c>。
    /// <paramref name="baseDir"/> 为空（拿不到应用目录）时返回 null。
    /// </summary>
    public static string? BundledDir(string? baseDir, string id)
    {
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(id)) return null;
        try { return Path.Combine(baseDir!, RuntimeFolderName, id); }
        catch { return null; }
    }

    /// <summary>
    /// 数据目录：<c>&lt;数据根&gt;\runtime\&lt;id&gt;\</c>。按需下载的落地位置就是它。
    /// 跟随 <see cref="FocusCapturePaths.Root"/>（含 RootOverride 隔离与自定义数据根）。
    /// </summary>
    public static string DataDir(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        try { return FocusCapturePaths.Combine(RuntimeFolderName, id); }
        catch { return ""; }
    }

    /// <summary>
    /// 候选目录清单，顺序即优先级：<b>自带 → 数据目录</b>。
    /// 只给「去哪找 / 往哪个 PATH 前缀塞」，**不含 PATH 本身**（见类注释）。
    /// </summary>
    public static IReadOnlyList<string> CandidateDirs(string? baseDir, string id)
    {
        var list = new List<string>(2);

        var bundled = BundledDir(baseDir, id);
        if (!string.IsNullOrEmpty(bundled)) list.Add(bundled!);

        var data = DataDir(id);
        // 两者重合是极少数情况（应用恰好装在数据根里），去重后不重复前置同一个目录
        if (!string.IsNullOrEmpty(data) && !list.Any(d => string.Equals(d, data, StringComparison.OrdinalIgnoreCase)))
            list.Add(data);

        return list;
    }

    /// <summary>
    /// 按候选顺序找可执行文件，返回**第一个命中的**；都不在返回 null。
    /// <paramref name="exeName"/> 不含扩展名（Windows 上统一按 <c>.exe</c> 找）。
    /// </summary>
    public static string? FindInCandidates(string? baseDir, string id, string exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return null;
        foreach (var dir in CandidateDirs(baseDir, id))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 候选目录里有畸形项是常态，跳过 */ }
        }
        return null;
    }

    /// <summary>在系统 PATH 里找（最后一档兜底）。PATH 读不到或全是畸形项时返回 null，**不抛**。</summary>
    public static string? FindOnPath(string exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return null;

        string path;
        try { path = Environment.GetEnvironmentVariable("PATH") ?? ""; }
        catch { return null; }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exeName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 同上 */ }
        }
        return null;
    }
}
