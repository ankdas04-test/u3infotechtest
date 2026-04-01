using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Order.Orchestrator.Api.Infrastructure;

// Using C# 12+ primary constructor
public class HealthCheckQueue(ILogger<HealthCheckQueue> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // For an in-memory Channel, it's generally always healthy.
            // If you swap to RabbitMQ/SQS, you would inject your IConnection here and ping the broker.
            bool isQueueConnected = true;

            if (isQueueConnected)
            {
                return Task.FromResult(HealthCheckResult.Healthy("Message queue is fully operational."));
            }

            return Task.FromResult(HealthCheckResult.Unhealthy("Message queue connection failed."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check failed for the message queue.");
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Queue exception occurred.", ex));
        }
    }
}