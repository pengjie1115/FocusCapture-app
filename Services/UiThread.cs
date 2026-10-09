namespace FocusCapture.Services;

/// <summary>
/// UI 线程封送（2026-09-20）。
///
/// <para>
/// <b>为什么需要它：</b>Agent 工具循环（<c>AgentRunService.RunAsync</c>）全程走
/// <c>ConfigureAwait(false)</c> —— 也就是说，<b>工具体跑在线程池线程上，不是 UI 线程</b>。
/// 而"弹个框问用户"必须回到 UI 线程：WPF 的 <c>MessageBox.Show(owner, …)</c> 会在内部
/// 对 owner 做权限校验，从非 UI 线程传窗口对象进去，直接抛
/// <c>InvalidOperationException：调用线程无法访问此对象，因为另一个线程拥有该对象</c>。
/// </para>
/// <para>
/// <b>这条是实测踩出来的，不是推演：</b>Skill 准入确认先在真机上炸了一整轮 ——
/// 堆栈落在 <c>MessageBox.Show(this, …)</c> 那一行，模型连着四次拿到
/// "工具执行出错：调用线程无法访问此对象"，而它读到的其余一切（清单、脚本名）都是对的。
/// 同一处代码形状在"写操作确认弹窗"里也存在，只是那份默认关着、没人踩到。
/// </para>
/// <para>
/// <b>约定：凡是从 Agent 工具线程上发起的任何 UI 交互，一律经过本类。</b>
/// 反过来说，UI 事件处理器里的 <c>MessageBox.Show(this, …)</c> 不必改 —— 它们本来就在 UI 线程上。
/// </para>
/// </summary>
public static class UiThread
{
    /// <summary>
    /// 在 UI 线程上执行"问用户一个是非"的回调，并把结果如实返回。
    ///
    /// <para>
    /// <b>调度失败一律返回 false（= 没拿到确认），绝不向上抛。</b>
    /// 语义上这是唯一安全的方向：拿不到确认就当作没确认 —— 绝不能反向当成放行。
    /// （与项目里"失败必须响、不许假成功"是同一条纪律的两面。）
    /// </para>
    /// <para>
    /// 已在 UI 线程上调用时直接执行，不绕一次调度 —— 省掉无谓的重入。
    /// </para>
    /// </summary>
    /// <param name="dispatcher">目标 UI 线程的调度器（窗口的 <c>Dispatcher</c>）</param>
    /// <param name="showDialog">真正弹窗的逻辑；它会在 UI 线程上执行，可以自由触碰 UI 对象</param>
    /// <param name="onError">调度失败时的上报出口（一般接 <c>AppLog.Warn</c>），可为 null</param>
    public static async Task<bool> AskAsync(Dispatcher? dispatcher, Func<bool> showDialog, Action<string>? onError = null)
    {
        if (dispatcher == null || showDialog == null) return false;

        if (dispatcher.CheckAccess()) return showDialog();

        try
        {
            return await dispatcher.InvokeAsync(showDialog).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 窗口已关 / 调度器已停 / 操作被中止 —— 都按"没拿到确认"处理
            onError?.Invoke($"UI 线程封送失败（按未确认处理）：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }
}
