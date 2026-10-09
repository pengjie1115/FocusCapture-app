using FocusCapture.Services.Destinations.GetNote;

namespace FocusCapture.Windows;

/// <summary>
/// 得到大脑「重复推送询问」三按钮弹窗（2026-10-01）。
/// <b>整批一次</b>：PushAsync 检测到本批含已推送条目时调用一次，用户对整批做同一处置 ——
/// 不是每条问一遍（旧版静默跳过的另一极端，问几十条会问到崩溃）。
/// ShowDialog 返回 true 时 <see cref="Choice"/> 有效。
/// </summary>
public partial class GetNoteDuplicateDialog : Window
{
    /// <summary>用户点了哪个按钮；ShowDialog 返回 false（Esc / 关闭）时恒为 Cancel。</summary>
    public GetNotePushService.DuplicateChoice Choice { get; private set; } =
        GetNotePushService.DuplicateChoice.Cancel;

    public GetNoteDuplicateDialog(IReadOnlyList<GetNotePushService.DuplicateInfo> duplicates)
    {
        InitializeComponent();
        var n = duplicates.Count;
        IntroText.Text = n == 1
            ? "这 1 条笔记之前已推送过得到大脑，这次要怎么处理？"
            : $"以下 {n} 条笔记之前已推送过得到大脑，这次要怎么处理？（对整批生效）";
        ItemListText.Text = BuildItemList(duplicates);
    }

    /// <summary>
    /// 重复条目清单文本（静态便于检查点直测）。注意括号位置：<c>string.Join</c> 必须先闭合、
    /// 折叠尾注拼在 <b>Join 的结果</b>之后 —— 此前括号写进了 Join 的参数里，C# 允许「对象+字符串」
    /// 隐式拼接，编译不报错，运行时把 LINQ 迭代器整个 ToString 成类型名（2026-10-01 修复）。
    /// </summary>
    public static string BuildItemList(IReadOnlyList<GetNotePushService.DuplicateInfo> duplicates)
    {
        var n = duplicates.Count;
        return string.Join("\n", duplicates.Take(50).Select((d, i) =>
        {
            var title = string.IsNullOrWhiteSpace(d.Title) ? "（无标题）" : d.Title;
            if (title.Length > 60) title = title[..60] + "…";
            return $"  {i + 1}. {title}";
        })) + (n > 50 ? $"\n  …（共 {n} 条）" : "");
    }

    private void BtnKeep_Click(object sender, RoutedEventArgs e) => Close(GetNotePushService.DuplicateChoice.Keep);
    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close(GetNotePushService.DuplicateChoice.Remove);
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close(GetNotePushService.DuplicateChoice.Cancel);

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close(GetNotePushService.DuplicateChoice.Cancel);
        }
    }

    private void Close(GetNotePushService.DuplicateChoice choice)
    {
        Choice = choice;
        DialogResult = true;
        Close();
    }
}
