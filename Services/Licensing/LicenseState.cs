using System.Text.Json.Serialization;

namespace FocusCapture.Services;

/// <summary>license.json 的磁盘模型（字段名沿用项目既有口径：源生成默认 PascalCase）。</summary>
internal sealed class LicenseFileModel
{
    /// <summary>首次运行日期（yyyy-MM-dd）。</summary>
    public string FirstRunDate { get; set; } = "";

    /// <summary>累计实际使用天数（自然日去重）。</summary>
    public int UsedDays { get; set; }

    /// <summary>最后一次见到的日期（yyyy-MM-dd）—— 时钟回拨判定的锚点。</summary>
    public string LastSeenDate { get; set; } = "";

    /// <summary>用户输入的激活码原文（<b>待验证的原始串，不是结论</b>）。</summary>
    public string Code { get; set; } = "";

    /// <summary>最近一次输入激活码的日期（仅供界面显示；<b>不参与任何放行判定</b>）。</summary>
    public string ActivatedAt { get; set; } = "";
}

/// <summary>
/// 试用与激活状态文件的序列化上下文（源生成，与主 AppJsonContext 隔离）。
/// 为什么单开一个：① 与 BaiduJsonContext / FileJsonContext 同惯例，改动互不牵连；
/// ② 这份文件的读写是**最不能出错**的一条链路，独立上下文便于检查点单独覆盖。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LicenseFileModel))]
internal partial class LicenseJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 试用 / 激活状态文件的读写（<b>慢层 I/O</b> —— 依赖 FocusCapturePaths，故不能进快层）。
///
/// <para><b>文件位置</b>：<c>FocusCapturePaths.Combine("license.json")</c>，即数据根下（测试经
/// <c>RootOverride</c> 隔离到临时沙箱，绝不触碰真实数据）。</para>
///
/// <para><b>铁律（任务书 4.2）：本文件里不得出现任何布尔标记</b>。
/// 「是否已激活」<b>必须每次启动都从 <see cref="Code"/> 重新验签得出</b>；
/// 一旦偷懒存一个 <c>activated: true</c>，任何人用记事本改成 true 就白送高级功能。
/// 签名是用户伪造不了的 —— 那是整个离线方案里唯一守得住的地方，不能为了省事废掉。</para>
///
/// <para><b>损坏文件的处置口径</b>：JSON 坏了 / 日期字段解析不出来 → <b>按全新试用处理</b>
/// （与「用户删掉文件」同口径），并把原文件改名留证（<c>license.json.corrupt-&lt;时间戳&gt;</c>）。
/// 为什么不按「已过期」处理：删文件本来就能重置（已知且接受的漏洞），对损坏更严只会误伤
/// 磁盘出错的正常用户，收益为零。</para>
/// </summary>
public sealed class LicenseState
{
    /// <summary>状态文件名。</summary>
    public const string FileName = "license.json";

    /// <summary>状态文件完整路径（数据根下）。</summary>
    public static string FilePath => FocusCapturePaths.Combine(FileName);

    /// <summary>日期在文件里的格式（固定不变，跨区域设置一致）。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>试用快照（首运行日 / 累计天数 / 最后见到日）。</summary>
    public TrialSnapshot Trial { get; private set; }

    /// <summary>用户输入的激活码原文。<b>注意：这不是「已激活」的结论</b>，用之前必须验签。</summary>
    public string Code { get; private set; } = "";

    /// <summary>最近一次输入激活码的日期。仅供显示，不参与放行判定。</summary>
    public string ActivatedAt { get; private set; } = "";

    /// <summary>本次载入是否检测到系统时钟回拨（调用方据此写运行日志）。</summary>
    public bool ClockRollbackDetected { get; private set; }

    /// <summary>本次载入是否因「文件缺失 / 损坏」而重新开始（即：这是一份全新状态）。</summary>
    public bool StartedFresh { get; private set; }

    private bool _needsSave;

    // ── 读取 ──

    /// <summary>
    /// 读盘并归一化（<b>不推进天数、不落盘</b>，纯读取）。
    /// 文件不存在 / 读不动 / JSON 坏 / 日期字段坏 → 全新试用状态，且 <see cref="StartedFresh"/> = true。
    /// </summary>
    public static LicenseState Load(DateOnly today)
    {
        var model = ReadModel(out var corrupt);
        var state = new LicenseState();

        if (model is null || !TryParseDate(model.FirstRunDate, out var first) || !TryParseDate(model.LastSeenDate, out var last))
        {
            if (corrupt || model is not null) BackupCorruptFile();   // 只有「真读到过坏内容」才留证
            state.Trial = TrialClock.Init(today);
            state.StartedFresh = true;
            state._needsSave = true;
            return state;
        }

        var normalized = TrialClock.Normalize(new TrialSnapshot(first, model.UsedDays, last));
        state.Trial = normalized;
        state.Code = model.Code ?? "";
        state.ActivatedAt = model.ActivatedAt ?? "";
        state._needsSave = normalized.UsedDays != model.UsedDays;   // 归一化改过值 → 需要回写
        return state;
    }

    /// <summary>启动时调用一次：读盘 → 推进一天 → 需要时落盘。返回本次状态。</summary>
    public static LicenseState OpenForToday(DateOnly today)
    {
        var state = Load(today);
        var advanced = state.RecordUsageDay(today);
        if (advanced || state._needsSave) state.Save();
        return state;
    }

    // ── 写入 ──

    /// <summary>
    /// 按「今天」推进一天（同日不动 / 跨天 +1 / 回拨不加）。
    /// </summary>
    /// <returns>true = 天数真的变了（调用方据此决定是否落盘）。</returns>
    public bool RecordUsageDay(DateOnly today)
    {
        var next = TrialClock.Advance(Trial, today, out var rollback);
        ClockRollbackDetected = rollback;
        if (next == Trial) return false;
        Trial = next;
        return true;
    }

    /// <summary>
    /// 记下激活码原文（<b>不验签</b> —— 验签在 <c>LicenseGate</c>，且每次启动重做）。
    /// 只有在验签通过后才该调用它，否则文件里会留一个无效码。
    /// </summary>
    public void SetCode(string code, DateOnly today)
    {
        Code = code ?? "";
        ActivatedAt = FormatDate(today);
    }

    /// <summary>
    /// 落盘。<b>永不抛</b>（返回是否成功）：I/O 失败时宁可本次不写，也不要让启动流程崩掉。
    /// </summary>
    public bool Save()
    {
        try
        {
            var model = new LicenseFileModel
            {
                FirstRunDate = FormatDate(Trial.FirstRunDate),
                UsedDays = Trial.UsedDays,
                LastSeenDate = FormatDate(Trial.LastSeenDate),
                Code = Code,
                ActivatedAt = ActivatedAt,
            };
            var json = JsonSerializer.Serialize(model, LicenseJsonContext.Default.LicenseFileModel);
            FocusCapturePaths.EnsureRoot();
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            _needsSave = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── 内部 ──

    private static LicenseFileModel? ReadModel(out bool corrupt)
    {
        corrupt = false;
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;          // 首次运行：不算损坏
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) { corrupt = true; return null; }
            return JsonSerializer.Deserialize(json, LicenseJsonContext.Default.LicenseFileModel);
        }
        catch
        {
            corrupt = true;
            return null;
        }
    }

    /// <summary>把坏文件改名留证（不删 —— 出问题时能看到它长什么样）。失败不影响主流程。</summary>
    private static void BackupCorruptFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(path, $"{path}.corrupt-{stamp}");
        }
        catch { /* 改名失败就照常覆盖写，不阻塞 */ }
    }

    /// <summary>DateOnly → 文件格式字符串（固定区域设置，避免不同区域写出别的写法）。</summary>
    public static string FormatDate(DateOnly d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>文件格式字符串 → DateOnly；必须严格匹配，宽松解析会让损坏文件被当成正常值。</summary>
    public static bool TryParseDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
