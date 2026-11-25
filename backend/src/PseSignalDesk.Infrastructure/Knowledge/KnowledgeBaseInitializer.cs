using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// Syncs the built-in playbooks into SQLite once at startup. A failure here is logged and
/// swallowed: the desk must still generate signals (ungrounded) if the knowledge base
/// cannot be seeded, and the Knowledge Base drawer offers a manual re-index button.
/// </summary>
public sealed class KnowledgeBaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILogger<KnowledgeBaseInitializer> _logger;

    public KnowledgeBaseInitializer(
        IServiceScopeFactory scopeFactory,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeBaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.IndexOnStartup)
        {
            _logger.LogInformation("Knowledge-base startup indexing is disabled by configuration.");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
            var result = await service.IndexBuiltInAsync(cancellationToken);

            _logger.LogInformation(
                "Knowledge base ready: {Indexed} new, {Updated} updated, {Skipped} unchanged, " +
                "{Embedded}/{Chunks} chunks embedded with {Model}.",
                result.DocumentsIndexed,
                result.DocumentsUpdated,
                result.DocumentsSkipped,
                result.ChunksEmbedded,
                result.ChunksIndexed,
                result.EmbeddingModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Knowledge-base seeding failed. Signals still run, but the prompt will carry no playbook grounding " +
                "until the library is indexed from the Knowledge Base drawer.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
