using System.Threading;

namespace GobanSource.Bus.Redis;

public interface IRedisSyncBus<TMessage> : IAsyncDisposable where TMessage : IMessage
{
    Task PublishAsync(TMessage message, CancellationToken cancellationToken = default);
    Task SubscribeAsync(Func<TMessage, Task> handler, Func<string, TMessage> deserializer, CancellationToken cancellationToken = default);
    Task UnsubscribeAsync(CancellationToken cancellationToken = default);
    string GetInstanceId();
}