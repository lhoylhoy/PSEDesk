namespace PseSignalDesk.Application.Services;

/// <summary>
/// Raised when Ollama Cloud is required but not configured. The API maps this to
/// HTTP 400 with a configuration hint.
/// </summary>
public sealed class OllamaConfigurationException : Exception
{
    public OllamaConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when the signal engine could not produce a valid response after retrying.
/// The API maps this to HTTP 502.
/// </summary>
public sealed class SignalGenerationException : Exception
{
    public SignalGenerationException(string message) : base(message)
    {
    }

    public SignalGenerationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when no usable market data exists for the requested trading day.
/// The API maps this to HTTP 503.
/// </summary>
public sealed class MarketDataUnavailableException : Exception
{
    public MarketDataUnavailableException(string message) : base(message)
    {
    }

    public MarketDataUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when an uploaded Daily Quotation Report cannot be parsed into any foreign-flow
/// figures. The API maps this to HTTP 400: the file is the problem, not the market feed.
/// </summary>
public sealed class QuotationReportImportException : Exception
{
    public QuotationReportImportException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when a quotation report targets a session that is not stored yet, so there is
/// nothing to enrich. The API maps this to HTTP 404.
/// </summary>
public sealed class MarketSessionNotFoundException : Exception
{
    public MarketSessionNotFoundException(string message) : base(message)
    {
    }
}
