using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Windows;

namespace FocusCapture.Diagnostics;

/// <summary>
/// 界面快照工具（2026-09-13 引入）：把窗口渲染成 PNG，供开发期自查 UI 问题
/// （控件溢出 / 文字对比度 / 图标对齐 / 字形残缺 / 裁切）。
///
/// 用法：<c>FocusCapture.exe --snapshot [--out &lt;目录&gt;] [--only &lt;关键字&gt;] [--list]</c>
/// 输出：默认 <c>%TEMP%\fc-ui-snapshot\</c>，文件名形如 <c>01-灵感速览面板.png</c>，
///       同时写一份 <c>snapshot.log</c> 记录每个窗口的实际像素尺寸（尺寸异常 = 布局异常的第一信号）。
///
/// <b>按需出图（2026-09-26 新增）</b>：45 个场景全量跑一遍约 1 分钟，改一个板块时大半的图是白跑的。
/// <c>--list</c> 只把「将要渲染的场景名」落盘成 <c>scenes.txt</c>（不构造窗口，毫秒级完成）；
/// <c>--only 03b,10</c> 只渲染名字里含这些子串的场景 —— <b>建议用编号</b>（<c>03</c> / <c>10b</c>），
/// 拿中文板块名当关键字会被后续改名甩掉（如 <c>03d-设置-AI 问答界面</c> 名字里根本没有「AI 功能」）。
/// 两者可叠加：<c>--list --only 03</c> = 只看 03 这一批有哪些场景。
/// <b>从 PowerShell / dev.ps1 调用时，值必须加引号</b>（<c>-Only '03d,10e'</c>）：不加引号时
/// PowerShell 会把 <c>03d</c> 当 decimal 后缀吃成 <c>3</c>，静默匹配到一堆无关场景（2026-09-26 实测踩到）。
///
/// <b>关键字一个都没匹配到时必须失败退出（退出码 2）</b>：按需出图的致命失败模式是
/// 「漏了图却不知道」—— 安静地出 0 张等于把整个判读环节骗过去，比全量慢危险得多。
///
/// 设计约束（改动本文件前务必先读）：
/// - <b>数据隔离</b>：强制把 <see cref="FocusCapturePaths.RootOverride"/> 指向临时沙箱，
///   绝不读写用户真实的 <c>%AppData%\FocusCapture</c>（沙箱用完即删）。
/// - <b>零侵入</b>：仅当显式传入 <c>--snapshot</c> 时执行；正常启动不进入该分支，行为与改造前一致。
/// - <b>必须 Show() 才能渲染</b>：窗口统一摆到屏幕外（Left/Top = -32000），不打扰用户。
/// - <b>不透明渲染</b>：面板本身 Opacity=0.8，半透明位图不利于像素比对，快照统一置为 1.0。
/// - <b>DPI 感知</b>：按窗口实际 DPI 缩放出图，保证与屏幕上看到的像素密度一致（125% 缩放也准确）。
/// </summary>
internal static class UiSnapshot
{
    /// <summary>触发快照的命令行开关。</summary>
    public const string Flag = "--snapshot";

    private const string OutFlag = "--out";

    /// <summary>只列清单不渲染（<c>--list</c>）。清单落盘成 <c>scenes.txt</c>，给 dev.ps1 读。</summary>
    private const string ListFlag = "--list";

    /// <summary>按关键字过滤场景（<c>--only &lt;关键字&gt;</c>，逗号分隔可给多个）。</summary>
    private const string OnlyFlag = "--only";

    /// <summary>本次是否只列清单（<c>--list</c>）。</summary>
    private static bool _listOnly;

    /// <summary>场景过滤关键字；空数组 = 全量出图（保持改造前的行为）。</summary>
    private static string[] _filters = Array.Empty<string>();

    /// <summary>入选（将要渲染）的场景名，顺序即渲染顺序。</summary>
    private static readonly List<string> _selected = new();

    /// <summary>被 Capture 调用过的场景总数 —— 用来算「入选 n / 共 m」，不硬编码 45。</summary>
    private static int _total;

    /// <summary>通过过滤的场景数。<c>--only</c> 下为 0 时要失败退出（见 Run 收尾）。</summary>
    private static int _matched;

    /// <summary>命令行是否请求了快照模式。</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>执行快照：构造各窗口 → 渲染 PNG → 落盘日志 → 退出应用。</summary>
    public static void Run(string[] args)
    {
        var outDir = ResolveOutDir(args);
        _listOnly = args.Any(a => a.Equals(ListFlag, StringComparison.OrdinalIgnoreCase));
        _filters = ResolveFilters(args);
        Directory.CreateDirectory(outDir);

        // 隔离沙箱：所有落盘路径改道，绝不触碰真实数据
        var sandbox = Path.Combine(Path.GetTempPath(), "fc-ui-snapshot",
            "sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        FocusCapturePaths.RootOverride = sandbox;

        var log = new StringBuilder();
        log.AppendLine($"输出目录: {outDir}");
        log.AppendLine($"沙箱目录: {sandbox}");
        log.AppendLine();

        var exitCode = 0;
        try
        {
            var settings = AppSettings.Load();
            settings.NotesPath = Path.Combine(sandbox, "notes");
            Directory.CreateDirectory(settings.NotesPath);
            var notes = new NoteService(settings, Path.Combine(sandbox, "deleted.json"));

            Capture("01-灵感速览面板", () => new QuickViewWindow(notes, settings), outDir, log);

            // ⚠ 板块索引 2026-09-23 变过一次：新「AI 功能」板块插在索引 2，此后所有板块索引 +1。
            //    2026-10-08 又新增「激活码」板块（追加到末尾，故既有索引全部不动）。
            //    改本文件里的 SelectedIndex 前，务必对照 SettingsWindow.xaml.cs 的 _sectionNames 顺序 ——
            //    按记忆写会选到隔壁板块，而且不报错（图看着"有内容"，只是内容不对）。
            //
            //    ── 权威对照表（与 SettingsWindow.xaml.cs 的 _sectionNames 同步，改动务必同改两处）──
            //      0 热键    1 AI 模型  2 AI 功能  3 整理规则  4 外观    5 显示
            //      6 灵感速览 7 输入框   8 云同步    9 待办与提醒 10 文件与网盘 11 通用  12 激活码
            //
            //    2026-10-08 修正：此前本文件 4 处 SelectedIndex 与实际板块存在整体偏移
            //    （曾把「显示」当「灵感速览」、「待办与提醒」当「文件与网盘」、
            //      「外观」当「显示」），已出图取证并逐条订正。教训见 PITFALLS.md。
            Capture("02-设置-灵感速览板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 6;   // 6 = 「灵感速览」
                return w;
            }, outDir, log);

            Capture("03-设置-热键板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 0;
                return w;
            }, outDir, log);

            // AI 模型板块（2026-09-23 起只管「供应商 × 模型」）。
            // 为什么必须单独出一张：卡片列表是 code-behind 动态生成的控件 ——
            //   ① 动态生成的按钮可能不继承窗口的隐式 Button 样式（深色主题下会变浅色默认样式）
            //   ② 卡片摘要里是长地址，深色底上要能看清、要会省略不撑破布局
            //   ③ 状态点配色有五种（通过/账户/Key/供应商/网络），配色错=误导用户，只能靠出图核
            Capture("03a-设置-AI 模型板块（供应商列表）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 1;      // 1 = 「AI 模型」
                w.SeedAiProvidersForSnapshot();   // 沙箱里没有任何供应商，不塞数据这张图只有一行空态提示
                return w;
            }, outDir, log);

            // 供应商编辑页（2026-09-23 新增窗口；同日按用户反馈改过两处排版）。
            // 展开第一行、第二行保持折叠 —— 一张图同时看到：
            //   ① 展开态的「上下文窗口 / 最大输出 Token 并排一行」（用户反馈改过的地方）
            //   ② 折叠态的展开箭头与垃圾桶图标（同样是反馈改过的：字形改成了画的 Path）
            Capture("03a2-供应商编辑页", () =>
            {
                var draft = new AiProviderEntry
                {
                    Id = "snap-edit", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-snapshot-not-a-real-key",
                    Models =
                    {
                        new AiModelEntry { Id = "deepseek-chat", DisplayName = "DeepSeek Chat", MaxOutputTokens = 8192 },
                        new AiModelEntry { Id = "deepseek-reasoner", DisplayName = "DeepSeek Reasoner", ContextWindow = 131072, MaxOutputTokens = 65536 },
                    },
                };
                var w = new AiProviderEditWindow(draft);
                w.ExpandFirstModelForSnapshot();
                return w;
            }, outDir, log);

            // 「获取可用模型」的多选窗（2026-09-23）：行由 code-behind 建（显示名 + 灰色模型 ID 两行），
            // 搜索与全选只切 Visibility、不重建。这两件事的布局问题静态代码都看不出来：
            // 超长模型 ID 会不会撑破行、计数与列表会不会对不上、按钮会不会被挤出窗口。
            Capture("03a3-获取可用模型（多选窗）", () =>
                new AiModelPickerWindow(new[]
                {
                    new ParsedModel("deepseek-chat", "DeepSeek Chat", 65536),
                    new ParsedModel("deepseek-reasoner", "DeepSeek Reasoner", 131072),
                    new ParsedModel("deepseek-ai/DeepSeek-V4-Flash", "硅基流动 ds v4 flash", 1048576),
                    new ParsedModel("Qwen/Qwen3-235B-A22B-Instruct-2507-长名字专门用来测截断", "通义千问 Qwen3 235B", 0),
                    new ParsedModel("glm-4-flash", "GLM-4-Flash", 0),
                    new ParsedModel("hunyuan-t1", "腾讯混元 T1", 0),
                }), outDir, log);

            // AI 功能板块（2026-09-23 从原「AI 模型」板块拆出）：Skill 分区住在这里。
            // 为什么必须单独出一张：这块有三类"静态代码看不出来"的东西 ——
            //   ① 已授权 Skill 列表是 code-behind 动态生成的控件（模板样式可能不继承）
            //   ② Skill 目录是长路径，深色底上要能看清、要会换行不撑破布局
            //   ③ 分区在板块底部，不滚到底根本看不见 —— 上一版就漏了这张图
            Capture("03b-设置-AI 功能板块（含 Skill 分区）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 2;      // 2 = 「AI 功能」
                w.UpdateLayout();                 // 先布局，否则 ScrollToEnd 无效
                w.ContentScroller.ScrollToEnd();  // Skill 分区在板块底部
                return w;
            }, outDir, log);

            // AI 功能板块的**最底部**：外部依赖区块（2026-09-20 新增）。
            // 为什么单独一张：依赖行是「异步探测 → 动态生成控件」两个坑叠在一起 ——
            //   ① 动态生成的按钮可能不继承窗口的隐式 Button 样式（深色主题下会变成浅色默认样式）
            //   ② 异步插入晚于"滚到底"，上一版快照里只剩一个区块标题、状态行与按钮完全看不见
            // 所以这里用同步入口先把行建出来再滚到底。这一张就是"必须出图才发现"的典型。
            Capture("03c-设置-AI 功能板块（底部·外部依赖）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 2;      // 2 = 「AI 功能」
                w.PrepareSkillDepsForSnapshot();
                return w;
            }, outDir, log);

            // AI 问答界面子块（2026-09-26）：本板块**最顶部**那一组（昵称 / 自定义欢迎语 / 用户头像）。
            // 为什么必须单独一张：03b 与 03c 都滚到了板块底部，顶部这组正好两张都覆盖不到 ——
            // 而"设置项加了却看不见/被挤出去"正是只有出图才能发现的问题。
            Capture("03d-设置-AI 问答界面", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 2;      // 2 = 「AI 功能」
                return w;
            }, outDir, log);

            // 其余可无副作用构造的窗口：用于横向排查同一类 UI 写法（按钮内边距 / 图标字形 / 对齐）
            Capture("04-全局查找弹窗", () => new SearchDialog(), outDir, log);
            Capture("05-待办汇总", () => new TodoSummaryWindow(notes, settings), outDir, log);
            Capture("06-每日总结", () => new DailySummaryWindow(notes, settings), outDir, log);
            Capture("07-回收站", () => new RecycleBinWindow(notes, notes.RecycleBin), outDir, log);
            Capture("08-输入框", () => new InputWindow(notes, settings), outDir, log);
            Capture("09-语音输入", () => new VoiceInputWindow(settings), outDir, log);
            Capture("10-AI 对话", () => new AIDialogWindow(notes, settings), outDir, log);

            // 侧边栏与分组视图（2026-09-23 重构）：默认收起的侧边栏在场景 10 里根本不在图上，
            // 这两张就是它们唯一的回归守护 —— 分区顺序对不对、置顶是不是两处都在、
            // 分组视图的工具行有没有被挤出去，都只有出图才看得见。
            Capture("10b-AI 对话-侧边栏", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedSidebarForSnapshot();
                return w;
            }, outDir, log);

            Capture("10c-AI 对话-分组视图", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedGroupViewForSnapshot();
                return w;
            }, outDir, log);

            // 起手页（2026-09-23）：空会话 → 欢迎语 + 输入框垂直居中。这是批 3 最直观的变化，
            // 也是「欢迎语会不会与输入框叠在一起」这种错唯一能看出来的地方。
            Capture("10d-AI 对话-起手页", () =>
            {
                settings.ChatUserNickname = "彭杰";
                return new AIDialogWindow(notes, settings);
            }, outDir, log);

            // 批量多选态（2026-09-24）：底部操作条 + ✓ 选中高亮 + 计数文案 ——
            // 三处只有进入多选才出现，默认布局的 10b/10c 永远覆盖不到。
            Capture("10e-AI 对话-批量操作", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedBatchModeForSnapshot();
                return w;
            }, outDir, log);

            // 分组内批量管理（2026-09-26 用户要求 1:1 复刻千问）：行首复选框 + 底部
            // 「全选/已选 n | 取消 / 移动到分组 / 删除」—— 侧边栏那张 10e 覆盖不到分组视图的这套条。
            // 编号用 10j：10h 已被「回答中按钮」占用（同号两图会互相误导判读）。
            Capture("10j-AI 对话-分组批量管理", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedGroupBatchForSnapshot();
                return w;
            }, outDir, log);

            // 分组搜索态（2026-09-24）：搜索框覆盖分组工具行 ——
            // 覆盖态会不会把分组名/按钮挤出可视区，只有出图才看得见（同批量态一个道理）。
            Capture("10f-AI 对话-分组搜索", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedGroupSearchForSnapshot();
                return w;
            }, outDir, log);

            // 对话态（2026-09-26）：有消息时输入区必须沉到底部。用户实测过「发完首条消息输入框还停在正中间」——
            // 根因是布局只在输入区变化时重算、气泡加进来时漏了重算；这条链路没有出图就守不住。
            // 侧边栏一起开着，顺带把「刚刚 / N分钟前」这套相对时间也验在图上。
            Capture("10g-AI 对话-对话态", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedConversationForSnapshot();
                return w;
            }, outDir, log);

            // 回答中的发送/停止按钮（2026-09-26）：蓝底圆 + 中心白色圆角方块（用户给的"铜钱"样式）。
            // 圆底色与图标是两处独立设置 —— 只切一处（老的实现就是只切图标、底色恒绿）在图上立刻看得见。
            Capture("10h-AI 对话-回答中按钮", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedBusyForSnapshot();
                return w;
            }, outDir, log);

            // 长助手名的标题栏（2026-09-26）：AI 助手名称是用户可自定义的，名字一长，
            // 标题栏右端那两个图标会不会被文字挤到/叠到，只有出图才看得见。
            // 这不是假想风险：本次实现的第一版用"同格左对齐 + 同格右对齐"叠放，TextBlock 拿到的可用宽度
            // 是整格，超长文本会一路伸到图标底下被盖住（TextTrimming 根本不生效）。
            // 助手名在窗口关闭时还原（Closed 在截图之后触发），后面的场景不受影响。
            Capture("10i-AI 对话-标题栏长助手名", () =>
            {
                var previous = settings.AiAssistantName;
                settings.AiAssistantName = "我的超级智能知识助手小秘书（项目开发专用版）";
                var w = new AIDialogWindow(notes, settings);
                w.SeedSidebarForSnapshot();
                w.Closed += (_, _) => settings.AiAssistantName = previous;
                return w;
            }, outDir, log);

            // 标题栏全功能预览：把 13 个功能全挂上、并放宽面板宽度避免溢出，
            // 用于一次性核验所有图标字形真实存在 —— 图标字符写错一个就会渲染成空框（豆腐块），
            // 这类错误静态代码看不出来，只能靠渲染结果判定。
            Capture("11-标题栏全功能预览", () =>
            {
                settings.QuickViewToolbarLeft = new List<string>
                    { "Calendar", "SyncUpload", "SyncDownload", "Search", "Refresh", "AiAsk" };
                settings.QuickViewToolbarRight = new List<string>
                    { "Export", "GetNote", "TodoSummary", "RecycleBin", "Settings", "Import", "DailySummary" };
                settings.QuickViewWidth = 1200;
                return new QuickViewWindow(notes, settings);
            }, outDir, log);

            // 图标字符自检：字体里有 ≠ WPF 渲染得出来（缺字形会变豆腐块），挑图标代码时用它一次过筛
            Capture("12-图标字符可用性自检", () => GlyphProbe.Build(), outDir, log);

            // 全启用状态下的设置面板：验证「下拉为空」的空态提示与按钮置灰。
            // 此状态在默认布局下不会出现（功能池有剩余），必须先把 13 个全挂上（复用 11 场景改过的 settings）。
            Capture("13-设置-全启用空态", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 6;   // 6 = 「灵感速览」
                return w;
            }, outDir, log);

            // 带附件的 AI 对话（2026-09-14）：输入区的附件 chip 与气泡里的缩略图，
            // 只在"真有附件"时才渲染，空会话快照（场景 10）覆盖不到这两个分支。
            Capture("14-AI 对话（带附件）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedAttachmentsForSnapshot();
                return w;
            }, outDir, log);

            // 文件与网盘板块（2026-09-16）：设置页新增的一整块，含三个按钮行与多段说明文字。
            // 10 = 「文件与网盘」（索引 2026-09-23 因新增「AI 功能」板块整体 +1）
            Capture("15-设置-文件与网盘板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 10;
                return w;
            }, outDir, log);

            // 已选择文件的卡片区（2026-09-16）：只在"用户真点过选择文件"时才渲染，
            // 空会话快照（场景 10）覆盖不到这个分支 —— 而它正是本次红线（AI 只能引用牌号）的界面落点。
            Capture("16-AI 对话（带已选择文件卡片）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedHandleChipsForSnapshot();
                return w;
            }, outDir, log);

            // 文件与网盘板块的**下半部分**（2026-09-16）：该板块最长，一屏装不下，
            // 滚动到底才能看到「对话附件」与「数据目录」两组 —— 而"控件被挤出可视区"正是
            // 静态代码查不出来、必须靠出图才能发现的问题类型。
            Capture("17-设置-文件与网盘板块（下半部分）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 10;   // 10 = 「文件与网盘」（索引 2026-09-23 因新增板块 +1）
                w.ScrollToEndForSnapshot();
                return w;
            }, outDir, log);

            // 云文件卡片的四个动作按钮（2026-09-16）：卡片只在"AI 真把文件取回并交付"时渲染，
            // 其它场景覆盖不到。加「彻底删除」后一行从三个按钮变四个 —— 正是"被挤出可视区"的高危形态。
            Capture("18-AI 对话（带云文件卡片）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedCloudFileCardsForSnapshot();
                return w;
            }, outDir, log);

            // ── 悬浮球拖放保存（2026-09-16）──
            // 这两个浮层是**独立窗口**（球窗口的透明区穿透，画在球里收不到鼠标事件），
            // 尺寸/配色/悬停都是线框数值，必须出图对照判读：小条宽 96 竖向；卡片宽 ≤276 **贴合内容**
            // （旧版卡片右侧留一大片空白，已否）；两者均**无图标**。

            Capture("19-拖放小条（得到大脑可用）",
                () => new DropActionStrip(3, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 未配得到大脑凭证 → 该项置灰 + 悬停提示去设置
            Capture("20-拖放小条（得到大脑未配置·置灰）",
                () => new DropActionStrip(3, getNoteAvailable: false, opacity: 1.0), outDir, log);

            // 非文本类文件：不显示「发到得到大脑」行（"只对文本类显示"是用户拍板）
            Capture("21-拖放卡片（单文件·非文本类）",
                () => new DropActionCard("季度汇报-2026Q3.pptx", "1.8 MB · 来自微信",
                    showGetNote: false, getNoteAvailable: false, opacity: 1.0), outDir, log);

            // 文本类文件：多一行「发到得到大脑」。标题用微信图片的自动生成名，顺带核验命名格式。
            Capture("22-拖放卡片（单文件·文本类）",
                () => new DropActionCard("微信图片_20260916_231045.jpg", "320 KB · 来自微信",
                    showGetNote: true, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 多文件：标题「N 个文件」+ 总大小（**推定规则，未与用户确认**，出图确认形态）
            Capture("23-拖放卡片（多文件）",
                () => new DropActionCard("3 个文件", "5.2 MB · 来自文件管理器",
                    showGetNote: false, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 设置「显示」板块（4 = 显示；索引 2026-09-23 因新增「AI 功能」板块 +1）。
            // ⚠ 设计稿原写「外观」板块（PanelAppearance），与实际不符 —— 三个透明度滑块其实都在
            //   「显示」（PanelDisplay）里，拖放这三项也落在同一板块（理由见 SettingsWindow.xaml 的注释）。
            //   新增设置项属 REGRESSION 维护触发条件，这里出图核验控件没被挤出可视区、默认值正确。
            Capture("24-设置-显示板块（含拖放设置）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 5;   // 5 = 「显示」（含拖放设置）
                return w;
            }, outDir, log);

            // ── 悬浮球角标（2026-09-19，两轮）──
            // 起因：用户实测「角标圆被窗口边界裁掉一块、数字在圈里偏上」（第一轮），
            //       改完又反馈「压球的面积太大、观感不好」（第二轮，窗口 48→56 把角标外移）。
            // 分工：「数字是否居中」「压球压了多少」靠 LogBadgeBounds 打的数字判读；观感靠出图判读 ——
            // 本工具的渲染不含窗口矩形的裁切，只看图会把"溢出窗口"误判成没问题（见该方法注释）。
            Capture("25-悬浮球（带角标）", () => new FloatBall(), outDir, log, afterShow: win =>
            {
                if (win is not FloatBall ball) return;
                ball.SetBadge(1, hasRead: false);   // 必须在 Show 之后调：SetBadge 内部检查 IsLoaded
                LogBadgeBounds(ball, log);
            }, scale: 6);   // 6 倍放大：角标只有 18 像素，不放大看不出数字有没有偏

            // ── 待办与提醒（2026-09-22）──
            // 起因：用户实测「创建/修改待办后确认提醒时间的弹窗，下面两个按钮被遮住一部分」。
            // 根因是 DueTimeDialog 写死了 Height=170，而 Height 是**含系统标题栏**的窗口总高 ——
            // 客户区不够，按钮就被窗口下沿裁掉一截。这类"被窗口边界裁掉一部分"的问题静态代码看不出来、
            // 也必须出图才发现，所以这张图就是它的回归守护（改造前 25 个场景里恰好没有这个弹窗）。
            Capture("26-设置提醒时间弹窗", () => new DueTimeDialog(
                DateTime.Today.AddDays(3).AddHours(9),
                $"识别到日期 {DateTime.Today.AddDays(3):yyyy-MM-dd}，几点提醒？"), outDir, log);

            // 待办汇总的编辑态与建议条：两个状态都只在用户交互之后才出现，默认快照（场景 05）覆盖不到。
            // 编辑态那张要核验三件事：编辑框的滚动条是不是深色细条（不能是老式白条）、
            // 「保存/取消」有没有被挤出可视区、编辑框与只读文本的宽度是否对齐。
            Capture("27-待办汇总（编辑态）", () =>
            {
                var w = new TodoSummaryWindow(notes, settings);
                w.RefreshAll(SampleTodos());
                return w;
            }, outDir, log, afterShow: win =>
            {
                if (win is TodoSummaryWindow w) w.SeedEditStateForSnapshot(1);
            });

            Capture("28-待办汇总（保存后建议条）", () =>
            {
                var w = new TodoSummaryWindow(notes, settings);
                w.RefreshAll(SampleTodos());
                return w;
            }, outDir, log, afterShow: win =>
            {
                if (win is TodoSummaryWindow w) w.SeedSuggestBarForSnapshot(1);
            });

            // ── AI 整理（2026-09-26）──
            // 为什么这两张必须出图：
            //   ① 行尾新增的「AI」按钮与「× 删除」**并排**，还要与「绿底白字 AI 徽章」共处一列 ——
            //      文字按钮会不会被挤出、和徽章会不会看混、长文本截断后按钮是否仍完整，静态代码看不出来；
            //   ② 三个出口（替换原文 / 另存为新笔记 / 复制）挤在底部一行，窗口拉窄时会不会挤出边界，
            //      只有出图才发现。
            // 放在最后跑：本场景要往沙箱写真实数据，排后面免得污染前面那些"空态 / 固定样本"的场景。
            Capture("29-灵感速览（行尾 AI 按钮）", () =>
            {
                // 显式按**默认宽度 620** 出图：宽面板验证不出"按钮被挤"，而 620 才是用户的实际默认
                //（前面 11 号场景把宽度改成了 1200、还把标题栏配置成了"全功能"，且都没还原 ——
                //  本图要的是"用户默认形态下行尾按钮长什么样"，不重置就把两种目的混在一张图里）
                settings.QuickViewWidth = 620;
                settings.QuickViewToolbarLeft = new List<string>();    // 空配置 → 回退默认布局（快层 [3] 已守）
                settings.QuickViewToolbarRight = new List<string>();
                notes.SaveNote(
                    "跟供应商聊了价格和交期都还行但是加急要加钱另外上周的会议纪要还没发给他明天上午十点前要处理掉",
                    "浏览器");
                var todo = notes.SaveNote("把整理好的方案发到项目群", "手动", NoteType.Todo, DateTime.Today.AddHours(21));
                // AI 释义挂到待办上：两条数据是同一分钟创建的，而 ref 只有分钟精度（挂靠逻辑无法区分归属），
                // 挂错行这张图就核验不了「绿底徽章与深底绿框按钮并存且不混淆」
                if (todo != null) notes.AppendToNote(todo, "这段讲的是供应商沟通进展与两项待办安排");
                return new QuickViewWindow(notes, settings);
            }, outDir, log);

            // 30：文本流形态 —— 新增的底部规则条（预置 4 条 chips + 自定义入口）与原有出口并排，
            // 规则多 / 窗口窄时会不会挤出边界、chips 与出口是否看混，只有出图才知道。
            // 样例文案 2026-09-27 phase2 起去 Markdown 化（** 与 - 已按用户拍板彻底放弃，改「· 」与正常序号）。
            Capture("30-AI 整理预览窗（规则条）", () => new AiTidyPreviewWindow(new AiTidyPreviewWindow.TidyPreviewRequest
            {
                Original = "跟供应商聊了价格和交期都还行但是加急要加钱另外上周的会议纪要还没发给他明天上午十点前要处理掉",
                InitialTidied = "1. 供应商沟通：\n   · 价格与交期都还行；加急要加钱。\n2. 待办：\n   1. 上周的会议纪要发给他；\n   2. 明天上午 10:00 前处理掉。",
                InitialRule = TidyRuleCatalog.ResolveDefaultRule(settings),
                Rules = TidyRuleCatalog.ResolveVisible(settings),
                ShowCustomEntry = settings.TidyShowCustomEntry,
                ModelLabel = "Agnes 3.0 Flash",
                Provider = null,   // 快照不联网：换规则重跑在快照环境不可用（点 chip 只会得到人话提示）
                Settings = settings,
            }), outDir, log);

            // 30b：提取待办形态 —— 结果区切待办行列表（文字 + 行内日期 / 时间框[小时/分钟分框] + 删行），
            // 「创建待办」出口只在规则条选中「提取待办」时出现；日期框已收窄压浅灰（phase2），深色底下什么样子只有出图才知道。
            Capture("30b-AI 整理预览窗（提取待办）", () => new AiTidyPreviewWindow(new AiTidyPreviewWindow.TidyPreviewRequest
            {
                Original = "跟供应商聊了价格和交期都还行但是加急要加钱另外上周的会议纪要还没发给他明天上午十点前要处理掉",
                InitialTidied = "【2026-09-27 10:00】把整理好的方案发到项目群\n给供应商回电话问加急费",
                InitialRule = TidyRuleCatalog.ResolveDefaultRule(settings),
                Rules = TidyRuleCatalog.ResolveVisible(settings),
                ShowCustomEntry = settings.TidyShowCustomEntry,
                ModelLabel = "Agnes 3.0 Flash",
                Provider = null,
                Settings = settings,
            }), outDir, log, afterShow: win =>
            {
                if (win is AiTidyPreviewWindow p) p.SeedTodoModeForSnapshot();
            });

            // 31：激活码板块（2026-10-08）。沙箱里没有 license.json → 走「首次运行」路径，
            // 因此出的是「免费体验中 · 已用 1 天」这一态。状态区颜色（绿=已激活 / 橙=已过期）
            // 只有出图才能确认深色底下对比度够不够 —— 这正是这个场景存在的理由。
            Capture("31-设置-激活码板块（试用中）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 12;   // 12 = 「激活码」（追加到末尾，勿插中间）
                return w;
            }, outDir, log);

            // 32：激活码弹窗（2026-10-08）—— 被拦时「就地激活」的窗口。
            // 沙箱无 license.json → 出「免费体验中」态。这张图守两件事：
            // ① 标题里的功能名有没有被换行截断（「AI 问答」比「焦点记录」长）；
            // ② 底部「激活 / 以后再说」有没有被窗口下沿裁掉 —— 固定 Height 的窗口踩过这个坑（见 DueTimeDialog 注释）。
            Capture("32-激活码弹窗（被拦·试用中）", () =>
                new LicenseWindow("AI 问答"), outDir, log);

            // 33：导出对话框（2026-10-08 新增场景）—— 此前 48 个场景里恰好没有它。
            // 为什么必须出图：这次在 Word 选项下加了一行「锁定提示」（未解锁时才显示），
            // 而本窗口是**固定 Height=340**（不像 DueTimeDialog 用 SizeToContent）——
            // 多一行就可能把底部「导入/取消/导出」挤出可视区。这张图就是守这个的。
            //
            // 沙箱全局开关默认 false（= 全放行）出的是解锁态，看不到提示行；
            // 所以这里**临时打开开关**造「未解锁」态 —— 快照本就在隔离沙箱里，开关不落盘。
            Capture("33-导出对话框（Word 未解锁）", () =>
            {
                // 造「未解锁」态：沙箱天然是「试用中」（license.json 刚建 → UsedDays=1 → 放行），
                // 所以只打开全局开关还不够，得把沙箱状态写成**已过期**。
                // 手法 = 直接写沙箱里的 license.json（不走产品代码路径，零侵入），
                // 再让 LicenseGate 重读；RootOverride 已生效，真实数据根一个字节都不会碰。
                LicenseGate.IsLicensingEnabled = true;
                File.WriteAllText(
                    Path.Combine(sandbox, "license.json"),
                    "{\"FirstRunDate\":\"2026-01-01\",\"UsedDays\":99,\"LastSeenDate\":\"2026-10-08\",\"Code\":\"\",\"ActivatedAt\":\"\"}",
                    new UTF8Encoding(false));
                LicenseGate.ReinitializeForSnapshot();

                return new ExportDialog(settings, notes);
            }, outDir, log, afterShow: _ =>
            {
                LicenseGate.ResetForSnapshot();   // 复位：别把「已过期 + 开关开」泄漏给后续场景
            });

            // ── 收尾（2026-09-26 按需出图）──
            // ⚠ 必须写在 finally 落 snapshot.log 之前，否则这两条结论进不了日志、排障时看不见。
            log.AppendLine();
            log.AppendLine($"场景入选 {_matched} / 共 {_total}" +
                           (_filters.Length > 0 ? $"（--only {string.Join(",", _filters)}）" : "（未过滤）"));

            if (_listOnly)
            {
                // 清单只能落文件：WPF 是 GUI 子系统程序，stdout 拿不到（`& exe` 连退出码都不给）。
                File.WriteAllLines(Path.Combine(outDir, "scenes.txt"), _selected, Encoding.UTF8);
                log.AppendLine($"清单模式：{_selected.Count} 个场景入选、一个窗口都没构造 -> scenes.txt");
            }
            else if (_filters.Length > 0 && _matched == 0)
            {
                // 按需出图的致命失败 = 漏图而不自知。这里必须失败退出，不能安静地出 0 张。
                exitCode = 2;
                log.AppendLine($"!! --only「{string.Join(",", _filters)}」未匹配到任何场景 —— " +
                               "先跑 `dev.ps1 snap -List` 拿清单，关键字建议用编号（如 03b / 10）");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("!! 快照流程异常: " + ex);
        }
        finally
        {
            FocusCapturePaths.RootOverride = null;
            try { Directory.Delete(sandbox, true); } catch { /* 沙箱删不掉不影响结果 */ }
            try { File.WriteAllText(Path.Combine(outDir, "snapshot.log"), log.ToString(), Encoding.UTF8); }
            catch { /* 日志写不出也别卡住 */ }
        }

        // 快照模式下不创建主窗口，显式退出。
        // 退出码 2 =「--only 一个都没匹配上」，其余一律 0 —— dev.ps1 靠它区分
        //「关键字写错了」和「渲染全失败」，这两种失败一个该改参数、一个该改代码。
        Application.Current?.Shutdown(exitCode);
    }

    /// <summary>
    /// 快照用的样例待办（**纯内存构造，一个字节都不落盘**）：三条刚好覆盖汇总面板的三个分组
    /// —— 已提醒暂缓(Read) / 待处理(Open 无提醒或今天还没到点) / 已过期(Open 且已过点)。
    /// 第二条故意写成超长文本，用来核验编辑框里长文本的换行与内部滚动条。
    /// </summary>
    private static List<NoteEntry> SampleTodos() => new()
    {
        new NoteEntry
        {
            Timestamp = DateTime.Today.AddHours(8).AddMinutes(30),
            Type = NoteType.Todo, TodoStatus = TodoStatus.Read,
            DueTime = DateTime.Today.AddHours(9),
            Content = "把季度复盘大纲写完"
        },
        new NoteEntry
        {
            Timestamp = DateTime.Today.AddHours(10),
            Type = NoteType.Todo, TodoStatus = TodoStatus.Open,
            Content = "待办汇总面板：双击进编辑、右键出菜单、保存后弹建议条 —— 这一条特意写长，"
                    + "用来核验编辑框里的换行与内部滚动条是不是深色细条。"
                    + "继续加长到超过编辑框的固定高度（80px ≈ 四行），"
                    + "这样滚动条才会真的出现 —— 内容刚好装下时它不显示，那张图就验不到东西。"
        },
        new NoteEntry
        {
            Timestamp = DateTime.Today.AddHours(11),
            Type = NoteType.Todo, TodoStatus = TodoStatus.Open,
            DueTime = DateTime.Today.AddHours(14),
            Content = "缴电费"
        }
    };

    /// <summary>渲染单个窗口为 PNG。任一环节失败只记日志，不影响其余窗口。</summary>
    /// <param name="afterShow">
    /// 窗口已 Show 且跑完一次布局后的钩子。两个用途：① 补那些**必须窗口已加载才生效**的状态
    /// （如悬浮球 <c>SetBadge</c> 会检查 <c>IsLoaded</c>，在 Show 之前调用会被它自己忽略，角标根本不出现）；
    /// ② 把实测尺寸打进日志（角标边界这类"看图会误判"的量必须落成数字）。
    /// </param>
    /// <param name="scale">
    /// 出图放大倍数（默认 1）。给小控件（几十像素的悬浮球角标）出放大图用 ——
    /// WPF 按 DPI 缩放渲染，矢量与文字都是高清重绘，不是把位图拉大，所以能看到亚像素级的居中偏差。
    /// </param>
    private static void Capture(string name, Func<Window> factory, string outDir, StringBuilder log,
        Action<Window>? afterShow = null, int scale = 1)
    {
        // ── 按需出图的守门员（2026-09-26）──
        // 不匹配的场景**连窗口都不构造**，两个理由：
        //   ① 快 —— --list 下整轮毫秒级（不构造窗口就没有布局与渲染开销）；
        //   ② 少串扰 —— settings 是同一份实例，场景 11/13/29 都会改它（宽度、标题栏、欢迎语），
        //      少跑一个场景就少一份"意外改到共享状态"的机会。
        _total++;
        if (!IsSelected(name))
        {
            log.AppendLine($"{name}: 跳过（不匹配 --only）");
            return;
        }
        _matched++;
        if (_listOnly)
        {
            _selected.Add(name);
            return;
        }

        Window? win = null;
        try
        {
            win = factory();
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -32000;          // 屏幕外，用户看不见
            win.Top = -32000;
            win.ShowInTaskbar = false;
            win.Opacity = 1.0;          // 见类注释：半透明位图不利于像素比对
            win.Show();
            win.UpdateLayout();
            afterShow?.Invoke(win);
            win.UpdateLayout();   // afterShow 若改了状态（如角标可见性），这里再排一次，确保渲染的是新布局

            // 布局与渲染管线是异步的，必须各跑完一轮，否则可能截到空白或旧状态
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(win);
            var w = (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX * scale);
            var h = (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY * scale);
            if (w <= 0 || h <= 0)
            {
                log.AppendLine($"{name}: 尺寸无效 {w}x{h}（窗口未完成布局）");
                return;
            }

            // DPI 同步乘以 scale：像素数与 DPI 一起放大，WPF 才会按放大后的比例**重绘**（文字/矢量依旧清晰）
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX * scale, dpi.PixelsPerInchY * scale, PixelFormats.Pbgra32);
            rtb.Render(win);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var path = Path.Combine(outDir, name + ".png");
            using (var fs = File.Create(path)) enc.Save(fs);

            log.AppendLine($"{name}: {w}x{h} (逻辑 {win.ActualWidth:0}x{win.ActualHeight:0}, DPI {dpi.PixelsPerInchX:0}) -> {path}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"{name}: 渲染失败 — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { win?.Close(); } catch { /* 关不掉不影响后续 */ }
        }
    }

    /// <summary>
    /// 把悬浮球角标的**实测边界**打进日志（2026-09-19）。
    ///
    /// 为什么非得打数字、不能只看图：<see cref="Capture"/> 是用 RenderTargetBitmap 渲染**视觉树**的，
    /// 而"溢出窗口矩形"这道裁切发生在更外层的窗口边界上 —— 视觉树里根本不存在这道裁切，
    /// 所以角标即使被切掉一半，快照里也照样完整画出来。**看图会得出"没问题"的错误结论。**
    /// 判据（数字形态）：Left/Top 不得为负、Right/Bottom 不得超出窗口宽高。
    /// </summary>
    private static void LogBadgeBounds(FloatBall ball, StringBuilder log)
    {
        try
        {
            var badge = ball.Badge;
            if (badge.Visibility != Visibility.Visible)
            {
                log.AppendLine("  角标：不可见（SetBadge 未生效，或窗口尚未加载）");
                return;
            }

            ball.UpdateLayout();   // 角标刚被 SetBadge 置为可见，这里必须先重排一次，否则读到的尺寸全是 0

            var origin = badge.TransformToAncestor(ball).Transform(new Point(0, 0));
            var right = origin.X + badge.ActualWidth;
            var bottom = origin.Y + badge.ActualHeight;

            log.AppendLine($"  角标边界：Left={origin.X:0.##} Top={origin.Y:0.##} Right={right:0.##} Bottom={bottom:0.##}");
            log.AppendLine($"  窗口尺寸：{ball.ActualWidth:0.##} × {ball.ActualHeight:0.##}");
            log.AppendLine(
                origin.X >= 0 && origin.Y >= 0 && right <= ball.ActualWidth && bottom <= ball.ActualHeight
                    ? "  角标位置：完整落在窗口内（不会被窗口边界裁切）"
                    : "  角标位置：⚠ 超出窗口边界 → 会被裁切");

            var text = ball.BadgeText;
            var tp = text.TransformToAncestor(badge).Transform(new Point(0, 0));
            log.AppendLine($"  数字「{text.Text}」行框：Left={tp.X:0.##} Top={tp.Y:0.##} " +
                           $"Right={tp.X + text.ActualWidth:0.##} Bottom={tp.Y + text.ActualHeight:0.##}（字号 {text.FontSize}）");
            log.AppendLine($"  角标 {badge.ActualWidth:0.##} × {badge.ActualHeight:0.##}｜角标中心 " +
                           $"({badge.ActualWidth / 2:0.##}, {badge.ActualHeight / 2:0.##})｜行框中心 " +
                           $"({tp.X + text.ActualWidth / 2:0.##}, {tp.Y + text.ActualHeight / 2:0.##})" +
                           "（行框中心是布局居中判据；字形视觉重心还要看出图）");

            // 角标与球的重叠深度（2026-09-19 第二轮加）：这是「压球压了多少」唯一的硬证据 ——
            // 看图只能得出"压得多不多"的模糊印象，说不清 9px 还是 4px；慢层检查点也断这条。
            // 球心取窗口中心、半径取 FloatBall.xaml 里球的声明尺寸一半（球在 Grid 里居中、恒为 40×40）。
            const double ballRadius = 20;
            var bx = origin.X + badge.ActualWidth / 2;
            var by = origin.Y + badge.ActualHeight / 2;
            var dist = Math.Sqrt((bx - ball.ActualWidth / 2) * (bx - ball.ActualWidth / 2)
                               + (by - ball.ActualHeight / 2) * (by - ball.ActualHeight / 2));
            var badgeRadius = badge.ActualWidth / 2;
            var overlap = ballRadius - (dist - badgeRadius);
            log.AppendLine($"  角标圆心距球心 {dist:0.##}px（球半径 {ballRadius}，角标半径 {badgeRadius:0.##}）" +
                           $" → 与球重叠 {(overlap > 0 ? overlap : 0):0.##}px" +
                           (dist > ballRadius ? "｜圆心在球外 ✓" : "｜⚠ 圆心在球内"));
            log.AppendLine("  悬停放大 1.1 倍时球半径 22：角标圆心" +
                           (dist > 22 ? "仍在球外 ✓（放大瞬间不会陷进球体）" : "⚠ 会陷进球体"));
        }
        catch (Exception ex)
        {
            log.AppendLine("  角标边界读取失败：" + ex.Message);
        }
    }

    /// <summary>解析 <c>--only</c>：逗号分隔可给多个关键字，子串匹配（不区分大小写）。</summary>
    private static string[] ResolveFilters(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(OnlyFlag, StringComparison.OrdinalIgnoreCase)) continue;
            // 只给开关不给值 = 忽略（退回全量）。刻意不报错：参数的笔误不该变成「一张图都没有」，
            // 失败方向要偏向「多出图」而不是「漏图」—— 漏图是无声的，多出图只是慢。
            if (i + 1 >= args.Length) break;
            foreach (var piece in args[i + 1].Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(piece);
            }
        }
        return list.ToArray();
    }

    /// <summary>场景名是否入选：没给过滤 = 全选；给了 = 名字里含任一关键字。</summary>
    private static bool IsSelected(string name) =>
        _filters.Length == 0 || _filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>解析输出目录：<c>--out &lt;dir&gt;</c> 优先，否则 %TEMP%\fc-ui-snapshot。</summary>
    private static string ResolveOutDir(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals(OutFlag, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
        {
            try { return Path.GetFullPath(args[i + 1]); } catch { /* 非法路径回退默认 */ }
        }
        return Path.Combine(Path.GetTempPath(), "fc-ui-snapshot");
    }
}
