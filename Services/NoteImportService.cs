using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// 笔记导入服务：将 TXT / Markdown / Word (.docx) 文件解析为 NoteEntry 列表（零依赖 OOXML 解析，
/// 复用 NoteService.NoteLineRegex 识别我们的行格式）。对项目自产文件 100% 兼容；第三方 Word
/// 仅保证纯文本提取（不解析样式/表格/图片）。
/// </summary>
public class NoteImportService
{
    /// <summary>导入预览结果（不写盘，仅预览）</summary>
    public class ImportPreview
    {
        public string SourcePath { get; init; } = "";
        public ImportFormat Format { get; init; }
        public List<NoteEntry> Entries { get; init; } = new();
        public List<string> Warnings { get; init; } = new();   // 解析过程的非致命告警
    }

    public enum ImportFormat { Markdown, Txt, Word, Unknown }

    /// <summary>UI 层枚举已勾选待导入的 NoteEntry，配合 targetDate 调用 NoteService.ImportNotes。</summary>
    public class ImportRequest
    {
        public List<NoteEntry> Entries { get; init; } = new();
        public DateTime TargetDate { get; init; } = DateTime.Today;
    }

    /// <summary>统一入口：根据扩展名分派解析。文件不存在/IO 错误抛出供 UI 提示。</summary>
    public ImportPreview Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);

        var format = DetectFormat(filePath);
        var entries = format switch
        {
            ImportFormat.Markdown => ParseMarkdown(File.ReadAllText(filePath, Encoding.UTF8)),
            ImportFormat.Txt => ParseText(File.ReadAllText(filePath, Encoding.UTF8)),
            ImportFormat.Word => ParseWord(filePath),
            _ => throw new InvalidDataException($"不支持的格式：{Path.GetExtension(filePath)}")
        };

        // 给所有无来源的笔记标注"导入自 xxx"，便于溯源
        var fileName = Path.GetFileName(filePath);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.SourceWindow))
                e.SourceWindow = $"导入自文件:{fileName}";
        }

        return new ImportPreview
        {
            SourcePath = filePath,
            Format = format,
            Entries = entries
        };
    }

    public static ImportFormat DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => ImportFormat.Markdown,
            ".txt" => ImportFormat.Txt,
            ".docx" => ImportFormat.Word,
            _ => ImportFormat.Unknown
        };
    }

    public static string FormatFilter => "支持的格式 (*.txt;*.md;*.docx)|*.txt;*.md;*.docx|文本 (*.txt)|*.txt|Markdown (*.md)|*.md|Word (*.docx)|*.docx";

    // ── 解析器 ──

    /// <summary>
    /// 解析 Markdown：复用 NoteService.NoteLineRegex，按行匹配。
    /// 标题行 `# 今日灵感 ...` 跳过。我们的格式：`- [HH:mm] 内容 — 来源: xxx`（兼容新格式带 yyyy-MM-dd）。
    /// 第三方纯 MD 文件（无时间戳）：每行作为一个无时间戳条目（导入时分配到 targetDate）。
    /// </summary>
    private static List<NoteEntry> ParseMarkdown(string content)
    {
        var entries = new List<NoteEntry>();
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;   // 标题行

            var m = NoteService.NoteLineRegex.Match(line);
            if (m.Success)
            {
                var day = m.Groups[1].Success
                    ? m.Groups[1].Value.Trim()
                    : DateTime.Today.ToString("yyyy-MM-dd");
                if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts))
                {
                    entries.Add(new NoteEntry
                    {
                        Timestamp = ts,
                        Content = m.Groups[3].Value.Replace("\u23CE", "\n"),
                        SourceWindow = m.Groups[4].Success ? m.Groups[4].Value : ""
                    });
                }
            }
            else
            {
                // 非标准行：作为无时间戳条目（导入时分到 targetDate）
                entries.Add(new NoteEntry
                {
                    Timestamp = DateTime.MinValue,   // 标记，ImportNotes 会重写
                    Content = line,
                    SourceWindow = ""
                });
            }
        }
        return entries;
    }

    /// <summary>解析 TXT：纯文本按行拆分，每行作为一条笔记（无时间戳标记）。</summary>
    private static List<NoteEntry> ParseText(string content)
    {
        var entries = new List<NoteEntry>();
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 跳过文件头（"今日灵感 YYYY-MM-DD"、分隔线、空标题）
            if (line.StartsWith("今日灵感", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("---") || line.StartsWith("===")) continue;
            if (line.Length >= 30 && line.All(c => c == '-' || c == '=' || c == ' ')) continue;

            // 先尝试匹配我们的行格式（带时间戳）
            var m = NoteService.NoteLineRegex.Match(line);
            if (m.Success)
            {
                var day = m.Groups[1].Success
                    ? m.Groups[1].Value.Trim()
                    : DateTime.Today.ToString("yyyy-MM-dd");
                if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts))
                {
                    entries.Add(new NoteEntry
                    {
                        Timestamp = ts,
                        Content = m.Groups[3].Value.Replace("\u23CE", "\n"),
                        SourceWindow = m.Groups[4].Success ? m.Groups[4].Value : ""
                    });
                    continue;
                }
            }

            // 无时间戳的纯行
            entries.Add(new NoteEntry
            {
                Timestamp = DateTime.MinValue,
                Content = line,
                SourceWindow = ""
            });
        }
        return entries;
    }

    /// <summary>
    /// 解析 Word (.docx)：.docx 是 zip，读 word/document.xml，抽所有 <w:t> 文本，按段落组织。
    /// 段落 = 笔记行；段落内 <w:t> 拼接为该行文本。无时间戳，每个段落作为一条笔记。
    /// 对第三方 Word：不解析表格/图片/样式，仅文本提取 —— 退化等同于纯文本。
    /// </summary>
    private static List<NoteEntry> ParseWord(string filePath)
    {
        var entries = new List<NoteEntry>();
        using var zip = ZipFile.OpenRead(filePath);
        var docEntry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Word 文件缺少 word/document.xml，格式异常");

        var xml = new XmlDocument();
        using (var stream = docEntry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            xml.LoadXml(reader.ReadToEnd());
        }

        var nsmgr = new XmlNamespaceManager(xml.NameTable);
        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        // 按段落迭代：每个 <w:p> 一条笔记
        var paragraphs = xml.SelectNodes("//w:p", nsmgr);
        if (paragraphs == null) return entries;

        foreach (XmlNode p in paragraphs)
        {
            var sb = new StringBuilder();
            var texts = p.SelectNodes(".//w:t", nsmgr);
            if (texts == null) continue;

            foreach (XmlNode t in texts)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t.InnerText);
            }

            var line = sb.ToString().Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 跳过 Heading1 标题（"今日灵感 YYYY-MM-DD" 样式）
            // 通过判断段落是否含粗体大字号太复杂，简化为：跳过包含"今日灵感"开头的行
            if (line.StartsWith("今日灵感", StringComparison.OrdinalIgnoreCase)) continue;

            // 也尝试匹配我们的行格式
            var m = NoteService.NoteLineRegex.Match(line);
            if (m.Success)
            {
                var day = m.Groups[1].Success
                    ? m.Groups[1].Value.Trim()
                    : DateTime.Today.ToString("yyyy-MM-dd");
                if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts))
                {
                    entries.Add(new NoteEntry
                    {
                        Timestamp = ts,
                        Content = m.Groups[3].Value.Replace("\u23CE", "\n"),
                        SourceWindow = m.Groups[4].Success ? m.Groups[4].Value : ""
                    });
                    continue;
                }
            }

            // 无时间戳段落
            entries.Add(new NoteEntry
            {
                Timestamp = DateTime.MinValue,
                Content = line,
                SourceWindow = ""
            });
        }

        return entries;
    }
}
