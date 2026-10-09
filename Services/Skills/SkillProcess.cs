using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>子进程调用结果（宿主只搬运事实，不下结论）</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError)
{
    /// <summary>启动成功、没超时、退出码 0 —— 三者都满足才算正常返回</summary>
    public bool Ok => StartError == null && !TimedOut && ExitCode == 0;

    public string Combined => (Stdout + "\n" + Stderr).Trim();
}

/// <summary>
/// Skill 子进程的统一装配（2026-09-20）。
///
/// <para>
/// <b>为什么单独抽出来：</b>执行器（跑 Skill 脚本）与「外部依赖」层（驱动 lark-cli 授权）
/// 都要起子进程，两处各写一套的代价已经见过了 —— 执行器当初在
/// <c>RedirectStandardInput = false</c> 时设了 <c>StandardInputEncoding</c>，
/// <c>Process.Start</c> 直接抛，8 条检查点全红，而错误文案还把它误报成"运行时缺失"。
/// 那份经验固化在这里，只有一处会错。
/// </para>
/// <para>
/// <b>环境纪律（三条都不由调用方决定）：</b>
/// ① 清掉宿主标记变量（否则子工具会跑到别的宿主空间，比如 lark-cli 串到 WorkBuddy 的账号）；
/// ② 输出按 UTF-8 解码（本机不注入也正常，但那是环境巧合，不能当普遍事实）；
/// ③ 可以往 PATH 前面塞目录（让子进程里 <c>shutil.which("lark-cli")</c> 命中候选目录里的那份（自带 → 数据目录）。
/// </para>
/// </summary>
internal static class SkillProcess
{
    /// <summary>会被清掉的环境变量：它们代表"宿主身份"，留着会让子工具跑到别的宿主空间去</summary>
    internal static readonly string[] HostMarkerVars = { "PYTHONPATH", "HERMES_HOME", "OPENCLAW_HOME", "LARK_CHANNEL" };

    /// <summary>
    /// 组装一个子进程启动信息。
    /// </summary>
    /// <param name="exePath">可执行文件绝对路径（调用方负责确保它是查表得到的，不拼模型给的字符串）</param>
    /// <param name="args">参数数组 —— 绝不拼命令行字符串</param>
    /// <param name="workingDir">工作目录（脚本=Skill 根目录；lark-cli 授权=临时目录，因为它的 -o 只收相对路径）</param>
    /// <param name="redirectStdin">是否喂标准输入</param>
    /// <param name="prependPath">前置到 PATH 的目录（可为空）</param>
    /// <param name="pythonFlags">是否注入 Python 行为控制项（只有跑 Python 脚本时才需要）</param>
    internal static ProcessStartInfo Build(
        string exePath,
        IEnumerable<string> args,
        string workingDir,
        bool redirectStdin,
        string? prependPath = null,
        bool pythonFlags = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,          // 必须 false，否则不能重定向、也不能自定义环境
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // ⚠ StandardInputEncoding 只能在 RedirectStandardInput = true 时设置，
            // 否则 Process.Start 抛「only supported when standard input is redirected」（实测踩过）。
        };
        if (redirectStdin) psi.StandardInputEncoding = new UTF8Encoding(false);

        foreach (var a in args) psi.ArgumentList.Add(a ?? "");

        if (pythonFlags)
        {
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";   // 不往应用目录写 __pycache__
            psi.Environment["PYTHONIOENCODING"] = "utf-8";      // 输出编码确定，不随机器 locale 漂
        }
        foreach (var v in HostMarkerVars) psi.Environment.Remove(v);

        if (!string.IsNullOrEmpty(prependPath))
        {
            var current = FindEnv(psi, "PATH");
            psi.Environment["PATH"] = string.IsNullOrEmpty(current)
                ? prependPath
                : prependPath + Path.PathSeparator + current;
        }

        return psi;
    }

    /// <summary>大小写不敏感地取一个环境变量（Windows 上 PATH/Path 混写很常见）</summary>
    private static string? FindEnv(ProcessStartInfo psi, string name)
    {
        foreach (var kv in psi.Environment)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>
    /// 跑完一个子进程。**永不抛** —— 启动失败 / 超时都通过 <see cref="ProcessResult"/> 返回，
    /// 让调用方自己决定怎么措辞（措辞是要按场景区分的，这也是当初"参数写错被误报成环境缺失"的教训）。
    /// </summary>
    /// <param name="onLine">
    /// 每读到一行就回调（stdout / stderr 混在一起，按到达顺序）。
    /// **专为「吐完输出之后还会阻塞很久」的命令准备**：lark-cli 的 <c>config init --new</c> 就是这种 ——
    /// 启动约 1 秒把验证链接吐完（走 stderr），然后一直等使用者在浏览器里把应用建完才退出。
    /// 按「进程退出后才读输出」的老做法在那类命令上等于死锁：链接明明已经出来了，却永远读不到。
    /// <para>
    /// ⚠ <b>回调里不要去 kill 进程</b>：那类命令吐的是设备码流（user_code），
    /// kill 掉就没有主体接收结果了，配置写不进去。什么时候结束由超时与取消令牌决定，不由回调决定。
    /// </para>
    /// <para>回调抛异常会被吞掉 —— 它只是「顺便看一眼」，不许影响主流程与最终结果。</para>
    /// </param>
    internal static async Task<ProcessResult> RunAsync(
        ProcessStartInfo psi,
        string? stdin,
        int timeoutMs,
        CancellationToken ct = default,
        Action<string>? onLine = null)
    {
        Process proc;
        try
        {
            // 先建对象、订阅回调、再 Start：BeginOutputReadLine 要求订阅早于启动，
            // 否则最早那几行可能落在「还没有读者」的管道里。
            proc = new Process { StartInfo = psi };
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", "", false, ex.Message);
        }

        // 按行累积 —— 这一条同时解决两个问题（2026-09-21）：
        //   ① 流式：读到就回调，不必等进程退出（config init --new 那类阻塞命令靠它）
        //   ② 超时保留已读：旧实现超时直接 return (…, "", "", true, null)，把已经读到的内容全丢了 ——
        //      而「输出完就卡住」的命令恰恰只有超时那一路才走得到，等于把唯一的证据也扔了。
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();

        void Accumulate(StringBuilder sink, string? data)
        {
            if (data == null) return;   // null = 该流已关闭（EOF）
            lock (gate) sink.AppendLine(data);
            if (onLine != null)
            {
                try { onLine(data); }
                catch { /* 回调是"顺便看一眼"，不许把主流程带下水 */ }
            }
        }

        proc.OutputDataReceived += (_, e) => Accumulate(stdout, e.Data);
        proc.ErrorDataReceived += (_, e) => Accumulate(stderr, e.Data);

        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                return new ProcessResult(-1, "", "", false, "Process.Start 返回空");
            }
        }
        catch (Exception ex)
        {
            proc.Dispose();
            return new ProcessResult(-1, "", "", false, ex.Message);
        }

        using (proc)
        {
            if (psi.RedirectStandardOutput) proc.BeginOutputReadLine();
            if (psi.RedirectStandardError) proc.BeginErrorReadLine();

            if (stdin != null && psi.RedirectStandardInput)
            {
                try
                {
                    await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                    proc.StandardInput.Close();
                }
                catch { /* 尽力而为：写不进去不影响读结果 */ }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs > 0 ? timeoutMs : 1);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                FlushReaders(proc);
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);
                FlushReaders(proc);
                // 超时也要把已经读到的内容交出去（调用方要拿它做诊断，不能是一串空字符串）
                return new ProcessResult(-1, Snapshot(stdout, gate), Snapshot(stderr, gate), true, null);
            }

            return new ProcessResult(proc.ExitCode, Snapshot(stdout, gate), Snapshot(stderr, gate), false, null);
        }
    }

    /// <summary>
    /// 等异步读取回调把管道理剩下的行收完（尾部几行否则可能还没进累积器）。
    /// <c>WaitForExit()</c> 的无参重载是这个语义的**官方保证**：它会等异步事件回调处理完。
    /// 先带超时问一次，避免进程杀不掉时把这里挂死 —— 拿不到就拿到多少算多少。
    /// </summary>
    private static void FlushReaders(Process proc)
    {
        try
        {
            if (proc.WaitForExit(2000)) proc.WaitForExit();
        }
        catch { /* 尽力而为 */ }
    }

    private static string Snapshot(StringBuilder sink, object gate)
    {
        lock (gate) return sink.ToString();
    }

    /// <summary>杀整棵进程树（脚本可能又起了子进程，比如 kb.py 会拉起 lark-cli）</summary>
    internal static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { /* 尽力而为 */ }
    }
}
