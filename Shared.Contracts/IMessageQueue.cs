namespace Shared.Contracts;

public interface IMessagePublisher<T>
{
    ValueTask PublishAsync(T message, CancellationToken cancellationToken = default);
}

public interface IMessageConsumer<T>
{
    IAsyncEnumerable<T> ConsumeAsync(CancellationToken cancellationToken = default);
}