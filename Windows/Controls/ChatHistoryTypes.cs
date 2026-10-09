using System.ComponentModel;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows.Controls;

// 2026-09-24 从已删除的 Windows/Controls/HistoryDrawer.xaml.cs（旧抽屉死代码，用户拍板删除）迁出：
// 下列三个类型是 ChatSidebar / AIDialogWindow / ChatTrashWindow / ChatExportService 正在使用的活基础设施，
// 删死文件时必须整体搬出来。原文件里的 GroupHeaderViewModel / ChatBatchAction / NonEmptyToVisibilityConverter
// 全仓零引用，随死文件一并退场（没有迁）。

/// <summary>历史会话列表项 ViewModel（列表展示用，FilePath 用于宿主还原完整会话；Id 为主键）。
/// 实现 INPC 仅用于多选模式切换（IsSelected / IsBatchMode）与相对时间刷新（RelativeTimeText），
/// 业务字段（Id/Title/Pinned/GroupId）只读。</summary>
public class HistoryItemViewModel : INotifyPropertyChanged
{
    public string Id { get; }
    public string FilePath { get; }
    /// <summary>日期 + 详细时间（MM-dd HH:mm）。**分组视图与全局搜索窗沿用这个口径**（用户 2026-09-26 拍板：
    /// 分组里的会话时间保持不变，只有侧边栏列表改相对时间）。</summary>
    public string TimeText { get; }
    /// <summary>相对时间文案（刚刚 / N分钟前 / N小时前 / N天前），只给侧边栏列表用（2026-09-26）。
    /// 会被 <see cref="RefreshRelativeTime"/> 按分钟重算，故带 INPC。</summary>
    public string RelativeTimeText { get; private set; }
    public string ModeText { get; }
    public string Title { get; }
    public bool Pinned { get; }
    public string GroupId { get; }
    public string GroupName { get; }
    public string Preview { get; }

    /// <summary>相对时间的基准时刻。列表项会被反复重算，原始时间戳必须留着（别从 TimeText 反推）。</summary>
    private readonly DateTime _savedAt;

    private bool _isSelected;
    /// <summary>多选模式下的勾选状态</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Fire(nameof(IsSelected)); } }
    }

    private bool _isBatchMode;
    /// <summary>列表是否处于多选模式（决定勾选框与三点按钮的显隐、条目点击行为）</summary>
    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (_isBatchMode == value) return;
            _isBatchMode = value;
            Fire(nameof(IsBatchMode));
            Fire(nameof(BatchCheckVis));
            Fire(nameof(MoreVis));
        }
    }

    public Visibility BatchCheckVis => _isBatchMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MoreVis => _isBatchMode ? Visibility.Collapsed : Visibility.Visible;

    public HistoryItemViewModel(string id, string filePath, DateTime savedAt, string modeText,
        string title, bool pinned, string groupId, string groupName, string preview)
    {
        Id = id;
        FilePath = filePath;
        _savedAt = savedAt;
        TimeText = savedAt.ToString("MM-dd HH:mm");
        RelativeTimeText = ChatTimeText.Relative(savedAt, DateTime.Now);
        ModeText = modeText;
        Title = title ?? "";
        Pinned = pinned;
        GroupId = groupId ?? "";
        GroupName = groupName ?? "";
        Preview = string.IsNullOrEmpty(preview) ? "（空会话）" : preview;
    }

    /// <summary>
    /// 按"此刻"重算相对时间文案（侧边栏每分钟调一次 —— 不重算的话侧边栏挂着不动，
    /// 「刚刚」会永远停在「刚刚」）。文案没变就不发通知，避免整列无谓重绑。
    /// </summary>
    public void RefreshRelativeTime(DateTime now)
    {
        var text = ChatTimeText.Relative(_savedAt, now);
        if (string.Equals(text, RelativeTimeText, StringComparison.Ordinal)) return;
        RelativeTimeText = text;
        Fire(nameof(RelativeTimeText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Fire(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 会话时间的相对化文案（2026-09-26 用户要求：侧边栏不再显示"09-24 20:19"这种日期 + 详细时间）。
///
/// 规则：&lt;1 分钟 = 刚刚；&lt;1 小时 = N分钟前；&lt;24 小时 = N小时前；其余 = N天前（天数不设上限）。
///
/// **刻意写成纯函数**（now 由调用方传入）—— 时间文案算错了不会抛异常，只会"看起来怪"，
/// 只能靠慢层检查点钉住边界（59 秒 / 60 秒 / 59 分 / 60 分 / 23 小时 / 24 小时）。
/// 未来时间（用户改系统时钟、跨端同步来一条时间戳更晚的会话）统一按「刚刚」处理：
/// 宁可显示"刚刚"，也不出现"-3分钟前"这种自曝其短的文案。
/// </summary>
public static class ChatTimeText
{
    public static string Relative(DateTime savedAt, DateTime now)
    {
        var delta = now - savedAt;
        if (delta < TimeSpan.FromMinutes(1)) return "刚刚";   // 含 delta < 0（时钟回拨 / 未来时间戳）
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}分钟前";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}小时前";
        return $"{(int)delta.TotalDays}天前";
    }
}

/// <summary>条目操作（三个点菜单）：宿主负责业务（改字段 → Save 自动同步；删除经 MarkDeleted 管道）</summary>
public enum ChatItemAction
{
    BatchStart,  // 进入多选模式
    Rename,      // 重命名（改 Title）
    TogglePin,   // 置顶/取消置顶（切 Pinned）
    Group,       // 分组到…（context = 目标分组 Id；null = 未分组）
    Export,      // 导出（context = ExportFormat 名称）
    Delete,      // 删除（移入会话回收站）
    Favorite,    // 收藏 / 取消收藏（GroupId ↔ FavoriteId 互斥切换，2026-09-23 新增）
}

/// <summary>ExplainMode 的界面文案（侧边栏与对话窗口共用，避免两处漂移）</summary>
public static class AiModeText
{
    public static string Get(string mode) =>
        Enum.TryParse<ExplainMode>(mode, out var m) ? Get(m) : "未知";

    public static string Get(ExplainMode mode) => mode switch
    {
        ExplainMode.Translate => "翻译",
        ExplainMode.Search => "搜索",
        _ => "问答",
    };
}
