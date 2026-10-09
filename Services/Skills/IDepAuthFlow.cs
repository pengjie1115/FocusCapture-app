using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>前置配置（<see cref="IDepAuthFlow.PrepareAsync"/>）的三种结局。</summary>
public enum DepPrepareState
{
    /// <summary>本来就不用准备 —— 已经配置好了，或者这个依赖压根不需要前置配置</summary>
    NotNeeded,

    /// <summary>准备完成，可以接着走授权</summary>
    Done,

    /// <summary>没准备成 —— 调用方**不得**继续走授权（硬走下去只会拿到 not_configured，白跑一趟）</summary>
    Failed,
}

/// <summary>
/// 兜底入口（"我有现成凭据"）需要的**一个输入字段**。
///
/// <para>
/// 为什么用描述而不是写死"appId + appSecret"：界面照这份描述渲染输入框，
/// 于是**窗口不需要认识任何一个具体 CLI**。接第二个 CLI 时，它的字段由它的实现给出。
/// </para>
/// </summary>
/// <param name="Key">字段标识（交给实现去取用）</param>
/// <param name="Label">给用户看的名字（如实写清楚"去哪儿复制"）</param>
/// <param name="Secret">是不是敏感值 —— 界面据此用密码框、并在用后立即清空输入控件</param>
public sealed record DepCredentialField(string Key, string Label, bool Secret);

/// <summary>一次前置配置的结果。措辞由实现给，调用方只决定"能不能继续"。</summary>
public sealed record DepPrepareResult(DepPrepareState State, string Message)
{
    /// <summary>无需准备（常态：第二次之后的机器都走这条）</summary>
    public static readonly DepPrepareResult NotNeeded = new(DepPrepareState.NotNeeded, "");

    public static DepPrepareResult Done(string message = "") => new(DepPrepareState.Done, message);

    public static DepPrepareResult Failed(string message) => new(DepPrepareState.Failed, message);

    /// <summary>能不能继续走授权：NotNeeded 与 Done 都可以，Failed 不行</summary>
    public bool CanContinue => State != DepPrepareState.Failed;
}

/// <summary>
/// 一个外部依赖的**授权协议**（2026-09-21 从 <see cref="SkillDependency"/> 基类里拆出来）。
///
/// <para>
/// <b>为什么必须拆：</b>拆之前，"申请码 / 出码 / 领 token / 登出 / 解析登录态"全是基类的
/// <c>virtual</c> 默认实现，而那些默认实现里**写死了 lark 的命令与字段**
/// （<c>auth login --no-wait --json</c>、<c>identities.user.available</c>…）——
/// 抽象形状是照着一个实现刻出来的，第二个 CLI 接进来只能先在基类里改默认值。
/// 现在基类只负责"起进程"，协议本身归实现类。
/// </para>
/// <para>
/// <b>协议内部的共同点（写在这里，避免每个实现各写一遍）：</b>
/// ① 一律**永不抛**，失败用返回值表达（调用方要按场景自己措辞）；
/// ② 状态判据是**输出里的字段**，不是退出码；
/// ③ 面向用户/模型的文案里不许出现命令行与开发者路径。
/// </para>
/// </summary>
public interface IDepAuthFlow
{
    /// <summary>
    /// 探测登录态要跑的命令参数。基类拿它去起进程，**解析归实现**（每个 CLI 的输出格式都不一样）。
    /// </summary>
    IReadOnlyList<string> ProbeArgs { get; }

    /// <summary>
    /// 解析登录态。
    ///
    /// <para>
    /// <b>stdout / stderr 都给</b>（2026-09-21 实测后改成这样的）：lark-cli 成功时 JSON 走 stdout、
    /// 出错时 JSON 走 stderr（未配置凭据的 <c>not_configured</c> 就在 stderr，退出码 3）。
    /// 只喂一路的话，最关键的那个状态正好看不见 —— 而它恰恰是"第一次用"的必经状态。
    /// </para>
    /// </summary>
    DependencyStatus ParseStatus(string? exePath, string stdout, string stderr);

    /// <summary>申请设备码（返回 device_code 与验证链接，不阻塞等用户）</summary>
    Task<(bool Ok, string Message, DeviceCodeSession? Session)> StartAuthAsync(CancellationToken ct = default);

    /// <summary>把验证链接转成二维码图片，返回图片绝对路径（失败返回 null）</summary>
    Task<string?> MakeQrPngAsync(string verificationUrl, string workDir, CancellationToken ct = default);

    /// <summary>用 device_code 领回 token（会阻塞轮询到用户确认或超时）</summary>
    Task<(bool Ok, string Message)> CompleteAuthAsync(string deviceCode, int timeoutMs, CancellationToken ct = default);

    /// <summary>撤销授权（只能授权不能撤销的安全机制是残缺的）</summary>
    Task<(bool Ok, string Message)> LogoutAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>前置配置</b>（2026-09-21 新增）—— 授权之前的"把前提搞齐"。
    ///
    /// <para>
    /// 为什么抽象里必须有这个位置：飞书的真缺口就在这里 —— 没有应用凭据时 <c>auth login</c>
    /// 连 device_code 都拿不到，**二维码根本画不出来**。而当时抽象里没有任何地方能表达
    /// "授权之前还得先干一件事"，于是这件事在代码里不存在，用户看到的就是"永远停在正在生成二维码"。
    /// </para>
    /// <para>
    /// <paramref name="showVerificationUrl"/> = 需要用户去浏览器完成时，把链接交给界面的回调；
    /// 传 null 表示**没有可展示链接的界面** —— 这时要求前置配置的实现**不要启动**那类流程
    /// （启动了也没人能完成，只会白等一个超时）。
    /// </para>
    /// </summary>
    Task<DepPrepareResult> PrepareAsync(Func<string, Task>? showVerificationUrl, CancellationToken ct = default);

    /// <summary>
    /// 兜底入口需要的字段（<b>空数组 = 该依赖不支持"粘贴凭据"</b>，界面就不显示这个入口）。
    /// </summary>
    IReadOnlyList<DepCredentialField> CredentialFields { get; }

    /// <summary>
    /// <b>用使用者提供的凭据完成前置配置</b>（兜底路径：不想新建应用、或想复用已有的）。
    ///
    /// <para>
    /// <b>凭据纪律（硬线，实现必须遵守）：</b>敏感值**只经 stdin 交给外部 CLI** ——
    /// 不进命令行参数、不进日志、不进仓库、不进分发包，应用不留任何副本。
    /// 任何一条失败宁可整体失败，也不许为了"调试方便"把它写出去。
    /// </para>
    /// </summary>
    Task<DepPrepareResult> PrepareWithCredentialAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
}

/// <summary>
/// 授权类 CLI 输出的**纯文本解析**（2026-09-21 抽出）。
///
/// <para>
/// 刻意做成 public 静态：解析是最容易出错、也最需要反复喂样本的地方，
/// 抽出来的目的就是让检查点能直接拿各种畸形输入来测，而不必真的去跑一次外部进程。
/// </para>
/// </summary>
public static class DepAuthText
{
    /// <summary>
    /// 从一行输出里提取验证链接。**从纯文本里正则提，不是解析 JSON** ——
    /// 实测 <c>config init --new</c> 吐的是「ASCII 块二维码 + 中文提示 + 一行 URL」的纯文本。
    /// 非 http(s) 的一律不认：免得把提示文字里的东西当成链接交给用户。
    /// </summary>
    public static string? ExtractVerificationUrl(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var m = Regex.Match(line, @"https?://[^\s""'<>]+");
        if (!m.Success) return null;

        var url = m.Value.TrimEnd('.', ',', ';', ':', ')', '）', '。', '，', '；');
        if (url.Length == 0) return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
    }
}
