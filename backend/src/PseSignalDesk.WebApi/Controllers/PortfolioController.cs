using Microsoft.AspNetCore.Mvc;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.WebApi.Controllers;

/// <summary>
/// The desk's own book: settings, mark-to-market summary, risk-sized order tickets and the
/// open/close lifecycle. Every rule (board lots, fees, cash, position cap, portfolio heat)
/// is enforced server-side; a rejected order comes back as a 400 with the exact reason.
/// </summary>
[ApiController]
[Route("api/portfolio")]
public sealed class PortfolioController : ControllerBase
{
    private readonly PortfolioService _portfolio;

    public PortfolioController(PortfolioService portfolio)
    {
        _portfolio = portfolio;
    }

    /// <summary>Settings, open positions with exit signals, closed ledger and alerts.</summary>
    [HttpGet("")]
    public async Task<ActionResult<PortfolioSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        var summary = await _portfolio.GetSummaryAsync(cancellationToken);
        return Ok(summary);
    }

    /// <summary>Partial update of the capital configuration.</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<PortfolioAccountDto>> UpdateSettings(
        [FromBody] PortfolioSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portfolio.UpdateSettingsAsync(request, cancellationToken));
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    /// <summary>
    /// Server-computed order ticket for one candidate: whole board lots, all-in buy cost,
    /// fee-paid peso risk and reward, and the break-even stop tick.
    /// </summary>
    [HttpGet("order-ticket")]
    public async Task<ActionResult<OrderTicketDto>> GetOrderTicket(
        [FromQuery] string? ticker,
        [FromQuery] decimal entry,
        [FromQuery] decimal stop,
        [FromQuery] decimal target,
        [FromQuery] EdgeVerdict verdict = EdgeVerdict.TRADE,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticker) || entry <= 0m || stop <= 0m || target <= 0m)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incomplete ticket request",
                Detail = "Pass ticker, entry, stop and target, for example " +
                         "order-ticket?ticker=ICT&entry=180&stop=172&target=196&verdict=TRADE."
            });
        }

        try
        {
            return Ok(await _portfolio.GetOrderTicketAsync(ticker, entry, stop, target, verdict, cancellationToken));
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    /// <summary>Logs a position ("Take Trade") after checking every desk rule.</summary>
    [HttpPost("trades")]
    public async Task<ActionResult<OpenPositionDto>> OpenTrade(
        [FromBody] OpenTradeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var position = await _portfolio.OpenTradeAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, position);
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    /// <summary>Moves the live stop of an open position.</summary>
    [HttpPatch("trades/{id:int}/stop")]
    public async Task<ActionResult<OpenPositionDto>> UpdateStop(
        int id,
        [FromBody] UpdateStopRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portfolio.UpdateStopAsync(id, request, cancellationToken));
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    /// <summary>Settles a position at the actual fill price.</summary>
    [HttpPost("trades/{id:int}/close")]
    public async Task<ActionResult<ClosedTradeDto>> CloseTrade(
        int id,
        [FromBody] CloseTradeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portfolio.CloseTradeAsync(id, request, cancellationToken));
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    /// <summary>Banks part of an open position, settling the slice as its own closed trade.</summary>
    [HttpPost("trades/{id:int}/trim")]
    public async Task<ActionResult<TrimTradeResultDto>> TrimTrade(
        int id,
        [FromBody] TrimTradeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portfolio.TrimTradeAsync(id, request, cancellationToken));
        }
        catch (PortfolioRuleViolationException ex)
        {
            return Reject(ex);
        }
    }

    private ObjectResult Reject(PortfolioRuleViolationException ex) =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Order rejected",
            detail: ex.Message);
}
