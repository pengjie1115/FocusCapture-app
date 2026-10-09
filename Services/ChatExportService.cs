using System.IO.Compression;
using System.Text.Encodings.Web;
using FocusCapture.Models;
using FocusCapture.Services.AI;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Services;

/// <summary>
/// AI 会话导出服务：Markdown / TXT / JSON / Word 四格式（PDF 不做）。
/// 导出内容 = user/assistant 气泡消息；过滤与 AgentRunService.cs:153 同一语义
/// （tool 中间消息与 assistant(tool_calls) 不导出——开工铁律 9：不另写语义）。
/// 文件名规则 = 标题（无标题回退预览）；同名去重（_1/_2 不覆盖）由调用方复用
/// NoteExportService.GetUniquePath/SanitizeFileName，本服务只负责内容生成与写出。
/// Word 的 OOXML 结构参考 NoteExportService（"参考"而非复用：不动笔记导出代码，保零回归红线）。
/// </summary>
public static class ChatExportService
{
    /// <summary>导出单个会话到指定完整路径（文件名去重由调用方负责，本方法只写出）。</summary>
    public static void ExportTo(SessionFile session, ExportFormat format, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var messages = session.Messages
            .Where(m => m.Role != ChatRoles.Tool && m.ToolCallsJson == null)
            .Where(m => m.Role != ChatRoles.System)
            .ToList();

        switch (format)
        {
            case ExportFormat.Json:
                File.WriteAllText(path, BuildJson(session, messages), Encoding.UTF8);
                break;
            case ExportFormat.Word:
                File.WriteAllBytes(path, BuildWord(session, messages));
                break;
            case ExportFormat.Txt:
                File.WriteAllText(path, BuildText(session, messages), Encoding.UTF8);
                break;
            default:
                File.WriteAllText(path, BuildMarkdown(session, messages), Encoding.UTF8);
                break;
        }
    }

    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.Json => ".json",
        ExportFormat.Txt => ".txt",
        ExportFormat.Word => ".docx",
        _ => ".md",
    };

    /// <summary>导出文件名：重命名标题优先，回退首条用户消息前 40 字</summary>
    public static string ResolveTitle(SessionFile session)
    {
        if (!string.IsNullOrWhiteSpace(session.Title)) return session.Title;
        var firstUser = session.Messages?.FirstOrDefault(m => m.Role == ChatRoles.User)?.Content ?? "";
        var line = firstUser.ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..40] : line;
    }

    private static string BuildMarkdown(SessionFile session, List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ResolveTitle(session)}");
        sb.AppendLine();
        sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm} · 模式：{AiModeText.Get(session.Mode)}");
        sb.AppendLine();
        foreach (var m in messages)
        {
            sb.AppendLine(m.Role == ChatRoles.User ? $"**我：** {m.Content}" : $"**AI：** {m.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildText(SessionFile session, List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{ResolveTitle(session)}");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm} · 模式：{AiModeText.Get(session.Mode)}");
        sb.AppendLine(new string('-', 30));
        foreach (var m in messages)
            sb.AppendLine(m.Role == ChatRoles.User ? $"我：{m.Content}" : $"AI：{m.Content}");
        return sb.ToString();
    }

    private static string BuildJson(SessionFile session, List<ChatMessage> messages)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("title", ResolveTitle(session));
            writer.WriteString("mode", session.Mode);
            writer.WriteString("exportedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            writer.WriteNumber("rev", session.Rev);
            writer.WriteStartArray("messages");
            foreach (var m in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", m.Role == ChatRoles.User ? "user" : "assistant");
                writer.WriteString("content", m.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── Word (.docx)：手写最小化 OOXML，无 NuGet 依赖（结构参考 NoteExportService，副本自持） ──

    private const string ContentTypesXml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>
  <Override PartName=""/word/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml""/>
</Types>";

    private const string RootRelsXml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>
</Relationships>";

    private const string DocumentRelsXml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>";

    private const string StylesXml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:style w:type=""paragraph"" w:styleId=""Normal"" w:default=""1"">
    <w:name w:val=""Normal""/>
    <w:pPr>
      <w:spacing w:after=""120""/>
    </w:pPr>
    <w:rPr>
      <w:rFonts w:ascii=""Microsoft YaHei"" w:eastAsia=""Microsoft YaHei"" w:hAnsi=""Microsoft YaHei""/>
      <w:sz w:val=""22""/>
    </w:rPr>
  </w:style>
  <w:style w:type=""paragraph"" w:styleId=""Heading1"">
    <w:name w:val=""heading 1""/>
    <w:basedOn w:val=""Normal""/>
    <w:pPr>
      <w:spacing w:before=""240"" w:after=""200""/>
    </w:pPr>
    <w:rPr>
      <w:b/>
      <w:sz w:val=""32""/>
      <w:color w:val=""2E7D32""/>
    </w:rPr>
  </w:style>
  <w:style w:type=""paragraph"" w:styleId=""NoteMeta"">
    <w:name w:val=""Note Meta""/>
    <w:basedOn w:val=""Normal""/>
    <w:pPr>
      <w:spacing w:after=""60""/>
    </w:pPr>
    <w:rPr>
      <w:color w:val=""888888""/>
      <w:sz w:val=""18""/>
    </w:rPr>
  </w:style>
</w:styles>";

    private static byte[] BuildWord(SessionFile session, List<ChatMessage> messages)
    {
        var documentXml = BuildDocumentXml(session, messages);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(zip, "[Content_Types].xml", ContentTypesXml);
            WriteZipEntry(zip, "_rels/.rels", RootRelsXml);
            WriteZipEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml);
            WriteZipEntry(zip, "word/styles.xml", StylesXml);
            WriteZipEntry(zip, "word/document.xml", documentXml);
        }
        return ms.ToArray();
    }

    private static string BuildDocumentXml(SessionFile session, List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");

        sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>");
        sb.Append($"<w:r><w:t xml:space=\"preserve\">{XmlEscape(ResolveTitle(session))}</w:t></w:r>");
        sb.Append("</w:p>");

        sb.Append("<w:p><w:pPr><w:pStyle w:val=\"NoteMeta\"/></w:pPr>");
        sb.Append($"<w:r><w:t xml:space=\"preserve\">{XmlEscape($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm} · 模式：{AiModeText.Get(session.Mode)}")}</w:t></w:r>");
        sb.Append("</w:p>");

        foreach (var m in messages)
        {
            var isUser = m.Role == ChatRoles.User;
            sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr>");
            sb.Append("<w:r><w:rPr><w:b/><w:color w:val=\"")
                .Append(isUser ? "2E7D32" : "555555")
                .Append("\"/></w:rPr>");
            sb.Append($"<w:t xml:space=\"preserve\">{XmlEscape(isUser ? "我：" : "AI：")}</w:t></w:r>");
            // 多行内容用软换行 <w:br/>（行内换行直接写 <w:t> 在 Word 中不显示）
            var lines = m.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                sb.Append($"<w:r><w:t xml:space=\"preserve\">{XmlEscape(lines[i])}</w:t></w:r>");
                if (i < lines.Length - 1) sb.Append("<w:r><w:br/></w:r>");
            }
            sb.Append("</w:p>");
        }

        sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr></w:p>");
        sb.Append("</w:body></w:document>");
        return sb.ToString();
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        es.Write(bytes, 0, bytes.Length);
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
