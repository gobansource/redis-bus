using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text;
using K4os.Compression.LZ4.Streams;
using System.Buffers;

namespace GobanSource.Bus.Redis;

/// <summary>
/// Implements a distributed message synchronization mechanism using Redis Pub/Sub.
/// This implementation provides true fan-out messaging where each message published
/// is delivered to all active subscribers across different application instances.
/// </summary>
/// <remarks>
/// Key Features:
/// - True fan-out: Each message is delivered to all active subscribers
/// - Instance filtering: Messages from the same instance are skipped
/// - No message persistence: Only active subscribers receive messages
/// - Pattern-based subscriptions: Uses channel patterns for message routing
/// 
/// Message Flow:
/// 1. Publisher sends message to a Redis channel 
/// 2. Redis broadcasts the message to all subscribers of that channel
/// 3. Each subscriber receives and processes the message if it's from a different instance
/// 
/// Limitations:
/// - No message persistence (offline subscribers miss messages)
/// - No message ordering guarantees across different channels
/// - No delivery confirmation
/// 
/// These limitations are acceptable for cache synchronization because:
/// - Cache instances should perform full sync on startup
/// - Order within a cache instance is maintained by channel pattern
/// - Missing messages are eventually corrected by subsequent operations
/// </remarks>
public class RedisSyncBus<TMessage> : IRedisSyncBus<TMessage> where TMessage : IMessage
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _channelPrefix;
    private readonly ILogger<RedisSyncBus<TMessage>> _logger;
    private readonly string _instanceId;
    private readonly bool _enableCompression;
    private ISubscriber _subscriber;
    private bool _isSubscribed;
    private string _messageTypeName;

    // LZ4 Frame format magic number: 0x184D2204
    private static readonly byte[] LZ4FrameMagicBytes = { 0x04, 0x22, 0x4D, 0x18 };

    /// <summary>
    /// Initializes a new instance of the RedisSyncBus.
    /// </summary>
    /// <param name="redis">Redis connection multiplexer for pub/sub operations</param>
    /// <param name="channelPrefix">Prefix for Redis channels to namespace messages</param>
    /// <param name="logger">Logger for operational monitoring</param>
    /// <param name="enableCompression">Enable LZ4 compression for messages</param>
    public RedisSyncBus(
        IConnectionMultiplexer redis,
        string channelPrefix,
        ILogger<RedisSyncBus<TMessage>> logger,
        bool enableCompression = false)
    {
        _redis = redis;
        _channelPrefix = channelPrefix;
        _logger = logger;
        _enableCompression = enableCompression;
        _instanceId = Guid.NewGuid().ToString();
        _subscriber = _redis.GetSubscriber();
        _messageTypeName = typeof(TMessage).Name;
    }

    /// <summary>
    /// Publishes a cache synchronization message to all other instances.
    /// </summary>
    /// <param name="message">The cache operation message to publish</param>
    /// <remarks>
    /// Each message includes a unique instance ID to prevent self-processing.
    /// Messages can be optionally compressed using LZ4 compression.
    /// </remarks>
    public async Task PublishAsync(TMessage message)
    {
        // Set the instance ID for the message to track its origin
        message.InstanceId = _instanceId;
        _logger.LogDebug("[RedisSyncBus][{InstanceId}] Set message InstanceId to {InstanceId}",
            _instanceId, _instanceId);

        var channel = $"{_channelPrefix}:{_messageTypeName}";
        _logger.LogDebug("[RedisSyncBus][{InstanceId}] Publishing to channel: {Channel}",
            _instanceId, channel);

        var serializedMessage = JsonSerializer.Serialize(message, message.GetType());
        _logger.LogDebug("[RedisSyncBus][{InstanceId}] Serialized message: {SerializedMessage}",
            _instanceId, serializedMessage);

        try
        {
            byte[] messageBytes;

            if (_enableCompression)
            {
                // Compress using LZ4 Frame format
                var jsonBytes = Encoding.UTF8.GetBytes(serializedMessage);
                var bufferWriter = new ArrayBufferWriter<byte>();
                LZ4Frame.Encode(jsonBytes.AsSpan(), bufferWriter);
                messageBytes = bufferWriter.WrittenMemory.ToArray();

                _logger.LogDebug("[RedisSyncBus][{InstanceId}] Compressed message: {OriginalSize} -> {CompressedSize} bytes",
                    _instanceId, jsonBytes.Length, messageBytes.Length);
            }
            else
            {
                // Use uncompressed JSON as UTF-8 bytes
                messageBytes = Encoding.UTF8.GetBytes(serializedMessage);
                _logger.LogDebug("[RedisSyncBus][{InstanceId}] Uncompressed message: {MessageSize} bytes",
                    _instanceId, messageBytes.Length);
            }

            _logger.LogDebug("[RedisSyncBus][{InstanceId}] Publishing message. Channel={Channel}, Compressed={Compressed}",
                _instanceId, channel, _enableCompression);
            await _subscriber.PublishAsync(RedisChannel.Pattern(channel), messageBytes);
            _logger.LogDebug("[RedisSyncBus][{InstanceId}] Successfully published message",
                _instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RedisSyncBus][{InstanceId}] Error publishing message to Redis",
                _instanceId);
            throw;
        }
    }

    /// <summary>
    /// Subscribes to cache synchronization messages from other instances.
    /// </summary>
    /// <param name="handler">Callback to process received cache messages</param>
    /// <remarks>
    /// Messages from the same instance (matching InstanceId) are skipped.
    /// Supports both compressed (LZ4) and uncompressed messages for backward compatibility.
    /// Pattern: {prefix}:{messageTypeName}
    /// </remarks>
    public async Task SubscribeAsync(Func<TMessage, Task> handler, Func<string, TMessage> deserializer)
    {
        if (_isSubscribed)
        {
            throw new InvalidOperationException($"[RedisSyncBus][{_instanceId}] Already subscribed");
        }

        var channel = $"{_channelPrefix}:{_messageTypeName}";
        _logger.LogDebug("[RedisSyncBus][{InstanceId}] Subscribing to channel pattern: {Channel}",
            _instanceId, channel);

        try
        {
            _logger.LogDebug("[RedisSyncBus][{InstanceId}] Subscribing to channel pattern: {Channel}",
                _instanceId, channel);
            await _subscriber.SubscribeAsync(RedisChannel.Pattern(channel), async (channel, message) =>
            {
                _logger.LogDebug("[RedisSyncBus][{InstanceId}] Received message on channel: {Channel}",
                    _instanceId, channel);

                try
                {
                    string messageString;
                    bool wasCompressed = false;

                    if (message.HasValue)
                    {
                        byte[] messageBytes = message!;

                        // Check for LZ4 Frame format magic number
                        if (messageBytes.Length >= LZ4FrameMagicBytes.Length &&
                            messageBytes.Take(LZ4FrameMagicBytes.Length).SequenceEqual(LZ4FrameMagicBytes))
                        {
                            // Decompress using LZ4 Frame format
                            var bufferWriter = new ArrayBufferWriter<byte>();
                            LZ4Frame.Decode(messageBytes.AsSpan(), bufferWriter);
                            var decompressedBytes = bufferWriter.WrittenMemory.ToArray();
                            messageString = Encoding.UTF8.GetString(decompressedBytes);
                            wasCompressed = true;
                            _logger.LogDebug("[RedisSyncBus][{InstanceId}] Decompressed message: {OriginalSize} -> {DecompressedSize} bytes",
                                _instanceId, messageBytes.Length, decompressedBytes.Length);
                        }
                        else
                        {
                            // Treat as uncompressed UTF-8 string
                            messageString = Encoding.UTF8.GetString(messageBytes);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Message is null");
                    }

                    _logger.LogDebug("[RedisSyncBus][{InstanceId}] Message content (compressed={Compressed}): {Message}",
                        _instanceId, wasCompressed, messageString);

                    TMessage messageObj = deserializer(messageString);

                    _logger.LogDebug("[RedisSyncBus][{InstanceId}] Deserialized message: InstanceId={InstanceId}",
                        _instanceId, messageObj.InstanceId);

                    // Skip messages from this instance
                    if (messageObj.InstanceId == _instanceId)
                    {
                        _logger.LogDebug("[RedisSyncBus][{InstanceId}] Skipping message from same instance",
                            _instanceId);
                        return;
                    }

                    // Process message if it's from a different instance
                    _logger.LogDebug("[RedisSyncBus][{InstanceId}] Processing message",
                        _instanceId);
                    await handler(messageObj);
                    _logger.LogDebug("[RedisSyncBus][{InstanceId}] Successfully processed message (compressed={Compressed})",
                        _instanceId, wasCompressed);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RedisSyncBus][{InstanceId}] Error processing message",
                        _instanceId);
                }
            });
            _isSubscribed = true;
            _logger.LogDebug("[RedisSyncBus][{InstanceId}] Successfully subscribed to channel pattern: {Channel}",
                _instanceId, channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RedisSyncBus][{InstanceId}] Error subscribing to Redis channel",
                _instanceId);
            throw;
        }
    }

    /// <summary>
    /// Unsubscribes from cache synchronization messages.
    /// </summary>
    /// <remarks>
    /// Safely handles multiple calls and cleans up resources.
    /// After unsubscribing, the instance will no longer receive cache updates.
    /// </remarks>
    public async Task UnsubscribeAsync()
    {
        if (!_isSubscribed)
        {
            return;
        }

        try
        {
            var channel = $"{_channelPrefix}:{_messageTypeName}";
            await _subscriber.UnsubscribeAsync(RedisChannel.Pattern(channel));
            _isSubscribed = false;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RedisSyncBus][{InstanceId}] Error unsubscribing from Redis channel",
                _instanceId);
            throw;
        }
    }

    /// <summary>
    /// Disposes of resources and ensures unsubscription.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isSubscribed)
        {
            await UnsubscribeAsync();
        }
    }

    /// <summary>
    /// Gets the unique identifier for this provider instance.
    /// This is used to prevent processing messages sent by this instance.
    /// </summary>
    /// <remarks>
    /// This method is primarily intended for testing purposes.
    /// </remarks>
    public string GetInstanceId() => _instanceId;
}