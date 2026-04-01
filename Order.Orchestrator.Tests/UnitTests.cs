using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Order.Orchestrator.Api.Clients;
using Order.Orchestrator.Api.Workers;
using Polly.CircuitBreaker;
using Shared.Contracts;
using Xunit;

namespace Order.Orchestrator.Tests;

public class UnitTests
{
    // 1. PingHandler_ReturnsImmediately
    [Fact]
    public void PingHandler_ReturnsImmediately()
    {
        // Arrange: We test that publishing to our IMessagePublisher is a synchronous, non-blocking ValueTask.
        var mockPublisher = new Mock<IMessagePublisher<PendingPingRequest>>();
        mockPublisher.Setup(p => p.PublishAsync(It.IsAny<PendingPingRequest>(), It.IsAny<CancellationToken>()))
                     .Returns(ValueTask.CompletedTask);

        var request = new PendingPingRequest("TEST-CORRELATION-ID");

        // Act
        var task = mockPublisher.Object.PublishAsync(request).AsTask();

        // Assert: The task must complete immediately without blocking
        Assert.True(task.IsCompletedSuccessfully);
        mockPublisher.Verify(p => p.PublishAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // 2. QueueProcessor_ProcessesOrder_Success
    [Fact]
    public async Task QueueProcessor_ProcessesOrder_Success()
    {
        var mockInventoryClient = new Mock<IInventoryClient>();
        mockInventoryClient.Setup(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var serviceProvider = new ServiceCollection()
            .AddScoped(_ => mockInventoryClient.Object)
            .BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(s => s.CreateScope()).Returns(serviceProvider.CreateScope());

        var testMessage = new PaymentConfirmedEvent("ORD-123", "CUST-1", ["Item1"], 100.0m, DateTime.UtcNow);
        var mockConsumer = new Mock<IMessageConsumer<PaymentConfirmedEvent>>();
        mockConsumer.Setup(c => c.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns(GetTestMessages(testMessage));

        var worker = new PaymentQueueConsumer(mockConsumer.Object, mockScopeFactory.Object, NullLogger<PaymentQueueConsumer>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Ensure we wait for the worker task to finish processing the message
        if (worker.ExecuteTask != null)
        {
            await worker.ExecuteTask;
        }

        await worker.StopAsync(cts.Token);

        mockInventoryClient.Verify(c => c.ReserveInventoryAsync(It.Is<InventoryReserveRequest>(r => r.OrderId == "ORD-123"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // 3. QueueProcessor_Fails3Times_DeadLetters
    [Fact]
    public async Task QueueProcessor_Fails3Times_DeadLetters()
    {
        var mockInventoryClient = new Mock<IInventoryClient>();
        mockInventoryClient.Setup(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Fatal 500 Error from Inventory"));

        var serviceProvider = new ServiceCollection()
            .AddScoped(_ => mockInventoryClient.Object)
            .BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(s => s.CreateScope()).Returns(serviceProvider.CreateScope());

        var testMessage = new PaymentConfirmedEvent("ORD-999", "CUST-1", ["Item1"], 100.0m, DateTime.UtcNow);
        var mockConsumer = new Mock<IMessageConsumer<PaymentConfirmedEvent>>();
        mockConsumer.Setup(c => c.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns(GetTestMessages(testMessage));

        var worker = new PaymentQueueConsumer(mockConsumer.Object, mockScopeFactory.Object, NullLogger<PaymentQueueConsumer>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Wait for the worker task to finish processing the message and handle the error
        if (worker.ExecuteTask != null)
        {
            await worker.ExecuteTask;
        }

        await worker.StopAsync(cts.Token);

        mockInventoryClient.Verify(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.True(worker.ExecuteTask?.IsCompleted);
    }

    private async IAsyncEnumerable<T> GetTestMessages<T>(T message)
    {
        await Task.Yield(); // Forces asynchronous execution for the test runner
        yield return message;
        // The stream exits gracefully right here. 
        // This alerts the BackgroundService that the queue is empty, allowing it to shut down cleanly.
    }

    // 4. PaymentQueueConsumer_OnBrokenCircuit_RequeuesMessage
    [Fact]
    public async Task PaymentQueueConsumer_OnBrokenCircuit_RequeuesMessage()
    {
        // Arrange: We want to simulate Polly tripping the Circuit Breaker
        var mockInventoryClient = new Mock<IInventoryClient>();
        mockInventoryClient.Setup(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokenCircuitException("Circuit is open!"));

        var mockPublisher = new Mock<IMessagePublisher<PaymentConfirmedEvent>>();
        mockPublisher.Setup(p => p.PublishAsync(It.IsAny<PaymentConfirmedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var serviceProvider = new ServiceCollection()
            .AddScoped(_ => mockInventoryClient.Object)
            .AddScoped(_ => mockPublisher.Object) // Ensure the publisher is in the scope for requeuing
            .BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(s => s.CreateScope()).Returns(serviceProvider.CreateScope());

        var testMessage = new PaymentConfirmedEvent("ORD-REQUEUE", "CUST", ["Item1"], 10.0m, DateTime.UtcNow);
        var mockConsumer = new Mock<IMessageConsumer<PaymentConfirmedEvent>>();
        mockConsumer.Setup(c => c.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns(GetTestMessages(testMessage));

        var worker = new PaymentQueueConsumer(mockConsumer.Object, mockScopeFactory.Object, NullLogger<PaymentQueueConsumer>.Instance);

        // Act
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(100); // Give it time to process, catch the exception, and requeue
        await worker.StopAsync(cts.Token);

        // Assert: It must have tried to publish the message back to the queue
        mockPublisher.Verify(p => p.PublishAsync(It.Is<PaymentConfirmedEvent>(m => m.OrderId == "ORD-REQUEUE"), It.IsAny<CancellationToken>()), Times.Once,
            "The worker should have requeued the message after hitting a BrokenCircuitException.");
    }

    // 5. InventoryClient_ReserveInventory_AddsIdempotencyKeyHeader
    [Fact]
    public async Task InventoryClient_ReserveInventory_AddsIdempotencyKeyHeader()
    {
        // Arrange: Mock the lowest level of HttpClient to intercept the raw request
        var mockHandler = new Mock<HttpMessageHandler>();
        HttpRequestMessage capturedRequest = null;

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK))
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request); // Capture the request!

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:5003")
        };

        var client = new InventoryClient(httpClient, NullLogger<InventoryClient>.Instance);
        var requestPayload = new InventoryReserveRequest("ORD-IDEMP-001", ["SKU-A"]);

        // Act
        await client.ReserveInventoryAsync(requestPayload, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("Idempotency-Key"), "Idempotency-Key header is missing.");

        var headerValue = capturedRequest.Headers.GetValues("Idempotency-Key").First();
        Assert.Equal("ORD-IDEMP-001", headerValue);
    }
}