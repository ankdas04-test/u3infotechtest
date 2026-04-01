using Shared.Contracts;
using Order.Orchestrator.Api.Clients;

namespace Order.Orchestrator.Api.Workers;

// Reads from a specific Channel acting as a trigger buffer so the Ping endpoint is non-blocking
public class OmsSyncBackgroundWorker(
    IMessageConsumer<PendingPingRequest> pingConsumer,
    IServiceScopeFactory scopeFactory,
    ILogger<OmsSyncBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ping in pingConsumer.ConsumeAsync(stoppingToken))
        {
            logger.LogInformation("Processing OMS Ping trigger. CorrelationId: {CorrelationId}", ping.CorrelationId);

            try
            {
                // Scoped service resolution because BackgroundServices are Singletons
                using var scope = scopeFactory.CreateScope();
                var omsClient = scope.ServiceProvider.GetRequiredService<IOmsClient>();
                var inventoryClient = scope.ServiceProvider.GetRequiredService<IInventoryClient>();

                var orders = await omsClient.GetPendingOrdersAsync(stoppingToken);
                foreach (var order in orders)
                {
                    await inventoryClient.AllocateInventoryAsync(new InventoryAllocationRequest(order.OrderId, order.Items), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync pending orders. CorrelationId: {CorrelationId}", ping.CorrelationId);
            }
        }
    }
}