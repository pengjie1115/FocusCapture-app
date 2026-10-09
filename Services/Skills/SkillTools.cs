using System.Threading;
using FocusCapture.Services.Agent;

namespace FocusCapture.Services.Skills;

/// <summary>
/// <c>load_skill</c> —— 把某个 Skill 的完整说明交给模型（2026-09-20，阶段一）。
///
/// <para>
/// 这是**渐进式披露**的第二段：系统提示里只放"名字 + 一句摘要"（省 token），
/// 模型判断确实要用时才调本工具把 SKILL.md 正文拉进来。这也是 WorkBuddy 原生的 Skill 机制做法。
/// </para>
/// <para>只读工具：不弹确认。</para>
/// </summary>
public sealed class LoadSkillTool : AgentTool
{
    private readonly SkillCatalog _catalog;
    private readonly IReadOnlyList<SkillDependency> _dependencies;

    public LoadSkillTool(SkillCatalog catalog, IReadOnlyList<SkillDependency>? dependencies = null)
    {
        _catalog = catalog;
        _dependencies = dependencies ?? Array.Empty<SkillDependency>();
    }

    public override string Name => "load_skill";

    public override string Description =>
        "读取一个已安装 Skill 的完整说明（SKILL.md 正文 + 脚本清单）。" +
        "名字必须来自系统提示里的「可用 Skill」清单，不要编造；清单里没有的就直说做不到。" +
        "读完按它的指引操作，并用 run_skill_script 执行其中需要的脚本。" +
        "注意：说明里若提到 Bash / Read / Write 等工具，在本应用里并不存在，不要尝试。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"name":{"type":"string","description":"Skill 名，必须来自「可用 Skill」清单"}},"required":["name"]}""";

    public override bool IsReadOnly => true;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "name", out var name))
            return Task.FromResult("错误：缺少参数 name。");

        if (!_catalog.TryGet(name, out var skill))
            return Task.FromResult($"错误：不存在名为「{name}」的 Skill。请只使用系统提示「可用 Skill」清单里的名字。");

        var sb = new StringBuilder();
        sb.Append("Skill「").Append(skill.Name).Append("」的说明：\n\n").Append(skill.Body);

        if (skill.ScriptFiles.Count > 0)
        {
            sb.Append("\n\n--- 可用脚本（用 run_skill_script 执行，script 参数只填文件名）---\n");
            foreach (var s in skill.ScriptFiles)
                sb.Append("- ").Append(s).Append('\n');
        }
        else
        {
            sb.Append("\n\n（该 Skill 没有自带脚本，按其说明直接作答即可。）");
        }

        // 依赖预告（2026-09-20 补）：让模型**在跑之前**就知道这个 Skill 依赖什么外部程序，
        // 从而在需要授权时如实说"需要授权"，而不是自己发明一套流程让用户去敲命令 ——
        // 实测它干过：照着 CLI 输出里的提示，把开发机上的安装路径拼成命令给了用户。
        var deps = SkillDependencies.DetectInSkill(_dependencies, skill);
        if (deps.Count > 0)
        {
            sb.Append("\n\n--- 外部依赖（由应用负责准备，你不需要管）---\n");
            foreach (var d in deps) sb.Append("- ").Append(d.DisplayName).Append('\n');
            sb.Append("若执行时报「未授权 / 缺少依赖」：如实说明需要在应用里完成授权，")
              .Append("指向「设置 → AI 功能 → Skill 扩展」；**不要给用户命令、终端步骤或任何安装路径。**");
        }

        if (skill.HealthNote.Length > 0)
            sb.Append("\n\n注意：").Append(skill.HealthNote)
              .Append(" —— 缺少时脚本可能无法运行，请如实告知用户，不要假设成功。");

        return Task.FromResult(sb.ToString());
    }
}

/// <summary>
/// <c>run_skill_script</c> —— 执行 Skill 自带脚本（2026-09-20，阶段一）。
///
/// <para>
/// 写操作（<see cref="IsReadOnly"/> = false），且执行器内部另有一道**准入确认**
/// （某个 Skill 首次跑脚本时问用户一次，之后记住）。两道闸可能叠出一次双弹窗，
/// 这是刻意的 —— 脚本执行的风险比"新增一条笔记"高一个量级。
/// </para>
/// <para>本类只做参数搬运与校验，真正的硬规则全在 <see cref="SkillScriptRunner"/> 里。</para>
/// </summary>
public sealed class RunSkillScriptTool : AgentTool
{
    private readonly SkillScriptRunner _runner;

    public RunSkillScriptTool(SkillScriptRunner runner) => _runner = runner;

    public override string Name => "run_skill_script";

    public override string Description =>
        "执行某个 Skill 自带的脚本（会真的在用户电脑上运行）。" +
        "脚本必须位于该 Skill 的 scripts/ 目录内，script 参数只填文件名。" +
        "args 按数组顺序原样传给脚本、不经过 shell；要传很长的 JSON 时建议改用 stdin。" +
        "返回的是退出码与原始输出（长输出会被截断，需要完整数据时请让脚本只输出摘要）。" +
        "成功与否由你根据输出自行判断并如实汇报 —— 不要假设成功，也不要编造结果。" +
        "某个 Skill 首次执行时会请用户确认。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"skill":{"type":"string","description":"Skill 名，必须来自「可用 Skill」清单"},"script":{"type":"string","description":"scripts/ 下的文件名，如 kb.py"},"args":{"type":"array","items":{"type":"string"},"description":"按顺序传给脚本的参数"},"stdin":{"type":"string","description":"可选，作为标准输入（UTF-8），适合传长 JSON"}},"required":["skill","script"]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        var skill = ToolArgs.TryGetString(argumentsJson, "skill", out var s) ? s : "(未指定)";
        var script = ToolArgs.TryGetString(argumentsJson, "script", out var f) ? f : "(未指定)";
        return $"在用户电脑上执行 Skill「{skill}」的脚本：scripts\\{script}";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "skill", out var skill))
            return "错误：缺少参数 skill。";

        if (!ToolArgs.TryGetString(argumentsJson, "script", out var script))
            return "错误：缺少参数 script（scripts/ 下的文件名）。";

        var args = ParseStringArray(argumentsJson, "args");

        string? stdin = null;
        if (ToolArgs.TryGetString(argumentsJson, "stdin", out var s)) stdin = s;

        return await _runner.RunAsync(skill, script, args, stdin, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取字符串数组参数。宽容处理：数字/布尔也转成字符串（模型偶尔会把 "1" 写成 1）。
    /// 解析失败一律当作"没有参数"，不抛异常 —— 真实错误留给执行器去报（信息更准）。
    /// </summary>
    private static List<string> ParseStringArray(string json, string key)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
            if (!doc.RootElement.TryGetProperty(key, out var arr)) return list;
            if (arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var e in arr.EnumerateArray())
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String: list.Add(e.GetString() ?? ""); break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False: list.Add(e.GetRawText()); break;
                }
            }
        }
        catch (JsonException) { }
        return list;
    }
}
