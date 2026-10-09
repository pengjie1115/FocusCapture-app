using System.Threading;
using FocusCapture.Services.AI;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 文件交付通道：工具把「要展示给用户的云文件」推到这里，UI 订阅后渲染成对话里的卡片。
///
/// 为什么不直接在工具里开窗/弹资源管理器：工具跑在后台线程，而且工具层不该知道 UI 长什么样。
/// 拆成一个事件，UI 侧决定展示形态，将来换界面不用动工具。
/// </summary>
public static class FileDeliveryHub
{
    /// <summary>工具请求交付某文件（UI 订阅）。</summary>
    public static event Action<FileMetadata>? Delivered;

    /// <summary>请求打开该文件（UI 订阅；工具带 action=open 时触发）。</summary>
    public static event Action<FileMetadata>? OpenRequested;

    /// <summary>请求在资源管理器中定位（UI 订阅）。</summary>
    public static event Action<FileMetadata>? LocateRequested;

    internal static void Deliver(FileMetadata meta) => Delivered?.Invoke(meta);

    internal static void RequestOpen(FileMetadata meta) => OpenRequested?.Invoke(meta);

    internal static void RequestLocate(FileMetadata meta) => LocateRequested?.Invoke(meta);
}

/// <summary>
/// 文件类工具共用的解析与格式化。
///
/// 这里集中体现本次更新最重要的一条红线：
/// <b>工具的路径参数绝不能让 AI 生成</b>。所有涉及「本机哪个文件」的入口，
/// 要么是用户亲手签发的牌号（FileHandleStore），要么是本地元数据里的编号（file_id）——
/// 两者都不是模型能凭空编出来的。
/// </summary>
internal static class FileToolSupport
{
    /// <summary>编号至少给到 8 位，避免模型复制 32 位长串时敲错。</summary>
    public const int RefLength = 12;

    /// <summary>把内部 id 压成便于模型引用的短编号。</summary>
    public static string ShortRef(string id) => id.Length <= RefLength ? id : id[..RefLength];

    /// <summary>按短编号（前缀）解析出唯一文件；有歧义时明确报错而不是猜一个。</summary>
    public static (FileMetadata? Meta, string? Error) ResolveFileRef(string? refId)
    {
        var id = (refId ?? "").Trim();
        if (id.Length == 0)
            return (null, "缺少参数 file_id。请先用 find_cloud_files 查询，拿到编号后再引用。");

        var all = FileRepository.AllMetadata();
        var exact = all.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return (exact, null);

        var matches = all
            .Where(m => m.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"找不到编号为「{id}」的文件。请先用 find_cloud_files 查询可用文件。"),
            _ => (null, $"编号「{id}」匹配到 {matches.Count} 个文件，请输入更长的编号以消歧。"),
        };
    }

    /// <summary>格式化一行文件描述（编号 + 类型 + 名称 + 时间 + 大小 + 本机副本 + **真实上传状态**）。</summary>
    public static string Describe(FileMetadata m)
    {
        var entry = FileRepository.FindCache(m.Id);
        var local = entry != null && File.Exists(entry.LocalPath);
        var parts = new List<string>
        {
            $"[{ShortRef(m.Id)}]",
            $"{FileTypes.Label(m.Type)}",
            m.Name,
            m.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            RootMigrationService.FormatSize(m.Size),
        };
        if (m.Tags.Count > 0) parts.Add("标签:" + string.Join("/", m.Tags));
        parts.Add(local ? "本机已有" : "需取回");
        // ⚠️ 上传状态**必须**出现在输出里。少了它，模型会把「本机已有」读成「已经传上去了」，
        // 然后对用户说"两个文件都已经上传成功，网盘上都能看到了" —— 而那批文件在账本里全是 failed
        // （2026-09-16 实测踩到：用户去网盘一看，网盘是空的，信任当场归零）。
        // 措辞由 UploadStates.Label 统一给，避免各处自己发挥。
        //
        // ⚠️ 顺序要紧：**先看云端状态，再看本机账本状态**（2026-09-27 补充）。
        // 账本只说"我们这边传成功过"，与"云端此刻还在不在"是两码事 —— 用户在网盘里手动删掉之后，
        // 账本仍然是 uploaded，这里就会输出「云端已就绪」，而 Agent 读到的正是它，
        // 转头就对用户说"文件还在网盘上、可以取回"（用户取回时才发现是空的）。
        // 所以「与云端核对」标出的云端状态必须排在账本状态之前。
        var cloudLabel = CloudStates.Label(m.CloudState);
        parts.Add(cloudLabel.Length > 0 ? cloudLabel : UploadStates.Label(entry?.UploadState));
        return string.Join("  ", parts);
    }
}

/// <summary>
/// 把文件存到网盘（链路 A / B / C，写操作需确认）。
///
/// 三种来源，都不接受「路径」参数：
/// - <c>content</c>：AI 当场生成的内容（链路 A，最轻，不需要任何文件路径）
/// - <c>handle</c>：用户在对话框点「选择文件」后签发的牌号（链路 C）
/// - <c>attachment_name</c>：当前对话里已经附上的附件（链路 B）
/// </summary>
public class StoreFileToCloudTool : AgentTool
{
    private readonly Func<IReadOnlyList<ChatAttachment>> _currentAttachments;

    public StoreFileToCloudTool(Func<IReadOnlyList<ChatAttachment>> currentAttachments)
        => _currentAttachments = currentAttachments;

    public override string Name => "store_file_to_cloud";

    public override string Description =>
        "把文件保存到用户的网盘（云端永久保留，之后可在任意设备取回）。三种用法，按用户意图选一种：\n" +
        "1) 用户说「把这份报告/这段话存到网盘」→ 用 content 参数把内容直接传进来，并用 file_name 起个带扩展名的名字（如「周报.md」）。\n" +
        "2) 用户说「把那张图/那个文件存上去」，且对话框里已经显示了「已选择文件」卡片 → 用 handle 参数原样引用卡片上的牌号（形如 file:20260916-1）。\n" +
        "3) 用户说「把刚才那张图存到网盘」→ 用 attachment_name 参数传当前对话附件里的文件名。\n" +
        "⚠️ 本工具没有也没有任何路径参数：你无法指定本机文件路径，也无法编造牌号。若用户想要的文件还没被选过，" +
        "请明确请他点对话框里的「选择文件」按钮，不要猜测或杜撰路径。\n" +
        "⚠️ 汇报纪律：上传是**后台异步**的，本工具返回时文件通常还没到网盘。除非返回值明确说「网盘上已有」，" +
        "否则**绝不许**对用户说「已上传成功」「已保存到网盘」—— 说错了用户会去网盘核对，然后发现是空的。" +
        "按返回值原意说「已存入本机、正在后台上传，网盘上要等传完才出现」即可。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"content":{"type":"string","description":"要保存的文本内容（用法 1，AI 生成的内容）"},"file_name":{"type":"string","description":"文件名，需带扩展名，如 周报.md。与 content 搭配使用"},"handle":{"type":"string","description":"用户在对话框里选择的文件牌号，形如 file:20260916-1。必须原样引用界面上显示的值，不可自行构造"},"attachment_name":{"type":"string","description":"当前对话中已附件的文件名（用法 3）"},"tags":{"type":"string","description":"可选，逗号分隔的标签，如：周报,2026Q3"},"type":{"type":"string","description":"可选，artifact=AI产出 / upload=用户上传 / attachment=对话附件，默认自动判断"}},"required":[]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "file_name", out var name);
        if (ToolArgs.TryGetString(argumentsJson, "handle", out var handle)) return $"把您选择的文件（{handle}）保存到网盘";
        if (ToolArgs.TryGetString(argumentsJson, "attachment_name", out var att)) return $"把对话附件「{att}」保存到网盘";
        if (name.Length > 0) return $"把内容保存到网盘，文件名「{name}」";
        return "把文件保存到网盘";
    }

    /// <summary>放后台线程执行：登记文件要算 SHA256/MD5 并复制文件，大文件上不该占着工具循环的线程。</summary>
    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => Task.Run(() => ExecuteCore(argumentsJson), ct);

    private string ExecuteCore(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "content", out var content);
        ToolArgs.TryGetString(argumentsJson, "file_name", out var fileName);
        ToolArgs.TryGetString(argumentsJson, "handle", out var handle);
        ToolArgs.TryGetString(argumentsJson, "attachment_name", out var attachmentName);
        ToolArgs.TryGetString(argumentsJson, "tags", out var tagsRaw);
        ToolArgs.TryGetString(argumentsJson, "type", out var typeRaw);

        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? null
            : tagsRaw.Split(new[] { ',', '，', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        var explicitType = FileTypes.IsValid(typeRaw) ? typeRaw : null;

        // ① 牌号（链路 C）
        if (handle.Length > 0)
        {
            if (!FileHandleStore.TryResolve(handle, out var path, out var error))
                return "错误：" + error;
            var sourceName = Path.GetFileName(path);
            var (meta, err) = FileRepository.RegisterLocalFile(
                path, explicitType ?? FileTypes.Upload, sourceName, attachExisting: false, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return DescribeSaved(meta, sourceName);
        }

        // ② 当前对话附件（链路 B）
        if (attachmentName.Length > 0)
        {
            var attachments = _currentAttachments();
            var hit = attachments.FirstOrDefault(a =>
                string.Equals(a.FileName, attachmentName, StringComparison.OrdinalIgnoreCase))
                ?? attachments.FirstOrDefault(a =>
                    a.FileName.Contains(attachmentName, StringComparison.OrdinalIgnoreCase));

            if (hit == null)
            {
                var available = attachments.Count == 0
                    ? "当前对话里没有任何附件。"
                    : "当前对话里的附件有：" + string.Join("、", attachments.Select(a => a.FileName));
                return $"错误：没找到名为「{attachmentName}」的对话附件。{available}";
            }

            var path = ChatAttachmentService.ResolvePath(hit);
            if (!File.Exists(path))
                return $"错误：附件「{hit.FileName}」的本体不在本机（可能是从其他设备同步过来的会话）。";

            var (meta, err) = FileRepository.RegisterLocalFile(
                path, explicitType ?? FileTypes.Attachment, hit.FileName, attachExisting: true, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return DescribeSaved(meta, hit.FileName);
        }

        // ③ AI 生成的内容（链路 A）
        if (!string.IsNullOrWhiteSpace(content))
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "错误：用 content 保存内容时必须同时给出 file_name（带扩展名，如「周报.md」）。";

            var (meta, err) = FileRepository.RegisterText(content, fileName.Trim(),
                explicitType ?? FileTypes.Artifact, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return DescribeSaved(meta, fileName.Trim());
        }

        return "错误：没有可保存的来源。请在 content（你生成的内容 + file_name）、handle（用户已选择的文件牌号）、" +
               "attachment_name（当前对话附件名）三者中提供至少一个。" +
               "如果用户想上传本机某个文件但还没选过，请请他点对话框里的「选择文件」按钮。";
    }

    private static void NotifyUpload() => UploadQueue.Kick();

    /// <summary>
    /// 如实描述「文件现在到底走到了哪一步」。
    ///
    /// ⚠️ 这里曾经一律说「已保存到网盘」，而上传其实还没开始、且随时可能失败 ——
    /// 用户去网盘一看是空的，整条链路当场失去信任（2026-09-16 实机复现）。
    /// 规矩：登记成功 ≠ 上传成功。宁可把话说慢，不能说错。
    /// </summary>
    private static string DescribeSaved(FileMetadata meta, string? sourceName)
    {
        var size = RootMigrationService.FormatSize(meta.Size);
        var id = FileToolSupport.ShortRef(meta.Id);
        var onCloud = FileRepository.FindCache(meta.Id)?.UploadState == UploadStates.Uploaded;

        // 名字被换掉 = 命中了内容去重，返回的是已有那条记录
        var merged = sourceName != null && !string.Equals(meta.Name, sourceName, StringComparison.Ordinal);
        if (merged)
        {
            return onCloud
                ? $"「{sourceName}」的内容和网盘上已有的「{meta.Name}」（编号 {id}）完全相同，已合并为同一个文件，不重复上传。"
                : $"「{sourceName}」的内容和本机已登记的「{meta.Name}」（编号 {id}）完全相同，已合并，不重复占用空间。" +
                  PendingNote();
        }

        if (onCloud)
            return $"「{meta.Name}」（{size}，编号 {id}）网盘上已有相同内容，不需要重复上传，随时可取回。";

        if (!FileRepository.CloudReady)
            return $"已存入本机文件区：「{meta.Name}」（{size}，编号 {id}）。" +
                   "尚未完成百度网盘授权，文件暂存在本机，授权后会自动补传。";

        return $"已存入本机文件区：「{meta.Name}」（{size}，编号 {id}）。正在后台上传 —— " +
               "**网盘上要等传完才会出现，现在去看还是空的**。传完后网盘上就能看到；" +
               "如果一直没出现，打开「设置 → 文件与网盘」能看到失败原因。";
    }

    private static string PendingNote() => FileRepository.CloudReady
        ? "正在后台上传，网盘上要等传完才会出现。"
        : "尚未完成百度网盘授权，授权后会自动补传。";
}

/// <summary>
/// 在本地元数据里检索文件（链路 D 的入口，只读）。
/// **纯离线**：不联网、不消耗网盘额度 —— 这正是「本地当工作区」的价值所在。
/// </summary>
public class FindCloudFilesTool : AgentTool
{
    public override string Name => "find_cloud_files";

    public override string Description =>
        "在用户的网盘文件记录里检索（本地离线查询，不联网、不消耗额度）。" +
        "用户说「把我上周存的那份文档找出来」「网盘里有没有关于 XX 的文件」时使用。" +
        "返回结果每行开头的中括号里是编号，后续用 fetch_cloud_file / read_cloud_file 时把它作为 file_id 传入。\n" +
        "⚠️ 这里查的是**本机记录**，不等于文件已经到了网盘 —— 每行末尾标着真实上传状态：" +
        "只有「云端已就绪」才算真的在网盘上；「等待上传 / 正在上传 / 上传失败」都表示**云端还没有这个文件**。" +
        "这种时候**绝不许**对用户说「已上传成功」「网盘上能看到」，只能说「还没传上去」并提示去「设置 → 文件与网盘」看原因。\n" +
        "⚠️ 另有两类状态：「云端已不存在」「云端已到期清理」—— 它们的含义是**网盘里已经没有这份文件了**，" +
        "本地记录只是留着让用户看得到「它去哪了」。遇到这两类，**绝不许**说「文件还在网盘上」或「可以取回」；" +
        "照实说「网盘里已经没有它了，本地还留着这条记录」，并请用户到「设置 → 文件与网盘 → 与云端核对」重新核对。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"keyword":{"type":"string","description":"文件名或标签关键词，模糊匹配"},"type":{"type":"string","description":"可选：artifact=AI产出 / upload=用户上传 / attachment=对话附件；也可填 图片 / 文档"},"tag":{"type":"string","description":"可选，按标签过滤"},"recent_days":{"type":"number","description":"可选，最近 N 天内新增（用户说「上周」填 7，「最近三天」填 3）"},"from_date":{"type":"string","description":"可选，起始日期 yyyy-MM-dd"},"to_date":{"type":"string","description":"可选，结束日期 yyyy-MM-dd"},"limit":{"type":"number","description":"可选，最多返回几条，默认 20"}},"required":[]}""";

    public override bool IsReadOnly => true;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ToolArgs.TryGetString(argumentsJson, "keyword", out var keyword);
        ToolArgs.TryGetString(argumentsJson, "type", out var type);
        ToolArgs.TryGetString(argumentsJson, "tag", out var tag);

        var query = new FileQuery
        {
            Keyword = keyword.Length > 0 ? keyword : null,
            Type = type.Length > 0 ? type : null,
            Tag = tag.Length > 0 ? tag : null,
            Limit = ToolArgs.TryGetInt(argumentsJson, "limit", out var lim) && lim > 0 ? Math.Min(lim, 100) : 20,
        };
        if (ToolArgs.TryGetInt(argumentsJson, "recent_days", out var days) && days > 0) query.RecentDays = days;
        if (ToolArgs.TryGetString(argumentsJson, "from_date", out var from) && DateTime.TryParse(from, out var fd)) query.From = fd;
        if (ToolArgs.TryGetString(argumentsJson, "to_date", out var to) && DateTime.TryParse(to, out var td)) query.To = td;

        var hits = FileRepository.Search(query);
        if (hits.Count == 0)
        {
            // 元数据清单还在从云端同步时**绝不能下「不存在」的结论** —— 那是把「还没同步」误报成「被删了」，
            // 用户会以为文件丢了。宁可让模型多说一句「稍后再试」。
            if (!FileStoreSync.InitialPullCompleted)
                return Task.FromResult("文件清单正在从云端同步中，暂时还不能下结论。请让用户稍等片刻再问一次。");

            var scope = keyword.Length > 0 ? $"包含「{keyword}」的" : "";
            return Task.FromResult($"没有找到{scope}文件记录。用户可以点对话框里的「选择文件」把本机文件存到网盘。");
        }

        var lines = hits.Select(FileToolSupport.Describe);
        return Task.FromResult($"共 {hits.Count} 个文件：\n" + string.Join("\n", lines)
            + "\n\n（要取回某个文件给用户，用 fetch_cloud_file 传它的编号；要读内容自己用，用 read_cloud_file。）"
            + CloudLocationNote());
    }

    /// <summary>
    /// 给模型一份「文件到底存在哪儿」的准确说法。
    /// 用户问过「上传到网盘哪个目录了」，而检索结果里本来不含路径 —— 不给这句，
    /// 模型就只能自己编一个目录名出来（2026-09-16 实测：它答了，答得毫无依据）。
    /// </summary>
    private static string CloudLocationNote() => FileRepository.CloudReady
        ? $"\n（若用户问「存到哪个目录了」：都在他网盘的「我的应用数据 / {AppName()}」里 —— " +
          "普通文件在 files 子目录、对话附件在 attachments 子目录。照这个答，不要编别的路径。）"
        : "";

    private static string AppName()
    {
        var root = FileRepository.NetFilesDir;                       // 形如 /apps/FocusCapture/files
        var parts = root.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : "应用目录";
    }
}

/// <summary>
/// 从网盘取回文件并交付给用户（链路 D 的出口，写操作需确认 —— 会下载数据、占用本地空间）。
/// </summary>
public class FetchCloudFileTool : AgentTool
{
    public override string Name => "fetch_cloud_file";

    public override string Description =>
        "把网盘里的某个文件取回本机，并在对话里给用户一张可操作的文件卡片（可打开 / 在文件夹中定位 / 另存为）。" +
        "用户说「把那份文档给我」「取回来看看」时使用。file_id 用 find_cloud_files 返回的编号。" +
        "首次取回需要从网盘下载，速度受用户网盘账号等级影响，可能需要等待。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"file_id":{"type":"string","description":"find_cloud_files 返回的编号（中括号里的字符串）"},"action":{"type":"string","description":"可选：open=取回后直接打开，locate=取回后在资源管理器中定位；不传则只给卡片"}},"required":["file_id"]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "file_id", out var id);
        var (meta, _) = FileToolSupport.ResolveFileRef(id);
        return meta == null
            ? $"从网盘取回文件（编号 {id}）"
            : $"从网盘取回「{meta.Name}」（{RootMigrationService.FormatSize(meta.Size)}）";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
            return "错误：缺少参数 file_id（用 find_cloud_files 返回的编号）。";

        var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
        if (meta == null) return "错误：" + error;

        var (path, dlError) = await FileRepository.EnsureLocalAsync(meta.Id, null, ct).ConfigureAwait(false);
        if (path == null) return "错误：" + dlError;

        FileDeliveryHub.Deliver(meta);

        ToolArgs.TryGetString(argumentsJson, "action", out var action);
        if (string.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
            FileDeliveryHub.RequestOpen(meta);
        else if (string.Equals(action, "locate", StringComparison.OrdinalIgnoreCase))
            FileDeliveryHub.RequestLocate(meta);

        return $"已取回「{meta.Name}」（{RootMigrationService.FormatSize(meta.Size)}），已在本机：" +
               $"{path}。对话里已给出文件卡片，用户可以打开、在文件夹中定位或另存为。";
    }
}

/// <summary>
/// 读取网盘文件的内容供自己使用（链路 E，只读）。本期只支持文本类（Markdown / 文本 / 代码）。
/// </summary>
public class ReadCloudFileTool : AgentTool
{
    public override string Name => "read_cloud_file";

    public override string Description =>
        "读取网盘里某个文件的内容，用于总结、翻译、分析。支持：文本类（md / txt / 代码 / csv / json 等）、" +
        "Word（.docx）、Excel（.xlsx）、**以及有文字层的 PDF**（2026-09-17 起扩展）。\n" +
        "⚠️ 读不了的两类：图片（本工具只产出文字）、扫描件/图片版 PDF（里面没有文字层，属 OCR 范畴，工具会明确告知）。" +
        "遇到读不了的文件，请用 fetch_cloud_file 取回给用户自己打开，**不要假装读到了内容**。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"file_id":{"type":"string","description":"find_cloud_files 返回的编号"},"max_chars":{"type":"number","description":"可选，最多读取多少字，默认 20000"}},"required":["file_id"]}""";

    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
            return "错误：缺少参数 file_id。";

        var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
        if (meta == null) return "错误：" + error;

        var maxChars = ToolArgs.TryGetInt(argumentsJson, "max_chars", out var mc) && mc > 0
            ? Math.Min(mc, 60000)
            : 20000;

        var (text, readError) = await FileRepository.ReadTextAsync(meta.Id, maxChars, ct).ConfigureAwait(false);
        if (text == null) return "错误：" + readError;

        return $"文件「{meta.Name}」的内容如下：\n\n{text}";
    }
}

/// <summary>
/// 改网盘文件在本机的记录（显示名 / 标签），写操作需确认。2026-09-17 新增。
///
/// ⚠️ 能力边界必须写进描述：它**不会重命名云端那份文件**。
/// 说成"已经帮你把网盘里的文件改名了"就是谎报 —— 用户去网盘一看名字没变，信任当场归零
/// （这与"登记成功 ≠ 上传成功"是同一类坑，见 FileTools.DescribeSaved 的注释）。
/// </summary>
public class UpdateFileMetaTool : AgentTool
{
    public override string Name => "update_file_meta";

    public override string Description =>
        "修改某个网盘文件在**本机记录**里的显示名称和/或标签。file_id 用 find_cloud_files 返回的编号。\n" +
        "⚠️ 边界：**只改本机记录，云端那份文件的名字不会变**（还是原来那个名字）。" +
        "所以对用户只能说「已把本地显示名/标签改成 X」，**绝不能说「网盘上的文件已改名」**。\n" +
        "用途：让以后更容易搜到（标签尤其有用）。改名前先跟用户确认新名字。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"file_id":{"type":"string","description":"find_cloud_files 返回的编号"},"name":{"type":"string","description":"可选：新的显示名称（含扩展名更清晰）"},"tags":{"type":"string","description":"可选：新标签，逗号分隔（会整体替换原有标签）"}},"required":["file_id"]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "file_id", out var id);
        var (meta, _) = FileToolSupport.ResolveFileRef(id);
        var target = meta == null ? $"编号 {id}" : $"「{meta.Name}」";

        var changes = new List<string>();
        if (ToolArgs.TryGetString(argumentsJson, "name", out var name)) changes.Add($"显示名改为「{name}」");
        if (ToolArgs.TryGetString(argumentsJson, "tags", out var tags)) changes.Add($"标签改为「{tags}」");

        return changes.Count == 0
            ? $"修改文件 {target} 的记录（未指定要改什么）"
            : $"修改文件 {target} 的本机记录：{string.Join("；", changes)}（不影响云端文件名）";
    }

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
            return Task.FromResult("错误：缺少参数 file_id（用 find_cloud_files 返回的编号）。");

        var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
        if (meta == null) return Task.FromResult("错误：" + error);

        var hasName = ToolArgs.TryGetString(argumentsJson, "name", out var newName);
        var hasTags = ToolArgs.TryGetString(argumentsJson, "tags", out var tagsRaw);
        if (!hasName && !hasTags)
            return Task.FromResult("错误：至少要提供 name（新显示名）或 tags（新标签）中的一项。");

        List<string>? tags = null;
        if (hasTags)
        {
            tags = tagsRaw.Split(new[] { ',', '，', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        var oldName = meta.Name;
        if (!FileRepository.UpdateMetadata(meta.Id, hasName ? newName : null, tags))
            return Task.FromResult("错误：文件记录更新失败（可能该文件已被删除）。");

        var done = new List<string>();
        if (hasName) done.Add($"本机显示名：{oldName} → {newName}");
        if (hasTags) done.Add("标签：" + (tags!.Count == 0 ? "已清空" : string.Join("/", tags)));

        return Task.FromResult("已更新本机记录：" + string.Join("；", done) +
                               "。（云端那份文件的名字没有变，仍然是原名。）");
    }
}
