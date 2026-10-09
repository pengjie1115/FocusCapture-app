using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace FocusCapture.Services.Files;

/// <summary>一个工作表读出来的内容（行 × 列，全部字符串化）</summary>
public sealed class SheetData
{
    public string Name { get; init; } = "";

    /// <summary>行主序；每行长度可能不等（稀疏表按最后一列补齐）</summary>
    public List<List<string>> Rows { get; } = new();

    /// <summary>是否因超出上限被截断（必须如实告诉用户，不能悄悄少给几行）</summary>
    public bool Truncated { get; set; }

    public int RowCount => Rows.Count;
}

/// <summary>
/// xlsx 读取器（2026-09-17）：**零第三方依赖** —— xlsx 本质是 zip + XML，
/// 与项目既有的 docx 手写 OOXML 同一思路（见 ChatAttachmentService.ExtractDocx）。
///
/// 为什么不用 NuGet 的表格库：这个应用是「数据全本地 + 离线可用」，
/// 能自己解的就别引依赖（PdfPig 是因为 PDF 格式真的手写不了才引，见 DocumentTextExtractor）。
///
/// 已知必须处理的坑（少一个就会给出错数据）：
///   ① 共享字符串表 sharedStrings.xml —— 文本单元格存的是索引，不查表就是一堆数字
///   ② 内联字符串 t="inlineStr"（部分导出工具产出）
///   ③ **日期是序列号**（44927 之类）—— 不按样式表转日期，用户看到的就是一串莫名其妙的数字
///   ④ 稀疏行 / 列跳跃（r="C5" 中间跳过 B）—— 要按列号落位，不能顺序追加
///   ⑤ 多 sheet
/// </summary>
public static class XlsxTextExtractor
{
    /// <summary>单个 sheet 最多读多少行（超出截断并标注；防超大表把内存和上下文一起撑爆）</summary>
    public const int MaxRowsPerSheet = 5000;

    /// <summary>最大列数（防异常宽表）</summary>
    public const int MaxColumns = 200;

    /// <summary>读取全部工作表。文件损坏/格式异常一律抛 InvalidDataException（调用方转人话提示）。</summary>
    public static List<SheetData> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyleIndexes(zip);

        var result = new List<SheetData>();
        foreach (var (name, entryPath) in ResolveSheets(zip))
        {
            var entry = zip.GetEntry(entryPath);
            if (entry == null) continue;
            result.Add(ReadSheet(entry, name, shared, dateStyles));
        }

        if (result.Count == 0)
            throw new InvalidDataException("这个 Excel 文件里没有可读的工作表");

        return result;
    }

    /// <summary>把工作表转成给模型看的文本（带行列规模，便于它判断截断与否）</summary>
    public static string ToPlainText(IReadOnlyList<SheetData> sheets, int maxChars, out string? note)
    {
        var sb = new StringBuilder();
        var truncatedByChars = false;
        string? firstTruncatedSheet = null;

        foreach (var sheet in sheets)
        {
            var cols = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);
            sb.Append("【工作表：").Append(sheet.Name).Append("】共 ")
              .Append(sheet.RowCount).Append(" 行 × ").Append(cols).Append(" 列");
            if (sheet.Truncated) sb.Append("（仅读取前 ").Append(MaxRowsPerSheet).Append(" 行）");
            sb.Append('\n');

            for (var i = 0; i < sheet.Rows.Count; i++)
            {
                var line = $"[{i + 1}] " + string.Join(" | ", sheet.Rows[i].Select(Clean));
                if (sb.Length + line.Length + 1 > maxChars)
                {
                    truncatedByChars = true;
                    break;
                }
                sb.Append(line).Append('\n');
            }

            if (truncatedByChars) { firstTruncatedSheet ??= sheet.Name; break; }
            sb.Append('\n');
        }

        note = truncatedByChars
            ? $"（内容过长，仅取到「{firstTruncatedSheet}」工作表的前面部分）"
            : null;
        return sb.ToString().TrimEnd();
    }

    /// <summary>单个单元格的值清理：换行压成空格，去掉首尾空白</summary>
    private static string Clean(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();

    // ══════════════════ 结构解析 ══════════════════

    /// <summary>工作表清单：读 workbook.xml 的名称 + rels 里的真实路径</summary>
    private static List<(string Name, string EntryPath)> ResolveSheets(ZipArchive zip)
    {
        var list = new List<(string, string)>();

        var rels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry != null)
        {
            var doc = LoadXml(relsEntry);
            foreach (XmlNode rel in doc.DocumentElement!.ChildNodes)
            {
                var id = rel.Attributes?["Id"]?.Value;
                var target = rel.Attributes?["Target"]?.Value;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
                var normalized = target!.Replace('\\', '/').TrimStart('/');
                rels[id!] = normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : "xl/" + normalized;
            }
        }

        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry != null)
        {
            var doc = LoadXml(wbEntry);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            nsmgr.AddNamespace("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

            var nodes = doc.SelectNodes("//m:sheets/m:sheet", nsmgr);
            if (nodes != null)
            {
                foreach (XmlNode n in nodes)
                {
                    var name = n.Attributes?["name"]?.Value ?? $"Sheet{list.Count + 1}";
                    var rid = n.Attributes?["r:id"]?.Value;
                    if (rid != null && rels.TryGetValue(rid, out var path)) list.Add((name, path));
                }
            }
        }

        if (list.Count == 0)
        {
            // 退化路径：没有 workbook/rels 时按文件名顺序兜底（部分第三方导出工具会省掉 rels）
            foreach (var e in zip.Entries
                         .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                list.Add((Path.GetFileNameWithoutExtension(e.Name), e.FullName));
        }

        return list;
    }

    /// <summary>共享字符串表：位置即索引，文本单元格靠它还原</summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;

        var doc = LoadXml(entry);
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

        var items = doc.SelectNodes("//m:si", nsmgr);
        if (items == null) return result;

        foreach (XmlNode si in items)
        {
            // 富文本一个 si 里有多个 <t>，要全部拼起来（只取第一个会丢字）
            var texts = si.SelectNodes(".//m:t", nsmgr);
            if (texts == null) { result.Add(""); continue; }
            var sb = new StringBuilder();
            foreach (XmlNode t in texts) sb.Append(t.InnerText);
            result.Add(sb.ToString());
        }
        return result;
    }

    /// <summary>需要当日期解释的 cellXfs 索引集合（数字 + 日期样式 = Excel 的日期单元格）</summary>
    private static HashSet<int> ReadDateStyleIndexes(ZipArchive zip)
    {
        var set = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry == null) return set;

        var doc = LoadXml(entry);
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

        // 自定义格式码（numFmtId >= 164 的写在 numFmts 里）
        var customDateIds = new HashSet<int>();
        var fmts = doc.SelectNodes("//m:numFmts/m:numFmt", nsmgr);
        if (fmts != null)
        {
            foreach (XmlNode f in fmts)
            {
                if (!int.TryParse(f.Attributes?["numFmtId"]?.Value, out var id)) continue;
                if (LooksLikeDateFormat(f.Attributes?["formatCode"]?.Value)) customDateIds.Add(id);
            }
        }

        var xfs = doc.SelectNodes("//m:cellXfs/m:xf", nsmgr);
        if (xfs == null) return set;

        for (var i = 0; i < xfs.Count; i++)
        {
            if (!int.TryParse(xfs[i]?.Attributes?["numFmtId"]?.Value, out var numFmtId)) continue;
            if (BuiltInDateFormatIds.Contains(numFmtId) || customDateIds.Contains(numFmtId)) set.Add(i);
        }
        return set;
    }

    /// <summary>Excel 内置的日期/时间格式编号（ECMA-376 定义）</summary>
    private static readonly HashSet<int> BuiltInDateFormatIds = new()
    {
        14, 15, 16, 17, 18, 19, 20, 21, 22,          // 日期与时间
        27, 28, 29, 30, 31, 32, 33, 34, 35, 36,      // 亚洲日期
        45, 46, 47,                                   // 时间（分:秒 / 时:分:秒）
        50, 51, 52, 53, 54, 55, 56, 57, 58,          // 更多亚洲日期
    };

    /// <summary>自定义格式码是否表示日期/时间（去掉引号里的字面量与 [条件] 段再判断）</summary>
    private static bool LooksLikeDateFormat(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < code!.Length; i++)
        {
            var ch = code[i];
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch == '[')
            {
                var close = code.IndexOf(']', i);
                if (close > i) { i = close; continue; }
            }
            if (ch is '\\' or '_' or '*') { i++; continue; }   // 转义/占位符，跳过下一个字符
            sb.Append(ch);
        }

        var body = sb.ToString().ToLowerInvariant();
        return body.Contains('y') || body.Contains('d') || body.Contains('h') || body.Contains('s')
               || body.Contains('m');   // m 可能是月也可能是分钟，两者都属时间语义
    }

    // ══════════════════ 工作表读取 ══════════════════

    private static SheetData ReadSheet(ZipArchiveEntry entry, string name, List<string> shared, HashSet<int> dateStyles)
    {
        var sheet = new SheetData { Name = name };

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Ignore,
            CloseInput = false,
        });

        List<string>? row = null;
        var cellCol = -1;
        var cellType = "";
        var cellIsDate = false;
        string? rawValue = null;
        var autoCol = 0;
        var inCell = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        if (sheet.RowCount >= MaxRowsPerSheet)
                        {
                            sheet.Truncated = true;
                            reader.Skip();
                            continue;
                        }
                        row = new List<string>();
                        autoCol = 0;
                        break;

                    case "c":
                        inCell = true;
                        autoCol = 0;
                        cellType = reader.GetAttribute("t") ?? "";
                        var styleAttr = reader.GetAttribute("s");
                        cellIsDate = styleAttr != null && int.TryParse(styleAttr, out var sid) && dateStyles.Contains(sid);

                        var cellRef = reader.GetAttribute("r");
                        cellCol = cellRef != null ? ColumnIndex(cellRef) : -1;
                        rawValue = null;

                        if (reader.IsEmptyElement)
                        {
                            WriteCell(row, cellCol >= 0 ? cellCol : autoCol, "", ref autoCol);
                            inCell = false;
                        }
                        break;

                    case "v":
                        if (inCell) rawValue = ReadElementText(reader);
                        break;

                    case "t":
                        // inlineStr 的富文本会拆成多个 <t>（<r><t>a</t></r><r><t>b</t></r>），必须拼接而不是覆盖
                        if (inCell && cellType == "inlineStr") rawValue = (rawValue ?? "") + ReadElementText(reader);
                        break;

                    case "is":
                        if (reader.IsEmptyElement) { rawValue = ""; }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "c" && inCell)
                {
                    WriteCell(row, cellCol >= 0 ? cellCol : autoCol, RenderCell(rawValue, cellType, cellIsDate, shared), ref autoCol);
                    inCell = false;
                }
                else if (reader.LocalName == "row" && row != null)
                {
                    sheet.Rows.Add(row);
                    row = null;
                }
            }
        }

        return sheet;
    }

    /// <summary>按列号落位写入（稀疏行/列跳跃时补空串，不能顺序追加）</summary>
    private static void WriteCell(List<string>? row, int col, string value, ref int autoCol)
    {
        if (row == null) return;
        if (col < 0 || col >= MaxColumns) { autoCol++; return; }

        while (row.Count <= col) row.Add("");
        row[col] = value;
        autoCol = col + 1;
    }

    /// <summary>单元格原始值 → 展示字符串</summary>
    private static string RenderCell(string? raw, string type, bool isDate, List<string> shared)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        switch (type)
        {
            case "s":
                return int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
            case "str":
            case "inlineStr":
            case "e":
                return raw!;
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
        }

        // t 缺省 / t="n" → 数字；日期样式则按序列号还原成日期时间
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return raw!;

        if (isDate && num > 0 && num < 2958466)   // 2958466 = 9999-12-31 的序列号
        {
            try
            {
                var dt = DateTime.FromOADate(num);
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd")
                    : dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (ArgumentException) { /* 越界序列号，退回数字显示 */ }
        }

        // 整数不带小数点；小数最多 10 位并去掉尾随零（避免浮点尾巴污染判断）
        if (Math.Abs(num - Math.Round(num)) < 1e-9)
            return Math.Round(num).ToString("0", CultureInfo.InvariantCulture);

        return num.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>A1 / AB12 → 0 基列号</summary>
    private static int ColumnIndex(string cellRef)
    {
        var col = 0;
        var sawLetter = false;
        foreach (var ch in cellRef)
        {
            if (ch >= 'A' && ch <= 'Z') { col = col * 26 + (ch - 'A' + 1); sawLetter = true; }
            else if (ch >= 'a' && ch <= 'z') { col = col * 26 + (ch - 'a' + 1); sawLetter = true; }
            else break;
        }
        return sawLetter ? col - 1 : -1;
    }

    /// <summary>读当前元素内的文本，停在对应的 EndElement 上（不吞掉后续节点）</summary>
    private static string ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return "";
        var depth = reader.Depth;
        var sb = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                sb.Append(reader.Value);
        }
        return sb.ToString();
    }

    private static XmlDocument LoadXml(ZipArchiveEntry entry)
    {
        var doc = new XmlDocument();
        using var s = entry.Open();
        doc.Load(s);
        return doc;
    }
}
