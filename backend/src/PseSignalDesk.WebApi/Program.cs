using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Infrastructure.AI;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;
using PseSignalDesk.Infrastructure.Knowledge;
using PseSignalDesk.Infrastructure.MarketData;
using PseSignalDesk.Infrastructure.Persistence;
using PseSignalDesk.Infrastructure.Scheduling;
using PseSignalDesk.WebApi.Middleware;
using PseSignalDesk.WebApi.Services;

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

// The binder appends configuration onto the initialized lists instead of replacing them, so a
// section that repeats the code defaults (the shipped appsettings.json does) would double every
// index symbol and proxy constituent. Normalize once, before any consumer reads the options.
builder.Services
    .AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
    .PostConfigure(options => options.Normalize());
builder.Services
    .AddOptions<KnowledgeBaseOptions>()
    .Bind(builder.Configuration.GetSection(KnowledgeBaseOptions.SectionName));

// Deployment-scope guardrails (personal-use market data + optional operator key). Bound from the
// "Compliance" section; the key itself is meant to arrive through PSEDESK_OPERATOR_API_KEY so it
// never has to be written into appsettings.json.
builder.Services
    .AddOptions<ComplianceOptions>()
    .Bind(builder.Configuration.GetSection(ComplianceOptions.SectionName))
    .PostConfigure(options =>
    {
        var environmentOperatorKey = builder.Configuration["PSEDESK_OPERATOR_API_KEY"];
        if (!string.IsNullOrWhiteSpace(environmentOperatorKey))
        {
            options.OperatorApiKey = environmentOperatorKey.Trim();
        }
    });

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options => options.AddPolicy("FrontendPolicy", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

// A LicensedCommercial deployment is normally served under a real host name, but the shipped
// AllowedHosts is the loopback allowlist (the personal default), so every request carrying a
// domain in Host: would answer 400 and the documented licensed mode could not be reached. Only a
// deployment that has explicitly declared the licensed mode may widen it, so a leftover value in
// configuration can never loosen the personal desk.
var complianceConfiguration = builder.Configuration.GetSection(ComplianceOptions.SectionName);

if (string.Equals(
        complianceConfiguration["DeploymentMode"],
        ComplianceOptions.LicensedCommercialMode,
        StringComparison.OrdinalIgnoreCase))
{
    var licensedHosts = complianceConfiguration["AllowedHosts"];

    if (!string.IsNullOrWhiteSpace(licensedHosts))
    {
        builder.Configuration["AllowedHosts"] = licensedHosts.Trim();
    }
}

// The expensive endpoints (the live run, the causal replay and the two training jobs) fan out to
// PSE Edge and spend Ollama Cloud credit, so they get an hourly ceiling on top of the
// single-job DeskWorkGate. Everything else stays uncapped: the terminal reads its stored session
// as often as it likes.
var expensiveRequestsPerHour = Math.Clamp(
    complianceConfiguration.GetValue("ExpensiveRequestsPerHour", 30),
    1,
    1000);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(DeskRateLimitPolicies.Expensive, _ =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: DeskRateLimitPolicies.Expensive,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = expensiveRequestsPerHour,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many expensive requests",
                Detail = $"The desk allows {expensiveRequestsPerHour} live runs, replays and training " +
                         "jobs per hour, so it never hammers PSE Edge or spends unbounded model credit. " +
                         "Nothing was persisted; wait for the window to reset and try again."
            },
            options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);
    };
});

// One expensive job at a time: a second concurrent run would double the upstream traffic and could
// overwrite a newer run for the same (date, horizon).
builder.Services.AddSingleton<DeskWorkGate>();

builder.Services.AddSingleton<QuantitativeFilterService>();
builder.Services.AddSingleton<SignalJsonParser>();
builder.Services.AddSingleton<IDailyQuotationParser, PseDailyQuotationParser>();

// One upstream cache for the whole process: the typed market-data client is transient, so a
// per-instance cache would never be reused. The daily run, the research endpoint and a manual
// backtest all request the same Yahoo chart URLs, and the cache serves those repeats (and
// concurrent duplicates) from a single download.
builder.Services.AddSingleton<MarketDataCache>();

// What each upstream actually answered, so a silent degradation — PSE Edge returning an empty
// disclosure page with HTTP 200 — is reported by GET /api/health/feeds instead of only existing
// as a log line. In memory only: a restart honestly reports "unknown" until the first fetch.
builder.Services.AddSingleton<IFeedHealthRegistry, FeedHealthRegistry>();

builder.Services.AddHttpClient<IOllamaSignalEngine, OllamaSignalEngine>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OllamaCloudOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, options.RequestTimeoutSeconds));
});

builder.Services.AddHttpClient<IMarketDataProvider, PseMarketDataProvider>((serviceProvider, client) =>
{
    var marketOptions = serviceProvider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(30);

    // A transparent, application-identifying User-Agent. The desk used to impersonate a desktop
    // Chrome build to get past upstream bot checks; that misrepresents the client and breaches
    // the feeds' terms of use, so it is configurable instead.
    if (!string.IsNullOrWhiteSpace(marketOptions.UserAgent))
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(marketOptions.UserAgent.Trim());
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Yahoo answers each chart with a few hundred kilobytes of JSON and PSE Edge sends HTML; the
    // default handler asks for the identity encoding, so every download would pay full size.
    AutomaticDecompression = DecompressionMethods.All,

    // Neither feed needs a cookie jar, and a shared one is a lock and an unbounded container for
    // no benefit.
    UseCookies = false,

    // Recycle idle connections so a DNS move (or a failover to the mirror host) is picked up
    // instead of pinning a stale socket for the life of the process.
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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

// Refuses non-loopback traffic in PersonalLocal mode (the scraped Yahoo Finance / PSE Edge feed
// may not be redistributed off this machine) and enforces the optional operator key on writes.
app.UseMiddleware<ComplianceGuardMiddleware>();

// Placed after the compliance guard on purpose: a refused request costs nothing upstream, so it
// must not consume the operator's hourly budget for the endpoints that actually fan out.
app.UseRateLimiter();

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

