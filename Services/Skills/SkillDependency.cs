using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>外部依赖的登录态（把 CLI 的原始状态词归一成宿主自己的语义）</summary>
public enum DependencyAuth
{
    /// <summary>没探测出来（命令没跑起来 / 输出看不懂）—— 按"未知"处理，绝不乐观地当成已授权</summary>
    Unknown,

    /// <summary>可执行文件在位，但用户身份没有 token</summary>
    Missing,

    /// <summary>有 token 但已失效</summary>
    Expired,

    /// <summary>
    /// 可执行文件在位，但**连应用凭据都还没配**（2026-09-21 增，第一次使用时的常见状态）。
    ///
    /// <para>
    /// 与 <see cref="Missing"/> 的区别很关键：Missing 是"扫一次码就能好"，
    /// NotConfigured 是"扫码根本开始不了"（拿不到 device_code → 没有验证链接 → 二维码画不出来）。
    /// 当初那场事故就是没把这两者分开：用户看到的是"永远停在正在生成二维码"，
    /// 而真正该做的事是"先去创建一个应用"。
    /// </para>
    /// </summary>
    NotConfigured,

    /// <summary>可用</summary>
    Ready,
}

/// <summary>一次探测的结论（只描述事实，措辞由调用方决定 —— UI 含糊但日志要精确）</summary>
public sealed record DependencyStatus(
    string Id,
    string DisplayName,
    bool Resolved,
    string? ExePath,
    DependencyAuth Auth,
    string Account,
    string Detail)
{
    /// <summary>在位但登录态不是 ready —— 这时候授权是有意义的动作（含「还没配应用凭据」）</summary>
    public bool NeedsAuth => Resolved && Auth != DependencyAuth.Ready;

    /// <summary>
    /// 需要**先做前置配置**才能谈授权（2026-09-21 增）。
    /// 有它，界面才能先走"创建应用"再走"扫码"；没有它，窗口只会永远停在"正在生成二维码"。
    /// </summary>
    public bool NeedsPrepare => Resolved && Auth == DependencyAuth.NotConfigured;

    /// <summary>能不能做授权（文件不在就没得授权，只能先解决"没有它"这件事）</summary>
    public bool CanAuthorize => Resolved;
}

/// <summary>设备码一次会话（<c>auth login --no-wait --json</c> 的返回）</summary>
public sealed record DeviceCodeSession(string DeviceCode, string VerificationUrl, int ExpiresInSeconds);

/// <summary>
/// 可执行文件是从哪一档找到的（只用于展示，不参与任何判定）。
/// 与 <see cref="SkillRuntimeLocations"/> 的候选顺序一一对应。
/// </summary>
public enum DependencySource
{
    /// <summary>说不清（路径为空 / 取不到所在目录）</summary>
    Unknown,

    /// <summary>随包分发的那一份（<c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c>）</summary>
    Bundled,

    /// <summary>按需下载落在数据目录的那一份（<c>&lt;数据根&gt;\runtime\&lt;id&gt;\</c>）</summary>
    DataDir,

    /// <summary>使用者自己装的那一份（系统 PATH 里找到的）</summary>
    SystemPath,
}

/// <summary>
/// 一个外部依赖：某个外部可执行程序 + 它的登录态 + 它的授权协议（2026-09-20；2026-09-21 拆出协议）。
///
/// <para>
/// <b>为什么需要这一层：</b>Skill 的脚本常常要调外部 CLI（写飞书要 lark-cli、发 GitHub 要 gh…），
/// 而这些 CLI 大多需要"先登录"。阶段一交付时这块是**空的** —— 结果是模型自己发挥了：
/// 它照着 CLI 输出里的提示，让用户去终端敲 <c>lark-cli auth login</c>，还把某个应用安装目录的
/// 绝对路径当命令写给了用户。**这是宿主该做的事被外包给了用户**，不是措辞问题。
/// </para>
/// <para>
/// <b>本类只干三件事：</b>① 找到可执行文件（<see cref="Locate"/>）；
/// ② 起进程去探测登录态（<see cref="ProbeAsync"/>）；③ 判断这个 Skill 用没用到它（<see cref="Matches"/>）。
/// <b>授权协议本身不在本类</b> —— 它归 <see cref="IDepAuthFlow"/> 的实现
/// （2026-09-21 拆的：拆之前申请码/出码/领 token/解析全在本类的 <c>virtual</c> 默认实现里，
/// 而那里面写死了 lark 的命令与字段名，等于把一个"通用依赖层"变成了"飞书专用层"）。
/// </para>
/// <para>
/// <b>定位顺序（刻意的）：</b>① 应用自带的 <c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c> —— 确定、可随包分发；
/// ② 数据目录 <c>&lt;数据根&gt;\runtime\&lt;id&gt;\</c> —— 按需下载落这里（2026-09-21 增，R2/R3）；
/// ③ 系统 PATH —— 兼容用户自己装过的情况。**不认识"别的应用安装目录里恰好有一份"**：
/// 那既不可移植，也让宿主的行为取决于别人装了什么。规则唯一入口 = <see cref="SkillRuntimeLocations"/>。
/// </para>
/// <para>
/// <b>两步才能用起来（这台机器上）：</b>先有<b>应用凭据</b>（<c>config init</c>，每台机器各建一次），
/// 再有<b>用户授权</b>（设备码流）。少了第一步，第二步连码都发不出来 —— 见
/// <see cref="IDepAuthFlow.PrepareAsync"/>。
/// </para>
/// </summary>
public class SkillDependency
{
    /// <summary>探测类命令的超时（扫码要人操作，不能用执行脚本那套 150 秒）</summary>
    private const int ProbeTimeoutMs = 30_000;

    private readonly string? _baseDir;

    /// <param name="id">稳定标识，同时也是运行时候选目录名（<c>runtime\&lt;id&gt;\</c>）</param>
    /// <param name="displayName">给用户看的名字</param>
    /// <param name="exeName">可执行文件名（不含扩展名）</param>
    /// <param name="scriptMarkers">静态扫描脚本时用来判断"这个 Skill 用到了它"的关键词</param>
    /// <param name="baseDir">应用目录（<c>AppContext.BaseDirectory</c>）</param>
    public SkillDependency(
        string id,
        string displayName,
        string exeName,
        IReadOnlyList<string> scriptMarkers,
        string? baseDir)
    {
        Id = id;
        DisplayName = displayName;
        ExeName = exeName;
        ScriptMarkers = scriptMarkers;
        _baseDir = string.IsNullOrEmpty(baseDir) ? null : baseDir;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ExeName { get; }
    public IReadOnlyList<string> ScriptMarkers { get; }

    /// <summary>授权时申请的权限域（飞书概念：base=多维表格，docs=文档，drive=云盘，sheets=表格，wiki=知识库）</summary>
    public virtual IReadOnlyList<string> AuthDomains => Array.Empty<string>();

    /// <summary>给用户看的权限域说明（透明优先：让他在扫码前知道要批什么）</summary>
    public virtual string AuthScopeText => "";

    /// <summary>
    /// 这个依赖的授权协议；<b>null = 它不需要登录</b>（"在位即可用"那类，如将来的 ffmpeg / gh 只读用法）。
    /// 界面据此决定要不要给"授权"按钮 —— 没有协议的依赖点了也没意义。
    /// </summary>
    public virtual IDepAuthFlow? Flow => null;

    /// <summary>自带目录（候选里的第一档）</summary>
    public string? BundledDir => SkillRuntimeLocations.BundledDir(_baseDir, Id);

    /// <summary>
    /// 候选目录清单，顺序即优先级：<b>自带 → 数据目录</b>（规则见 <see cref="SkillRuntimeLocations"/>）。
    /// 执行器会把**全部**候选前置到子进程 PATH —— 只前置自带那一份时，
    /// 按需下载到数据目录的 CLI 在脚本里 <c>shutil.which()</c> 会找不到。
    /// </summary>
    public IReadOnlyList<string> CandidateDirs => SkillRuntimeLocations.CandidateDirs(_baseDir, Id);

    /// <summary>
    /// 这个可执行文件是从哪一档找到的（设置页显示「应用自带 / 已下载 / 系统 PATH」用）。
    /// 判据是**所在目录相等**，不是字符串前缀 —— 前缀判法会把相邻目录（如 <c>lark-cli-old\</c>）误判成同一档。
    /// </summary>
    public DependencySource SourceOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return DependencySource.Unknown;

        string? dir;
        try { dir = Path.GetDirectoryName(exePath!); } catch { dir = null; }
        if (string.IsNullOrEmpty(dir)) return DependencySource.Unknown;

        if (SameDir(dir!, BundledDir)) return DependencySource.Bundled;
        if (SameDir(dir!, SkillRuntimeLocations.DataDir(Id))) return DependencySource.DataDir;
        return DependencySource.SystemPath;
    }

    private static bool SameDir(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        return string.Equals(TrimEnd(a), TrimEnd(b!), StringComparison.OrdinalIgnoreCase);

        static string TrimEnd(string p) =>
            p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>确认这个依赖被某段脚本文本用到（大小写不敏感的朴素匹配，宁可多判不可漏判）</summary>
    public bool Matches(string scriptText)
    {
        if (string.IsNullOrEmpty(scriptText)) return false;
        foreach (var m in ScriptMarkers)
            if (scriptText.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>定位可执行文件：候选目录（自带 → 数据目录）优先，其次系统 PATH。找不到返回 null（**不抛**）</summary>
    public string? Locate() =>
        SkillRuntimeLocations.FindInCandidates(_baseDir, Id, ExeName)
        ?? SkillRuntimeLocations.FindOnPath(ExeName);

    /// <summary>
    /// 探测登录态。**永不抛** —— 探测不出来就是 <see cref="DependencyAuth.Unknown"/>，
    /// 绝不因为"读不懂输出"而当成已授权或未授权。
    ///
    /// <para>保留 <c>virtual</c> 是有意的：有些依赖的"在不在位"根本不需要起进程（或需要别的判据），
    /// 实现可以整体覆盖；默认实现是"起进程 → 交给协议解析"。</para>
    /// </summary>
    public virtual async Task<DependencyStatus> ProbeAsync(CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null)
            return new DependencyStatus(Id, DisplayName, false, null, DependencyAuth.Unknown, "",
                $"未找到 {ExeName}（应用未自带、按需目录里没有、系统 PATH 里也没有）");

        // 没有授权协议的依赖：文件在位就是可用，不去跑任何探测命令
        // （基类里**不再写死任何 CLI 的命令** —— 那正是 2026-09-21 拆出 flow 的直接原因）
        if (Flow == null)
            return new DependencyStatus(Id, DisplayName, true, exe, DependencyAuth.Ready, "", "在位（无需登录）");

        var psi = SkillProcess.Build(exe, Flow.ProbeArgs, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, ProbeTimeoutMs, ct).ConfigureAwait(false);

        if (r.StartError != null)
            return new DependencyStatus(Id, DisplayName, true, exe, DependencyAuth.Unknown, "",
                $"状态查询无法启动：{r.StartError}");
        if (r.TimedOut)
            return new DependencyStatus(Id, DisplayName, true, exe, DependencyAuth.Unknown, "", "状态查询超时");

        // ⚠ 刻意**不**在退出码非 0 时一刀切判"探测失败"：实测"未配置凭据"时退出码就是 3，
        //    而那条输出里带着判据（not_configured）+ 它走的是 stderr。一刀切会把
        //    "第一次用、该去创建应用"误报成"查询失败"，用户拿到的指引正好是错的。
        //    两路输出都交给协议去解析，判不出来的才落到 Unknown。
        return Flow.ParseStatus(exe, r.Stdout, r.Stderr);
    }

    /// <summary>
    /// 前置配置：授权之前"把前提搞齐"（2026-09-21 增）。没有协议的依赖直接返回"无需准备"。
    /// </summary>
    public Task<DepPrepareResult> PrepareAsync(Func<string, Task>? showVerificationUrl, CancellationToken ct = default)
        => Flow == null
            ? Task.FromResult(DepPrepareResult.NotNeeded)
            : Flow.PrepareAsync(showVerificationUrl, ct);

    /// <summary>把一段输出压成单行短文本（日志与细节文案用）—— 两个实现（基类与协议）共用</summary>
    internal static string Short(string? s)
    {
        var t = (s ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 200 ? t : t[..200] + "…";
    }
}

/// <summary>
/// 依赖表：**注册式**（2026-09-21 由硬编码数组改）。
///
/// <para>
/// 改之前是 <c>All() =&gt; new[] { new LarkCliDependency(baseDir) }</c> —— 加一个依赖必须改这一行，
/// 也就意味着"机制层"每次都要为一个具体 CLI 动一次刀。现在加依赖 = **写一个类 + 注册一行**：
/// </para>
/// <code>
/// SkillDependencies.Register(baseDir =&gt; new MyCliDependency(baseDir));
/// </code>
/// <para>
/// 内置依赖的注册点在本类的静态构造函数里（唯一一处），外部依赖包通过 <see cref="Register"/> 追加。
/// </para>
/// </summary>
public static class SkillDependencies
{
    /// <summary>飞书 CLI 的依赖标识（同时也是运行时候选目录名 <c>runtime\lark-cli\</c>）</summary>
    public const string LarkCliId = "lark-cli";

    private static readonly object Gate = new();
    private static readonly List<Func<string?, SkillDependency>> Factories = new();

    static SkillDependencies()
    {
        // 内置依赖的注册处（唯一一处）：加一个内置依赖就在这儿加一行
        Register(baseDir => new LarkCliDependency(baseDir));
    }

    /// <summary>
    /// 注册一个依赖。<paramref name="factory"/> 收应用目录、返回依赖实例。
    /// **加依赖不需要动 <see cref="All"/>**，也不需要在共享代码里引用具体 CLI 类型。
    /// </summary>
    public static void Register(Func<string?, SkillDependency> factory)
    {
        if (factory == null) return;
        lock (Gate) Factories.Add(factory);
    }

    /// <summary>
    /// 全部已知依赖。<paramref name="baseDir"/> 传应用目录。
    ///
    /// <para>同 <c>Id</c> 只保留先注册的那一项（重复注册不该让界面上出现两行同一依赖）；
    /// 某个 factory 造不出来就跳过 —— 一个依赖出问题不许拖垮整张表。</para>
    /// </summary>
    public static IReadOnlyList<SkillDependency> All(string? baseDir)
    {
        var list = new List<SkillDependency>();
        lock (Gate)
        {
            foreach (var factory in Factories)
            {
                SkillDependency? dep;
                try { dep = factory(baseDir); }
                catch { continue; }
                if (dep == null) continue;
                if (list.Any(d => string.Equals(d.Id, dep.Id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(dep);
            }
        }
        return list;
    }

    /// <summary>从脚本文本里找出该 Skill 用到的依赖（用来做"跑之前先看缺什么"的预检）</summary>
    public static IReadOnlyList<SkillDependency> DetectIn(
        IReadOnlyList<SkillDependency> all, IEnumerable<string> scriptTexts)
    {
        var hit = new List<SkillDependency>();
        foreach (var dep in all)
        {
            foreach (var text in scriptTexts)
            {
                if (dep.Matches(text) && !hit.Contains(dep)) { hit.Add(dep); break; }
            }
        }
        return hit;
    }

    /// <summary>
    /// 读一个 Skill 的脚本文本（依赖静态扫描用）。**读不动就跳过、绝不抛** ——
    /// 漏检只意味着少一次预检，不该影响别的任何事。
    /// </summary>
    public static List<string> ReadScriptTexts(SkillInfo skill, int maxCharsPerFile = 200_000)
    {
        var texts = new List<string>();
        if (skill == null) return texts;
        foreach (var name in skill.ScriptFiles)
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(skill.RootPath, "scripts", name));
                texts.Add(text.Length > maxCharsPerFile ? text[..maxCharsPerFile] : text);
            }
            catch { /* 跳过 */ }
        }
        return texts;
    }

    /// <summary>一个 Skill 用到了哪些已注册依赖（执行器预检与 load_skill 说明共用同一套判据）</summary>
    public static IReadOnlyList<SkillDependency> DetectInSkill(
        IReadOnlyList<SkillDependency> all, SkillInfo skill) =>
        DetectIn(all, ReadScriptTexts(skill));
}

/// <summary>
/// 飞书 CLI 的具体接线：命令名、权限域、以及它的授权协议（机制见 <see cref="SkillDependency"/>，
/// 协议见 <see cref="LarkDeviceCodeFlow"/>）。
/// </summary>
internal sealed class LarkCliDependency : SkillDependency
{
    public LarkCliDependency(string? baseDir)
        : base(
            id: SkillDependencies.LarkCliId,
            displayName: "飞书（lark-cli）",
            exeName: "lark-cli",
            scriptMarkers: new[] { "lark-cli", "lark_cli", "larkcli" },
            baseDir: baseDir)
    {
        Flow = new LarkDeviceCodeFlow(this);
    }

    /// <summary>
    /// 权限域是<b>判断而非权威依据</b>：取自 Skill 生态里最常见的写入目标
    /// （多维表格 / 文档 / 云盘 / 电子表格 / 知识库）。窗口会把这份清单原文显示给用户，
    /// 让他扫码前就知道要批什么；要收窄或扩大只改这一处。
    /// </summary>
    public override IReadOnlyList<string> AuthDomains { get; } =
        new[] { "base", "docs", "drive", "sheets", "wiki" };

    public override string AuthScopeText => "多维表格 / 文档 / 云盘 / 电子表格 / 知识库";

    public override IDepAuthFlow? Flow { get; }
}
