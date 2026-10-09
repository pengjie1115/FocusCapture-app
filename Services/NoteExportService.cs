using System.IO.Compression;
using System.Text.Encodings.Web;
using FocusCapture.Models;

namespace FocusCapture.Services;

public class NoteExportService
{
    public string BuildExport(List<NoteEntry> notes, ExportConfig config)
    {
        return config.Format switch
        {
            ExportFormat.Markdown => BuildMarkdown(notes, config),
            ExportFormat.Json => BuildJson(notes, config),
            ExportFormat.Txt => BuildText(notes, config),
            _ => BuildMarkdown(notes, config)
        };
    }

    /// <summary>Word 导出返回二进制（.docx 本质是 zip）</summary>
    public byte[] BuildWord(List<NoteEntry> notes, ExportConfig config)
        => BuildDocxBytes(notes, config);

    public string GetFileExtension(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.Json => ".json",
        ExportFormat.Txt => ".txt",
        ExportFormat.Word => ".docx",
        _ => ".md"
    };

    private string BuildMarkdown(List<NoteEntry> notes, ExportConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 今日灵感 {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine();
        foreach (var note in notes)
        {
            // 待办导出（v3.5 Phase 4）：- [ ] 内容 (提醒: …)（Open/Read）/ - [x] 内容 (提醒: …)（Done）。
            // 只改行文本标记（内容区已把【待办】前缀剥离进 Type），无提醒不输出 (提醒:)。
            if (note.Type == NoteType.Todo)
            {
                var box = note.TodoStatus == TodoStatus.Done ? "[x]" : "[ ]";
                var body = config.IncludeContent ? note.Content.Trim() : "";
                var todoLine = $"- {box} {body}".TrimEnd();
                if (note.DueTime.HasValue)
                    todoLine += $" (提醒: {note.DueTime:yyyy-MM-dd HH:mm})";

                var todoMeta = new List<string>();
                if (config.IncludeTag && !string.IsNullOrWhiteSpace(note.Tag))
                    todoMeta.Add($"#{note.Tag}");
                if (config.IncludeSource && !string.IsNullOrWhiteSpace(note.SourceWindow))
                    todoMeta.Add($"来源: {note.SourceWindow}");
                if (todoMeta.Count > 0)
                    todoLine += " — " + string.Join(" ", todoMeta);

                sb.AppendLine(todoLine);
                continue;
            }

            // 普通笔记（现状不变，可回读；内容不含待办标记）
            var parts = new List<string>();
            if (config.IncludeTime) parts.Add($"[{note.Timestamp:HH:mm}]");
            if (config.IncludeContent) parts.Add(note.Content);
            var line = parts.Count > 0 ? "- " + string.Join(" ", parts) : "-";

            var meta = new List<string>();
            if (config.IncludeTag && !string.IsNullOrWhiteSpace(note.Tag))
                meta.Add($"#{note.Tag}");
            if (config.IncludeSource && !string.IsNullOrWhiteSpace(note.SourceWindow))
                meta.Add($"来源: {note.SourceWindow}");
            if (meta.Count > 0)
                line += " — " + string.Join(" ", meta);

            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private string BuildJson(List<NoteEntry> notes, ExportConfig config)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("date", DateTime.Now.ToString("yyyy-MM-dd"));
            writer.WriteStartArray("notes");
            foreach (var n in notes)
            {
                writer.WriteStartObject();
                if (config.IncludeTime)
                    writer.WriteString("time", n.Timestamp.ToString("HH:mm"));
                if (config.IncludeContent)
                    writer.WriteString("content", n.Content);
                if (config.IncludeTag && !string.IsNullOrWhiteSpace(n.Tag))
                    writer.WriteString("tag", n.Tag);
                if (config.IncludeSource && !string.IsNullOrWhiteSpace(n.SourceWindow))
                    writer.WriteString("source", n.SourceWindow);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildText(List<NoteEntry> notes, ExportConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"今日灵感 {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine(new string('-', 30));
        foreach (var note in notes)
        {
            var parts = new List<string>();
            if (config.IncludeTime) parts.Add($"[{note.Timestamp:HH:mm}]");
            if (config.IncludeContent) parts.Add(note.Content);
            if (config.IncludeTag && !string.IsNullOrWhiteSpace(note.Tag)) parts.Add($"#{note.Tag}");
            if (config.IncludeSource && !string.IsNullOrWhiteSpace(note.SourceWindow))
                parts.Add($"来源: {note.SourceWindow}");
            sb.AppendLine(string.Join(" ", parts));
        }
        return sb.ToString();
    }

    /// <summary>替换文件名中的非法字符为 _</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    /// <summary>自动处理同名文件冲突，加 _1 _2 后缀</summary>
    public static string GetUniquePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        for (var i = 1; i < 100; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // fallback: timestamp suffix
        return Path.Combine(dir, $"{name}_{DateTime.Now:HHmmss}{ext}");
    }

    // ── Word (.docx) 导出：手写最小化 OOXML，无 NuGet 依赖 ──

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

    private byte[] BuildDocxBytes(List<NoteEntry> notes, ExportConfig config)
    {
        var documentXml = BuildDocumentXml(notes, config);
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

    private string BuildDocumentXml(List<NoteEntry> notes, ExportConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");

        // 标题
        sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>");
        sb.Append($"<w:r><w:t xml:space=\"preserve\">{XmlEscape($"今日灵感 {DateTime.Now:yyyy-MM-dd}")}</w:t></w:r>");
        sb.Append("</w:p>");

        // 每条笔记
        foreach (var note in notes)
        {
            sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr>");

            // 时间（小字灰色）
            if (config.IncludeTime)
            {
                sb.Append("<w:r><w:rPr><w:color w:val=\"888888\"/><w:sz w:val=\"20\"/></w:rPr>");
                sb.Append($"<w:t xml:space=\"preserve\">[{XmlEscape(note.Timestamp.ToString("HH:mm"))}] </w:t></w:r>");
            }
            // 主内容
            if (config.IncludeContent)
            {
                sb.Append("<w:r>");
                sb.Append($"<w:t xml:space=\"preserve\">{XmlEscape(note.Content)}</w:t></w:r>");
            }
            sb.Append("</w:p>");

            // 元信息行（来源/标签）—— 单独段落
            var meta = new List<string>();
            if (config.IncludeSource && !string.IsNullOrWhiteSpace(note.SourceWindow))
                meta.Add($"来源：{note.SourceWindow}");
            if (config.IncludeTag && !string.IsNullOrWhiteSpace(note.Tag))
                meta.Add($"#{note.Tag}");

            if (meta.Count > 0)
            {
                sb.Append("<w:p><w:pPr><w:pStyle w:val=\"NoteMeta\"/></w:pPr>");
                sb.Append($"<w:r><w:t xml:space=\"preserve\">{XmlEscape(string.Join("  ·  ", meta))}</w:t></w:r>");
                sb.Append("</w:p>");
            }
        }

        // 文档结束段落（OOXML 要求 w:body 必须以 w:p 结尾）
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
