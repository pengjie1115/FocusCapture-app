using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace FocusCapture.Services.Files;

/// <summary>
/// 文档正文抽取（2026-09-17）：**统一入口**，docx / xlsx / pdf / 纯文本全走这里。
///
/// 为什么要统一：改造前 docx 抽取写在 <c>ChatAttachmentService</c> 里（private），
/// 只有「对话附件」这一条路能用 —— 同一个 docx，贴进对话能读、从网盘调读不了。
/// 这种「同一能力两条路径不一致」的坑用户无法理解，也没法自己绕开。
/// 现在两条路共用本类：附件侧 <c>ChatAttachmentService</c>、网盘侧 <c>FileRepository.ReadTextAsync</c>。
///
/// 各格式的可行性差别很大，这是"引不引第三方库"的唯一判据：
///   - docx / xlsx：本质是 zip + XML，**纯 C# 手写**（与项目既有导出 .docx 的经验一脉相承）
///   - pdf：二进制容器（交叉引用表 + FlateDecode 压缩流 + 字体 CID/ToUnicode 映射），手写不现实 → 引 PdfPig
/// </summary>
public static class DocumentTextExtractor
{
    /// <summary>需要"解析"才能得到文本的后缀（纯文本类不在此列，直接按编码读）</summary>
    public static readonly HashSet<string> RichDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pdf",
    };

    /// <summary>抽取正文上限（字符）：超出截断并标注，防单个长文档撑爆会话文件与模型上下文</summary>
    public const int DefaultMaxChars = 50000;

    /// <summary>PDF 最多翻多少页（字符上限通常先到；这个只是给异常超长文件兜底）</summary>
    public const int MaxPdfPages = 300;

    public static bool IsRichDocument(string path) =>
        RichDocumentExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 抽取正文，返回（文本, 截断说明）。
    /// 失败一律抛 <see cref="InvalidDataException"/>，消息是**给用户看的人话**（调用方直接展示，不要自己再包一层）。
    /// </summary>
    public static (string Text, string? Note) Extract(string path, int maxChars = DefaultMaxChars)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var raw = ext switch
        {
            ".docx" => ExtractDocx(path),
            ".xlsx" => ExtractXlsx(path, maxChars),
            ".pdf" => ExtractPdf(path, maxChars),
            _ => ExtractPlainText(path),
        };

        var text = raw.Text.Replace("\r\n", "\n").Trim();
        var note = raw.Note;

        if (text.Length == 0)
            throw new InvalidDataException(raw.EmptyMessage);

        if (text.Length > maxChars)
            return (text[..maxChars], $"（内容过长，仅取前 {maxChars} 字）");

        return (text, note);
    }

    // ══════════════════ docx ══════════════════

    /// <summary>docx 抽文本：解 zip 读 word/document.xml，按 &lt;w:p&gt; 段落组织（零依赖，与 NoteImportService 同思路）</summary>
    private static (string Text, string? Note, string EmptyMessage) ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Word 文件缺少 word/document.xml，格式异常");

        var xml = new XmlDocument();
        using (var s = entry.Open())
            xml.Load(s);

        var nsmgr = new XmlNamespaceManager(xml.NameTable);
        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        var sb = new StringBuilder();
        var paragraphs = xml.SelectNodes("//w:p", nsmgr);
        if (paragraphs != null)
        {
            foreach (XmlNode p in paragraphs)
            {
                var texts = p.SelectNodes(".//w:t", nsmgr);
                if (texts == null) { sb.Append('\n'); continue; }
                foreach (XmlNode t in texts) sb.Append(t.InnerText);
                sb.Append('\n');
            }
        }

        return (sb.ToString(), null, "Word 文档里没有可提取的文字。");
    }

    // ══════════════════ xlsx ══════════════════

    private static (string Text, string? Note, string EmptyMessage) ExtractXlsx(string path, int maxChars)
    {
        var sheets = XlsxTextExtractor.Read(path);
        var text = XlsxTextExtractor.ToPlainText(sheets, maxChars, out var note);
        return (text, note, "这个 Excel 里没有可提取的内容（所有工作表都是空的）。");
    }

    // ══════════════════ pdf ══════════════════

    /// <summary>
    /// PDF 抽文本。**能力边界（必须让用户知道）**：只认有文字层的 PDF；
    /// 扫描件 / 图片版 PDF 里根本没有文字，属 OCR 范畴，本方法会明确说明而不是编内容。
    /// </summary>
    private static (string Text, string? Note, string EmptyMessage) ExtractPdf(string path, int maxChars)
    {
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new InvalidDataException("这份 PDF 有密码保护，读不了内容。请先用阅读器去掉密码再试。");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"PDF 打不开（{ex.GetType().Name}）：文件可能已损坏或不是标准 PDF。");
        }

        using (doc)
        {
            var sb = new StringBuilder();
            var pageCount = doc.NumberOfPages;
            var limit = Math.Min(pageCount, MaxPdfPages);
            var stoppedByChars = false;

            for (var i = 1; i <= limit; i++)
            {
                string pageText;
                try
                {
                    pageText = doc.GetPage(i).Text ?? "";
                }
                catch (Exception)
                {
                    continue;   // 单页解析失败不该让整份文件读不了
                }

                if (pageText.Trim().Length > 0)
                    sb.Append(pageText.TrimEnd()).Append("\n\n");

                if (sb.Length >= maxChars) { stoppedByChars = true; break; }
            }

            var text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException(
                    $"这份 PDF 共 {pageCount} 页，但一页文字都没提取出来 —— 多半是扫描件或图片版 PDF" +
                    "（内容其实是图片，需要 OCR 才能转成文字，本应用暂不做）。");

            var note = stoppedByChars || pageCount > limit
                ? $"（PDF 共 {pageCount} 页，此处只取了前面部分）"
                : null;

            return (text, note, "这份 PDF 里没有可提取的文字。");
        }
    }

    // ══════════════════ 纯文本 ══════════════════

    /// <summary>
    /// 纯文本类：要求合法 UTF-8。非 UTF-8（如 ANSI/GBK）会解码失败并给出明确提示，
    /// 不做自动编码嗅探（宁可能力边界清晰，也不猜错编码导致乱码内容被发给模型）。
    /// </summary>
    public static (string Text, string? Note, string EmptyMessage) ExtractPlainText(string path)
    {
        var bytes = StripBom(File.ReadAllBytes(path));
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), null, "文件里没有文字内容。");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("文件不是 UTF-8 编码，暂不支持（请另存为 UTF-8 后重试）。");
        }
    }

    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes[3..];
        return bytes;
    }
}
