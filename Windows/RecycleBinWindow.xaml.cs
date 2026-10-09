using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusCapture.Services;
using FocusCapture.Services.Sync;

namespace FocusCapture.Windows;

/// <summary>回收站列表项 ViewModel：包装 (记录文件名 + 记录内容)，供绑定展示；带 IsSelected 用于多选（2026-08-15 回收站 UI 优化）</summary>
public class RecycleItemViewModel : INotifyPropertyChanged
{
    public string FileName { get; }
    public RecycleBinEntry Entry { get; }
    public string Preview => Entry.Preview;
    public string DeletedAtText => Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm");
    public string ExpiresAtText => Entry.ExpiresAt.ToString("yyyy-MM-dd");

    private bool _isSelected;
    /// <summary>是否被勾选（CheckBox 双向绑定；ListBox.SelectionMode 设为 None，弃用 ListBox 选中机制避免双轨）</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public RecycleItemViewModel(string fileName, RecycleBinEntry entry)
    {
        FileName = fileName;
        Entry = entry;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class RecycleBinWindow : Window
{
    private readonly NoteService _noteService;
    private readonly RecycleBinService _recycleBin;
    private readonly SyncEngine? _syncEngine;
    private List<RecycleItemViewModel> _viewModels = new();

    public RecycleBinWindow(NoteService noteService, RecycleBinService recycleBin, SyncEngine? syncEngine = null)
    {
        InitializeComponent();
        _noteService = noteService;
        _recycleBin = recycleBin;
        _syncEngine = syncEngine;
        Reload();
    }

    private void Reload()
    {
        _viewModels = _recycleBin.List()
            .Select(x => new RecycleItemViewModel(x.FileName, x.Entry))
            .ToList();
        RecycleList.ItemsSource = _viewModels;
        EmptyHint.Visibility = _viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUI();
    }

    /// <summary>选中状态汇总：刷新按钮可用性 + 已选计数显示 + "恢复所选"文字带 (N)</summary>
    private void UpdateSelectionUI()
    {
        var selectedCount = _viewModels.Count(vm => vm.IsSelected);
        BtnRestore.IsEnabled = selectedCount > 0;
        BtnPurge.IsEnabled = _viewModels.Count > 0;

        if (selectedCount > 0)
        {
            SelectedCountText.Text = $"已选 {selectedCount} 条";
            SelectedCountText.Visibility = Visibility.Visible;
            BtnRestore.Content = $"恢复所选 ({selectedCount})";
        }
        else
        {
            SelectedCountText.Visibility = Visibility.Collapsed;
            BtnRestore.Content = "恢复所选";
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove 在窗口未显示时会抛异常 */ }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>CheckBox 勾选/取消 → 刷新选中 UI（点条目本身不触发，绑定走 TwoWay 直接更新 vm）</summary>
    private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateSelectionUI();

    /// <summary>全选：所有条目 CheckBox 勾上</summary>
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = true;
        UpdateSelectionUI();
    }

    /// <summary>反选：所有条目 CheckBox 状态取反</summary>
    private void BtnInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = !vm.IsSelected;
        UpdateSelectionUI();
    }

    /// <summary>恢复所选：批量恢复所有勾选项（2026-08-15 修复：原版只支持单条 ListBox 选中）</summary>
    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModels.Where(vm => vm.IsSelected)
            .Select(vm => (vm.FileName, vm.Entry))
            .ToList();
        if (selected.Count == 0) return;

        var restored = _recycleBin.RestoreBatch(selected, _noteService);
        if (restored > 0 && _syncEngine != null)
        {
            // 恢复 → PendingRestores 压入（push 覆盖云端删除标记）+ 触发同步传播到其他设备
            foreach (var (_, entry) in selected)
                _syncEngine.QueuePendingRestore(entry.RelativePath, entry.Lines);
            _noteService.RaiseNotesChanged();
        }
        if (restored < selected.Count)
        {
            System.Windows.MessageBox.Show(
                $"{restored}/{selected.Count} 条恢复成功，部分条目失败（详见调试日志）",
                "部分恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Reload();
    }

    private void BtnPurge_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "确认清空回收站？\n\n清空后这些笔记将被彻底删除，无法恢复。",
            "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var purged = _recycleBin.PurgeAll();
        // 清空回收站 → 触发同步软删（Deleted=true 上传）
        if (purged.Count > 0)
        {
            _noteService.RaiseNotesChanged();
            _syncEngine?.QueueRecycleBinPurge(purged);
        }
        Reload();
    }
}
