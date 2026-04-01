using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
var app = builder.Build();

app.MapPost("/inventory/allocate", (InventoryAllocationRequest request, ILogger<Program> logger) =>
{
    logger.LogInformation("INVENTORY: Allocated items for Flow 1. OrderId: {OrderId}", request.OrderId);
    return Results.Ok();
});

app.MapPost("/inventory/reserve", (InventoryReserveRequest request, HttpRequest httpReq, ILogger<Program> logger) =>
{
    var idempotencyKey = httpReq.Headers["Idempotency-Key"].FirstOrDefault();
    logger.LogInformation("INVENTORY: Reserved items for Flow 2. OrderId: {OrderId}, IdempotencyKey: {Key}", request.OrderId, idempotencyKey);
    return Results.Ok();
});

// Health check endpoint required by Orchestrator
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

app.Run();