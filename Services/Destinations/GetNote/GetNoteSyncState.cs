using System.Security.Cryptography;
using FocusCapture.Models;

namespace FocusCapture.Services.Destinations.GetNote;

/// <summary>
/// 得到大脑推送去重映射（按钮路径专用；对话路径内容每次由模型加工，不适用内容级去重）。
/// 指纹 = MD5(条目时间戳 + 类型 + 内容后4位)，即「每条笔记一生只推一次」：
/// 推送后编辑内容不会产生新指纹，避免 note/save 无幂等键导致的云端重复。
/// 落盘 %AppData%\FocusCapture\getnote_sync_state.json，结构 { "指纹": "云端 note_id" }。
/// 已知取舍：在得到大脑 App 手动删除云笔记后本地仍记「已推」会跳过重推（解决需每次推送前查云端，成本不划算，接受）。
/// </summary>
public class GetNoteSyncState
{
    private static string StatePath => FocusCapturePaths.Combine("getnote_sync_state.json");

    private readonly Dictionary<string, string> _map;
    private readonly object _lock = new();

    public GetNoteSyncState()
    {
        _map = Load();
    }

    /// <summary>该条目是否已推送过（含 note_id）</summary>
    public bool TryGetNoteId(NoteEntry entry, out string noteId)
    {
        lock (_lock)
            return _map.TryGetValue(Fingerprint(entry), out noteId!);
    }

    /// <summary>推送成功后按条目记录指纹 → note_id 并落盘</summary>
    public void Record(IEnumerable<NoteEntry> entries, string noteId)
    {
        lock (_lock)
        {
            foreach (var e in entries)
                _map[Fingerprint(e)] = noteId;
            Save();
        }
    }

    /// <summary>指纹：MD5(时间戳到分 | 类型 | 内容后4位) 取 hex。内容参与只辅助同分钟多条的消歧。</summary>
    internal static string Fingerprint(NoteEntry entry)
    {
        var content = entry.EditedContent ?? entry.Content;
        var tail = content.Length >= 4 ? content[^4..] : content;
        var raw = $"{entry.Timestamp:yyyy-MM-dd HH:mm}|{(int)entry.Type}|{tail}";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed != null) return parsed;
            }
        }
        catch { /* 损坏则从空开始，最多导致重推一次 */ }
        return new Dictionary<string, string>();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_map));
        }
        catch { /* best effort：落盘失败仅影响下次启动后的去重记忆 */ }
    }
}
