using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>
/// 飞书（lark-cli）的授权协议实现（2026-09-21 从 <see cref="SkillDependency"/> 基类搬出来）。
///
/// <para>
/// <b>为什么单独一个文件：</b>下面每一条命令、每一个字段名都是飞书独有的
/// （<c>auth status</c> / <c>auth login --no-wait --json</c> / <c>auth qrcode</c> /
/// <c>identities.user.available</c> / <c>config init --new</c>）。它们以前写在基类的
/// <c>virtual</c> 默认实现里，等于把"通用依赖层"变成"飞书专用层"。
/// </para>
/// <para>
/// <b>实测真值（2026-09-21，独立探针 <c>tools/depdiag</c>，非推测）：</b>
/// </para>
/// <list type="bullet">
/// <item>成功时 JSON 走 <b>stdout</b>、退出码 0；出错时 JSON 走 <b>stderr</b>（未配置凭据 = <c>not_configured</c>、退出码 3）</item>
/// <item><c>config init --new</c> 输出：<b>全走 stderr、纯文本 + ASCII 块二维码 + URL、非 JSON</b>，
///   启动约 1 秒吐完，然后**阻塞**等用户在浏览器里把应用建完才自然退出</item>
/// <item>抓到链接**不能 kill** —— 它是设备码流（user_code），kill 掉就没有主体接收结果，配置写不进去</item>
/// </list>
/// </summary>
internal sealed class LarkDeviceCodeFlow : IDepAuthFlow
{
    /// <summary>授权类命令的超时（扫码要人操作，不能用执行脚本那套 150 秒）</summary>
    private const int QuickCommandTimeoutMs = 30_000;

    /// <summary>
    /// <c>config init --new</c> 的整体上限。给 10 分钟是因为它要等用户在浏览器里把应用建出来 ——
    /// 这不是"卡住"，是设计如此（官方 README 也是这么用的：后台跑、抓链接、交给用户）。
    /// </summary>
    private const int PrepareTimeoutMs = 10 * 60_000;

    private readonly SkillDependency _owner;

    public LarkDeviceCodeFlow(SkillDependency owner) => _owner = owner;

    /// <summary>登录态探测命令。<b>判据是输出里的字段，不是退出码</b>（用户身份缺失时它照样返回 0）。</summary>
    public IReadOnlyList<string> ProbeArgs { get; } = new[] { "auth", "status" };

    // ────────────────────────── 登录态解析 ──────────────────────────

    public DependencyStatus ParseStatus(string? exePath, string stdout, string stderr)
    {
        // ① 成功路径：正常输出在 stdout（实测）
        var fromStdout = TryReadIdentities(exePath, stdout);
        if (fromStdout != null) return fromStdout;

        // ② 出错时的 JSON 走 stderr（实测 not_configured 就在这里）—— 两路都要看，只看一路会漏掉最关键的状态
        var fromStderr = TryReadIdentities(exePath, stderr);
        if (fromStderr != null) return fromStderr;

        // ③ 「还没配置应用凭据」是第一次使用时的**正常状态**，不是故障。
        //    必须与"读不懂输出"分开：混在一起的后果就是当初那场事故 ——
        //    用户看到的是一句像故障的话，而实际该做的是"去创建应用"。
        if (LooksNotConfigured(stdout) || LooksNotConfigured(stderr))
            return new DependencyStatus(_owner.Id, _owner.DisplayName, true, exePath,
                DependencyAuth.NotConfigured, "",
                "尚未配置应用凭据（第一次使用需要先在浏览器里创建一个飞书应用）");

        var combined = (stdout + " " + stderr).Trim();
        return new DependencyStatus(_owner.Id, _owner.DisplayName, true, exePath, DependencyAuth.Unknown, "",
            combined.Length == 0
                ? "状态查询没有任何输出"
                : $"状态输出读不懂：{SkillDependency.Short(combined)}");
    }

    /// <summary>
    /// 从一段输出里读 <c>identities.user</c>；读不出来（不是 JSON / 没有该节点）返回 null，
    /// 由调用方决定是"再看另一路"还是"判为未知"。**绝不因为读不懂就乐观当成已授权。**
    /// </summary>
    private DependencyStatus? TryReadIdentities(string? exe, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text!);
            var root = doc.RootElement;
            if (!root.TryGetProperty("identities", out var ids) ||
                !ids.TryGetProperty("user", out var user)) return null;

            var account = user.TryGetProperty("userName", out var n) ? (n.GetString() ?? "") : "";
            var available = user.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
            var word = user.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";

            var status = available || string.Equals(word, "ready", StringComparison.OrdinalIgnoreCase)
                ? DependencyAuth.Ready
                : string.Equals(word, "expired", StringComparison.OrdinalIgnoreCase)
                    ? DependencyAuth.Expired
                    : DependencyAuth.Missing;

            var detail = status switch
            {
                DependencyAuth.Ready => account.Length > 0 ? $"已授权：{account}" : "已授权",
                DependencyAuth.Expired => "授权已过期，需要重新授权",
                DependencyAuth.Missing => "尚未授权",
                _ => "状态未知",
            };
            return new DependencyStatus(_owner.Id, _owner.DisplayName, true, exe, status, account, detail);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 这段输出是不是"应用凭据还没配"。
    /// 优先按 JSON 结构判（稳），结构读不出来时退回朴素匹配 ——
    /// **宁可多认一次，也不要把这个状态漏成"读不懂"**（漏了就等于用户又看到一句像故障的话）。
    /// </summary>
    private static bool LooksNotConfigured(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text!);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("subtype", out var st) &&
                string.Equals(st.GetString(), "not_configured", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (JsonException) { /* 不是 JSON，走下面的朴素匹配 */ }

        return text!.Contains("not_configured", StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────── 授权协议 ──────────────────────────

    public async Task<(bool Ok, string Message, DeviceCodeSession? Session)> StartAuthAsync(CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return (false, $"未找到 {_owner.ExeName}，无法发起授权。", null);

        var args = new List<string> { "auth", "login", "--no-wait", "--json" };
        if (_owner.AuthDomains.Count > 0)
        {
            args.Add("--domain");
            args.Add(string.Join(",", _owner.AuthDomains));
        }

        var psi = SkillProcess.Build(exe, args, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            LogDetail("发起授权失败", r);     // R9：完整报错落盘（界面上仍给短句）
            // 未配置凭据时这里拿到的是 not_configured（退出码 3）—— 如实转达，别把它说成"网络问题"
            var detail = LooksNotConfigured(r.Stderr) || LooksNotConfigured(r.Stdout)
                ? "尚未配置应用凭据，无法发起授权（需要先创建飞书应用）。"
                : (r.TimedOut ? "发起授权超时。" : $"发起授权失败：{SkillDependency.Short(r.Combined)}");
            return (false, detail, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            var root = doc.RootElement;
            var code = root.TryGetProperty("device_code", out var c) ? c.GetString() : null;
            var url = root.TryGetProperty("verification_url", out var u) ? u.GetString() : null;
            var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 600;

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(url))
                return (false, $"授权响应缺少必要字段：{SkillDependency.Short(r.Stdout)}", null);

            return (true, "", new DeviceCodeSession(code!, url!, expires));
        }
        catch (JsonException)
        {
            return (false, $"授权响应不是合法 JSON：{SkillDependency.Short(r.Stdout)}", null);
        }
    }

    /// <summary>
    /// 把验证链接转成二维码 PNG，返回图片绝对路径（失败返回 null）。
    ///
    /// <para>
    /// ⚠ <b>统一用「相对名 + 调用方指定的 CWD」</b>，因为这里踩过一个版本差异（都实测过）：
    /// CLI <b>1.0.92</b> 拒绝绝对输出路径
    /// （<c>"unsafe output path: --output must be a relative path within the current directory"</c>，退出码 2），
    /// 而 <b>1.0.96</b> 接受绝对路径。相对名在两个版本上都成立 —— 选它是为了不依赖具体版本。
    /// </para>
    /// </summary>
    public async Task<string?> MakeQrPngAsync(string verificationUrl, string workDir, CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return null;

        const string fileName = "qr.png";
        var args = new[] { "auth", "qrcode", verificationUrl, "-o", "./" + fileName, "--size", "360" };
        var psi = SkillProcess.Build(exe, args, workDir, false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            LogDetail("二维码生成失败", r);   // R9：完整输出落盘，别只留 200 字符
            return null;
        }

        var png = Path.Combine(workDir, fileName);
        // 产物优先：不信它输出的 JSON，只认文件真的在
        return File.Exists(png) ? png : null;
    }

    /// <summary>用 device_code 领回 token（这一步会阻塞轮询，直到用户扫码确认或超时）。</summary>
    public async Task<(bool Ok, string Message)> CompleteAuthAsync(
        string deviceCode, int timeoutMs, CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return (false, $"未找到 {_owner.ExeName}。");

        var args = new[] { "auth", "login", "--device-code", deviceCode };
        var psi = SkillProcess.Build(exe, args, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, timeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut)
        {
            LogDetail("等待扫码超时", r);
            return (false, "等待扫码超时（授权码已过期），请重新发起。");
        }
        if (!r.Ok)
        {
            LogDetail("授权未完成", r);
            return (false, $"授权未完成：{SkillDependency.Short(r.Combined)}");
        }
        return (true, "已授权");
    }

    public async Task<(bool Ok, string Message)> LogoutAsync(CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return (false, $"未找到 {_owner.ExeName}。");

        var psi = SkillProcess.Build(exe, new[] { "auth", "logout", "--json" }, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        if (!r.Ok) LogDetail("退出登录失败", r);
        return r.Ok ? (true, "已退出登录") : (false, $"退出登录失败：{SkillDependency.Short(r.Combined)}");
    }

    // ────────────────────────── 前置配置（应用凭据） ──────────────────────────

    /// <summary>创建应用用的命令（<c>--new</c> = 在浏览器里新建一个应用）</summary>
    internal static IReadOnlyList<string> ConfigInitArgs { get; } = new[] { "config", "init", "--new" };

    public async Task<DepPrepareResult> PrepareAsync(Func<string, Task>? showVerificationUrl, CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return DepPrepareResult.Failed($"未找到 {_owner.ExeName}，无法准备应用凭据。");

        // 已经配置好就什么都别做（第二次之后的机器都走这条）
        var before = await ProbeStatusAsync(ct).ConfigureAwait(false);
        if (before.Auth == DependencyAuth.Unknown)
            return DepPrepareResult.Failed($"无法确认应用凭据状态：{before.Detail}");
        if (before.Auth != DependencyAuth.NotConfigured) return DepPrepareResult.NotNeeded;

        if (showVerificationUrl == null)
            return DepPrepareResult.Failed(
                "需要先创建一个飞书应用，但当前没有可以展示配置链接的界面。");

        AppLog.Info("Skill", $"{_owner.Id} 开始前置配置：创建飞书应用（需用户在浏览器完成）");

        // 跑 config init --new：启动约 1 秒把验证链接吐出来（走 stderr），然后**一直阻塞**
        // 等用户在浏览器里把应用建完才自然退出。
        // ⚠ 抓到链接**不能 kill** —— 这是设备码流，kill 掉就没有主体接收结果，配置写不进去。
        var psi = SkillProcess.Build(exe, ConfigInitArgs, Path.GetDirectoryName(exe) ?? "", false);
        var urlReported = false;

        var r = await SkillProcess.RunAsync(psi, null, PrepareTimeoutMs, ct, line =>
        {
            if (urlReported) return;
            var url = DepAuthText.ExtractVerificationUrl(line);
            if (url == null) return;
            urlReported = true;
            _ = ReportUrlAsync(showVerificationUrl, url);
        }).ConfigureAwait(false);

        if (r.StartError != null)
        {
            LogDetail("前置配置无法启动", r);
            return DepPrepareResult.Failed($"无法开始创建应用：{r.StartError}");
        }
        if (r.TimedOut)
        {
            LogDetail("前置配置等待超时（浏览器里未完成）", r);
            return DepPrepareResult.Failed("等待在浏览器里完成应用创建超时，请重新发起。");
        }
        if (r.ExitCode != 0)
        {
            LogDetail("前置配置未完成", r);
            return DepPrepareResult.Failed($"创建应用没有完成（退出码 {r.ExitCode}）。");
        }

        // 产物判据：配置真的写进去了才算成功 —— 不信退出码那句"完成"
        var after = await ProbeStatusAsync(ct).ConfigureAwait(false);
        if (after.Auth == DependencyAuth.NotConfigured || after.Auth == DependencyAuth.Unknown)
            return DepPrepareResult.Failed("创建流程已退出，但应用凭据仍未就绪，请重试。");

        AppLog.Info("Skill", $"{_owner.Id} 前置配置完成：{after.Detail}");
        return DepPrepareResult.Done("应用凭据已就绪");
    }

    // ────────────────────────── 兜底入口：用现成凭据 ──────────────────────────

    /// <summary>
    /// 兜底入口的字段。文案要写清"去哪儿复制"，用户在飞书开放平台里找得到这两样。
    /// </summary>
    public IReadOnlyList<DepCredentialField> CredentialFields { get; } = new[]
    {
        new DepCredentialField("appId", "应用 App ID（飞书开放平台 → 凭证与基础信息）", false),
        new DepCredentialField("appSecret", "应用 App Secret（同一页，只显示一次）", true),
    };

    public async Task<DepPrepareResult> PrepareWithCredentialAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var exe = _owner.Locate();
        if (exe == null) return DepPrepareResult.Failed($"未找到 {_owner.ExeName}，无法配置应用凭据。");

        var appId = values.TryGetValue("appId", out var id) ? (id ?? "").Trim() : "";
        var secret = values.TryGetValue("appSecret", out var s) ? (s ?? "") : "";
        if (appId.Length == 0) return DepPrepareResult.Failed("请填写应用 App ID。");
        if (secret.Length == 0) return DepPrepareResult.Failed("请填写应用 App Secret。");

        // 凭据纪律（硬线）：secret **只经 stdin** 交给 CLI，既不进命令行也不进日志。
        // 日志里连 app-id 都不写全，只记长度 —— 够定位问题，又不留痕。
        var args = new[] { "config", "init", "--app-id", appId, "--brand", "feishu", "--app-secret-stdin" };
        var psi = SkillProcess.Build(exe, args, Path.GetDirectoryName(exe) ?? "", redirectStdin: true);

        AppLog.Info("Skill",
            $"{_owner.Id} 用现成凭据做前置配置（appId {appId.Length} 字符、secret {secret.Length} 字符，值均不记录）");

        ProcessResult r;
        try
        {
            // 结尾补一个换行：读行式实现靠它收尾；随后 SkillProcess 会关闭 stdin（= EOF）
            r = await SkillProcess.RunAsync(psi, secret + "\n", QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Skill", $"{_owner.Id} 用现成凭据配置时异常", ex);
            return DepPrepareResult.Failed("配置应用凭据时出错，请重试。");
        }

        if (r.StartError != null)
        {
            LogDetail("用现成凭据配置无法启动", r);
            return DepPrepareResult.Failed($"无法开始配置：{r.StartError}");
        }
        if (r.TimedOut)
        {
            LogDetail("用现成凭据配置超时", r, secret);
            return DepPrepareResult.Failed("配置应用凭据超时，请重试。");
        }
        if (r.ExitCode != 0)
        {
            LogDetail("用现成凭据配置失败", r, secret);
            return DepPrepareResult.Failed($"应用凭据配置失败（退出码 {r.ExitCode}），请核对 ID 与 Secret。");
        }

        // 产物判据：命令说成功不算，状态真的不再是"未配置"才算
        var after = await ProbeStatusAsync(ct).ConfigureAwait(false);
        if (after.Auth == DependencyAuth.NotConfigured || after.Auth == DependencyAuth.Unknown)
        {
            AppLog.Warn("Skill", $"{_owner.Id} 凭据配置命令已退出，但状态仍未就绪：{after.Detail}");
            return DepPrepareResult.Failed("配置命令已完成，但应用凭据仍未就绪，请核对 ID 与 Secret。");
        }

        AppLog.Info("Skill", $"{_owner.Id} 前置配置完成（用现成凭据）");
        return DepPrepareResult.Done("应用凭据已就绪");
    }

    /// <summary>
    /// 把**完整输出**落进日志（R9）。
    ///
    /// <para>
    /// 修之前：失败时只把报错截到 200 字符交给界面，日志里什么都没有 ——
    /// 用户来问"为什么失败"，翻日志翻不到任何线索，只能靠复现。
    /// </para>
    /// <para>
    /// <paramref name="scrub"/> 非空时先把该串从输出里抹掉：CLI 有可能把凭据回显出来，
    /// **宁可日志里缺一段，也不许落一份凭据**（凭据纪律是硬线，没有例外）。
    /// </para>
    /// </summary>
    private void LogDetail(string what, ProcessResult r, string? scrub = null)
    {
        try
        {
            var text = r.Combined;
            if (!string.IsNullOrEmpty(scrub)) text = text.Replace(scrub!, "***REDACTED***");
            if (text.Length > 4000) text = text[..4000] + "…（已截断）";
            AppLog.Warn("Skill",
                $"{_owner.Id} {what}：exit={r.ExitCode} 超时={r.TimedOut} 启动错误={r.StartError}\n{text}");
        }
        catch { /* 日志失败绝不影响主流程 */ }
    }

    /// <summary>把链接交给界面 —— 不阻塞读流（回调是在读流线程上被调用的）</summary>
    private static async Task ReportUrlAsync(Func<string, Task> callback, string url)
    {
        try { await callback(url).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("Skill", $"展示配置链接失败：{ex.Message}"); }
    }

    /// <summary>跑一次状态查询（前置配置前后各用一次做对照）</summary>
    private async Task<DependencyStatus> ProbeStatusAsync(CancellationToken ct)
    {
        var exe = _owner.Locate();
        if (exe == null)
            return new DependencyStatus(_owner.Id, _owner.DisplayName, false, null, DependencyAuth.Unknown, "",
                $"未找到 {_owner.ExeName}");

        var psi = SkillProcess.Build(exe, ProbeArgs, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);

        if (r.StartError != null)
            return new DependencyStatus(_owner.Id, _owner.DisplayName, true, exe, DependencyAuth.Unknown, "",
                $"状态查询无法启动：{r.StartError}");
        if (r.TimedOut)
            return new DependencyStatus(_owner.Id, _owner.DisplayName, true, exe, DependencyAuth.Unknown, "",
                "状态查询超时");

        return ParseStatus(exe, r.Stdout, r.Stderr);
    }
}
