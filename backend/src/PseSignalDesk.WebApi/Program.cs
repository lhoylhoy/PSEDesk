using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Infrastructure.AI;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Knowledge;
using PseSignalDesk.Infrastructure.MarketData;
using PseSignalDesk.Infrastructure.Persistence;
using PseSignalDesk.Infrastructure.Scheduling;
using PseSignalDesk.WebApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var connectionString = ResolveConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection"),
    builder.Environment.ContentRootPath);

builder.Services.AddDbContext<PseSignalDbContext>(options => options.UseSqlite(connectionString));

builder.Services
    .AddOptions<OllamaCloudOptions>()
    .Bind(builder.Configuration.GetSection(OllamaCloudOptions.SectionName))
    .PostConfigure(options =>
    {
        var environmentApiKey = builder.Configuration["OLLAMA_API_KEY"];
        if (!string.IsNullOrWhiteSpace(environmentApiKey))
        {
            options.ApiKey = environmentApiKey.Trim();
        }

        var environmentBaseUrl = builder.Configuration["OLLAMA_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(environmentBaseUrl))
        {
            options.BaseUrl = environmentBaseUrl.Trim();
        }

        var environmentModel = builder.Configuration["OLLAMA_MODEL"];
        if (!string.IsNullOrWhiteSpace(environmentModel))
        {
            options.Model = environmentModel.Trim();
        }

        var environmentFastModel = builder.Configuration["OLLAMA_FAST_MODEL"];
        if (!string.IsNullOrWhiteSpace(environmentFastModel))
        {
            options.FastExtractionModel = environmentFastModel.Trim();
        }

        var environmentEmbeddingModel = builder.Configuration["OLLAMA_EMBEDDING_MODEL"];
        if (!string.IsNullOrWhiteSpace(environmentEmbeddingModel))
        {
            options.EmbeddingModel = environmentEmbeddingModel.Trim();
        }
    });

builder.Services
    .AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services
    .AddOptions<KnowledgeBaseOptions>()
    .Bind(builder.Configuration.GetSection(KnowledgeBaseOptions.SectionName));

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options => options.AddPolicy("FrontendPolicy", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<QuantitativeFilterService>();
builder.Services.AddSingleton<SignalJsonParser>();
builder.Services.AddSingleton<IDailyQuotationParser, PseDailyQuotationParser>();

builder.Services.AddHttpClient<IOllamaSignalEngine, OllamaSignalEngine>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OllamaCloudOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, options.RequestTimeoutSeconds));
});

builder.Services.AddHttpClient<IMarketDataProvider, PseMarketDataProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
});

builder.Services.AddHttpClient<OllamaEmbeddingClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
builder.Services.AddScoped<IKnowledgeRetrievalService, SqliteKnowledgeService>();

builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<IPortfolioRepository, PortfolioRepository>();
builder.Services.AddScoped<ProbabilisticEdgeService>();
builder.Services.AddScoped<SignalOutcomeService>();
builder.Services.AddScoped<SignalPipelineService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<TickerResearchService>();
builder.Services.AddScoped<DailyQuotationImportService>();
builder.Services.AddScoped<BacktestService>();
builder.Services.AddScoped<MoneyManagerTrainingService>();
builder.Services.AddHostedService<KnowledgeBaseInitializer>();
builder.Services.AddHostedService<DailySignalSchedulerService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<PseSignalDbContext>();
        await db.Database.MigrateAsync();
        logger.LogInformation("PSE Signal Desk SQLite schema is up to date.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not apply PSE Signal Desk database migrations.");
        throw;
    }
}

app.UseExceptionHandler();

// Registered inside UseExceptionHandler on purpose: a request the browser abandoned is not
// a fault, so it is answered with a quiet 499 before the problem-details handler sees it.
app.UseMiddleware<RequestCancellationMiddleware>();

app.UseCors("FrontendPolicy");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "PseSignalDesk.WebApi",
    utc = DateTime.UtcNow
}));

app.Run();

static string ResolveConnectionString(string? configured, string contentRootPath)
{
    var value = string.IsNullOrWhiteSpace(configured) ? "Data Source=pse_signals.db" : configured;
    var connectionBuilder = new SqliteConnectionStringBuilder(value);

    if (string.IsNullOrWhiteSpace(connectionBuilder.DataSource) ||
        connectionBuilder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
        Path.IsPathRooted(connectionBuilder.DataSource))
    {
        return value;
    }

    connectionBuilder.DataSource = Path.Combine(contentRootPath, connectionBuilder.DataSource);
    return connectionBuilder.ToString();
}

