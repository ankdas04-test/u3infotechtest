### `TECH_NOTES.md`

# Architectural Decisions & Tech Notes

## Message Queue (`IOrderQueue` Implementation)
**Choice:** `System.Threading.Channels` via generic `IMessagePublisher<T>` and `IMessageConsumer<T>` interfaces.
**Justification:** The assignment allowed for local dev implementations. `Channel<T>` provides a highly performant, native in-memory queue without requiring Docker or external dependencies. 
**Swappability:** By creating standard publisher/consumer interfaces, migrating to `RabbitMQ.Client` or `AWSSDK.SQS` requires zero changes to the background workers; we merely swap the DI registration in `Program.cs`. Because Channels are in-memory per process, the Payment Gateway uses an internal HTTP route on the Orchestrator to inject messages into the Orchestrator's memory space. In a true distributed setup, the Gateway would publish directly to the broker.

## Flow 1: Non-Blocking Background Work
**Decision:** `POST /orders/pending-ping` pushes a trigger message to a local `Channel` rather than spawning a `Task.Run` or blocking the HTTP thread.
**Justification:** `Task.Run` is dangerous in ASP.NET Core as unawaited background threads are terminated abruptly during AppPool recycles. A `BackgroundService` consuming a `Channel` respects the `CancellationToken` on app shutdown, ensuring graceful termination.

## Typed HTTP Clients & Health Checks
**Decision:** External service calls are encapsulated in typed HTTP clients registered via `AddHttpClient<TInterface, TImplementation>()` and monitored via a custom `/health` endpoint.
**Justification:** This abstracts raw HTTP logic away from the workers and centralizes URL configuration and Polly resilience.
* **Dependency Nuance (`Degraded`):** If the external Inventory Service times out, the health check returns `Degraded` rather than completely `Unhealthy`. This is an intentional decoupling strategy: the Orchestrator can still successfully accept OMS Pings and Payment events (buffering them safely in the queue) even if downstream processing is temporarily paused by the Circuit Breaker.
* **Queue Criticality (`Unhealthy`):** If the internal `MessageQueue` health check fails, the system reports as `Unhealthy`. If the Orchestrator cannot read/write to its own queue, it cannot fulfill its primary purpose.

## Resilience & Idempotency
**Decision:** Utilized `Microsoft.Extensions.Http.Resilience` with a configured exponential backoff (2s, 4s, 8s) and Circuit Breaker. Added an `Idempotency-Key` header to outbound POST requests.
**Justification:** Retries without idempotency cause duplicate inventory reservations. Passing the `OrderId` as an idempotency key ensures the Inventory Service can safely drop duplicate requests resulting from network timeouts. If the Inventory service dies entirely, the Circuit Breaker trips, pausing the queue consumption so we don't spam a dead service.

## Testing Strategy & Stress Target
**Decision:** The test suite utilizes `WebApplicationFactory` and `Moq.Protected` to intercept raw `HttpMessageHandler` traffic, alongside simulated Polly `BrokenCircuitException` states. The stress target is 100 messages in under 5 seconds.
**Justification:** Standard "happy path" tests do not prove system reliability. To ensure this architecture is truly production-grade, the test suite explicitly validates:
1. **Idempotency Enforcement:** We assert that the `InventoryClient` natively attaches the `Idempotency-Key` header, proving we aren't relying on downstream services to magically handle duplicate HTTP POSTs.
2. **Circuit Breaker Requeuing:** If the Circuit Breaker trips, the system throws a `BrokenCircuitException`. The tests prove that our `BackgroundService` catches this specific exception and publishes the message *back* to the queue, ensuring zero data loss during external outages.
3. **Background Worker Survival:** The integration tests force the simulated OMS to throw a fatal exception, proving that the Orchestrator logs the error, drops the current cycle, but stays alive to successfully process the next incoming trigger.
4. **Stress Target:** A 5-second target for 100 messages proves our in-memory Channels and DI scope resolutions are highly efficient and completely free of artificial deadlocks.

## AI Agent Usage Disclosure
* **Design & Architecture:** AI was used to map the requested sequence diagrams into a .NET 8+ idiom, specifically suggesting the `System.Threading.Channels` abstraction and the `Microsoft.Extensions.Http.Resilience` package.
* **Implementation:** Used AI to generate boilerplate C# `record` types, DI container setup, health check configurations, and Polly policies. 
* **Testing:** AI assisted in writing the XUnit boilerplate, resolving deadlocks in asynchronous queue stream simulations, and creating custom `HttpMessageHandler` interceptors.
* **Documentation:** AI drafted the initial structure of this `TECH_NOTES.md` and the `README.md` curl commands.
```
