using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.WebApi.Controllers;

/// <summary>
/// On-demand research for a single PSE ticker, including names the daily catalog does not
/// track. The response is deterministic: levels, probability, verdict and size all come
/// from the server's own engine.
/// </summary>
[ApiController]
[Route("api/research")]
public sealed class ResearchController : ControllerBase
{
    private readonly TickerResearchService _research;

    public ResearchController(TickerResearchService research)
    {
        _research = research;
    }

    /// <summary>
    /// The tracked catalog: ticker, company name and sector, never a price. It is static
    /// metadata, so the browser may hold it for an hour — a reload of the desk should not
    /// re-download a list that only changes when the build changes.
    /// </summary>
    [HttpGet("catalog")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public ActionResult<IReadOnlyList<CatalogStockDto>> GetCatalog() =>
        Ok(_research.GetCatalog());

    [HttpGet("{symbol}")]
    [EnableRateLimiting(DeskRateLimitPolicies.Upstream)]
    public async Task<ActionResult<TickerResearchDto>> GetResearch(
        string symbol,
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        [FromQuery] decimal? averageCost = null,
        [FromQuery] int? shares = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid ticker",
                Detail = "Provide a PSE ticker such as ICT or BDO."
            });
        }

        if (averageCost is decimal cost && cost <= 0m)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid average cost",
                Detail = "Pass the price you actually paid, for example averageCost=52.30. It must be greater than zero."
            });
        }

        if (shares is int held && held < 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid share count",
                Detail = "Pass the shares you own, for example shares=200. It cannot be negative."
            });
        }

        if (!Enum.IsDefined(horizon))
        {
            horizon = SignalHorizon.WEEK;
        }

        try
        {
            return Ok(await _research.ResearchAsync(
                symbol,
                horizon,
                cancellationToken,
                averageCost,
                shares));
        }
        catch (MarketDataUnavailableException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Research data unavailable",
                detail: ex.Message);
        }
    }
}
