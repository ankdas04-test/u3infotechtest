# E-Commerce Order Processing Platform

This project is a resilient, event-driven order processing system built in **.NET 10**. It demonstrates asynchronous message processing, distributed microservice communication, and robust failure handling using Clean Architecture principles.

## Architecture Overview

The solution consists of four primary microservices:
1. **Order Orchestrator (`Order.Orchestrator.Api` - Port 5002):** The core engine. It manages background queues, coordinates calls to downstream services, and handles resilience (retries, circuit breaking).
2. **OMS API (`Oms.Api` - Port 5001):** A stub Order Management System that holds pending orders.
3. **Inventory API (`Inventory.Service.Api` - Port 5003):** A stub service that manages inventory allocation and reservation, requiring idempotency.
4. **Payment Gateway (`Payment.Gateway.Api` - Port 5004):** A stub webhook receiver that triggers the queue when a payment is confirmed.

**Key Technologies Used:**
* `System.Threading.Channels` for high-performance, in-memory message queuing (swappable for RabbitMQ/SQS).
* `Microsoft.Extensions.Http.Resilience` (Polly v8) for exponential backoff and circuit breaking.
* `Microsoft.Extensions.Diagnostics.HealthChecks` for production-ready system monitoring.
* `xUnit` & `Moq` for unit and integration testing.


## Prerequisites

* [.NET 10 SDK](https://dotnet.microsoft.com/download) installed on your machine.


## Build & Test

Run the following commands from the root of the solution to build the projects and execute the test suite:

dotnet build  
dotnet test Order.Orchestrator.Tests

Test Suite Coverage Includes:
* Unit Tests: Validates synchronous non-blocking handlers, queue ingestion, and dead-lettering.
* Resilience Tests: Simulates BrokenCircuitException to prove the worker safely requeues messages instead of dropping them.
* Network Protocol Tests: Uses custom HttpMessageHandler mocks to verify the Idempotency-Key is strictly attached to outbound HTTP requests.
* Integration Stress Tests: Processes 100 queued messages concurrently in-memory, asserting a sub-5-second execution target to prove DI scope efficiency.
* Worker Recovery Tests: Proves the BackgroundService survives transient 500 Internal Server Errors from downstream APIs and successfully processes subsequent triggers.
	

## Running the Application Locally

* The ports for each service are pre-configured in their respective appsettings.json files, so no CLI port arguments are required.
* To run the solution locally, open 4 separate terminal windows, navigate to the solution root in each, and start the services:
	* Terminal 1: Order Management System (OMS)
	dotnet run --project Oms.Api
	* Terminal 2: Order Orchestrator
	dotnet run --project Order.Orchestrator.Api
	* Terminal 3: Inventory Service
	dotnet run --project Inventory.Service.Api
	* Terminal 4: Payment Gateway
	dotnet run --project Payment.Gateway.Api


## Testing with Postman 
* Health Check: GET http://localhost:5002/health
* OMS Ping: POST http://localhost:5002/orders/pending-ping (Set Body to raw JSON and paste the Flow 1 payload).
* Payment Webhook: POST http://localhost:5004/payment-confirmed (Set Body to raw JSON and paste the Flow 2 payload).


## Technical Notes 

For detailed architectural decisions, justifications for the swappable queue design, and testing strategies, please refer to the TECH_NOTES.md file included in this repository.