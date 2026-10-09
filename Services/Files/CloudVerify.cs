namespace FocusCapture.Services.Files;

/// <summary>云端清单里的一条（路径 + 大小）。</summary>
public sealed class CloudEntry
{
    /// <summary>云端完整路径，形如 /apps/FocusCapture/files/xxx.md</summary>
    public string Path { get; init; } = "";

    public long Size { get; init; }
}

/// <summary>
/// 本地记录的判定输入 —— <b>刻意与 <see cref="FileMetadata"/> 解耦</b>。
///
/// 为什么解耦：本文件要进**快层检查点**（纯逻辑、零 I/O、零网络），而快层工程刻意不引用主项目
/// （见 tests/FocusCapture.Tests.csproj 的说明）。任何项目内类型（AppLog / 配置 / WPF）出现在这里，
/// 快层就会编译不过。所以这里只吃 BCL 类型，由调用方 CloudVerifyService 负责从
/// FileMetadata + 本机账本组装成这个扁平结构。
/// </summary>
public sealed class LocalRecord
{
    public string Id { get; init; } = "";

    /// <summary>元数据里记的云端路径</summary>
    public string NetPath { get; init; } = "";

    public bool Deleted { get; init; }

    /// <summary>见 <see cref="CloudStates"/>；快层不引主项目，故此处用字面量，取值必须一致。</summary>
    public string CloudState { get; init; } = "";

    /// <summary>本机账本里是否存在这条记录</summary>
    public bool HasLedgerEntry { get; init; }

    /// <summary>本机账本是否记过「上传成功」</summary>
    public bool WasUploaded { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>核对参数。</summary>
public sealed class CloudVerifyOptions
{
    /// <summary>
    /// 「记录太新就先不下结论」的时间窗（默认 1 小时）。
    ///
    /// <b>为什么需要它（2026-09-27 实机验证后修正）</b>：本方案初稿的规则是
    /// 「本机账本里没有这条 → 一律不标注」，理由是"记录应当是他端同步来的"。
    /// 实测推翻了这条假设：本机 28 条活跃记录中有 <b>17 条（61%）云端不存在且无账本证据</b>
    /// （账本条目被本地缓存淘汰 <c>FileRepository.DropLocal</c> 连带移除）——
    /// 按原规则，失真正好<b>全部漏报</b>。
    ///
    /// 改用本时间窗来防另一种误报：他端刚上传、元数据先同步过来、云端文件还在传 ——
    /// 这时云端确实还没有，但它不是「缺失」，只是「还没到」。
    /// 窗口之外的记录，云端没有就是没有。
    /// </summary>
    public TimeSpan FreshWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>判定用的"现在"（显式传入，便于快层检查点构造确定性场景）。</summary>
    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>核对结果。</summary>
public sealed class CloudVerifyResult
{
    /// <summary>应标注为「云端已不存在」的记录 id</summary>
    public List<string> MarkMissing { get; } = new();

    /// <summary>云端又有了、应撤销「云端已不存在」标注的记录 id</summary>
    public List<string> ClearMissing { get; } = new();

    /// <summary>云端有、本地无记录的路径 —— <b>只报告，绝不入账</b>（见实施清单 §5.3）</summary>
    public List<string> CloudOnly { get; } = new();

    public int Hit { get; set; }
    public int SkipTombstone { get; set; }
    public int SkipCloudExpired { get; set; }
    public int SkipCloudPending { get; set; }
    public int SkipNotUploaded { get; set; }
    public int SkipTooFresh { get; set; }

    public int Scanned { get; set; }
    public int CloudTotal { get; set; }

    /// <summary>有差异吗（没有任何要标/要撤的）</summary>
    public bool HasChanges => MarkMissing.Count > 0 || ClearMissing.Count > 0;
}

/// <summary>
/// 云端核对的核心判定（**纯函数**）—— 本地记录 × 云端清单 → 差异。
///
/// 三条设计纪律（改本文件前必读）：
/// 1. <b>零项目依赖</b>：只用 BCL。它要进快层检查点做全分支覆盖，任何项目内引用都会让快层编译不过。
/// 2. <b>只判"状态"，不判"身份"</b>：匹配键是 <see cref="LocalRecord.NetPath"/> 的精确字符串
///    （仅去尾部斜杠，**大小写敏感**）。**不用 md5** —— 2026-09-27 实测 11 条样本与本地内容 MD5
///    相等 0 条，官方也注明该字段"非文件真实 MD5"。
/// 3. <b>结论最重只到"疑似缺失"</b>：路径匹配不上不等于文件被删（用户可能只是改过名/移动过），
///    所以本函数只产出"要不要标 Missing"的意见，是否应用由用户确认。
/// </summary>
public static class CloudVerify
{
    // 取值必须与 CloudStates 保持一致（快层不引主项目，故此处用字面量而非常量引用）
    private const string StateMissing = "missing";
    private const string StateExpired = "expired";
    private const string StateCleanupPending = "cleanup-pending";

    /// <summary>
    /// 判定表（与 docs/2026-09-27-云端核对方案实施清单.md §5.2 一一对应）：
    /// <list type="bullet">
    /// <item>墓碑 / 云端已到期 / 云端待清理 → 跳过（都不是"意外消失"）</item>
    /// <item>路径命中云端 → 命中；若原为 Missing 则撤销标注</item>
    /// <item>未命中 + 账本记过 uploaded → <b>标 Missing</b>（确凿：曾验证传成功，现在没了）</item>
    /// <item>未命中 + 账本存在但没传成功 → 跳过（云端本该没有）</item>
    /// <item>未命中 + 无账本记录 + 记录够旧 → <b>标 Missing</b>（2026-09-27 修正新增）</item>
    /// <item>未命中 + 无账本记录 + 记录很新 → 跳过（防"他端刚传、元数据先到"的中间态）</item>
    /// </list>
    /// </summary>
    public static CloudVerifyResult Compare(
        IReadOnlyList<LocalRecord> local,
        IReadOnlyList<CloudEntry> cloud,
        CloudVerifyOptions options)
    {
        var result = new CloudVerifyResult { Scanned = local.Count, CloudTotal = cloud.Count };

        var cloudSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cloud) cloudSet.Add(Norm(c.Path));

        var localPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in local) localPaths.Add(Norm(l.NetPath));

        foreach (var l in local)
        {
            if (l.Deleted) { result.SkipTombstone++; continue; }
            if (l.CloudState == StateExpired) { result.SkipCloudExpired++; continue; }
            if (l.CloudState == StateCleanupPending) { result.SkipCloudPending++; continue; }

            if (cloudSet.Contains(Norm(l.NetPath)))
            {
                result.Hit++;
                if (l.CloudState == StateMissing) result.ClearMissing.Add(l.Id);   // 云端又有了 → 撤销标注
                continue;
            }

            // 云端没有 → 分三种，只有前两种配标「缺失」
            if (l.HasLedgerEntry)
            {
                if (l.WasUploaded) result.MarkMissing.Add(l.Id);
                else result.SkipNotUploaded++;
                continue;
            }

            // 本机账本里根本没有这条：可能是他端记录，也可能是账本被本地淘汰清过（实测后者是多数）
            if (options.Now - l.CreatedAt < options.FreshWindow) { result.SkipTooFresh++; continue; }
            result.MarkMissing.Add(l.Id);
        }

        // 云端多出：本地（含墓碑）按路径完全找不到 → 只报告，不入账
        foreach (var c in cloud)
            if (!localPaths.Contains(Norm(c.Path))) result.CloudOnly.Add(c.Path);

        return result;
    }

    /// <summary>路径规范化：只去尾部斜杠，**大小写敏感**（百度网盘路径大小写敏感）。</summary>
    private static string Norm(string? path) => (path ?? "").TrimEnd('/');
}
