using System.Threading;

namespace FocusCapture.Services.AI;

public interface IChatProvider
{
    string Model { get; }
    string BaseUrl { get; }
    string ApiKey { get; }
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}
