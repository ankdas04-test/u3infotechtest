using Shared.Contracts;

namespace Order.Orchestrator.Api.Clients
{
    public interface IOmsClient
    {
        Task<IEnumerable<PendingOrder>> GetPendingOrdersAsync(CancellationToken ct);
    }
}
