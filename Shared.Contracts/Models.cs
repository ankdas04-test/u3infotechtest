namespace Shared.Contracts;

// Flow 1
public record PendingPingRequest(string CorrelationId);
public record PendingOrder(string OrderId, string CustomerId, string[] Items, decimal Total);
public record InventoryAllocationRequest(string OrderId, string[] Items);

// Flow 2
public record PaymentConfirmedEvent(string OrderId, string CustomerId, string[] Items, decimal Total, DateTime PaidAt);
public record InventoryReserveRequest(string OrderId, string[] Items);

// Dead Letter Model
public record DeadLetterMessage(object Payload, string Reason, int RetryCount, DateTime FailedAt);
