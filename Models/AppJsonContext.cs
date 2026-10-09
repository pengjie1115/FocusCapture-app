using System.Text.Json.Serialization;
using FocusCapture.Models;

namespace FocusCapture;

/// <summary>
/// 编译期 JSON 序列化上下文（Source Generator），
/// 兼容 PublishTrimmed / IsReflectionEnabledByDefault=false 环境。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HotkeyBinding))]
[JsonSerializable(typeof(ExportConfig))]
[JsonSerializable(typeof(DeletedNote))]
[JsonSerializable(typeof(List<DeletedNote>))]
// 注意：SyncSettings 在 AppSettings 内走源生成，漏注册运行时抛 NotSupportedException。
// SyncNote 由 [JsonPropertyName] 锁定 camelCase，与独立 SyncJson.Options（桶文件）命名一致。
[JsonSerializable(typeof(SyncSettings))]
[JsonSerializable(typeof(SyncNote))]
[JsonSerializable(typeof(List<SyncNote>))]
[JsonSerializable(typeof(SyncBucket))]
// v3.9 标题栏可组装：AppSettings.QuickViewToolbarLeft/Right 为 List<string>，漏注册运行时抛 NotSupportedException。
[JsonSerializable(typeof(List<string>))]
// 2026-09-23 AI 模型多供应商：AppSettings.AiModelProviders 是 List<AiProviderEntry>。
// 漏注册 → 运行时抛 NotSupportedException，**编译期零提示**（本文件已经因同类原因踩过两次）。
[JsonSerializable(typeof(AiProviderEntry))]
[JsonSerializable(typeof(List<AiProviderEntry>))]
[JsonSerializable(typeof(AiModelEntry))]
[JsonSerializable(typeof(List<AiModelEntry>))]
// 2026-09-27 AI 整理规则化：AppSettings.TidyRules 是 List<TidyRule>。
// 漏注册 → 运行时抛 NotSupportedException（本文件注释里已踩过两次的坑）。
[JsonSerializable(typeof(TidyRule))]
[JsonSerializable(typeof(List<TidyRule>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
