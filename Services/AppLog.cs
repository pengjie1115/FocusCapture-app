using System.Text;

namespace FocusCapture.Services;

/// <summary>
/// 运行日志：AppData\Roaming\FocusCapture\logs\app_yyyy-MM-dd.log，按天一个文件。
/// 保留天数可配置（AppSettings.LogRetentionDays，默认 30，启动时自动清理更早的）；
/// 单日文件超 10MB 停写（防错误风暴刷爆磁盘）。线程安全。Error 级别同步输出到 Debug（调试器可见）。
/// </summary>
public static class AppLog
{
    public const int DefaultRetentionDays = 30;
    private const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly object _lock = new();
    private static bool _cleaned;
    private static int _retentionDays = DefaultRetentionDays;

    /// <summary>日志保留天数（设置面板可改；启动时从 AppSettings 惰性读取，改设置即时生效）</summary>
    public static int RetentionDays
    {
        get
        {
            if (_retentionDays == DefaultRetentionDays && !_retentionLoaded)
            {
                try { _retentionDays = Math.Clamp(FocusCapture.Models.AppSettings.Load().LogRetentionDays, 1, 365); }
                catch { /* 读不到就用默认 30 */ }
                _retentionLoaded = true;
            }
            return _retentionDays;
        }
    }
    private static bool _retentionLoaded;

    /// <summary>设置面板修改保留天数后调用：立即生效并重新执行一次过期清理</summary>
    public static void ApplyRetention(int days)
    {
        _retentionDays = Math.Clamp(days, 1, 365);
        _retentionLoaded = true;
        lock (_lock)
        {
            try { CleanOldLogs(Dir); _cleaned = true; } catch { /* 清理失败不影响主流程 */ }
        }
    }

    private static string Dir => FocusCapturePaths.Combine("logs");

    /// <summary>日志目录（设置面板跳转用，确保目录存在）</summary>
    public static string EnsureLogDir()
    {
        Directory.CreateDirectory(Dir);
        return Dir;
    }

    public static void Info(string tag, string message) => Write("INFO", tag, message);
    public static void Warn(string tag, string message) => Write("WARN", tag, message);

    public static void Error(string tag, string message)
    {
        Write("ERROR", tag, message);
        System.Diagnostics.Debug.WriteLine($"[FocusCapture][{tag}] {message}");
    }

    public static void Error(string tag, string message, Exception ex)
        => Error(tag, $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string tag, string message)
    {
        try
        {
            lock (_lock)
            {
                var dir = Dir;
                Directory.CreateDirectory(dir);

                if (!_cleaned)
                {
                    CleanOldLogs(dir);
                    _cleaned = true;
                }

                var filePath = Path.Combine(dir, $"app_{DateTime.Now:yyyy-MM-dd}.log");

                // 单文件保险丝：超限停写（写一次告警提示后就静默，避免告警本身也刷盘）
                if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxFileBytes)
                    return;

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{tag}] {message}";
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不能影响主流程，静默放弃
        }
    }

    /// <summary>删除超过保留期的 app_*.log（首次写日志时执行一次，改保留天数时重跑）</summary>
    private static void CleanOldLogs(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(dir, "app_*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // app_yyyy-MM-dd
                if (name.Length >= 13 &&
                    DateTime.TryParseExact(name[4..13], "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var date) &&
                    date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
