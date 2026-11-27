using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Turns the quantitatively filtered candidate set into a ranked Top 10 with
/// drop explanations. Implementations are responsible for parse retries.
/// </summary>
public interface IOllamaSignalEngine
{
    string EngineName { get; }

    Task<OllamaSignalResult> GenerateSignalAsync(
        OllamaSignalRequest request,
        CancellationToken cancellationToken = default);
}
