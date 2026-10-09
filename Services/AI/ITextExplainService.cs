using System.Threading;

namespace FocusCapture.Services.AI;

public interface ITextExplainService
{
    Task<string> ExplainAsync(string text, ExplainMode mode, CancellationToken ct = default);
}
