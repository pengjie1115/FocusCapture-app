using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>
/// Skill 脚本执行器（2026-09-20，Skill 运行时阶段一）。
///
/// <para>
/// <b>这是整个方案里唯一真正危险的地方</b> —— 它会在用户电脑上跑第三方脚本。
/// 所以十条硬规则全部写死在代码里，**不由模型决定、也不做成可配置项**：
/// </para>
/// <list type="number">
/// <item>Skill 名只在已扫描目录表里查，取表中预存的绝对路径 —— 绝不把模型传的字符串拼进路径</item>
/// <item>脚本解析后必须仍在 <c>&lt;skill&gt;\scripts\</c> 内（<c>..</c> / 分隔符 / 绝对路径一律拒）</item>
/// <item>解释器白名单：只认 <c>.py</c></item>
/// <item>参数用 <c>ArgumentList</c> 数组传，不拼命令行字符串</item>
/// <item>超时 150 秒，到点杀进程树（必须大于脚本内部超时：kb.py 自己就设了 90 秒）</item>
/// <item>输出截断（保留头尾）</item>
/// <item>CWD 固定为该 Skill 根目录</item>
/// <item>环境变量：注入 Python 行为控制项、清掉宿主标记（防串号）、把依赖的全部候选目录（自带 → 数据目录）前置到 PATH</item>
/// <item>首次执行某 Skill 必须用户确认一次，之后记住</item>
/// <item><b>跑之前预检外部依赖</b>（在不在 / 要不要先授权）—— 授权在宿主内闭环，绝不外包给用户</item>
/// </list>
/// <para>
/// <b>防线在"准入"而不在"运行时拦截"</b>：脚本一旦跑起来，它读什么文件、连什么网，宿主管不了 ——
/// 这是"允许执行任意脚本"的固有代价，不是本类的缺陷。所以第 9、10 条才是地基。
/// </para>
/// </summary>
public sealed class SkillScriptRunner
{
    /// <summary>
    /// 默认超时。**必须大于脚本内部超时** —— kb.py 自己就设了 90 秒，两个 90 秒叠一起会误杀正常脚本。
    /// 检查点注入更小的值来测超时路径；生产一律用这个默认值。
    /// </summary>
    public const int DefaultTimeoutMs = 150_000;

    private const int OutputMaxChars = 4000;

    /// <summary>没授权的信号词。**只用来补一句应用内指引**，不作为放行/拦截判据。</summary>
    private static readonly string[] UnauthorizedMarkers =
    {
        "need_user_authorization", "no_token", "token_missing", "user identity is missing",
    };

    private readonly int _timeoutMs;

    private static readonly string[] AllowedExtensions = { ".py" };

    private readonly SkillCatalog _catalog;
    private readonly SkillRuntime _runtime;
    private readonly IReadOnlyList<SkillDependency> _dependencies;

    /// <param name="timeoutMs">执行超时；默认 <see cref="DefaultTimeoutMs"/>。
    /// 生产不要改它 —— 检查点用更小的值来验证"到点确实杀得掉"。</param>
    /// <param name="dependencies">已知外部依赖（见 <see cref="SkillDependencies"/>）。
    /// 传空 = 不做依赖预检；检查点用空表单独验证执行器自身的行为。</param>
    public SkillScriptRunner(
        SkillCatalog catalog,
        SkillRuntime runtime,
        int timeoutMs = DefaultTimeoutMs,
        IReadOnlyList<SkillDependency>? dependencies = null)
    {
        _catalog = catalog;
        _runtime = runtime;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        _dependencies = dependencies ?? Array.Empty<SkillDependency>();
    }

    /// <summary>
    /// 准入确认回调：某 Skill 首次要跑脚本时问用户一次。返回 true = 允许（并记住）。
    /// 未设置时一律拒绝 —— 宁可功能不可用，也不静默放行。
    /// </summary>
    public Func<SkillInfo, Task<bool>>? TrustPrompt { get; set; }

    /// <summary>
    /// 依赖授权回调（2026-09-20 补）：某个 Skill 依赖的外部 CLI 还没登录时，**在宿主里**把授权走完。
    ///
    /// <para>
    /// 实现方必须经 <c>UiThread.AskAsync</c> 封送回 UI 线程 —— 本回调是在工具线程（线程池）上被调用的。
    /// 返回 true = 用户完成授权，可以继续执行脚本；false = 没授权（**不得继续执行，也不得假装成功**）。
    /// </para>
    /// <para>未设置时不会静默放行：执行器直接返回"需要授权 + 应用内入口"。</para>
    /// </summary>
    public Func<SkillDependency, DependencyStatus, Task<bool>>? AuthPrompt { get; set; }

    /// <summary>已授权的 Skill 名（大小写不敏感）。持久化由调用方负责。</summary>
    public HashSet<string> TrustedSkills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>刚刚授权某个 Skill 时回调，调用方据此写配置</summary>
    public Action<string>? OnTrusted { get; set; }

    /// <summary>
    /// 执行一个 Skill 脚本。返回**给模型读的固定三段文本**（退出码 + stdout + stderr），
    /// 宿主**不做成败判定** —— 判定交给模型，避免宿主替模型"宣布成功"。
    /// </summary>
    public async Task<string> RunAsync(
        string? skillName,
        string? scriptFile,
        IReadOnlyList<string>? args,
        string? stdin,
        CancellationToken ct)
    {
        // ── 规则 1：查表，不拼路径 ──
        if (!_catalog.TryGet(skillName, out var skill))
            return $"错误：不存在名为「{skillName}」的 Skill。可用 Skill 见系统提示中的清单。";

        // ── 规则 2/3：脚本名与扩展名校验 ──
        var file = (scriptFile ?? "").Trim();
        if (file.Length == 0) return "错误：缺少参数 script。";
        if (file.Contains("..") || file.Contains('\\') || file.Contains('/') ||
            !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            return "错误：script 只能是文件名，不能包含路径分隔符或 ..";

        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return $"错误：不支持的脚本类型「{ext}」。当前只允许：{string.Join("、", AllowedExtensions)}。";

        // 解析后再校验一次越界（双保险：即使上面的字符检查被绕过）
        var scriptsDir = Path.GetFullPath(Path.Combine(skill.RootPath, "scripts"));
        var scriptPath = Path.GetFullPath(Path.Combine(scriptsDir, file));
        var prefix = scriptsDir.EndsWith(Path.DirectorySeparatorChar)
            ? scriptsDir
            : scriptsDir + Path.DirectorySeparatorChar;

        if (!scriptPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "错误：脚本路径越界 —— 只允许执行该 Skill 的 scripts 目录内的文件。";

        if (!File.Exists(scriptPath))
            return $"错误：脚本不存在：scripts\\{file}（该 Skill 的脚本：{DescribeScripts(skill)}）。";

        // ── 规则 9：准入确认 ──
        if (!TrustedSkills.Contains(skill.Name))
        {
            if (TrustPrompt == null)
                return $"错误：Skill「{skill.Name}」尚未被授权执行脚本，且当前无法弹出确认。请在「设置 → AI 功能 → Skill 扩展」中查看。";

            var allowed = await TrustPrompt(skill).ConfigureAwait(false);
            if (!allowed)
                return "用户已取消该操作。请停止此动作，不要重复尝试。";

            TrustedSkills.Add(skill.Name);
            OnTrusted?.Invoke(skill.Name);
        }

        // ── 规则 10：外部依赖预检（跑之前问清楚，别等脚本吐一堆英文错）──
        var blocked = await PreflightDependenciesAsync(skill, ct).ConfigureAwait(false);
        if (blocked != null) return blocked;

        // ── 规则 8 的一半：运行时体检（文件在 ≠ 能跑）──
        var (runtimeOk, runtimeMsg) = await _runtime.ProbeAsync(ct).ConfigureAwait(false);
        if (!runtimeOk)
        {
            // 措辞纪律：这里**绝不能**出现"已完成/已执行/成功"之类的字样
            return $"错误：Skill「{skill.Name}」无法执行 —— {runtimeMsg}\n{_runtime.MissingHint}";
        }

        // ── 组装进程（环境纪律统一在 SkillProcess，只有一处会错）──
        var psi = SkillProcess.Build(
            _runtime.PythonPath,
            new[] { scriptPath }.Concat(args ?? Array.Empty<string>()),
            skill.RootPath,                                  // 规则 7
            redirectStdin: stdin != null,
            prependPath: DependencyPathPrefix(),              // 让脚本里的 which 命中候选目录（自带 + 数据目录）
            pythonFlags: true);

        AppLog.Info("Skill", $"执行 {skill.Name}\\scripts\\{file}，参数 {args?.Count ?? 0} 个" +
                             (stdin != null ? $"，stdin {stdin.Length} 字符" : ""));

        var r = await SkillProcess.RunAsync(psi, stdin, _timeoutMs, ct).ConfigureAwait(false);

        if (r.StartError != null)
        {
            AppLog.Error("Skill", $"启动脚本进程失败：{skill.Name}\\{file} —— {r.StartError}");
            // 只有解释器确实不在时，才提示"去装运行时" —— 否则会把"参数/配置写错"误导成"环境缺失"。
            // （2026-09-20：检查点里真的踩到过这个误导 —— StandardInputEncoding 配错，提示却说运行时没装。）
            var hint = File.Exists(_runtime.PythonPath) ? "" : "\n" + _runtime.MissingHint;
            return $"错误：无法启动脚本进程 —— {r.StartError}{hint}";
        }

        if (r.TimedOut)
        {
            AppLog.Warn("Skill", $"脚本超时被杀：{skill.Name}\\{file}");
            return $"错误：脚本执行超时（{_timeoutMs / 1000} 秒），进程已被终止。\n" +
                   "若脚本本身就该跑很久，请让它在 Skill 说明里改成分批调用。";
        }

        AppLog.Info("Skill", $"脚本退出：{skill.Name}\\{file}，exit={r.ExitCode}，" +
                             $"stdout {r.Stdout.Length} 字符，stderr {r.Stderr.Length} 字符");

        var text = FormatResult(r);
        var hint2 = AuthFailureHint(r);
        return hint2.Length == 0 ? text : text + "\n\n" + hint2;
    }

    /// <summary>
    /// 依赖预检。返回 null = 放行；非 null = 直接作为工具结果返回（不放行）。
    ///
    /// <para>
    /// <b>为什么"找不到"不阻断：</b>脚本可能有自己的兜底（kb.py 就会去别的路径找 lark-cli）。
    /// 预检宁可少拦一次，也不能把本来能跑的 Skill 拦死。但"要授权"必须拦：
    /// 没登录的脚本跑下去只会失败，而失败的报错会**诱导模型自己编解决办法**（实测过）。
    /// </para>
    /// </summary>
    private async Task<string?> PreflightDependenciesAsync(SkillInfo skill, CancellationToken ct)
    {
        if (_dependencies.Count == 0) return null;

        var detected = SkillDependencies.DetectInSkill(_dependencies, skill);
        foreach (var dep in detected)
        {
            DependencyStatus status;
            try
            {
                status = await dep.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Skill", $"依赖探测异常（{dep.Id}）：{ex.Message}");
                continue;   // 探测本身出问题，不许因此拦住用户
            }

            if (!status.Resolved)
            {
                AppLog.Warn("Skill", $"{skill.Name} 依赖 {dep.Id}，但未能定位它：{status.Detail}");
                continue;
            }
            if (!status.NeedsAuth) continue;

            AppLog.Info("Skill", $"{skill.Name} 依赖 {dep.Id} 需要授权：{status.Detail}");

            if (AuthPrompt == null)
                return $"错误：Skill「{skill.Name}」依赖的{status.DisplayName}尚未完成授权（{status.Detail}），" +
                       "且当前无法弹出授权界面。请让用户到「设置 → AI 功能 → Skill 扩展」里点「授权」完成后再重试。" +
                       "不要建议用户去命令行执行任何命令。";

            var done = false;
            try
            {
                done = await AuthPrompt(dep, status).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Skill", $"依赖授权界面异常（{dep.Id}）", ex);
            }

            if (!done)
                return $"用户没有完成{status.DisplayName}的授权，所以脚本**没有执行**。" +
                       "请如实告知用户需要先在应用里完成授权（设置 → AI 功能 → Skill 扩展），" +
                       "不要假设已成功，也不要建议用户去命令行操作。";
        }

        return null;
    }

    /// <summary>
    /// 把依赖的**全部候选目录**组成 PATH 前缀（自带 + 数据目录；只包含真的存在的目录）。
    ///
    /// <para>
    /// 只前置自带那一份是不够的：按需下载的 CLI 落在数据目录，
    /// 脚本里的 <c>shutil.which("lark-cli")</c> 就找不到 → 桥接脚本静默失效（设计稿 R2）。
    /// 顺序与候选一致（自带优先），命中哪一份由 PATH 顺序自己决定。
    /// </para>
    /// </summary>
    private string? DependencyPathPrefix()
    {
        var dirs = new List<string>();
        foreach (var dep in _dependencies)
        {
            foreach (var dir in dep.CandidateDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                if (dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;   // 自带与数据目录重合时不重复塞
                dirs.Add(dir);
            }
        }
        return dirs.Count == 0 ? null : string.Join(Path.PathSeparator, dirs);
    }

    /// <summary>
    /// 脚本已经失败、且失败原因是"依赖没授权"时，给模型补一句**应用内的**下一步。
    ///
    /// <para>
    /// 为什么需要它：预检不可能覆盖所有情况（依赖可能被间接调用、也可能状态判断不到）。
    /// 一旦脚本自己把"未授权"报出来，工具结果里就必须带上应用内入口 ——
    /// 否则模型只能照着 CLI 输出里的提示自己发挥，实测它会去教用户敲命令行。
    /// </para>
    /// </summary>
    private string AuthFailureHint(ProcessResult r)
    {
        if (r.Ok || _dependencies.Count == 0) return "";

        var text = r.Combined;
        if (text.Length == 0) return "";
        var hit = false;
        foreach (var marker in UnauthorizedMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
        }
        if (!hit) return "";

        return "（宿主补充：这次失败看起来是依赖未授权或授权已失效，**不是脚本写错了**。" +
               "请在应用内完成授权：设置 → AI 功能 → Skill 扩展 → 授权，完成后重试。" +
               "不要让用户去命令行执行命令。）";
    }

    /// <summary>固定三段输出：退出码 + stdout + stderr。宿主只搬运事实，不下结论。</summary>
    private static string FormatResult(ProcessResult r)
    {
        var sb = new StringBuilder();
        sb.Append("EXIT=").Append(r.ExitCode).Append('\n');
        sb.Append("--- stdout ---\n").Append(Truncate(r.Stdout.TrimEnd(), OutputMaxChars)).Append('\n');
        sb.Append("--- stderr ---\n")
          .Append(r.Stderr.Trim().Length == 0 ? "（空）" : Truncate(r.Stderr.TrimEnd(), OutputMaxChars));
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n…（中间省略 {s.Length - max} 字符）…\n" + s[^tail..];
    }

    private static string DescribeScripts(SkillInfo skill) =>
        skill.ScriptFiles.Count == 0 ? "（无）" : string.Join("、", skill.ScriptFiles);
}
