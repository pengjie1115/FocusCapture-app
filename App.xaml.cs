using FocusCapture.Diagnostics;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Skills;

namespace FocusCapture;

public partial class App : WpfApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF 原生复制（TextBox/RichTextBox 的 Ctrl+C/X）失败默认完全静默（catch 后 return）——
        // 远程工具占用剪贴板时，用户在灵感输入框等处的复制"没反应"却无从察觉（2026-10-01 修复⑤）。
        // 打开官方开关让失败抛出，由下面的全局 DispatcherUnhandledException 记日志并弹提示（不再静默）。
        // 必须在首个窗口创建前设置。
        System.Windows.FrameworkCompatibilityPreferences.ShouldThrowOnCopyOrCutFailure = true;

        // 数据根定位（2026-09-16）：必须排在所有落盘动作之前 —— AppLog 自己也住在数据根下。
        // 读的是默认根里的指针文件；自定义根不可用时只出提示，绝不偷换回默认根（防数据分裂成两套）。
        var rootWarning = FocusCapturePaths.LoadCustomRoot();

        AppLog.Info("App", $"FocusCapture 启动（{typeof(App).Assembly.GetName().Version}）");

        // 全局异常兜底：防止静默崩溃，确保用户能看到错误信息。
        // 2026-08-13 审查修正：可恢复的 UI 异常（如绑定错误）不再强制 Shutdown(1)——
        // 记录日志 + 弹窗提示 + Handled 继续运行，避免"报错→点确定→闪退"（回收站窗口教训）。
        DispatcherUnhandledException += (_, args) =>
        {
            // 剪贴板被占用（0x800401D0 CLIPBRD_E_CANT_OPEN）不弹框：WPF 内置 Copy/Cut 在
            // OleSetClipboard 成功后才走到 Clipboard.Flush() 固化 —— 抛这个异常时数据已进剪贴板
            // （延迟渲染态），粘贴照常可用，弹"界面错误"是虚惊（2026-10-01 远程工具共开时实测）。
            // 记 crash.log 便于统计发生频率；AI 问答输入框的 Ctrl+C/X 已自己接管，这里是其他窗口的兜底。
            if (args.Exception is System.Runtime.InteropServices.COMException ce
                && ce.ErrorCode == unchecked((int)0x800401D0))
            {
                LogCrash("UI-剪贴板占用", args.Exception);
                args.Handled = true;
                return;
            }
            LogCrash("UI", args.Exception);
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"FocusCapture 遇到一个界面错误：\n\n{args.Exception.Message}\n\n" +
                    "详细日志已写入：\n%LocalAppData%\\FocusCapture\\crash.log\n\n程序将继续运行。",
                    "FocusCapture 提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* 弹窗也失败就放弃 */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        };

        // 必须先初始化 OLE/STA，否则在 WPF 线程上构造 NotifyIcon
        // 在某些 Windows 版本（特别是 Win11 24H2+）会静默失败，
        // 导致 OnSourceInitialized 中断、悬浮球和托盘都不出现。
        try
        {
            System.Windows.Forms.Application.OleRequired();
        }
        catch { /* best effort */ }

        base.OnStartup(e);

        // 界面快照模式（--snapshot）：把窗口渲染成 PNG 后退出，不创建主窗口。
        // 仅供开发期 UI 自查使用；正常启动（不带该参数）不进入此分支，行为与改造前完全一致。
        if (UiSnapshot.IsRequested(e.Args))
        {
            UiSnapshot.Run(e.Args);
            return;
        }

        // 拖放探针模式（--dragprobe）：只拉起探针球 + 诊断面板，不创建主窗口
        // （避免悬浮球/托盘/剪贴板监听干扰拖放验证）。仅供开发期验证「拖到悬浮球」交互是否成立。
        if (DragProbe.IsRequested(e.Args))
        {
            DragProbe.Run(e.Args);
            return;
        }

        // 手验开关（--license-demo，2026-10-08）：让门卫在**不改源码默认值**的前提下上岗。
        //
        // 背景：IsLicensingEnabled 发布前必须为 false（防「AI 被拦但 Word 还没接上」的中间态），
        //   但那样手验就无从下手 —— 改 license.json 看不出任何效果，因为 IsAllowed 第一行
        //   就 `if (!IsLicensingEnabled) return true;` 全放行了。
        // 安全：本开关只在 DEBUG 构建存在（见 LicenseGate.ApplyDemoSwitch 的 #if DEBUG）——
        //   Release 编译期物理屏蔽，参数被忽略、门卫保持默认值（任务书 §12.4 铁律）。
        // 位置：排在两个开发期模式**之后**（它们各自 return，不会走到这里）；与既有参数解析同区。
        // 配套：tools\dev.ps1 license（写状态文件）+ license launch（带本参数启动 exe）。
        LicenseGate.ApplyDemoSwitch(e.Args);

        new MainWindow().Show();

        // 试用 / 激活状态初始化（2026-10-08）。
        //
        // ⚠ 位置同样排在两个开发期模式**之后**，理由与下面内置 Skill 落地完全一样：
        //   放在前面的话，快照模式启动时会以**真实数据根**去 Load/推进 license.json
        //   —— 等于「跑一次快照就消耗用户一天试用」（`UiSnapshot.Run` 内部的 RootOverride 还没设）。
        //   快照模式有自己的沙箱路径，所以它会自己生成一份沙箱 license.json，两边互不干扰。
        //
        // 为什么启动时就要调：不调也能工作（首次访问会按需载入），但「时钟回拨」这类异常
        // 只有在启动时推进才第一时间进日志；试用计日也要求「每次启动 +1 天」。
        // 内部自己 try 住（Initialize 不抛），这里再加一层是为了绝不让启动路径因它挂掉。
        try
        {
            LicenseGate.Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Warn("License", $"试用状态初始化失败：{ex.Message}");
        }

        // 内置 Skill 首次落地（2026-09-21，授权闭环步骤 4）。
        //
        // ⚠ **位置是硬的：必须排在上面两个开发期模式之后**（`--snapshot` / `--dragprobe`）。
        // 那两个模式自己会把数据根指到临时沙箱、然后 `return`，所以放在它们后面 = 它们一个字节都不写；
        // 而它们设沙箱的动作发生在更后面（UiSnapshot.Run 内部），放在前面就会拿**真实数据根**去落地。
        // 2026-09-21 实测踩到：快照模式跑完，真实数据目录里凭空多出 `Skills\lark-cli\`
        // —— 快照本该零副作用，这一条是"开发工具污染用户数据"。
        //
        // 落地策略：跟着数据根走（用户自定义了数据根，技能也要落到那边）；**只补不覆盖**，
        // 目标里已经有 SKILL.md 就一个字节都不动，用户改过的版本优先。
        // try 是硬要求：这段住在启动路径上，它抛异常的表现是"程序打不开"，比"少一个技能"严重一个量级。
        try
        {
            var skillsRoot = FocusCapturePaths.Combine("Skills");
            var deployed = BuiltinSkills.Deploy(BuiltinSkills.SourceRoot(AppContext.BaseDirectory), skillsRoot);
            if (deployed.Count > 0)
                AppLog.Info("Skill", $"内置 Skill 首次落地：{string.Join("、", deployed)}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Skill", "内置 Skill 落地失败（不影响启动）：" + ex.Message);
        }

        // 自定义数据根不可用：主窗口起来后再提示（启动期弹窗会挡住托盘/悬浮球的初始化）
        if (rootWarning != null)
        {
            AppLog.Warn("App", "自定义数据目录不可用：" + FocusCapturePaths.Root);
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(rootWarning, "数据目录不可用", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }

        // 附件孤儿清理（2026-09-14）：删掉没有任何会话引用的附件文件（会话被裁剪/删除/云端覆盖后留下的）。
        // 放后台线程 + 失败静默 —— 纯清理动作，绝不能拖慢或影响启动。
        _ = Task.Run(() =>
        {
            try
            {
                var removed = ChatAttachmentService.CleanupOrphans();
                if (removed > 0) AppLog.Info("App", $"附件孤儿清理：删除 {removed} 个无引用文件");
            }
            catch { /* best effort */ }
        });
    }

    private static void LogCrash(string source, Exception ex)
    {
        AppLog.Error("Crash", $"{source} 未处理异常", ex);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusCapture");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { /* 写不进日志也别崩 */ }
    }
}
