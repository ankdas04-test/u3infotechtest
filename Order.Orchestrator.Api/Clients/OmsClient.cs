using Shared.Contracts;

namespace Order.Orchestrator.Api.Clients;

public class OmsClient(HttpClient httpClient, ILogger<OmsClient> logger) : IOmsClient
{
    public async Task<IEnumerable<PendingOrder>> GetPendingOrdersAsync(CancellationToken ct)
    {
        logger.LogInformation("Fetching pending orders from OMS...");

        // Calls the GET /orders/pending endpoint on the Oms.Api
        var orders = await httpClient.GetFromJsonAsync<IEnumerable<PendingOrder>>("/orders/pending", ct);

        // Return the parsed orders or an empty array if null
        return orders ?? [];
    }
}