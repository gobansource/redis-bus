using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace GobanSource.Bus.Redis;

/// <summary>
/// Generic hosted service that subscribes to a message sync bus and processes
/// messages of a specific type using the corresponding handler.
/// </summary>
/// <typeparam name="TMessage">The type of message to handle.</typeparam>
public class MessageSyncHostedService<TMessage> : IHostedService where TMessage : IMessage
{
    private readonly IRedisSyncBus<TMessage> _syncBus;
    private readonly IMessageHandler<TMessage> _handler;
    private readonly ILogger<MessageSyncHostedService<TMessage>> _logger;
    private readonly string _messageTypeName;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageSyncHostedService{TMessage}"/> class.
    /// </summary>
    /// <param name="syncBus">The message synchronization bus to subscribe to.</param>
    /// <param name="handler">The handler for processing messages.</param>
    /// <param name="logger">The logger.</param>
    public MessageSyncHostedService(
        IRedisSyncBus<TMessage> syncBus,
        IMessageHandler<TMessage> handler,
        ILogger<MessageSyncHostedService<TMessage>> logger)
    {
        _syncBus = syncBus;
        _handler = handler;
        _logger = logger;
        _messageTypeName = typeof(TMessage).Name;
    }

    /// <summary>
    /// Starts the service and subscribes to the message bus.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting {ServiceName} for message type {MessageType}",
            nameof(MessageSyncHostedService<TMessage>), _messageTypeName);

        await _syncBus.SubscribeAsync(
            async message => await MessageHandler(message),
            json => TypedDeserializer(json));

        _logger.LogInformation("{ServiceName} started for message type {MessageType}",
            nameof(MessageSyncHostedService<TMessage>), _messageTypeName);
    }

    /// <summary>
    /// Stops the service and unsubscribes from the message bus.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping {ServiceName} for message type {MessageType}",
            nameof(MessageSyncHostedService<TMessage>), _messageTypeName);

        await _syncBus.UnsubscribeAsync();

        _logger.LogInformation("{ServiceName} stopped for message type {MessageType}",
            nameof(MessageSyncHostedService<TMessage>), _messageTypeName);
    }

    /// <summary>
    /// Handles incoming messages from the sync bus.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task MessageHandler(IMessage message)
    {
        // Skip messages that are not of the expected type
        if (message is not TMessage typedMessage)
        {
            _logger.LogTrace("Skipping message of type {ReceivedType}, expected {ExpectedType}",
                message.GetType().Name, _messageTypeName);
            return;
        }

        _logger.LogDebug("Processing message of type {MessageType}", _messageTypeName);

        try
        {
            await _handler.HandleAsync(typedMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message of type {MessageType}", _messageTypeName);
        }
    }

    // Type-specific deserializer for TMessage
    private TMessage TypedDeserializer(string json)
    {
        try
        {
            _logger.LogDebug("Deserializing message as {MessageType}", _messageTypeName);

            // Deserialize directly to the expected message type
            var message = JsonSerializer.Deserialize<TMessage>(json);

            if (message == null)
            {
                _logger.LogWarning("Failed to deserialize message as {MessageType}", _messageTypeName);
                throw new InvalidOperationException($"Failed to deserialize message as {_messageTypeName}");
            }

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deserializing message as {MessageType}", _messageTypeName);
            throw;
        }
    }
}