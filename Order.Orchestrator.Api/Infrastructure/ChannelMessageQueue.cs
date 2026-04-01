using Shared.Contracts;
using System.Threading.Channels;

namespace Order.Orchestrator.Api.Infrastructure
{
    public class ChannelMessageQueue<T> : IMessagePublisher<T>, IMessageConsumer<T>
    {
        private readonly Channel<T> _channel;

        public ChannelMessageQueue()
        {
            // Unbounded for simplicity, but in production, bounded channels prevent OOM issues
            _channel = Channel.CreateUnbounded<T>();
        }

        public async ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
        {
            await _channel.Writer.WriteAsync(message, cancellationToken);
        }

        public IAsyncEnumerable<T> ConsumeAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
