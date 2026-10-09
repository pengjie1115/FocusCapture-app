using System.IO.Compression;
using System.Net.Http;
using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>安装进度（给界面做行内进度用）。<see cref="Percent"/> 在拿不到总长度时为 null。</summary>
public sealed record RuntimeProgress(string Phase, long Received, long? Total)
{
    public int? Percent => Total is > 0
        ? (int)Math.Clamp(Received * 100 / Total.Value, 0, 100)
        : null;

    /// <summary>
    /// 给使用者看的一行字（两个入口共用同一份措辞，免得两处说法不一致）。
    /// 拿得到总长度时补百分比 —— 47MB 的下载没有百分比会让人以为卡死。
    /// </summary>
    public string Describe() => Percent is int p ? $"{Phase}（{p}%）" : Phase;
}

/// <summary>一次安装的结果（措辞由界面给，这里只搬事实）</summary>
public sealed record RuntimeInstallResult(
    bool Ok,
    bool AlreadyPresent,
    string TargetDir,
    string Detail)
{
    /// <summary>安装/下载失败时的失败结果</summary>
    public static RuntimeInstallResult Fail(string targetDir, string detail) =>
        new(false, false, targetDir, detail);
}

/// <summary>
/// 运行时部件（外部 CLI / 内置 Python）的按需下载器（2026-09-21，授权闭环步骤 5）。
///
/// <para>
/// <b>流程（所有部件共用，差异全在 <see cref="RuntimeRecipe"/>）：</b>
/// ① 目标目录已装好且自检通过 → 什么都不做（数据目录那份归使用者，不动它）；
/// ② 逐个下载源试（国内镜像优先、官方兜底），下到临时文件，边下边报进度；
/// ③ 解压到**暂存目录**（不是直接进目标目录！），按配方删掉必须删的文件（如 Python 的 <c>*._pth</c>）；
/// ④ **在暂存目录上自检** —— 只有真的能跑才提交到目标目录，失败就把暂存目录清掉；
/// ⑤ 提交：目标不存在就整体搬过去；已存在（半成品）就逐文件覆盖 —— **绝不删除目标里已有的东西**。
/// </para>
/// <para>
/// <b>永不抛</b>：网络断、403、磁盘满、压缩包结构变了，一律通过返回值如实报告。
/// 这是 UI 事件路径上调用的东西，抛出去就是那个吃掉点击的模态框。
/// </para>
/// <para>
/// <b>绝不留下半成品</b>：先自检后提交，所以失败时目标目录要么还是原样、要么整个不存在 ——
/// 不会出现"看起来装上了但跑不起来"这种最难查的状态。
/// </para>
/// </summary>
public sealed class RuntimeDownloader
{
    /// <summary>整体超时：47MB 走国内镜像通常几十秒；给足余量但别无限等</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(20);

    /// <summary>暂存目录名的后缀（与目标目录同级，候选目录扫描不会把它当成部件）</summary>
    internal const string StagingSuffix = ".staging-";

    private readonly HttpClient _http;

    /// <summary>
    /// 日志回调（可选）。**刻意不用 <c>AppLog</c>**：那会经 <c>AppSettings</c> 拖进一整条依赖链，
    /// 而本类要能被快层检查点直接链接编译 —— 下载器的错法都是"静默"的（下了一半算成功、
    /// 该删的没删），正需要"每次改动都跑"的那一层守着。生产由调用方接上 AppLog。
    /// </summary>
    public Action<string>? OnLog { get; set; }

    /// <param name="handler">仅检查点用（注入假 HttpClientHandler）；生产不传</param>
    public RuntimeDownloader(HttpMessageHandler? handler = null)
    {
        _http = handler == null
            ? new HttpClient { Timeout = RequestTimeout }
            : new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };
    }

    private void Log(string message)
    {
        try { OnLog?.Invoke(message); } catch { /* 日志失败不许影响安装 */ }
    }

    /// <summary>
    /// 安装一个部件。见类注释的流程说明。**永不抛**。
    /// </summary>
    /// <param name="recipe">下载配方</param>
    /// <param name="progress">进度回调；可为 null</param>
    /// <param name="ct">取消令牌（界面在下载中途关窗时用）</param>
    public async Task<RuntimeInstallResult> InstallAsync(
        RuntimeRecipe recipe,
        IProgress<RuntimeProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (recipe == null) return RuntimeInstallResult.Fail("", "没有下载配方。");

        var targetDir = SkillRuntimeLocations.DataDir(recipe.Id);
        if (string.IsNullOrEmpty(targetDir))
            return RuntimeInstallResult.Fail("", $"拿不到数据目录位置，无法安装{recipe.DisplayName}。");

        // ── ① 已经装好就不再折腾 ──
        // ⚠ 必须先判目录在不在，再调自检 —— Verify 的契约是"这个目录里的东西能不能用"，
        // 对**不存在**的目录它未必返回 false（有的配方只查"文件齐不齐"，齐了就算过）。
        // 少了这一句，一个宽松的自检就会把"压根没下载"报成"已经装好" —— 正是本项目最忌的假装成功。
        // （2026-09-21 由检查点 [10] 逮住：那一组里有两个用例故意用了恒真的自检桩。）
        if (Directory.Exists(targetDir))
        {
            try
            {
                var existing = await recipe.Verify(targetDir, ct).ConfigureAwait(false);
                if (existing.Ok)
                    return new RuntimeInstallResult(true, true, targetDir, existing.Detail);
            }
            catch (Exception ex)
            {
                // 自检自己出错不算"已装好"，继续往下走；但记下来，最后失败时它是线索
                Log($"警告：安装前自检异常（{recipe.Id}）：{ex.Message}");
            }
        }

        var errors = new List<string>();

        // ── 重试前先清扫上一次的暂存残留 ──
        // 2026-09-22 真机事故：lark-cli 在用户机上连续 8 次失败，runtime\ 下留下 7 个
        // lark-cli.staging-* 目录 —— 失败路径的 finally 删除也被同一个锁挡住（被安全软件
        // 扫描中的目录删不掉），残留目录既误导用户又没人提示。这里每次安装前先扫一遍，
        // 清得掉就清（记日志），清不掉也记日志，至少不再无声。
        var parentDir = Path.GetDirectoryName(targetDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
        {
            try
            {
                foreach (var stale in Directory.EnumerateDirectories(parentDir, recipe.Id + StagingSuffix + "*"))
                {
                    try
                    {
                        Directory.Delete(stale, true);
                        Log($"安装 {recipe.Id}：已清扫历史暂存目录 {Path.GetFileName(stale)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"警告：安装 {recipe.Id}：历史暂存目录 {Path.GetFileName(stale)} 清不掉" +
                            $"（多半被安全软件锁定），可手动删除：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"警告：安装 {recipe.Id}：扫描历史暂存目录失败：{ex.Message}");
            }
        }

        foreach (var url in recipe.Urls)
        {
            ct.ThrowIfCancellationRequested();
            var attempts = await TryOneSourceAsync(recipe, url, targetDir, progress, ct).ConfigureAwait(false);
            if (attempts.Ok) return attempts;
            errors.Add($"{HostOf(url)}：{attempts.Detail}");
        }

        var failMessage = $"所有下载源都没成功。\n{string.Join("\n", errors)}";
        if (LooksLikeSecurityBlock(errors))
            failMessage += SecurityBlockHint;
        return RuntimeInstallResult.Fail(targetDir, failMessage);
    }

    /// <summary>试一个下载源：下 → 解压到暂存 → 删该删的 → 自检 → 提交</summary>
    private async Task<RuntimeInstallResult> TryOneSourceAsync(
        RuntimeRecipe recipe,
        string url,
        string targetDir,
        IProgress<RuntimeProgress>? progress,
        CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var zipPath = Path.Combine(Path.GetTempPath(), $"fc-dl-{recipe.Id}-{token}.zip");
        var staging = Path.Combine(Path.GetDirectoryName(targetDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? Path.GetTempPath(),
            recipe.Id + StagingSuffix + token);

        try
        {
            progress?.Report(new RuntimeProgress($"正在从 {HostOf(url)} 下载", 0, null));
            await DownloadAsync(url, zipPath, recipe.MinBytes, progress, ct).ConfigureAwait(false);

            progress?.Report(new RuntimeProgress("正在解压", 0, null));
            Extract(recipe, zipPath, staging);

            foreach (var pattern in recipe.DeleteAfterExtract)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(staging, pattern, SearchOption.AllDirectories))
                    {
                        File.Delete(f);
                        Log($"安装 {recipe.Id}：已删除 {Path.GetFileName(f)}（{pattern}）");
                    }
                }
                catch (Exception ex)
                {
                    Log($"警告：安装 {recipe.Id}：删 {pattern} 失败：{ex.Message}");
                }
            }

            progress?.Report(new RuntimeProgress("正在自检", 0, null));
            var check = await recipe.Verify(staging, ct).ConfigureAwait(false);
            if (!check.Ok)
                return RuntimeInstallResult.Fail(targetDir, $"下载到了，但自检没通过：{check.Detail}");

            progress?.Report(new RuntimeProgress("正在安装", 0, null));
            Commit(staging, targetDir);

            Log($"运行时安装完成：{recipe.Id} → {targetDir}（{check.Detail}）");
            return new RuntimeInstallResult(true, false, targetDir, check.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"警告：从 {HostOf(url)} 安装 {recipe.Id} 失败：{ex.Message}");
            return RuntimeInstallResult.Fail(targetDir, ex.Message);
        }
        finally
        {
            // 临时件一律不留：半成品比没有更坏（使用者会以为已经装好了）。
            // 清不掉必须留日志 —— 2026-09-22 真机实测：暂存目录被安全软件锁住时删除
            // 会失败，静默吞掉的话用户就对着 7 个 staging 目录一头雾水。
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (Exception ex) { Log($"警告：安装 {recipe.Id}：临时下载文件删不掉：{ex.Message}"); }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex)
            {
                Log($"警告：安装 {recipe.Id}：暂存目录 {staging} 删不掉（多半被安全软件锁定），" +
                    $"已留作残留，可手动删除：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 失败原因是不是「本地磁盘写入/执行被拒」这一类（2026-09-22 真机事故的形状：
    /// 两个下载源报同一个 UnauthorizedAccess_IODenied —— 网络问题不会两源同错）。
    /// internal 供快层检查点直接验证（本文件被快层按源码链接编译）。
    /// 只做宽泛匹配给提示用，不参与成败判定。
    /// </summary>
    internal static bool LooksLikeSecurityBlock(IReadOnlyCollection<string> details) => details.Any(d =>
        d.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("IODenied", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("拒绝访问", StringComparison.Ordinal));

    /// <summary>命中 <see cref="LooksLikeSecurityBlock"/> 时附在失败详情末尾的人话提示</summary>
    private const string SecurityBlockHint =
        "\n\n提示：这不是网络问题 —— 安装包已经下载下来了，是在写入磁盘或运行自检时被系统拒绝。" +
        "最常见的原因是安全软件（Windows Defender / 360 / 火绒等）的实时防护拦下了未知的 exe。" +
        "请把本应用的 runtime 目录（%AppData%\\FocusCapture\\runtime）加入信任区或白名单后重试；" +
        "若 runtime 目录下有残留的 staging 文件夹，确认放行后可手动删除。";

    // ────────────────────────── 下载 ──────────────────────────

    private async Task DownloadAsync(
        string url,
        string zipPath,
        long minBytes,
        IProgress<RuntimeProgress>? progress,
        CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(zipPath))
        {
            var buf = new byte[128 * 1024];
            long received = 0;
            long lastReported = 0;

            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;

                // 节流：每 128KB 报一次就够刷新百分比了，报太密反而抢 UI 线程
                if (progress != null && received - lastReported >= buf.Length)
                {
                    lastReported = received;
                    progress.Report(new RuntimeProgress("正在下载", received, total));
                }
            }

            progress?.Report(new RuntimeProgress("下载完成", received, total ?? received));

            // 体积下限：拦"下到一个错误页 / 空壳却当成成功"（不是为了防篡改）
            if (received < minBytes)
                throw new InvalidOperationException(
                    $"下载内容只有 {received / 1024} KB，小于合理下限 {minBytes / 1024} KB —— " +
                    "多半是拿到了一个错误页而不是安装包。");
        }
    }

    // ────────────────────────── 解压 / 提交 ──────────────────────────

    /// <summary>按配方解压到暂存目录（整包解压 / 从包里挑一个文件）</summary>
    private static void Extract(RuntimeRecipe recipe, string zipPath, string staging)
    {
        Directory.CreateDirectory(staging);

        using var zip = ZipFile.OpenRead(zipPath);

        if (recipe.ExtractAll)
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;   // 目录项，靠解文件时建
                var dest = SafeJoin(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return;
        }

        // 从包里挑一个文件：**递归找**，不假设它摆在压缩包的哪一层
        // （官方包结构变过一次就是靠这个递归才没出事：fetch 脚本当初也是遍历找的）
        var want = (recipe.PickFileName ?? "").Trim();
        if (want.Length == 0) throw new InvalidOperationException("配方没有指定要从包里取哪个文件。");

        var hit = zip.Entries.FirstOrDefault(e =>
            !string.IsNullOrEmpty(e.Name) &&
            string.Equals(Path.GetFileName(e.Name), want, StringComparison.OrdinalIgnoreCase));

        if (hit == null)
            throw new InvalidOperationException(
                $"下载到的压缩包里没有 {want} —— 官方包结构可能变了（包里共 {zip.Entries.Count} 项）。");

        hit.ExtractToFile(Path.Combine(staging, want), overwrite: true);
    }

    /// <summary>防 zip 里的路径穿越（<c>../</c>）—— 只允许落在暂存目录内</summary>
    private static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"压缩包里有一项越界路径，已中止：{relative}");
        return full;
    }

    /// <summary>
    /// 把暂存目录提交到目标目录。目标不存在就整体搬过去；已存在就**逐文件覆盖**。
    /// 刻意不删目标里多出来的文件 —— 本项目的删除类动作只给使用者（AGENTS.md 红线 7 的同一条精神）。
    /// </summary>
    private static void Commit(string staging, string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(staging, targetDir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, file);
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }
}
