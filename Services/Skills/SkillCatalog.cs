namespace FocusCapture.Services.Skills;

/// <summary>
/// 一个已安装 Skill 的元数据。
/// <para>
/// <see cref="RootPath"/> 是**扫描时确定的绝对路径**，执行器只认它 —— 绝不拿模型传来的名字去拼路径
/// （见 SkillScriptRunner 的规则 1）。这是"模型编不出没被授权过的路径"那条防线的一半。
/// </para>
/// </summary>
public sealed record SkillInfo(
    string Name,
    string DirectoryName,
    string RootPath,
    string Description,
    string Body,
    IReadOnlyList<string> ScriptFiles,
    string HealthNote)
{
    /// <summary>是否有可执行的脚本目录（决定要不要标"需要运行时"）</summary>
    public bool HasScripts => ScriptFiles.Count > 0;
}

/// <summary>
/// Skill 目录扫描与解析（2026-09-20，Skill 运行时阶段一）。
///
/// <para>
/// **刻意零项目依赖** —— 不引用 AppLog / FocusCapturePaths / 任何 WPF 类型，
/// 根目录由构造注入。这样它能被快层检查点工程直接链接编译（快层的规矩是秒级、不引主项目），
/// 而解析恰恰是最需要反复测的部分（真实 SKILL.md 的写法千奇百怪）。
/// </para>
/// <para>
/// **解析纪律：绝不因为一个 Skill 畸形就让整次扫描失败** —— 跳过它、记一条警告、继续扫下一个。
/// 症状是"某个 Skill 不见了"，而不是"整个 Skill 清单空了"。
/// </para>
/// <para>
/// 缓存策略见 <see cref="GetSkills"/>：进程内缓存 + 根目录时间戳校验。
/// 用户中途装了新 Skill 不必重启，但也不会每次对话都去翻 30 个目录。
/// </para>
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>description 进清单时的截断长度（防 token 爆炸）</summary>
    private const int DescriptionMaxChars = 200;

    /// <summary>SKILL.md 正文交给模型时的截断长度（约 2700 token）</summary>
    private const int BodyMaxChars = 8000;

    /// <summary>
    /// 已知的常见第三方包（黑名单式探测）。
    /// 用它而非"标准库白名单"的理由：漏判的代价只是少一句提示，不影响功能；
    /// 而白名单要跟着 Python 版本维护，注定过期。
    /// </summary>
    private static readonly string[] KnownThirdParty =
    {
        "requests", "numpy", "pandas", "sklearn", "docx", "bs4", "httpx", "certifi",
        "PIL", "openpyxl", "lxml", "yaml", "matplotlib", "torch", "openai",
    };

    private readonly string _root;
    private readonly Action<string>? _onWarn;

    private List<SkillInfo>? _cache;
    private DateTime _cacheStamp = DateTime.MinValue;

    /// <param name="root">Skills 根目录（如 %AppData%\FocusCapture\Skills）</param>
    /// <param name="onWarn">警告回调（解析失败等）；不传就静默跳过</param>
    public SkillCatalog(string root, Action<string>? onWarn = null)
    {
        _root = root ?? "";
        _onWarn = onWarn;
    }

    /// <summary>Skills 根目录</summary>
    public string Root => _root;

    /// <summary>
    /// 取已安装 Skill 清单（按名称排序，保证顺序稳定）。
    /// 缓存判据是根目录的 <c>LastWriteTimeUtc</c> —— 新增/删除 Skill 目录会改变它；
    /// 在已有 Skill 内部改文件不会（这种场景点设置页的「重新扫描」即可）。
    /// </summary>
    public IReadOnlyList<SkillInfo> GetSkills(bool forceRescan = false)
    {
        var stamp = Directory.Exists(_root) ? Directory.GetLastWriteTimeUtc(_root) : DateTime.MinValue;

        if (!forceRescan && _cache != null && stamp == _cacheStamp)
            return _cache;

        _cache = Scan();
        _cacheStamp = stamp;
        return _cache;
    }

    /// <summary>按名查找（不区分大小写）。名字来源是清单，不是模型自由发挥。</summary>
    public bool TryGet(string? name, out SkillInfo skill)
    {
        skill = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;

        foreach (var s in GetSkills())
        {
            if (string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.DirectoryName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                skill = s;
                return true;
            }
        }
        return false;
    }

    // ────────────────────────── 扫描 ──────────────────────────

    private List<SkillInfo> Scan()
    {
        var list = new List<SkillInfo>();
        if (!Directory.Exists(_root)) return list;

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var dirName = Path.GetFileName(dir);
            var skillMd = Path.Combine(dir, "SKILL.md");

            // 没有 SKILL.md 的目录不是 Skill —— 安静跳过（不放警告，免得干扰用户自建的杂目录）
            if (!File.Exists(skillMd)) continue;

            try
            {
                var info = LoadOne(dir, dirName, skillMd);
                if (info != null) list.Add(info);
            }
            catch (Exception ex)
            {
                // 单个 Skill 畸形只影响它自己
                _onWarn?.Invoke($"解析 Skill「{dirName}」失败，已跳过：{ex.Message}");
            }
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private SkillInfo? LoadOne(string dir, string dirName, string skillMd)
    {
        // SKILL.md 约定为 UTF-8（可带 BOM）；File.ReadAllText 默认按 UTF-8 + BOM 探测读取
        var text = File.ReadAllText(skillMd);
        var (fmName, fmDesc, body) = ParseFrontmatter(text);

        var name = string.IsNullOrWhiteSpace(fmName) ? dirName : fmName.Trim();

        var desc = fmDesc;
        if (string.IsNullOrWhiteSpace(desc)) desc = FirstNonEmptyLine(body);
        if (string.IsNullOrWhiteSpace(desc)) desc = "(无描述)";
        desc = Truncate(desc.Trim(), DescriptionMaxChars);

        var scripts = ListScripts(dir);
        var note = BuildHealthNote(scripts, dir);

        return new SkillInfo(
            Name: name,
            DirectoryName: dirName,
            RootPath: Path.GetFullPath(dir),
            Description: desc,
            Body: Truncate(body.Trim(), BodyMaxChars),
            ScriptFiles: scripts,
            HealthNote: note);
    }

    /// <summary>列 scripts\ 下的文件（只列名，按名排序）</summary>
    private static List<string> ListScripts(string skillDir)
    {
        var result = new List<string>();
        var scriptsDir = Path.Combine(skillDir, "scripts");
        if (!Directory.Exists(scriptsDir)) return result;

        foreach (var f in Directory.EnumerateFiles(scriptsDir))
            result.Add(Path.GetFileName(f));   // 只给文件名，不给路径 —— 执行器会自己拼并做越界校验

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// 静态能力预检（尽力而为，不追求完备）。
    /// 只做"能不能看出缺什么"，看不出来就不标注 —— 缺标注只是少一句提示，不会造成误判。
    /// </summary>
    private static string BuildHealthNote(List<string> scripts, string skillDir)
    {
        var pyScripts = scripts.Where(s => s.EndsWith(".py", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pyScripts.Count == 0) return "";

        var missing = new List<string>();
        foreach (var s in pyScripts)
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(skillDir, "scripts", s));
                foreach (var pkg in KnownThirdParty)
                {
                    if (UsesPackage(text, pkg) && !missing.Contains(pkg)) missing.Add(pkg);
                }
            }
            catch { /* 读不了就算了，不影响扫描 */ }
        }

        return missing.Count > 0 ? $"需要第三方包：{string.Join("、", missing)}" : "";
    }

    private static bool UsesPackage(string text, string package)
    {
        foreach (Match m in Regex.Matches(text, @"^\s*(?:import|from)\s+([A-Za-z0-9_\.]+)", RegexOptions.Multiline))
        {
            var root = m.Groups[1].Value.Split('.')[0];
            if (string.Equals(root, package, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ────────────────────────── frontmatter 解析 ──────────────────────────

    /// <summary>
    /// 解析 SKILL.md 的 YAML frontmatter，只取 <c>name</c> 与 <c>description</c>。
    ///
    /// <para>
    /// **刻意手写而不用 YAML 库**：需求只有两个标量字段，引库的代价（依赖 + 版本 + 异常面）
    /// 大于收益；而且真实文件里的写法比规范更野，手写反而更容易做到"读不懂也不抛"。
    /// </para>
    /// <para>容错约定：没有 frontmatter / 没有闭合 / 字段缺失 —— 一律返回 null，不抛异常。</para>
    /// </summary>
    internal static (string? Name, string? Description, string Body) ParseFrontmatter(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (null, null, "");

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF') normalized = normalized[1..];

        var lines = normalized.Split('\n');

        // 允许前导空行
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].TrimEnd() != "---") return (null, null, normalized);

        var start = i + 1;
        var end = -1;
        for (var j = start; j < lines.Length; j++)
        {
            if (lines[j].TrimEnd() == "---") { end = j; break; }
        }
        if (end < 0) return (null, null, normalized);   // 没闭合，当作没有 frontmatter

        string? name = null;
        string? desc = null;

        for (var j = start; j < end; j++)
        {
            var line = lines[j];
            // 只认顶层键：缩进行属于上一个键的续行，跳过
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t') continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            var raw = line[(colon + 1)..].Trim();

            if (key == "name" && name == null)
            {
                name = Unquote(raw);
            }
            else if (key == "description" && desc == null)
            {
                // 多行标量（> | >- |- 等）：把后续缩进行拼成一行
                if (raw is ">" or "|" or ">-" or "|-" or ">+" or "|+")
                {
                    var sb = new StringBuilder();
                    for (var k = j + 1; k < end; k++)
                    {
                        var l = lines[k];
                        if (l.Length == 0) { if (sb.Length > 0) sb.Append(' '); continue; }
                        if (l[0] != ' ' && l[0] != '\t') break;   // 回到顶层键，结束
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(l.Trim());
                    }
                    desc = sb.ToString();
                }
                else
                {
                    desc = Unquote(raw);
                }
            }
        }

        var body = string.Join("\n", lines.Skip(end + 1)).TrimStart('\n');
        return (name, desc, body);
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 &&
            ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            return v[1..^1];
        return v;
    }

    private static string FirstNonEmptyLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim().TrimStart('#').Trim();
            if (t.Length > 0) return t;
        }
        return "";
    }

    /// <summary>超长截断（保留头尾，中间标注省略量）</summary>
    internal static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n…（中间省略 {s.Length - max} 字符）…\n" + s[^tail..];
    }
}
