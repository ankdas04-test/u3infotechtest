using Shared.Contracts;

namespace Order.Orchestrator.Api.Clients;

public class InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger) : IInventoryClient
{
    public async Task AllocateInventoryAsync(InventoryAllocationRequest request, CancellationToken ct)
    {
        logger.LogInformation("Allocating inventory for Order {OrderId}", request.OrderId);
        var response = await httpClient.PostAsJsonAsync("/inventory/allocate", request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReserveInventoryAsync(InventoryReserveRequest request, CancellationToken ct)
    {
        logger.LogInformation("Reserving inventory for Order {OrderId}", request.OrderId);
        // Using an Idempotency-Key header to ensure safely retrying POST operations
        var req = new HttpRequestMessage(HttpMethod.Post, "/inventory/reserve")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add("Idempotency-Key", request.OrderId);

        var response = await httpClient.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }
}