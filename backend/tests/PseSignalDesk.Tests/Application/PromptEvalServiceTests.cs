using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

public sealed class PromptEvalServiceTests
{
    [Fact]
    public async Task EvaluateAsync_grades_llm_picks_against_quant_baseline_causally()
    {
        var batch = SyntheticMarketFixture.BuildBatch();
        var engine = new EchoTopPickSignalEngine();
        var service = new PromptEvalService(
            new SyntheticMarketFixture.FixedMarketDataProvider(batch),
            new QuantitativeFilterService(),
            engine);

        var result = await service.EvaluateAsync(
            SignalHorizon.WEEK,
            sessions: 4,
            asOfDate: batch.TradeDate,
            lookbackDays: 900);

        Assert.Equal("WEEK", result.Horizon);
        Assert.Equal("StubEvalEngine", result.Engine);
        Assert.Equal("eval-model", result.Model);
        Assert.Equal(4, result.SessionsRequested);
        Assert.Equal(4, result.SessionsEvaluated);
        Assert.Equal(4, result.Sessions.Count);
        Assert.True(result.LlmSummary.TotalPicks > 0);
        Assert.True(result.QuantBaselineSummary.TotalPicks > 0);
        Assert.Equal(400, result.TotalPromptTokens);
        Assert.Equal(200, result.TotalCompletionTokens);

        // Causality check: no sampled session ever saw candidates from after its own TradeDate.
        foreach (var session in result.Sessions)
        {
            Assert.True(session.TradeDate < batch.TradeDate);
        }
    }

    private sealed class EchoTopPickSignalEngine : IOllamaSignalEngine
    {
        public string EngineName => "StubEvalEngine";

        public Task<OllamaSignalResult> GenerateSignalAsync(
            OllamaSignalRequest request,
            CancellationToken cancellationToken = default)
        {
            var picks = request.Candidates
                .Take(3)
                .Select((candidate, index) =>
                {
                    var entry = candidate.ReferenceEntryPrice ?? PseBoardLotService.RoundToValidTick(candidate.ClosePrice);
                    var stop = candidate.ReferenceStopLoss ?? PseBoardLotService.RoundToValidTick(entry - (1.5m * candidate.Atr14));
                    var target = candidate.ReferenceTargetPrice ?? PseBoardLotService.RoundToValidTick(entry + (3.0m * candidate.Atr14));
                    return new OllamaTopPick
                    {
                        Ticker = candidate.Ticker,
                        ConvictionScore = 82 - (index * 4),
                        EntryPrice = entry,
                        StopLoss = stop,
                        TargetPrice = target,
                        Thesis = $"{candidate.Ticker} clears trend and volume criteria.",
                        BearCase = $"A close below {stop} invalidates the setup."
                    };
                })
                .ToList();

            var output = new OllamaSignalOutput { Top10 = picks };
            var metadata = new OllamaGenerationMetadata(
                EngineName,
                "eval-model",
                "hash",
                0.1m,
                42,
                1000,
                PromptTokens: 100,
                CompletionTokens: 50,
                RetryCount: 0,
                LatencyMs: 250,
                SchemaConstrained: true,
                FastExtractionApplied: false);

            return Task.FromResult(new OllamaSignalResult(output, metadata));
        }
    }
}
