using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
var app = builder.Build();

app.MapGet("/orders/pending", (ILogger<Program> logger) =>
{
    logger.LogInformation("OMS: Received request for pending orders.");

    // Return dummy data
    var pendingOrders = new List<PendingOrder>
    {
        new("ORD-100", "CUST-A", ["SKU-1", "SKU-2"], 150.00m),
        new("ORD-101", "CUST-B", ["SKU-3"], 45.50m)
    };

    return Results.Ok(pendingOrders);
});

app.Run();