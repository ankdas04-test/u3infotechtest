using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure structured JSON logging
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// 2. Read the Orchestrator URL from appsettings.json, falling back to 5002 if missing
var orchestratorUrl = builder.Configuration["Orchestrator:BaseUrl"] ?? "http://localhost:5002";

// 3. Register the HttpClient used to forward events to the Orchestrator
builder.Services.AddHttpClient("Orchestrator", c => c.BaseAddress = new Uri(orchestratorUrl));

var app = builder.Build();

// 4. Map the webhook endpoint
app.MapPost("/payment-confirmed", async (PaymentConfirmedEvent payload, IHttpClientFactory clientFactory, ILogger<Program> logger) =>
{
    logger.LogInformation("GATEWAY: Payment confirmed for OrderId: {OrderId}. Forwarding to Orchestrator queue...", payload.OrderId);

    // Because the queue is in-memory on the Orchestrator for local dev, 
    // we use HTTP to inject the message into the Orchestrator's memory space.
    // If using a real RabbitMQ/SQS setup, this would publish directly to the broker instead.
    var client = clientFactory.CreateClient("Orchestrator");
    var response = await client.PostAsJsonAsync("/internal/queue/payment-confirmed", payload);

    if (response.IsSuccessStatusCode)
    {
        return Results.Accepted();
    }

    logger.LogError("GATEWAY: Failed to forward payment for OrderId: {OrderId}. Orchestrator returned status code: {StatusCode}", payload.OrderId, response.StatusCode);
    return Results.StatusCode(500);
});

// 5. Run the application (respecting appsettings.json or --urls CLI flags)
app.Run();