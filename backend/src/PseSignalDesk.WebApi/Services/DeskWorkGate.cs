namespace PseSignalDesk.WebApi.Services;

/// <summary>
/// One expensive desk job at a time: the live pipeline run and the model-training jobs.
/// </summary>
/// <remarks>
/// A single <c>run-now</c> fans out to roughly 280 PSE Edge chart posts and up to four model
/// calls, and it drops the upstream download cache first, so two concurrent presses double both
/// the traffic and the spend — and the slower one can overwrite the newer run for the same
/// (date, horizon). The per-hour rate limit below bounds how often that can happen; this gate
/// bounds how many can happen <em>at once</em>, and answers the second caller immediately with a
/// <c>409</c> instead of letting it queue behind minutes of work.
///
/// It is a singleton: the whole point is that two requests on two connections cannot both win.
/// </remarks>
public sealed class DeskWorkGate
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Claims the gate without waiting. True means the caller owns it and must call
    /// <see cref="Exit"/> in a <c>finally</c> — including on every failure path.
    /// </summary>
    public bool TryEnter() => _gate.Wait(0);

    /// <summary>Releases the gate. Only correct after a successful <see cref="TryEnter"/>.</summary>
    public void Exit() => _gate.Release();
}
