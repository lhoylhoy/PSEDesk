namespace PseSignalDesk.Application.Services;

/// <summary>
/// Raised when a blotter action would break a deterministic desk rule (board-lot
/// multiple, cash, position cap, portfolio heat, price-grid or lifecycle). The API maps
/// this to HTTP 400 with the exact reason, so a rejected order is explainable instead of
/// silently clamped.
/// </summary>
public sealed class PortfolioRuleViolationException : Exception
{
    public PortfolioRuleViolationException(string message) : base(message)
    {
    }
}
