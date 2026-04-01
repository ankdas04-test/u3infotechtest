using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Order.Orchestrator.Api.Clients;
using Shared.Contracts;
using Xunit;

namespace Order.Orchestrator.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // 1. Flow1_OmsPing_InventoryReceives
    [Fact]
    public async Task Flow1_OmsPing_InventoryReceives()
    {
        // Arrange
        var mockOmsClient = new Mock<IOmsClient>();
        mockOmsClient.Setup(c => c.GetPendingOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PendingOrder("ORD-FLOW1", "C1", ["SKU-1"], 50m)]);

        var mockInventoryClient = new Mock<IInventoryClient>();
        var tcs = new TaskCompletionSource<bool>();

        mockInventoryClient.Setup(c => c.AllocateInventoryAsync(It.IsAny<InventoryAllocationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.SetResult(true)); // Signal when the background task successfully calls this

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => mockOmsClient.Object);
                services.AddScoped(_ => mockInventoryClient.Object);
            });
        }).CreateClient();

        // Act
        var pingRequest = new PendingPingRequest("PING-TEST-01");
        var response = await client.PostAsJsonAsync("/orders/pending-ping", pingRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); // Endpoint must return 202 immediately

        // Wait up to 3 seconds for the background worker to process the queue and hit the mock
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Equal(tcs.Task, completedTask);

        mockInventoryClient.Verify(c => c.AllocateInventoryAsync(It.Is<InventoryAllocationRequest>(r => r.OrderId == "ORD-FLOW1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // 2. Flow2_PaymentConfirmed_InventoryReceives
    [Fact]
    public async Task Flow2_PaymentConfirmed_InventoryReceives()
    {
        // Arrange
        var mockInventoryClient = new Mock<IInventoryClient>();
        var tcs = new TaskCompletionSource<bool>();

        mockInventoryClient.Setup(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.SetResult(true));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => mockInventoryClient.Object);
            });
        }).CreateClient();

        // Act
        var eventPayload = new PaymentConfirmedEvent("ORD-FLOW2", "C2", ["SKU-2"], 100m, DateTime.UtcNow);
        // We hit the internal queue ingestion endpoint that the Gateway uses
        var response = await client.PostAsJsonAsync("/internal/queue/payment-confirmed", eventPayload);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Equal(tcs.Task, completedTask);

        mockInventoryClient.Verify(c => c.ReserveInventoryAsync(It.Is<InventoryReserveRequest>(r => r.OrderId == "ORD-FLOW2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // 3. Queue_StressTest_100MessagesProcessedWithinTargetTime
    [Fact]
    public async Task Queue_StressTest_100MessagesProcessedWithinTargetTime()
    {
        // Arrange
        var messageCount = 100;
        var processedCount = 0;
        var tcs = new TaskCompletionSource<bool>();
        var mockInventoryClient = new Mock<IInventoryClient>();

        mockInventoryClient.Setup(c => c.ReserveInventoryAsync(It.IsAny<InventoryReserveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() =>
            {
                // Thread-safe increment since the BackgroundService might process concurrently
                if (Interlocked.Increment(ref processedCount) == messageCount)
                {
                    tcs.SetResult(true);
                }
            });

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => mockInventoryClient.Object);
            });
        }).CreateClient();

        var stopwatch = Stopwatch.StartNew();

        // Act - Enqueue 100 messages rapidly
        var tasks = new List<Task>();
        for (int i = 0; i < messageCount; i++)
        {
            var eventPayload = new PaymentConfirmedEvent($"STRESS-ORD-{i}", "CUST", ["SKU"], 10m, DateTime.UtcNow);
            tasks.Add(client.PostAsJsonAsync("/internal/queue/payment-confirmed", eventPayload));
        }
        await Task.WhenAll(tasks); // Wait for all HTTP POSTs to finish

        // Assert - Target time is 5000ms (5 seconds) for local in-memory processing
        var targetTimeMs = 5000;
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(targetTimeMs));

        stopwatch.Stop();

        // Validate we didn't hit the timeout
        Assert.Equal(tcs.Task, completedTask);
        Assert.Equal(messageCount, processedCount);

        // Validate processing speed
        Assert.True(stopwatch.ElapsedMilliseconds < targetTimeMs, $"Stress test took {stopwatch.ElapsedMilliseconds}ms, which exceeded the {targetTimeMs}ms target.");
    }

    // 4. Flow1_OmsSync_FailsGracefully_AndProcessesNextPing
    [Fact]
    public async Task Flow1_OmsSync_FailsGracefully_AndProcessesNextPing()
    {
        // Arrange
        var mockOmsClient = new Mock<IOmsClient>();

        // Setup the mock to THROW an error on the FIRST call, but SUCCEED on the SECOND call
        mockOmsClient.SetupSequence(c => c.GetPendingOrdersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("OMS is temporarily down!"))
            .ReturnsAsync([new PendingOrder("ORD-RECOVERY", "C1", ["SKU-1"], 50m)]);

        var mockInventoryClient = new Mock<IInventoryClient>();
        var tcs = new TaskCompletionSource<bool>();

        mockInventoryClient.Setup(c => c.AllocateInventoryAsync(It.IsAny<InventoryAllocationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.SetResult(true));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => mockOmsClient.Object);
                services.AddScoped(_ => mockInventoryClient.Object);
            });
        }).CreateClient();

        // Act - Ping 1 (Will trigger the exception)
        await client.PostAsJsonAsync("/orders/pending-ping", new PendingPingRequest("PING-FAIL"));

        // Wait a tiny bit to ensure the background worker picked up the first failing ping
        await Task.Delay(100);

        // Act - Ping 2 (Will succeed)
        await client.PostAsJsonAsync("/orders/pending-ping", new PendingPingRequest("PING-SUCCESS"));

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));

        // Assert
        Assert.Equal(tcs.Task, completedTask); // Ensure the success task triggered and we didn't timeout

        // Verify that even though the first sync failed, the BackgroundService stayed alive to process the second one
        mockInventoryClient.Verify(c => c.AllocateInventoryAsync(It.Is<InventoryAllocationRequest>(r => r.OrderId == "ORD-RECOVERY"), It.IsAny<CancellationToken>()), Times.Once);

        // Verify OMS was called exactly twice
        mockOmsClient.Verify(c => c.GetPendingOrdersAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}