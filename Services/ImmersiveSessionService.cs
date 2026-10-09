namespace FocusCapture.Services;

/// <summary>沉浸式输入会话状态：会话激活时，对应笔记禁止编辑/回填</summary>
public static class ImmersiveSessionService
{
    public static bool IsActive { get; private set; }
    public static DateTime? ActiveTimestamp { get; private set; }

    public static void Start(DateTime timestamp) { IsActive = true; ActiveTimestamp = timestamp; }
    public static void Stop() { IsActive = false; ActiveTimestamp = null; }

    /// <summary>判断笔记是否处于沉浸式锁定中</summary>
    public static bool IsLocked(DateTime noteTimestamp)
        => IsActive && ActiveTimestamp.HasValue
           && Math.Abs((noteTimestamp - ActiveTimestamp.Value).TotalSeconds) < 60;
}
