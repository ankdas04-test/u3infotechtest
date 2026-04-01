using Shared.Contracts;

namespace Order.Orchestrator.Api.Clients
{
    public interface IInventoryClient
    {
        Task AllocateInventoryAsync(InventoryAllocationRequest request, CancellationToken ct);
        Task ReserveInventoryAsync(InventoryReserveRequest request, CancellationToken ct);
    }
}
