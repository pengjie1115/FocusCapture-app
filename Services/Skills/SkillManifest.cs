namespace FocusCapture.Services.Skills;

/// <summary>
/// 生成注入系统提示的「可用 Skill」清单（2026-09-20，阶段一）。
///
/// <para>
/// 走的是既有通道 <c>AgentRunService.ExtraSystemContext</c> —— 那条回调**每轮求值、附在消息末尾、
/// 不写入会话历史**，本来就是给"随手会变的短期状态"用的。装了 Skill 就会变，但没必要污染会话文件。
/// 因此本功能**对主循环零改动**。
/// </para>
/// <para>
/// 两个刻意的设计：
/// ① **清单只放摘要**（每个 Skill 一句、已在解析时截断到 200 字符），完整说明由 <c>load_skill</c> 按需拉取；
/// ② **总量封顶** <see cref="MaxChars"/> —— 否则"装几十个 Skill"会让每一轮请求都爆上下文。
/// </para>
/// </summary>
public static class SkillManifest
{
    /// <summary>清单总长上限，超了截断并在末尾说明还有几个没列出</summary>
    public const int MaxChars = 6000;

    /// <param name="skills">已安装的 Skill（来自 SkillCatalog）</param>
    /// <param name="runtimePresent">内置 Python 是否在位（只查文件，不启动进程）</param>
    public static string Build(IReadOnlyList<SkillInfo> skills, bool runtimePresent)
    {
        if (skills.Count == 0)
            return "[可用 Skill] 用户当前没有安装任何 Skill。不要猜测自己具备清单之外的能力，做不了就直说。";

        var sb = new StringBuilder();
        sb.Append("[可用 Skill] 以下是用户安装的扩展能力。只在确实相关时使用：先用 load_skill 读完整说明，")
          .Append("再用 run_skill_script 执行其中需要的脚本。\n");
        sb.Append("只能使用本应用提供给你的工具；Skill 说明里若提到 Bash / Read / Write 之类的工具，")
          .Append("在本应用里并不存在，做不了就直说，不要编造替代方案的结果。\n");
        // 下面两条是 2026-09-20 补的硬约束。起因是实测：一个 Skill 因为"依赖没登录"失败后，
        // 模型照着 CLI 输出里的提示，教用户去终端敲命令，还把某个安装目录的绝对路径拼进命令里 ——
        // 那些路径来自**开发机**，对用户毫无意义，也把"该由应用做的事"外包给了用户。
        sb.Append("⚠ 用户是普通用户：**不许让用户去命令行执行命令**，也不要给他命令、终端步骤或任何安装路径。\n");
        sb.Append("外部程序与登录态由应用负责。若某个 Skill 因「缺少依赖 / 未授权」失败，")
          .Append("就如实说需要授权（或缺少什么），并指向应用里的「设置 → AI 功能 → Skill 扩展」，")
          .Append("由用户在那里一键完成；不要自己发明流程。\n");

        var shown = 0;
        for (var i = 0; i < skills.Count; i++)
        {
            var line = FormatOne(skills[i], runtimePresent);
            if (sb.Length + line.Length + 1 > MaxChars) break;
            sb.Append(line).Append('\n');
            shown++;
        }

        if (shown < skills.Count)
            sb.Append($"（还有 {skills.Count - shown} 个未列出：清单较长时只列前 {shown} 个）");

        return sb.ToString().TrimEnd();
    }

    private static string FormatOne(SkillInfo s, bool runtimePresent)
    {
        var sb = new StringBuilder();
        sb.Append("- ").Append(s.Name).Append('：').Append(s.Description);

        var tags = new List<string>();
        if (s.HasScripts)
            tags.Add(runtimePresent ? "含可执行脚本" : "含脚本，但缺少运行时，当前不可执行");
        if (s.HealthNote.Length > 0)
            tags.Add(s.HealthNote);

        if (tags.Count > 0)
            sb.Append("（").Append(string.Join("；", tags)).Append('）');

        return sb.ToString();
    }
}
