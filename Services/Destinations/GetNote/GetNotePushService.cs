using System.Text.Json.Nodes;
using FocusCapture.Models;

namespace FocusCapture.Services.Destinations.GetNote;

/// <summary>
/// 按钮路径推送编排：重复询问（批量一次，用户可选保留再推/跳过/取消）→ 包装台① → 确认弹窗 →
/// 走 GetNoteDestination 的 getnote_save_note（卡车唯一通道）→ 记指纹。
/// 限流/失败重试：每篇最多 3 次尝试（1 + 重试 2 次），间隔 1 秒；部分成功不回滚已推篇目。
/// </summary>
public class GetNotePushService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly GetNoteDestination _destination;
    private readonly GetNoteSyncState _syncState;

    /// <summary>发送前确认回调（UI 弹窗实现）：返回 true = 确认推送</summary>
    public Func<string, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>
    /// 检测到已推送条目时的询问回调（UI 三按钮弹窗实现，<b>批量一次</b>，2026-10-01 拍板）：
    /// Keep = 已推送的也照推（汇总版场景：把已推过的 A 一起再包进新汇总）；
    /// Remove = 维持旧口径跳过；Cancel = 整批取消。**一条重复都没有时不会调用**（无弹窗）。
    /// 未注入时按 Remove 处理（后台/测试路径维持旧口径，不弹窗）。
    /// </summary>
    public Func<IReadOnlyList<DuplicateInfo>, Task<DuplicateChoice>>? DuplicateHandler { get; set; }

    public GetNotePushService(AppSettings settings, GetNoteDestination destination, GetNoteSyncState syncState)
    {
        _settings = settings;
        _destination = destination;
        _syncState = syncState;
    }

    /// <summary>重复条目的展示信息（标题 + 上次推送得到的云端 note_id）。</summary>
    public sealed record DuplicateInfo(string Title, string NoteId);

    /// <summary>用户对重复条目的处置（弹窗三按钮）。</summary>
    public enum DuplicateChoice { Keep, Remove, Cancel }

    public sealed record PushOutcome(bool Success, bool Cancelled, int PushedEntries, int SkippedEntries, int NoteCount, string Message);

    public async Task<PushOutcome> PushAsync(IReadOnlyList<NoteEntry> entries, bool fromSelection, System.Threading.CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return new PushOutcome(false, false, 0, 0, 0, "当前没有可上传的笔记。");

        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return new PushOutcome(false, false, 0, 0, 0, "未配置得到大脑凭证（API Key / Client ID），请到设置 → AI 功能 → 得到大脑 配置。");

        // ── 去重：找出本批中已推送过的条目，整批问一次怎么处理 ──
        // （旧口径是"每条一生只推一次"直接静默跳过 —— 汇总版想把已推过的 A 再包进去就做不到，2026-10-01 改询问）
        var pending = new List<NoteEntry>(entries);
        var skipped = 0;
        var keptDuplicates = 0;
        var duplicateAsked = false;   // 重复询问弹过且用户已表态 → 不再二次确认（2026-10-01 拍板）
        var duplicates = new List<(NoteEntry Entry, string NoteId)>();
        foreach (var e in entries)
            if (_syncState.TryGetNoteId(e, out var noteId)) duplicates.Add((e, noteId));

        if (duplicates.Count > 0)
        {
            var choice = DuplicateHandler != null
                ? await DuplicateHandler(duplicates.Select(d => new DuplicateInfo(d.Entry.FirstLine, d.NoteId)).ToList())
                : DuplicateChoice.Remove;
            if (choice == DuplicateChoice.Cancel)
            {
                AppLog.Info("GetNote", "按钮推送：用户在重复询问处取消");
                return new PushOutcome(false, true, 0, 0, 0, "已取消推送。");
            }
            duplicateAsked = DuplicateHandler != null;
            if (choice == DuplicateChoice.Remove)
            {
                skipped = duplicates.Count;
                foreach (var (e, _) in duplicates) pending.Remove(e);
                if (pending.Count == 0)
                    return new PushOutcome(true, false, 0, skipped, 0,
                        $"这 {entries.Count} 条都已推送过得到大脑，按你的选择本次跳过。");
            }
            else
            {
                keptDuplicates = duplicates.Count;
            }
        }

        // ── 包装台①：制式包装（超长自动分篇）──
        var chunks = GetNotePayloadBuilder.Build(pending, fromSelection);

        // ── 发送前确认 ──
        // 重复询问弹窗里用户已经对整批表过态（仍要推送/跳过），再弹一次「确认推送？」是连续两问 —— 跳过。
        // 没有重复（或后台路径没有注入 DuplicateHandler）时保持原有确认不变。
        if (ConfirmHandler != null && !duplicateAsked)
        {
            var topicName = string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicName)
                ? "账号默认库" : _settings.GetNoteDefaultTopicName;
            var titles = string.Join("\n", chunks.Select((c, i) => $"  {i + 1}. 《{c.Title}》（{c.Entries.Count} 条）"));
            var dupNote = skipped > 0
                ? $"（另有 {skipped} 条已推送过，按你的选择跳过）"
                : keptDuplicates > 0 ? $"（含 {keptDuplicates} 条已推送过、你选择保留再推）" : "";
            var desc = $"将推送 {chunks.Count} 篇到知识库【{topicName}】：\n{titles}\n" +
                       $"共含 {pending.Count} 条{dupNote}\n\n确认推送？";
            if (!await ConfirmHandler(desc))
            {
                AppLog.Info("GetNote", "按钮推送：用户取消");
                return new PushOutcome(false, true, 0, skipped, 0, "已取消推送。");
            }
        }

        // ── 逐篇推送（失败重试，部分成功不回滚）──
        var pushedEntries = 0;
        var errors = new List<string>();
        foreach (var chunk in chunks)
        {
            var result = await PushChunkAsync(chunk, ct);
            if (result.Success)
            {
                _syncState.Record(chunk.Entries, result.NoteId ?? "");
                pushedEntries += chunk.Entries.Count;
                AppLog.Info("GetNote", $"推送成功：《{chunk.Title}》{chunk.Entries.Count} 条，note_id={result.NoteId}");
            }
            else
            {
                errors.Add($"《{chunk.Title}》：{result.Message}");
                AppLog.Warn("GetNote", $"推送失败：《{chunk.Title}》：{result.Message}");
            }
        }

        if (errors.Count == 0)
        {
            var msg = $"已推送 {chunks.Count} 篇（{pushedEntries} 条）到得到大脑。";
            if (keptDuplicates > 0) msg += $"含 {keptDuplicates} 条重复推送。";
            if (skipped > 0) msg += $"另有 {skipped} 条已推送过，按你的选择跳过。";
            return new PushOutcome(true, false, pushedEntries, skipped, chunks.Count, msg);
        }

        var failMsg = $"推送完成 {pushedEntries} 条，失败 {pending.Count - pushedEntries} 条：\n" + string.Join("\n", errors);
        if (keptDuplicates > 0) failMsg = $"（含 {keptDuplicates} 条重复推送）" + failMsg;
        if (skipped > 0) failMsg += $"\n（另有 {skipped} 条已推送过，按你的选择跳过）";
        return new PushOutcome(false, false, pushedEntries, skipped, chunks.Count - errors.Count, failMsg);
    }

    /// <summary>单篇推送：通过适配器统一执行入口（卡车），失败重试至 MaxAttempts 次</summary>
    private async Task<OutboundResult> PushChunkAsync(GetNotePayloadBuilder.PayloadChunk chunk, System.Threading.CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["title"] = chunk.Title,
            ["content"] = chunk.Content,
            ["tags"] = new JsonArray(chunk.Tags.Select(t => (JsonNode?)t).ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicId))
            body["topic_id"] = _settings.GetNoteDefaultTopicId;

        OutboundResult result = new(false, "未执行");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            result = await _destination.ExecuteAsync("getnote_save_note", body.ToJsonString(), ct);
            if (result.Success) break;
            if (attempt < MaxAttempts) await Task.Delay(RetryDelay, ct);
        }
        return result;
    }
}
