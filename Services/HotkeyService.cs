using FocusCapture.Models;

namespace FocusCapture.Services;

public class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly AppSettings _settings;

    // 热键 ID 常量
    public const int ID_SUMMON = 1001;
    public const int ID_CLIPBOARD_TOGGLE = 1002;
    public const int ID_QUICK_VIEW = 1003;
    public const int ID_VOICE_INPUT = 1004;
    public const int ID_TODO_SWITCH = 1005;   // v3.5：输入框笔记/待办类型切换（全局热键，设置可改）
    public const int ID_SETTINGS = 1006;      // v3.7：唤出设置面板
    public const int ID_AI_ASK = 1007;        // v3.8：唤起 AI 问答
    public const int ID_TODO_SUMMARY = 1008;  // v3.8：待办汇总面板（按一次唤出，再按收起）

    /// <summary>注册失败的一条热键（键位被其他程序占用等）。</summary>
    public sealed record HotkeyFailure(string Name, string Keys);

    private readonly List<HotkeyFailure> _lastFailures = new();

    /// <summary>最近一次 RegisterAll 的失败清单（设置面板据此提示用户换键位）。
    /// 只反映最近一次整体注册结果，不代表录制期（StartCapture 临时注销）的中间状态。</summary>
    public IReadOnlyList<HotkeyFailure> LastFailures => _lastFailures;

    public event Action<int>? HotkeyPressed; // 回调传热键 ID

    public HotkeyService(IntPtr hwnd, AppSettings settings)
    {
        _hwnd = hwnd;
        _settings = settings;
    }

    public void RegisterAll()
    {
        UnregisterAll();
        _lastFailures.Clear();

        Register(ID_SUMMON, _settings.SummonHotkey, "呼出输入框");
        Register(ID_CLIPBOARD_TOGGLE, _settings.ClipboardToggleHotkey, "剪贴板捕获开关");
        Register(ID_QUICK_VIEW, _settings.QuickViewHotkey, "今日笔记速览");
        Register(ID_VOICE_INPUT, _settings.VoiceInputHotkey, "沉浸记录");
        Register(ID_TODO_SWITCH, _settings.TodoSwitchHotkey, "类型切换快捷键");
        Register(ID_SETTINGS, _settings.SettingsHotkey, "唤出设置面板");
        Register(ID_AI_ASK, _settings.AiAskHotkey, "唤起 AI 问答");
        Register(ID_TODO_SUMMARY, _settings.TodoSummaryHotkey, "待办汇总面板");
    }

    public void UnregisterAll()
    {
        Win32.UnregisterHotKey(_hwnd, ID_SUMMON);
        Win32.UnregisterHotKey(_hwnd, ID_CLIPBOARD_TOGGLE);
        Win32.UnregisterHotKey(_hwnd, ID_QUICK_VIEW);
        Win32.UnregisterHotKey(_hwnd, ID_VOICE_INPUT);
        Win32.UnregisterHotKey(_hwnd, ID_TODO_SWITCH);
        Win32.UnregisterHotKey(_hwnd, ID_SETTINGS);
        Win32.UnregisterHotKey(_hwnd, ID_AI_ASK);
        Win32.UnregisterHotKey(_hwnd, ID_TODO_SUMMARY);
    }

    /// <summary>注册单个全局热键。失败（多为他程序已占用同一组合）不抛异常：
    /// 记入 LastFailures 供设置面板提示 + 写运行日志留痕，其余热键照常注册。</summary>
    private void Register(int id, HotkeyBinding hk, string name)
    {
        if (hk.Key == 0) return; // disabled hotkey
        var mods = Win32.ModifiersToWin32(hk.Modifiers);
        if (Win32.RegisterHotKey(_hwnd, id, mods, (uint)hk.Key)) return;

        var keys = Win32.HotkeyToString(hk);
        _lastFailures.Add(new HotkeyFailure(name, keys));
        AppLog.Warn("Hotkey", $"全局热键注册失败（可能被其他程序占用）：{name} = {keys}");
    }

    /// <summary>处理 WM_HOTKEY 消息</summary>
    public void HandleHotkey(int id)
    {
        HotkeyPressed?.Invoke(id);
    }

    public void Dispose() => UnregisterAll();
}
