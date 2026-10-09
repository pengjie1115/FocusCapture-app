using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>
/// 一个运行时部件的「下载配方」（2026-09-21，授权闭环步骤 5）。
///
/// <para>
/// <b>为什么把配方抽出来：</b>lark-cli 与内置 Python 的差别全在"怎么装"，而
/// "多源兜底 / 进度 / 临时文件 / 自检 / 永不抛"这套流程完全一样。流程写在
/// <see cref="RuntimeDownloader"/>，两个部件的差异只写在这里，加第三个部件就是加一条配方。
/// </para>
/// <para>
/// <b>⚠ 版本刻意写死，不解析「latest」</b>（与 <c>tools\fetch-*.ps1</c> 里"问 registry 取最新"的做法不同）：
/// 实测教训摆在那儿 —— lark-cli 从 1.0.92 到 1.0.96 改掉了 <c>auth qrcode -o</c> 对绝对路径的接受
/// （见 REGRESSION B-18 那条"会咬人的版本差异"）。**用户机器上悄悄换一个版本，可能直接破坏我们验证过的行为**。
/// 要升级就改这一行的版本号，并跑一次真机验收；不要让它自己漂。
/// </para>
/// <para>
/// 下载源照抄 <c>tools\fetch-lark-cli.ps1</c> / <c>tools\fetch-python-runtime.ps1</c> 里**已在脚本里验证过可用**
/// 的那几套（国内镜像优先、官方兜底）。两边的主机名由慢层检查点cross-check，防漂移。
/// </para>
/// </summary>
public sealed class RuntimeRecipe
{
    public RuntimeRecipe(
        string id,
        string displayName,
        IReadOnlyList<string> urls,
        long minBytes,
        bool extractAll,
        string? pickFileName,
        IReadOnlyList<string> deleteAfterExtract,
        Func<string, CancellationToken, Task<(bool Ok, string Detail)>> verify)
    {
        Id = id;
        DisplayName = displayName;
        Urls = urls;
        MinBytes = minBytes;
        ExtractAll = extractAll;
        PickFileName = pickFileName;
        DeleteAfterExtract = deleteAfterExtract;
        Verify = verify;
    }

    /// <summary>落地目录名：<c>&lt;数据根&gt;\runtime\&lt;Id&gt;\</c>（与候选目录机制一致）</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>下载源，顺序即优先级</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>归档体积下限。**不是为了防篡改，是为了防"下到一个错误页/空壳却当成成功"**</summary>
    public long MinBytes { get; }

    /// <summary>
    /// 归档怎么装：<c>true</c> = 整包解压到目录（Python 是这种）；
    /// <c>false</c> = 从包里挑出 <see cref="PickFileName"/> 那一个文件（lark-cli 的包里还有别的东西）。
    /// </summary>
    public bool ExtractAll { get; }

    /// <summary><see cref="ExtractAll"/> 为 false 时要在包里递归找的文件名</summary>
    public string? PickFileName { get; }

    /// <summary>
    /// 解压后必须删掉的文件通配（相对目录）。Python 的 <c>*._pth</c> 是唯一一条，也是**最容易漏的一条**：
    /// 留着它 Python 进 isolated 模式 —— 脚本所在目录不进 <c>sys.path</c>、<c>PYTHONPATH</c> 被忽略，
    /// 「同目录模块 import」直接失败。症状是"Python 装好了但某些 Skill 静默失效"。
    /// </summary>
    public IReadOnlyList<string> DeleteAfterExtract { get; }

    /// <summary>自检：**传进来的目录**里这个东西能不能真的用（不是"文件在不在"）。永不抛。</summary>
    public Func<string, CancellationToken, Task<(bool Ok, string Detail)>> Verify { get; }
}

/// <summary>
/// 已知运行时部件的配方表（2026-09-21）。
///
/// <para>版本与下载源**必须与 <c>tools\fetch-*.ps1</c> 保持一致** —— 那两份脚本是开发机拉取用的，
/// 这里是给使用者按需下载用的；两边漂了就会出现"开发机上是 A 版本、用户下到 B 版本"这类难查的问题。</para>
/// </summary>
public static class RuntimeRecipes
{
    /// <summary>
    /// 飞书 CLI 的部件名（同时也是 <c>runtime\&lt;id&gt;\</c> 目录名）。
    /// <b>刻意写成字面量而不是引用 <c>SkillDependencies.LarkCliId</c></b>：本文件与
    /// <see cref="RuntimeDownloader"/> 要能被快层检查点直接链接编译（那里刻意不引主项目），
    /// 而 <c>SkillDependencies</c> 会把整条依赖链（协议、授权流程…）拖进来。
    /// 两个常量是否一致由慢层的一条断言守着（漂移了就会红）。
    /// </summary>
    public const string LarkCliId = "lark-cli";

    /// <summary>内置 Python 的部件名</summary>
    public const string PythonId = "python";

    /// <summary>版本号只在这一处出现，且是刻意写死的 —— 理由见 <see cref="RuntimeRecipe"/> 类注释</summary>
    public const string LarkCliVersion = "1.0.96";
    public const string PythonVersion = "3.13.12";

    /// <summary>按部件名取配方；没有配方的部件返回 null（界面据此决定不给下载按钮）</summary>
    public static RuntimeRecipe? For(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(id, LarkCliId, StringComparison.OrdinalIgnoreCase)) return LarkCli();
        if (string.Equals(id, PythonId, StringComparison.OrdinalIgnoreCase)) return Python();
        return null;
    }

    /// <summary>全部配方</summary>
    public static IReadOnlyList<RuntimeRecipe> All() => new[] { LarkCli(), Python() };

    // ────────────────────────── 飞书 CLI ──────────────────────────

    public static RuntimeRecipe LarkCli()
    {
        var archive = $"lark-cli-{LarkCliVersion}-windows-amd64.zip";
        return new RuntimeRecipe(
            id: LarkCliId,
            displayName: "飞书命令行（lark-cli）",
            urls: new[]
            {
                // 国内镜像优先：GitHub 在本机网络上不稳（SSL/502），47MB 从 GitHub 拉大概率失败
                $"https://registry.npmmirror.com/-/binary/lark-cli/v{LarkCliVersion}/{archive}",
                $"https://github.com/larksuite/cli/releases/download/v{LarkCliVersion}/{archive}",
            },
            minBytes: 10L * 1024 * 1024,   // 真实约 40MB+；10MB 只用来拦"错误页 / 空壳"
            extractAll: false,
            pickFileName: "lark-cli.exe",
            deleteAfterExtract: Array.Empty<string>(),
            verify: VerifyLarkCliAsync);
    }

    /// <summary>
    /// 自检 = 真跑一次 <c>--version</c>，必须吐出 <c>lark-cli version</c>。
    /// 与 <c>fetch-lark-cli.ps1</c> 的自检同口径 —— "文件在" ≠ "能跑"。
    /// </summary>
    private static async Task<(bool Ok, string Detail)> VerifyLarkCliAsync(string dir, CancellationToken ct)
    {
        var exe = Path.Combine(dir, "lark-cli.exe");
        if (!File.Exists(exe)) return (false, "缺少 lark-cli.exe");

        try
        {
            var psi = SkillProcess.Build(exe, new[] { "--version" }, dir, false);
            var r = await SkillProcess.RunAsync(psi, null, 30_000, ct).ConfigureAwait(false);
            if (r.StartError != null) return (false, $"无法启动：{r.StartError}");
            if (r.TimedOut) return (false, "启动后 30 秒无响应");

            var text = r.Combined;
            return text.Contains("lark-cli version", StringComparison.OrdinalIgnoreCase)
                ? (true, text.Trim())
                : (false, $"输出不是预期内容：{Short(text)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ────────────────────────── 内置 Python ──────────────────────────

    public static RuntimeRecipe Python()
    {
        var archive = $"python-{PythonVersion}-embed-amd64.zip";
        return new RuntimeRecipe(
            id: PythonId,
            displayName: "内置 Python 运行时",
            urls: new[]
            {
                $"https://mirrors.huaweicloud.com/python/{PythonVersion}/{archive}",
                $"https://registry.npmmirror.com/-/binary/python/{PythonVersion}/{archive}",
                $"https://www.python.org/ftp/python/{PythonVersion}/{archive}",
            },
            minBytes: 3L * 1024 * 1024,     // 真实约 11MB
            extractAll: true,
            pickFileName: null,
            deleteAfterExtract: new[] { "*._pth" },
            verify: VerifyPythonAsync);
    }

    /// <summary>
    /// 自检 = 真跑一次解释器，并且**必须验"同目录模块 import 能通"**。
    /// 这一条是 <c>*._pth</c> 是否漏删的唯一可靠判据：文件在、python.exe 也能跑，
    /// 但 isolated 模式下同目录 import 会失败 —— 那种状态会让 local-rag 那类 Skill 静默失效。
    /// 探针写法与 <c>fetch-python-runtime.ps1</c> 一致。
    /// </summary>
    private static async Task<(bool Ok, string Detail)> VerifyPythonAsync(string dir, CancellationToken ct)
    {
        var exe = Path.Combine(dir, "python.exe");
        if (!File.Exists(exe)) return (false, "缺少 python.exe");

        var probeDir = Path.Combine(Path.GetTempPath(), "fc-pyprobe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probeDir);
            File.WriteAllText(Path.Combine(probeDir, "sib.py"), "V = 'ok'", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(probeDir, "probe.py"), ProbeScript, new UTF8Encoding(false));

            var psi = SkillProcess.Build(exe, new[] { Path.Combine(probeDir, "probe.py") }, probeDir, false);
            var r = await SkillProcess.RunAsync(psi, null, 60_000, ct).ConfigureAwait(false);
            if (r.StartError != null) return (false, $"无法启动：{r.StartError}");
            if (r.TimedOut) return (false, "启动后 60 秒无响应");

            var text = r.Combined;
            if (text.Contains("\"sibling_import\": true")) return (true, "同目录 import 通过");
            if (text.Contains("\"sibling_import\": false"))
                return (false, "同目录模块 import 不通（多半是 *._pth 没删干净 → Python 进了 isolated 模式）");
            return (false, $"自检输出无法判读：{Short(text)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(probeDir, true); } catch { /* 临时探针目录，删不掉不影响结论 */ }
        }
    }

    /// <summary>把一段输出压成单行短文本（本地版本，避免为一个截断函数把整条依赖链拖进快层）</summary>
    internal static string Short(string? s)
    {
        var t = (s ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 200 ? t : t[..200] + "…";
    }

    /// <summary>与 <c>fetch-python-runtime.ps1</c> 里那个探针同口径（连字段名都一致，便于对照）</summary>
    private const string ProbeScript = """
import sys, json
r = {"py": sys.version.split()[0]}
try:
    import sib
    r["sibling_import"] = (sib.V == "ok")
except Exception:
    r["sibling_import"] = False
try:
    import json as _j, urllib.request, sqlite3, subprocess, pathlib, ssl
    r["stdlib"] = True
except Exception:
    r["stdlib"] = False
r["sys_path_len"] = len(sys.path)
print(json.dumps(r))
""";
}
