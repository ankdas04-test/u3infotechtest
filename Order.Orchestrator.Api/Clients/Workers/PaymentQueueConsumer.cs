using Shared.Contracts;
using Order.Orchestrator.Api.Clients;
using Polly.CircuitBreaker;

namespace Order.Orchestrator.Api.Workers;

public class PaymentQueueConsumer(
    IMessageConsumer<PaymentConfirmedEvent> messageConsumer,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentQueueConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting PaymentQueueConsumer...");

        await foreach (var message in messageConsumer.ConsumeAsync(stoppingToken))
        {
            await ProcessMessageAsync(message, stoppingToken);
        }
    }

    private async Task ProcessMessageAsync(PaymentConfirmedEvent message, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var inventoryClient = scope.ServiceProvider.GetRequiredService<IInventoryClient>();

        try
        {
            logger.LogInformation("Dequeued PaymentConfirmedEvent for OrderId: {OrderId}", message.OrderId);
            await inventoryClient.ReserveInventoryAsync(new InventoryReserveRequest(message.OrderId, message.Items), stoppingToken);
            logger.LogInformation("Successfully reserved inventory for OrderId: {OrderId}", message.OrderId);
        }
        catch (BrokenCircuitException)
        {
            logger.LogWarning("Circuit broken. Pausing consumption for OrderId: {OrderId}. Message will be retried later.", message.OrderId);
            // In a real message broker, we would NACK the message to leave it in the queue.
            // For this Channel demo, we'll delay and re-queue.
            try
            {
                await Task.Delay(5000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation here so we can still attempt to re-queue the message
                // when the host is stopping. The unit test expects the message to be re-published.
            }

            var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher<PaymentConfirmedEvent>>();
            await publisher.PublishAsync(message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process OrderId: {OrderId} after retries. Moving to Dead Letter.", message.OrderId);
            await DeadLetterAsync(message, ex);
        }
    }

    private Task DeadLetterAsync(PaymentConfirmedEvent message, Exception ex)
    {
        // Implementation of dead-letter storage (e.g., DB or specialized queue)
        var dlqMessage = new DeadLetterMessage(message, ex.Message, 3, DateTime.UtcNow);
        logger.LogError("DEAD LETTER: {@DeadLetterMessage}", dlqMessage);
        return Task.CompletedTask;
    }
}