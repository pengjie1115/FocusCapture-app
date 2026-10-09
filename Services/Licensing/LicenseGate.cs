namespace FocusCapture.Services;

/// <summary>激活 / 试用状态快照（供界面展示，不含任何判定逻辑）。</summary>
public sealed record LicenseStatus(
    bool Activated,
    bool Expired,
    int UsedDays,
    int RemainingDays,
    int TrialDays,
    string Code)
{
    /// <summary>试用中（未激活且未过期）。</summary>
    public bool InTrial => !Activated && !Expired;

    /// <summary>高级功能是否可用（= 已激活 或 试用中）。</summary>
    public bool Unlocked => Activated || !Expired;
}

/// <summary>
/// 权限检查中心（门卫）—— <b>所有高级功能入口的唯一判定口</b>。
///
/// <para><b>为什么必须单点</b>：散落的 <c>if (试用中)</c> 一定会漏 —— 漏掉的那个入口就是免费的洞，
/// 而且<b>不报错</b>。所以新功能一律只问 <see cref="IsAllowed"/> / <see cref="EnsureAllowed"/>。</para>
///
/// <para><b>判定规则</b>：
/// <list type="number">
/// <item>全局开关关闭（发布前的默认值）→ 全部放行，行为与未接线时完全一致；</item>
/// <item>功能没登记在高级档 → 放行（免费档不登记）；</item>
/// <item>高级功能 → <see cref="IsUnlocked"/>：已激活 <b>或</b> 试用未过期。</item>
/// </list></para>
///
/// <para><b>铁律（任务书 4.2）</b>：激活状态<b>每次都从 code 重新验签得出</b>，
/// 绝不缓存「已激活」这个结论，更不在任何文件里存布尔标记。
/// 签名是用户伪造不了的东西 —— 那是整个离线方案里唯一守得住的地方。</para>
///
/// <para><b>历史</b>：本类 2026-09-21 建为骨架（全局开关 + 功能键 + 唯一检查入口，全部放行）；
/// 2026-10-08 接入试用计日与离线验签，正式启用。</para>
/// </summary>
public static class LicenseGate
{
    // ── 功能键 ──
    // 免费档的功能**不需要登记** —— 不登记即放行。这里只列高级档。
    // 命名与用户拍板的功能分档一一对应。

    /// <summary>AI 问答。</summary>
    public const string FeatureAiChat = "ai_chat";

    /// <summary>AI 整理（笔记 / 待办一键整理）。</summary>
    public const string FeatureAiTidy = "ai_tidy";

    /// <summary>跨设备 / 云端（云同步、网盘上传、得到大脑、云端核对）。</summary>
    public const string FeatureSync = "sync";

    /// <summary>高级导出：Word (.docx)。基础导出（Markdown / 纯文本）免费。</summary>
    public const string FeatureExportWord = "export_word";

    // ── 曾经登记/定义过的功能键（2026-10-08 铺开拦截点时清理）──
    // voice_input / search / export 三个键从未进过 _paid 集合，实际一直放行；
    // 它们的调用点也已改造完毕：
    //   · 语音输入（沉浸记录）与搜索 = 免费档 → 调用点**刻意不设门卫**（见 MainWindow.ShowVoiceInput
    //     与 QuickViewWindow.BtnSearch_Click 的注释）；
    //   · 导出 = 改**选项级**拦截 → 只有 Word 走 FeatureExportWord，Markdown / 纯文本免费。
    //
    // agent（2026-10-08 晚删除，**从未接过调用点**）：曾定义 `FeatureAgent` 并登记进 _paid。
    // 删除理由 —— **Agent 工具不是独立产品档位**：它是「AI 问答」在 `AgentEnabled` 开关下的子模式，
    // 与普通对话共用同一个发送入口 `AIDialogWindow.SendAsync`（该方法 2028 行注释明写「普通/Agent
    // 两路径统一」）和同一个窗口。用户拍板的功能分档里只有「AI 问答」这一档，没有单独的 Agent 档。
    // 留着它会误导后人以为 Agent 要单独判定，还会诱导去错位置打补丁（`EnsureAgentRegistry()` 是
    // void 方法，拦不住调用方 `SendViaAgentAsync`，加在那里等于白加）。
    //
    // 通则：**每个登记进 _paid 的键都必须有真实调用点**，否则就是「说要做、没做」的半成品，
    // 后人读代码时会以为它生效着。删除原因与上面三个键同源：**不登记才是正确表达**。

    /// <summary>高级档功能集合（用户拍板的功能分档）。</summary>
    private static readonly HashSet<string> _paid = new()
    {
        FeatureAiChat,
        FeatureAiTidy,
        FeatureSync,
        FeatureExportWord,
    };

    /// <summary>
    /// 全局开关：<c>false</c> = 全部免费开放（行为与未接线时完全一致）；<c>true</c> = 按功能分档拦截。
    ///
    /// <para><b>默认值按构建类型分支（2026-10-08 定案，用户拍板「现在解决」）</b>：
    /// Debug（开发 / 自用）→ <c>false</c>：不打扰开发；要验证拦截效果时用
    /// <see cref="ApplyDemoSwitch"/> 的 <c>--license-demo</c> 临时开启。
    /// Release（发布）→ <c>true</c>：门卫必须上岗 —— 否则试用期与激活码**形同虚设**，
    /// 所有人都能白用，而且不会有任何报错提醒你。</para>
    ///
    /// <para><b>为什么用编译期分支，而不是「发布前记得手改默认值」</b>：手改是一条**会忘的纪律**，
    /// 忘掉的后果是「发布版静默不限权」—— 没有报错、没有异常，等发现时已经白送很久。
    /// 编译期分支让「忘了」在物理上不可能发生。这条同源自任务书 §12.4 的「后门必须编译期物理屏蔽」
    /// —— 同一个道理的正反两面：该关的关在编译期，该开的也开在编译期。</para>
    /// </summary>
#if DEBUG
    public static bool IsLicensingEnabled { get; set; } = false;
#else
    public static bool IsLicensingEnabled { get; set; } = true;
#endif

    /// <summary>本进程的试用 / 激活状态（启动时载入一次，之后常驻）。</summary>
    private static LicenseState? _state;
    private static readonly object _stateLock = new();

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>当前状态（首次访问时按需载入 —— 避免调用顺序导致读到空状态）。</summary>
    private static LicenseState State
    {
        get
        {
            if (_state is not null) return _state;
            lock (_stateLock)
            {
                return _state ??= LicenseState.OpenForToday(Today());
            }
        }
    }

    /// <summary>
    /// 应用启动时调用一次：载入状态文件、推进试用天数、必要时落盘。
    /// 不调用也能工作（首次访问时会按需载入），但启动时调用能保证「时钟回拨」这类异常第一时间进日志。
    /// </summary>
    /// <param name="today">仅供测试注入「今天」；正式运行传 null 取系统日期。</param>
    public static void Initialize(DateOnly? today = null)
    {
        var d = today ?? Today();
        lock (_stateLock)
        {
            _state = LicenseState.OpenForToday(d);
        }

        if (_state.ClockRollbackDetected)
        {
            AppLog.Warn("License", $"检测到系统时钟回拨：本次不推进试用天数（已累计 {_state.Trial.UsedDays} 天，"
                                 + $"最后见到日期 {LicenseState.FormatDate(_state.Trial.LastSeenDate)}）。");
        }
        if (_state.StartedFresh)
        {
            AppLog.Info("License", $"试用状态已初始化：{LicenseState.FormatDate(_state.Trial.FirstRunDate)} 起共 {TrialClock.TrialDays} 天。");
        }
    }

    /// <summary>
    /// 手验开关（2026-10-08）：识别 <c>--license-demo</c> 启动参数，让门卫在**不改源码默认值**的前提下上岗。
    ///
    /// <para><b>为什么需要它</b>：<see cref="IsLicensingEnabled"/> 在发布前必须为 <c>false</c>
    /// （防「AI 被拦但 Word 还没接上」这类中间态），但这样一来手验就无从下手 ——
    /// 改 <c>license.json</c> 看不出任何效果，因为 <see cref="IsAllowed"/> 第一行
    /// <c>if (!IsLicensingEnabled) return true;</c> 已经把一切放行了。
    /// 于是需要一个「只影响手验、绝不影响发布」的开关。</para>
    ///
    /// <para><b>安全设计（任务书 §12.4 铁律：后门必须编译期物理屏蔽）</b>：
    /// 本方法体在 Release 构建里**根本不存在** —— 参数被忽略、门卫保持调用前的值。
    /// 「只藏起来」（如加个隐藏菜单/隐藏参数）等于发布版带洞、任何人免费解锁，所以这里用
    /// <c>#if DEBUG</c> 编译期开关，不靠运行时判断「是不是开发环境」。</para>
    /// </summary>
    /// <remarks>
    /// fail-closed：参数不存在 → 什么都不做（门卫保持默认 <c>false</c>），绝不「解析失败就放行」。
    /// 配套工具：<c>tools\dev.ps1 license</c>（写状态文件）+ <c>license launch</c>（带本参数启动）。
    /// </remarks>
    public static void ApplyDemoSwitch(string[]? args)
    {
#if DEBUG
        // 手验期间唯一的开启路径。注意这里**只**动 IsLicensingEnabled，不碰任何状态文件。
        if (args is not null && Array.Exists(args,
                a => string.Equals(a, "--license-demo", StringComparison.OrdinalIgnoreCase)))
        {
            IsLicensingEnabled = true;
            AppLog.Info("License", "检测到 --license-demo：门卫已上岗（仅 Debug 构建的手验开关，Release 无此分支）。");
        }
#else
        // Release 构建：本分支在编译期被清空。参数被忽略，门卫保持调用前的值 —— 这是「物理屏蔽」的落点。
        _ = args;
#endif
    }

    /// <summary>当前状态快照（供界面展示）。</summary>
    public static LicenseStatus GetStatus()
    {
        var st = State;
        return new LicenseStatus(
            Activated: IsLicenseValid(),
            Expired: TrialClock.IsExpired(st.Trial),
            UsedDays: st.Trial.UsedDays,
            RemainingDays: TrialClock.RemainingDays(st.Trial),
            TrialDays: TrialClock.TrialDays,
            Code: st.Code);
    }

    /// <summary>
    /// 许可证是否有效 —— <b>每次调用都从 code 重新验签</b>，不缓存结论。
    /// 代价是每次要跑一次 ECDSA 验签（微秒级），换来的是「改文件无法伪造成已激活」。
    /// </summary>
    public static bool IsLicenseValid()
    {
        var code = State.Code;
        if (string.IsNullOrWhiteSpace(code)) return false;
        return LicenseCode.TryRead(code, LicensePublicKey.SpkiBase64, out _, out _);
    }

    /// <summary>高级功能是否可用（已激活 或 试用未过期）。</summary>
    public static bool IsUnlocked() => IsLicenseValid() || !TrialClock.IsExpired(State.Trial);

    /// <summary>功能是否允许使用 —— <b>所有接线点的唯一检查入口</b>。</summary>
    public static bool IsAllowed(string featureKey)
    {
        if (!IsLicensingEnabled) return true;          // 全局开关关 = 放行
        if (!_paid.Contains(featureKey)) return true;  // 未登记为高级 = 免费档，放行
        return IsUnlocked();                           // 高级功能 → 验票
    }

    /// <summary>
    /// 带提示的检查：UI 接线点一行调用，不通过时弹出激活窗并返回 false。
    ///
    /// <para><b>为什么弹窗而不是 MessageBox</b>（2026-10-08 用户拍板方案 A）：被拦是「临门一脚」场景 ——
    /// 用户点某个高级功能才发现到期。只丢一句「需要激活」等于让他自己找入口，
    /// 就地激活的转化率最高，所以弹的是能直接输码的 <see cref="Windows.LicenseWindow"/>。
    /// 设置里的「激活码」板块仍是查看详情的完整入口，两者共用本类的判定。</para>
    /// </summary>
    /// <param name="featureKey">功能键（见本类常量）。</param>
    /// <param name="featureName">功能中文名，用于窗口标题文案。</param>
    /// <param name="owner">模态窗 owner。传 null 时回退当前主窗口 —— 沿用 AIDialogHelper 的既有写法。</param>
    /// <remarks>⚠ 只允许在 UI 线程调用（弹模态窗）。后台线程请改用 <see cref="IsAllowed"/> 并自行降级
    /// （例：<c>SyncEngine</c> 返回「同步是专业版功能」的失败结果，而不是弹窗）。</remarks>
    public static bool EnsureAllowed(string featureKey, string featureName, Window? owner = null)
    {
        if (IsAllowed(featureKey)) return true;

        // 弹窗属于「外部资源操作」：自己 try 住，绝不把异常抛给调用方
        // （更不允许「点了没反应」—— 那是最难排查的一类故障）。
        try
        {
            var w = new Windows.LicenseWindow(featureName)
            {
                Owner = owner ?? Application.Current?.MainWindow,
            };
            w.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Warn("License", $"弹出激活窗失败（功能「{featureName}」）：{ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 校验并接受一个激活码。成功则写入状态文件并立刻生效（后续 <see cref="IsLicenseValid"/> 即为 true）。
    /// </summary>    /// <param name="code">用户输入的激活码（原文）。</param>
    /// <param name="error">失败原因（中文，可直接展示给用户）。</param>
    public static bool TryActivate(string code, out string error)
    {
        if (!LicenseCode.TryRead(code, LicensePublicKey.SpkiBase64, out var payload, out error)) return false;

        var trimmed = code.Trim();
        lock (_stateLock)
        {
            var st = State;
            st.SetCode(trimmed, Today());
            if (!st.Save())
            {
                error = "激活码有效，但状态写入失败（磁盘不可写？）。请检查数据目录权限后重试。";
                return false;
            }
            AppLog.Info("License", $"激活成功：{LicenseCode.EncodePayload(payload)}。");
        }
        return true;
    }

    // ── 快照辅助（2026-10-08）──
    // 为什么需要：快照沙箱里的 license.json 是**全新创建**的（UsedDays=1 → 试用中 → 放行），
    // 所以「未解锁」这一态在沙箱里天然出不来。要让 Word 项置灰那类界面能被出图验证，
    // 必须能强制造出「已过期」态。
    // 设计与既有 SeedAiProvidersForSnapshot / PrepareSkillDepsForSnapshot 同一思路：
    // **只给快照用的入口，生产路径永不调用**；且只影响进程内状态，不写任何真实文件。

    /// <summary>快照专用：丢弃已缓存的状态，下次访问按当前数据根重新载入（配合预先写好的沙箱 license.json）。</summary>
    internal static void ReinitializeForSnapshot()
    {
        lock (_stateLock) { _state = null; }
    }

    /// <summary>快照专用：把进程内状态与全局开关复位，避免「已过期」泄漏给后续场景。</summary>
    internal static void ResetForSnapshot()
    {
        lock (_stateLock) { _state = null; }
        IsLicensingEnabled = false;
    }
}
