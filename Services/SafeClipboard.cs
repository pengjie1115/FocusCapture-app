using System.Runtime.InteropServices;
using System.Threading;

namespace FocusCapture.Services;

/// <summary>
/// 剪贴板写入容错封装（2026-09-18 新增）。
///
/// 背景：Windows 剪贴板是**系统级独占**资源，任一进程 OpenClipboard 期间，其他进程写入必定失败。
/// WPF 的 Clipboard.SetText 内部走 OleFlushClipboard，此时抛
/// COMException 0x800401D0 (CLIPBRD_E_CANT_OPEN)。
/// 该错误是**暂时性**的（占用方通常毫秒级释放，Windows 官方亦建议重试），
/// 但若调用点没有兜底，异常会冒泡到 App.DispatcherUnhandledException 弹出模态错误框。
///
/// 本项目实际故障：灵感速览面板「单击复制」未捕获该异常 ——
/// 双击笔记时第一下先走复制路径 → 抛错弹模态框 → 第二下点击被模态框吃掉
/// → 用户表现为「双击进不了编辑状态」（右键菜单走 CtxEdit_Click，不碰剪贴板，故正常）。
///
/// 设计约束：本文件**不得引用任何 WPF 类型**（底层写入函数由调用方以 Action&lt;string&gt; 注入），
/// 这样快层检查点工程（net8.0 / 无 WPF 引用）可直接链接本文件验证重试策略与返回值。
/// </summary>
public static class SafeClipboard
{
    /// <summary>默认尝试次数（首次 + 2 次重试）</summary>
    public const int DefaultAttempts = 3;

    /// <summary>默认退避基数（毫秒），间隔依次 25 / 50；实测剪贴板占用多为毫秒级</summary>
    public const int DefaultBaseDelayMs = 25;

    /// <summary>加固写入第二程（Win32 固化）的默认重试参数：10 次尝试、15ms 起步、单次封顶 150ms，
    /// 总退避窗口约 1.1 秒，覆盖远程工具（向日葵 / ToDesk 等）对剪贴板的典型占用时长</summary>
    public const int HardenedWin32Attempts = 10;
    public const int HardenedWin32BaseDelayMs = 15;
    public const int HardenedWin32MaxDelayMs = 150;

    /// <summary>
    /// 尝试写入剪贴板，失败按指数退避重试。返回是否成功，**绝不抛异常**。
    /// </summary>
    /// <param name="text">要写入的文本；null / 空串直接返回 false（Clipboard.SetText 对空串会抛 ArgumentException）</param>
    /// <param name="write">底层写入委托，生产代码传 WpfClipboard.SetText</param>
    public static bool TrySetText(string? text, Action<string> write)
        => TrySetText(text, write, DefaultAttempts, DefaultBaseDelayMs, null, Thread.Sleep);

    /// <summary>
    /// 可测入口：attempts / baseDelayMs / sleep 均可注入 —— 检查点据此断言退避序列与返回值，
    /// 既不需要真实等待，也不需要真实剪贴板。
    /// 仅对「暂时性」异常重试（COMException 及其基类 ExternalException，即剪贴板被占用 / 无法打开）；
    /// 其余异常（参数错、线程非 STA 等）属确定性失败，重试无意义，立即返回 false。
    /// </summary>
    public static bool TrySetText(string? text, Action<string> write, int attempts, int baseDelayMs,
        Action<int, Exception>? onAttemptFailed = null, Action<int>? sleep = null)
    {
        if (write is null) throw new ArgumentNullException(nameof(write));
        if (string.IsNullOrEmpty(text)) return false;

        if (attempts < 1) attempts = 1;
        if (baseDelayMs < 0) baseDelayMs = 0;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                write(text!);
                return true;
            }
            catch (Exception ex)
            {
                onAttemptFailed?.Invoke(attempt, ex);

                var retryable = ex is ExternalException;      // 含 COMException（CLIPBRD_E_CANT_OPEN）
                if (!retryable || attempt == attempts) return false;

                // 指数退避：25 / 50 / 100 …（shift 封顶 8 位，避免 attempts 被传大时整数溢出）
                sleep?.Invoke(baseDelayMs << Math.Min(attempt - 1, 8));
            }
        }

        return false;
    }

    /// <summary>
    /// 双程加固写入（2026-10-01，剪贴板被远程工具长期占用场景的接近根治方案）。
    ///
    /// 第一程：OLE 路径（生产传 WpfClipboard.SetText，多格式 + flush 固化语义），只试 1 次 ——
    /// WPF 内部对 OleSetClipboard / OleFlushClipboard 自带 10 次 × 100ms 重试（为远程/终端会话设计），
    /// 外层叠加重试只会拉长卡顿、不会提高成功率。失败含一种**中间态**：OleSetClipboard 已把数据
    /// 挂上剪贴板（延迟渲染）而 OleFlushClipboard 被占用打断 —— 表现为「剪贴板查看器里看不到、
    /// 稍后却能粘贴出来」，且数据依赖本进程存活。
    ///
    /// 第二程：Win32 经典路径强制固化（生产传 Win32.WriteClipboardText：单格式 CF_UNICODETEXT、
    /// 持锁微秒级、立即渲染），带 10 次指数退避（15ms 起步、单次封顶 150ms）。EmptyClipboard
    /// 会顺带把第一程留下的延迟渲染中间态清掉。
    ///
    /// 两程都失败才返回 false —— **此时调用方必须给用户可见提示，不得静默**。
    /// 约束同 TrySetText：本文件不引用 WPF 类型，两程写入都由调用方注入；
    /// 生产调用形如 TrySetTextHardened(text, WpfClipboard.SetText, Win32.WriteClipboardText)。
    /// </summary>
    public static bool TrySetTextHardened(string? text, Action<string> oleWrite, Func<string, bool> rawWrite,
        Action<int>? sleep = null)
    {
        if (oleWrite is null) throw new ArgumentNullException(nameof(oleWrite));
        if (rawWrite is null) throw new ArgumentNullException(nameof(rawWrite));
        if (string.IsNullOrEmpty(text)) return false;

        if (TrySetText(text, oleWrite, 1, 0)) return true;

        for (var attempt = 1; attempt <= HardenedWin32Attempts; attempt++)
        {
            try
            {
                if (rawWrite(text)) return true;
            }
            catch { /* 第二程同样绝不把异常抛给调用方 */ }
            if (attempt < HardenedWin32Attempts)
                sleep?.Invoke(Math.Min(HardenedWin32BaseDelayMs << Math.Min(attempt - 1, 4), HardenedWin32MaxDelayMs));
        }
        return false;
    }
}
