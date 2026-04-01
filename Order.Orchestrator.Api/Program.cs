using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Order.Orchestrator.Api.Clients;
using Order.Orchestrator.Api.Infrastructure;
using Order.Orchestrator.Api.Workers;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Shared.Contracts;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 1. Structured Logging Setup (Using standard JSON console out of the box for .NET 8+)
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// 2. Register Swappable Message Queues (Using Singleton Channel for local memory)
builder.Services.AddSingleton<ChannelMessageQueue<PendingPingRequest>>();
builder.Services.AddSingleton<IMessagePublisher<PendingPingRequest>>(sp => sp.GetRequiredService<ChannelMessageQueue<PendingPingRequest>>());
builder.Services.AddSingleton<IMessageConsumer<PendingPingRequest>>(sp => sp.GetRequiredService<ChannelMessageQueue<PendingPingRequest>>());

builder.Services.AddSingleton<ChannelMessageQueue<PaymentConfirmedEvent>>();
builder.Services.AddSingleton<IMessagePublisher<PaymentConfirmedEvent>>(sp => sp.GetRequiredService<ChannelMessageQueue<PaymentConfirmedEvent>>());
builder.Services.AddSingleton<IMessageConsumer<PaymentConfirmedEvent>>(sp => sp.GetRequiredService<ChannelMessageQueue<PaymentConfirmedEvent>>());

// 3. Register Background Services
builder.Services.AddHostedService<OmsSyncBackgroundWorker>();
builder.Services.AddHostedService<PaymentQueueConsumer>();

// 4. Configure HTTP Clients with Polly Resilience
var inventoryUri = builder.Configuration["InventoryService:BaseUrl"] ?? "http://localhost:5003";
var omsUri = builder.Configuration["OmsService:BaseUrl"] ?? "http://localhost:5001";

builder.Services.AddHttpClient<IInventoryClient, InventoryClient>(c =>
{
    c.BaseAddress = new Uri(inventoryUri);
    c.Timeout = TimeSpan.FromSeconds(30);
})
    .AddResilienceHandler("inventory-resilience", pipelineBuilder =>
    {
        // 3 retries with exponential backoff (2s, 4s, 8s)
        pipelineBuilder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(r => !r.IsSuccessStatusCode),
            Delay = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential
        });

        // Circuit Breaker: Trip after 5 consecutive failures, break for 30s
        pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(r => !r.IsSuccessStatusCode),
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30)
        });
    });

builder.Services.AddHttpClient<IOmsClient, OmsClient>(c => c.BaseAddress = new Uri(omsUri));

// 5. Health Checks
builder.Services.AddHealthChecks()
    .AddUrlGroup(new Uri($"{inventoryUri}/health"), name: "InventoryService", failureStatus: HealthStatus.Degraded, timeout: TimeSpan.FromSeconds(3))
    .AddCheck<HealthCheckQueue>(name: "MessageQueue", failureStatus: HealthStatus.Unhealthy);

var app = builder.Build();

// Map Health Check Endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            Status = report.Status.ToString(),
            Duration = report.TotalDuration.ToString(),
            Dependencies = report.Entries.Select(e => new
            {
                Component = e.Key,
                Status = e.Value.Status.ToString(),
                Description = e.Value.Description,
                Exception = e.Value.Exception?.Message
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
});

app.MapPost("/orders/pending-ping", async (PendingPingRequest request, IMessagePublisher<PendingPingRequest> publisher, ILogger<Program> logger) =>
{
    logger.LogInformation("Received Ping from OMS. Enqueueing task.");
    // Return 202 immediately, offload work to background channel
    await publisher.PublishAsync(request);
    return Results.Accepted();
});

app.MapPost("/internal/queue/payment-confirmed", async (PaymentConfirmedEvent request, IMessagePublisher<PaymentConfirmedEvent> publisher) =>
{
    await publisher.PublishAsync(request);
    return Results.Accepted();
});

app.Run();