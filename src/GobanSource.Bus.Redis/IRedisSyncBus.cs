namespace GobanSource.Bus.Redis;

public interface IRedisSyncBus<TMessage> : IAsyncDisposable where TMessage : IMessage
{
    Task PublishAsync(TMessage message);
    Task SubscribeAsync(Func<TMessage, Task> handler, Func<string, TMessage> deserializer);
    Task UnsubscribeAsync();
    string GetInstanceId();
}