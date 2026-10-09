using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.Agent;

namespace FocusCapture.Services.Destinations;

/// <summary>
/// 得到大脑（Get笔记）开放平台适配器：首个 IOutboundDestination 实现。
/// 能力目录见 AGENT_IMPLEMENTATION.md 3.3；新增能力 = Capabilities 加一条 + ExecuteAsync 加一个 case。
/// 鉴权：header Authorization + X-Client-ID（实测自 WorkBuddy getnote-sync 脚本）。
/// recall / note/detail 的响应结构未经本机实测，采用原样透传 data（截断）策略，实测有出入只改本文件。
/// </summary>
public class GetNoteDestination : IOutboundDestination
{
    private const string BaseUrl = "https://openapi.biji.com/open/api/v1";

    /// <summary>测试缝：检查点把请求指到本机拒连端口，保证慢层全程离线（null = 正式地址）。</summary>
    internal string? BaseUrlOverride;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public GetNoteDestination(AppSettings settings) => _settings = settings;

    public string Name => "得到大脑";

    /// <summary>
    /// AI 工具路径门禁回调（AIDialogWindow 注册时注入，与本地文件/文档工具同一套 FsGuard 机制）。
    /// getnote_save_image 的 image_path 由模型给出，落盘读取前必须过这道门；
    /// null = 调用方自行把关（悬浮球拖放等按钮路径，路径来自用户亲手拖入，不经过模型）。
    /// </summary>
    public Func<string, Task<bool>>? AskAllowDir { get; set; }

    private static readonly List<OutboundCapability> _capabilities = new()
    {
        new("getnote_save_note",
            "把一篇整理好的笔记保存到得到大脑（云端知识库）。标题与正文由你按用户对话约定组织，" +
            "整理/合并/跳过了哪些来源内容必须在答复中明示。需要用户确认。凭证未配置时不可用。",
            """{"type":"object","properties":{"title":{"type":"string","description":"笔记标题"},"content":{"type":"string","description":"正文，Markdown 纯文本"},"tags":{"type":"array","items":{"type":"string"},"description":"可选，标签列表"},"topic_id":{"type":"string","description":"可选，知识库 id（8 位字母数字）；不填则用账号默认"}},"required":["title","content"]}""",
            IsReadOnly: false),
        new("getnote_save_image",
            "把一张本机图片上传为得到大脑的图片笔记（img_text）：先取上传令牌，图片直传云端 OSS，再保存笔记。" +
            "支持 jpg/png/gif/webp，文件扩展名必须与真实格式一致（不一致会被拒绝，可提示用户另存后重试）。" +
            "需要用户确认。图片文件内容会上传到得到大脑云端。凭证未配置时不可用。",
            """{"type":"object","properties":{"image_path":{"type":"string","description":"本机图片文件的完整路径"},"title":{"type":"string","description":"可选，笔记标题；不填用文件名（去扩展名）"},"tags":{"type":"array","items":{"type":"string"},"description":"可选，标签列表"},"topic_id":{"type":"string","description":"可选，知识库 id（8 位字母数字）；不填则用账号默认"}},"required":["image_path"]}""",
            IsReadOnly: false),
        new("getnote_search",
            "语义搜索得到大脑云端已有笔记（向量召回，非关键词硬匹配）。" +
            "仅当用户明确提到\"得到大脑/云上/云端/以前存到云端的笔记\"时才调用。" +
            "注意：云端笔记内容会发送给模型服务方。返回条目含 id，读取全文须用本工具返回的 id。",
            """{"type":"object","properties":{"query":{"type":"string","description":"搜索内容描述"}},"required":["query"]}""",
            IsReadOnly: true),
        new("getnote_read_note",
            "读取得到大脑云端单篇笔记全文。id 必须来自 getnote_search 的返回结果，禁止编造。全文超长时截断。",
            """{"type":"object","properties":{"id":{"type":"string","description":"getnote_search 返回的笔记 id"}},"required":["id"]}""",
            IsReadOnly: true),
        new("getnote_list_notebooks",
            "列出得到大脑账号下的知识库（名称与 topic_id）。用户问\"我有哪些知识库\"或推送前需要确定 topic_id 时使用。",
            """{"type":"object","properties":{"page":{"type":"integer","description":"页码，默认 1"}}}""",
            IsReadOnly: true),
    };

    public IReadOnlyList<OutboundCapability> Capabilities => _capabilities;

    public async Task<OutboundResult> ExecuteAsync(string capabilityName, string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return new OutboundResult(false, "未配置得到大脑凭证（API Key / Client ID），无法执行。请在得到大脑开放平台创建应用获取凭证并配置到应用设置。");

        try
        {
            return capabilityName switch
            {
                "getnote_save_note" => await SaveNoteAsync(argumentsJson, ct),
                "getnote_save_image" => await SaveImageAsync(argumentsJson, ct),
                "getnote_search" => await SearchAsync(argumentsJson, ct),
                "getnote_read_note" => await ReadNoteAsync(argumentsJson, ct),
                "getnote_list_notebooks" => await ListNotebooksAsync(argumentsJson, ct),
                _ => new OutboundResult(false, $"未知能力: {capabilityName}"),
            };
        }
        catch (HttpRequestException ex)
        {
            return new OutboundResult(false, $"网络请求失败：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return new OutboundResult(false, "请求超时（30 秒），请稍后重试。");
        }
    }

    // ── 各能力实现 ──

    private async Task<OutboundResult> SaveNoteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "title", out var title))
            return new OutboundResult(false, "缺少参数 title。");
        if (!ToolArgs.TryGetString(argumentsJson, "content", out var content))
            return new OutboundResult(false, "缺少参数 content。");

        var body = new JsonObject
        {
            ["note_type"] = "plain_text",
            ["title"] = title,
            ["content"] = content,
        };
        ToolArgs.TryGetString(argumentsJson, "topic_id", out var topicId);
        if (!string.IsNullOrWhiteSpace(topicId)) body["topic_id"] = topicId;

        var tags = ExtractTags(argumentsJson);
        if (tags.Count > 0) body["tags"] = new JsonArray(tags.Select(t => (JsonNode?)t).ToArray());

        var json = await SendAsync(HttpMethod.Post, "/resource/note/save", body, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d : default;
        var noteId = data.ValueKind == JsonValueKind.Object
            ? (data.TryGetProperty("id", out var id) ? id.ToString() : "")
            : "";
        return new OutboundResult(true, $"已保存到得到大脑（note_id={noteId}，标题《{title}》）。", noteId);
    }

    // ── 图片上传（2026-10-01 新增）：三步 = 取上传令牌 → OSS 直传 → 存 img_text 笔记 ──
    // 接口口径来自得到大脑官方 CLI 的开源实现（iswalle/getnote-cli v1.6.0）。
    // OSS multipart 表单字段顺序官方注释标注为签名要求，按其原序：key → OSSAccessKeyId → policy →
    // signature → callback → Content-Type → file（file 分段的 Content-Type 必须与 policy 校验值一致）。

    private sealed record ImageUploadToken(string Host, string ObjectKey, string AccessId, string Policy,
        string Signature, string Callback, string AccessUrl, string OssContentType);

    /// <summary>支持的图片格式：扩展名、upload_token 的 mime_type 参数值、真实 MIME、魔数。</summary>
    private static readonly (string Ext, string Short, string Mime, byte[] Magic, int MagicLen)[] ImageFormats =
    {
        (".jpg",  "jpg",  "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF }, 3),
        (".jpeg", "jpg",  "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF }, 3),
        (".png",  "png",  "image/png",  new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 4),
        (".gif",  "gif",  "image/gif",  new byte[] { 0x47, 0x49, 0x46, 0x38 }, 4),          // "GIF8"
        (".webp", "webp", "image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46 }, 4),          // "RIFF"，另验 8~12 位 WEBP
    };

    /// <summary>
    /// OSS 直传报文体（静态便于检查点逐字节断言）。口径照抄官方 CLI 的 Go 实现（ImageUploadToOSS）：
    /// 字段顺序 key → OSSAccessKeyId → policy → signature → callback → Content-Type → file（签名要求）；
    /// 普通字段只带 Content-Disposition（name 加引号，不带 Content-Type）；file 分段 Content-Type
    /// 必须与 policy 校验值一致；文件名为原始 UTF-8，仅转义反斜杠与引号（同 Go 的 quoteEscaper）。
    /// </summary>
    public static (string ContentType, byte[] Body) BuildOssMultipart(
        string boundary, string objectKey, string accessId, string policy, string signature,
        string callback, string ossContentType, byte[] fileBytes, string fileName)
    {
        var escapedName = fileName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        void Field(string name, string value) =>
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        Field("key", objectKey);
        Field("OSSAccessKeyId", accessId);
        Field("policy", policy);
        Field("signature", signature);
        Field("callback", callback);
        Field("Content-Type", ossContentType);
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{escapedName}\"\r\n" +
              $"Content-Type: {ossContentType}\r\n\r\n");
        ms.Write(fileBytes);
        Write($"\r\n--{boundary}--\r\n");
        return ($"multipart/form-data; boundary={boundary}", ms.ToArray());
    }

    private async Task<OutboundResult> SaveImageAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "image_path", out var imagePath))
            return new OutboundResult(false, "缺少参数 image_path（本机图片文件的完整路径）。");

        // path 来源门禁（与文档工具同一道闸，2026-10-01）：AI 工具路径必须过 FsGuard（白名单 / 弹窗加白）；
        // 按钮路径 AskAllowDir 为 null —— 路径来自用户亲手拖入或选择，不经模型。
        if (AskAllowDir != null)
        {
            var (_, gateErr) = await FsGuard.GateAsync(imagePath, _settings, AskAllowDir);
            if (gateErr != null) return new OutboundResult(false, gateErr);
        }

        ToolArgs.TryGetString(argumentsJson, "title", out var title);
        ToolArgs.TryGetString(argumentsJson, "topic_id", out var topicId);

        // ① 扩展名先行拦截（不读文件就能判：官方只支持 jpg/png/gif/webp，猜错宁可拒绝）
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var format = ImageFormats.FirstOrDefault(f => f.Ext == ext);
        if (format.Mime == null)
            return new OutboundResult(false, $"不支持的图片格式「{ext}」。支持 jpg / png / gif / webp。");

        // ② 扩展名与真实格式必须一致（官方口径：不一致直接拒绝，绝不猜 —— 猜错传上去的是坏图）
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(imagePath, ct); }
        catch (Exception ex) { return new OutboundResult(false, $"读取图片失败：{ex.Message}"); }

        if (bytes.Length < 12 || !bytes.Take(format.MagicLen).SequenceEqual(format.Magic))
            return new OutboundResult(false, $"文件扩展名与真实格式不一致：扩展名 {ext}，但文件头不是 {format.Mime}。请确认文件后重试。");
        if (ext == ".webp" && System.Text.Encoding.ASCII.GetString(bytes[8..12]) != "WEBP")
            return new OutboundResult(false, "文件扩展名与真实格式不一致：扩展名 .webp，但文件头不是 image/webp。请确认文件后重试。");

        // ② 取上传令牌
        var tokenJson = await SendAsync(HttpMethod.Get,
            $"/resource/image/upload_token?mime_type={format.Short}&count=1", null, ct);
        if (!tokenJson.TryGetProperty("success", out var tokOk) || tokOk.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(tokenJson));
        var token = ParseUploadToken(tokenJson.TryGetProperty("data", out var tokData) ? tokData : default);
        if (token == null || string.IsNullOrEmpty(token.AccessId) || string.IsNullOrEmpty(token.Policy))
            return new OutboundResult(false, "上传令牌返回不完整（缺 accessid/policy），无法直传图片。");

        // ③ OSS 直传（multipart，字段顺序固定；file 分段 Content-Type 必须与 policy 一致）
        // ③ OSS 直传 —— 报文体手工构造，逐字节对齐官方 CLI（Go mime/multipart）。
        //    不要换回 MultipartFormDataContent：它给每个字段附 Content-Type: text/plain; charset=utf-8、
        //    字段名不加引号、非 ASCII 文件名编码成 RFC2047 encoded-word（filename="=?utf-8?B?…?="），
        //    OSS 解析不了 → HTTP 400 MalformedPOSTRequest（2026-10-01 实测）。
        var (ossContentType, ossBody) = BuildOssMultipart(
            Guid.NewGuid().ToString("N"), token.ObjectKey, token.AccessId, token.Policy,
            token.Signature, token.Callback, token.OssContentType, bytes, Path.GetFileName(imagePath));
        using (var ossRequest = new HttpRequestMessage(HttpMethod.Post, token.Host))
        {
            ossRequest.Content = new ByteArrayContent(ossBody);
            ossRequest.Content.Headers.TryAddWithoutValidation("Content-Type", ossContentType);
            using var ossResp = await Http.SendAsync(ossRequest, ct);
            if (!ossResp.IsSuccessStatusCode)
            {
                var ossErrBody = Truncate(await ossResp.Content.ReadAsStringAsync(ct), 200);
                return new OutboundResult(false, $"图片直传 OSS 失败（HTTP {(int)ossResp.StatusCode}）：{ossErrBody}");
            }
        }

        // ④ 存 img_text 图片笔记
        var saveBody = new JsonObject
        {
            ["note_type"] = "img_text",
            ["image_urls"] = new JsonArray((JsonNode?)token.AccessUrl),
        };
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(imagePath);
        saveBody["title"] = title;
        if (!string.IsNullOrWhiteSpace(topicId)) saveBody["topic_id"] = topicId;
        var tags = ExtractTags(argumentsJson);
        if (tags.Count > 0) saveBody["tags"] = new JsonArray(tags.Select(t => (JsonNode?)t).ToArray());

        var saveJson = await SendAsync(HttpMethod.Post, "/resource/note/save", saveBody, ct);
        if (!saveJson.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(saveJson));

        var data = saveJson.TryGetProperty("data", out var d) ? d : default;
        var (taskId, noteId) = ExtractSaveIds(data);

        // ⑤ 图片笔记是异步任务：官方口径轮询 2 秒 × 40 次，这里 30 次（≈60 秒）封顶
        if (!string.IsNullOrEmpty(taskId))
        {
            var polled = await PollNoteTaskAsync(taskId, ct);
            if (!polled.Success) return polled;
            noteId = polled.NoteId ?? noteId;
        }
        else if (string.IsNullOrEmpty(noteId))
        {
            return new OutboundResult(false, "图片已上传，但保存响应未返回 note_id 或 task_id，无法确认结果；请勿直接重复上传。");
        }

        return new OutboundResult(true, $"已上传图片并保存为得到大脑图片笔记（note_id={noteId}，标题《{title}》）。", noteId);
    }

    private static ImageUploadToken? ParseUploadToken(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        string Get(string name) => data.TryGetProperty(name, out var v) ? v.ToString() : "";
        return new ImageUploadToken(
            Get("host"), Get("object_key"), Get("accessid"), Get("policy"),
            Get("signature"), Get("callback"), Get("access_url"), Get("oss_content_type"));
    }

    /// <summary>save 响应的 data 里抠 task_id（兼容 {task_id} 与 {tasks:[{task_id}]} 两种形状）与 note_id。</summary>
    private static (string TaskId, string NoteId) ExtractSaveIds(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return ("", "");
        string Get(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
        var taskId = Get(data, "task_id");
        if (taskId == "" && data.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tasks.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && (taskId = Get(t, "task_id")) != "") break;
        }
        return (taskId, Get(data, "note_id") is "" or "0" ? "" : Get(data, "note_id"));
    }

    private async Task<OutboundResult> PollNoteTaskAsync(string taskId, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(2000, ct);
            var body = new JsonObject { ["task_id"] = taskId };
            var json = await SendAsync(HttpMethod.Post, "/resource/note/task/progress", body, ct);
            if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
                return new OutboundResult(false, "查询图片保存进度失败：" + DescribeApiError(json));
            if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                continue;

            var status = data.TryGetProperty("status", out var s) ? s.ToString() : "";
            var noteId = data.TryGetProperty("note_id", out var n) &&
                         n.ValueKind is JsonValueKind.String or JsonValueKind.Number ? n.ToString() : "";
            if (status is "done" or "success")
            {
                if (noteId is "" or "0")
                    return new OutboundResult(false, $"保存任务已结束但未返回有效 note_id，无法确认结果；请勿直接重复上传。");
                return new OutboundResult(true, "图片笔记已保存。", noteId);
            }
            if (status == "failed")
            {
                var msg = data.TryGetProperty("error_msg", out var em) && em.ToString().Length > 0 ? em.ToString()
                    : data.TryGetProperty("msg", out var m) ? m.ToString() : "未知原因";
                return new OutboundResult(false, $"图片笔记保存失败：{msg}");
            }
        }
        return new OutboundResult(false, $"保存任务 {taskId} 超过 60 秒仍未结束；请稍后到得到大脑确认结果，不要直接重复上传。");
    }

    private async Task<OutboundResult> SearchAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "query", out var query))
            return new OutboundResult(false, "缺少参数 query。");

        var body = new JsonObject { ["query"] = query };
        var json = await SendAsync(HttpMethod.Post, "/resource/recall", body, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
        return new OutboundResult(true, "搜索结果（JSON 原样，note id 用于 getnote_read_note）：\n" + Truncate(data, 1500));
    }

    private async Task<OutboundResult> ReadNoteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "id", out var id))
            return new OutboundResult(false, "缺少参数 id（必须来自 getnote_search 返回）。");

        var url = $"/resource/note/detail?id={Uri.EscapeDataString(id)}";
        var json = await SendAsync(HttpMethod.Get, url, null, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
        return new OutboundResult(true, "笔记详情（JSON 原样）：\n" + Truncate(data, 2000));
    }

    private async Task<OutboundResult> ListNotebooksAsync(string argumentsJson, CancellationToken ct)
    {
        var page = "1";
        if (ToolArgs.TryGetInt(argumentsJson, "page", out var parsed) && parsed > 0)
            page = parsed.ToString();

        var json = await SendAsync(HttpMethod.Get, $"/resource/knowledge/list?page={page}", null, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("topics", out var topics) &&
            topics.ValueKind == JsonValueKind.Array)
        {
            var topicList = ParseTopics(data);
            var lines = topicList.Select(t => $"- {t.Name} (topic_id={t.Id})");
            return new OutboundResult(true, "知识库列表：\n" + string.Join("\n", lines));        }
        return new OutboundResult(true, "知识库列表（JSON 原样）：\n" + Truncate(json.GetProperty("data").GetRawText(), 1000));
    }

    /// <summary>知识库条目（名称 + topic_id），按钮路径默认知识库与设置面板测试连接共用</summary>
    public sealed record GetNoteTopic(string Name, string Id);

    /// <summary>
    /// 设置面板「测试连接」：验证凭证 + 拉取知识库列表（兼测读权限）。
    /// 与对话工具 list_notebooks 走同一接口；不写测试笔记，避免脏数据。
    /// </summary>
    public async Task<(bool Ok, string Message, List<GetNoteTopic> Topics)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return (false, "请先填写 API Key 和 Client ID。", new List<GetNoteTopic>());
        try
        {
            var json = await SendAsync(HttpMethod.Get, "/resource/knowledge/list?page=1", null, ct);
            if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
                return (false, DescribeApiError(json), new List<GetNoteTopic>());
            var topics = json.TryGetProperty("data", out var data) ? ParseTopics(data) : new List<GetNoteTopic>();
            return (true, $"连接成功，已加载 {topics.Count} 个知识库。", topics);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络请求失败：{ex.Message}", new List<GetNoteTopic>());
        }
        catch (TaskCanceledException)
        {
            return (false, "请求超时（30 秒），请稍后重试。", new List<GetNoteTopic>());
        }
    }

    /// <summary>解析 knowledge/list 的 topics 数组；topic_id/id 兼容数字与字符串</summary>
    private static List<GetNoteTopic> ParseTopics(JsonElement data)
    {
        var topics = new List<GetNoteTopic>();
        if (data.TryGetProperty("topics", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : "";
                string? tid = null;
                if (t.TryGetProperty("topic_id", out var tidEl)) tid = tidEl.ToString();
                else if (t.TryGetProperty("id", out var idEl)) tid = idEl.ToString();
                if (!string.IsNullOrWhiteSpace(tid))
                    topics.Add(new GetNoteTopic(name ?? "(未命名)", tid));
            }
        }
        return topics;
    }

    // ── HTTP 与解析 ──

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, (BaseUrlOverride ?? BaseUrl) + url);
        request.Headers.Authorization = new AuthenticationHeaderValue(_settings.GetNoteApiKey);
        request.Headers.Add("X-Client-ID", _settings.GetNoteClientId);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(DescribeHttpError(response.StatusCode, text));

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new HttpRequestException($"响应不是合法 JSON: {Truncate(text, 300)}");
        }
    }

    /// <summary>
    /// 非 2xx 报错翻译（2026-10-01）：insufficient_scope 原样甩 JSON 用户不知道去哪改 ——
    /// 报文会指名缺哪个 scope（如图片上传的 note.image.upload），翻成人话并给出操作指引。
    /// </summary>
    internal static string DescribeHttpError(System.Net.HttpStatusCode status, string body)
    {
        if (body.Contains("insufficient_scope"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(body, @"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+");
            var scope = m.Success ? m.Value : "对应";
            return $"HTTP {(int)status}：得到大脑 API Key 缺少「{scope}」权限。" +
                   "请登录得到开放平台，在 API Key 的权限设置里勾选该权限后重试。";
        }
        return $"HTTP {(int)status}: {Truncate(body, 300)}";
    }

    /// <summary>官方响应信封 {success, error:{...}}，error 文案透出（含限流 10202 等）</summary>
    private static string DescribeApiError(JsonElement json)
    {
        if (json.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "未知错误";
            if (err.ValueKind == JsonValueKind.Object)
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var hint = code == "10202" ? "（疑似触发限流，请稍后分批重试）" : "";
                return $"API 错误 {code}: {msg}{hint}";
            }
        }
        return "API 返回失败（未提供错误详情）：" + Truncate(json.GetRawText(), 200);
    }

    private static List<string> ExtractTags(string argumentsJson)
    {
        var tags = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                tags.AddRange(arr.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString() ?? "")
                    .Where(t => t.Length > 0));
        }
        catch (JsonException) { }
        return tags;
    }

    private static string Truncate(string text, int maxLength)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…（已截断）";
    }
}
