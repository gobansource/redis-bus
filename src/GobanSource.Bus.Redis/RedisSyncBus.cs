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
/// 1. Publisher sends message to a Redis channel (format: {prefix}:{appId}:{cacheInstanceId})
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
    private readonly string _appId;
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
    /// <param name="appId">Unique identifier for the application instance group</param>
    /// <param name="channelPrefix">Prefix for Redis channels to namespace messages</param>
    /// <param name="logger">Logger for operational monitoring</param>
    /// <param name="enableCompression">Enable LZ4 compression for messages</param>
    public RedisSyncBus(
        IConnectionMultiplexer redis,
        string appId,
        string channelPrefix,
        ILogger<RedisSyncBus<TMessage>> logger,
        bool enableCompression = false)
    {
        _redis = redis;
        _appId = appId;
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
    /// The message is published to a Redis channel specific to the app and cache instance.
    /// Each message includes a unique instance ID to prevent self-processing.
    /// Channel pattern: {prefix}:{appId}:{cacheInstanceId}
    /// Messages can be optionally compressed using LZ4 compression.
    /// </remarks>
    public async Task PublishAsync(TMessage message)
    {
        message.AppId = _appId;
        // Set the instance ID for the message to track its origin
        message.InstanceId = _instanceId;
        Console.WriteLine($"[DEBUG] Set message InstanceId to {_instanceId}");

        var channel = $"{_channelPrefix}:{message.AppId}:{_messageTypeName}";
        Console.WriteLine($"[DEBUG] Publishing to channel: {channel}");

        var serializedMessage = JsonSerializer.Serialize(message, message.GetType());
        Console.WriteLine($"[DEBUG] Serialized message: {serializedMessage}");

        try
        {
            byte[] messageBytes;

            if (_enableCompression)
            {
                // Compress using LZ4 Frame format
                var jsonBytes = Encoding.UTF8.GetBytes(serializedMessage);
                var bufferWriter = new ArrayBufferWriter<byte>();
                var actualLength = LZ4Frame.Encode(jsonBytes.AsSpan(), bufferWriter);
                messageBytes = bufferWriter.WrittenMemory.ToArray();

                Console.WriteLine($"[DEBUG] {actualLength} Compressed message: {jsonBytes.Length} -> {messageBytes.Length} bytes ({(double)messageBytes.Length / jsonBytes.Length:P1} ratio)");
                _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Compressed message: {OriginalSize} -> {CompressedSize} bytes",
                    _appId, _instanceId, jsonBytes.Length, messageBytes.Length);
            }
            else
            {
                // Use uncompressed JSON as UTF-8 bytes
                messageBytes = Encoding.UTF8.GetBytes(serializedMessage);
                Console.WriteLine($"[DEBUG] Uncompressed message: {messageBytes.Length} bytes");
            }

            _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Publishing message. Channel={Channel}, Compressed={Compressed}",
                _appId, _instanceId, channel, _enableCompression);
            await _subscriber.PublishAsync(RedisChannel.Pattern(channel), messageBytes);
            Console.WriteLine($"[DEBUG] Successfully published message");
            _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Successfully published message",
                _appId, _instanceId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error publishing message: {ex.Message}");
            _logger.LogError(ex, "[RedisSyncBus][{AppId}][{InstanceId}] Error publishing message to Redis",
                _appId, _instanceId);
            throw;
        }
    }

    /// <summary>
    /// Subscribes to cache synchronization messages from other instances.
    /// </summary>
    /// <param name="handler">Callback to process received cache messages</param>
    /// <remarks>
    /// Subscribes to a pattern matching all channels for the current app ID.
    /// Messages from the same instance (matching InstanceId) are skipped.
    /// Only processes messages matching the current AppId.
    /// Supports both compressed (LZ4) and uncompressed messages for backward compatibility.
    /// Pattern: {prefix}:{appId}:*
    /// </remarks>
    public async Task SubscribeAsync(Func<TMessage, Task> handler, Func<string, TMessage> deserializer)
    {
        if (_isSubscribed)
        {
            Console.WriteLine($"[DEBUG] Already subscribed to channel pattern");
            throw new InvalidOperationException($"[RedisSyncBus][{_appId}][{_instanceId}] Already subscribed");
        }

        var channel = $"{_channelPrefix}:{_appId}:{_messageTypeName}";
        Console.WriteLine($"[DEBUG] Subscribing to channel pattern: {channel}");

        try
        {
            _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Subscribing to channel pattern: {Channel}",
                _appId, _instanceId, channel);
            await _subscriber.SubscribeAsync(RedisChannel.Pattern(channel), async (channel, message) =>
            {
                Console.WriteLine($"[DEBUG] Received message on channel: {channel}");

                try
                {
                    string messageString;
                    bool wasCompressed = false;

                    if (message.HasValue)
                    {
                        byte[] messageBytes = message;

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
                            Console.WriteLine($"[DEBUG] Decompressed message: {messageBytes.Length} -> {decompressedBytes.Length} bytes");
                        }
                        else
                        {
                            // Treat as uncompressed UTF-8 string
                            messageString = Encoding.UTF8.GetString(messageBytes);
                        }
                    }
                    else
                    {
                        messageString = message.ToString() ?? string.Empty;
                    }

                    Console.WriteLine($"[DEBUG] Message content (compressed={wasCompressed}): {messageString}");

                    var messageObj = deserializer(messageString);
                    if (messageObj == null)
                    {
                        Console.WriteLine($"[DEBUG] Failed to deserialize message");
                        return;
                    }
                    Console.WriteLine($"[DEBUG] Deserialized message: AppId={messageObj.AppId}, InstanceId={messageObj.InstanceId}");

                    // Skip messages from this instance
                    if (messageObj.InstanceId == _instanceId)
                    {
                        Console.WriteLine($"[DEBUG] Skipping message from same instance {messageObj.InstanceId}");
                        _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Skipping message from same instance",
                            _appId, _instanceId);
                        return;
                    }

                    // Only process messages for this app
                    if (messageObj.AppId == _appId)
                    {
                        Console.WriteLine($"[DEBUG] Processing message with AppId={messageObj.AppId}");
                        await handler(messageObj);
                        Console.WriteLine($"[DEBUG] Successfully processed message");
                        _logger.LogDebug("[RedisSyncBus][{AppId}][{InstanceId}] Successfully processed message (compressed={Compressed})",
                            _appId, _instanceId, wasCompressed);
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG] Skipping message with non-matching AppId: {messageObj.AppId} != {_appId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] Error processing message: {ex.Message}");
                    _logger.LogError(ex, "[RedisSyncBus][{AppId}][{InstanceId}] Error processing message",
                        _appId, _instanceId);
                }
            });
            _isSubscribed = true;
            Console.WriteLine($"[DEBUG] Successfully subscribed to channel pattern: {channel}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error subscribing to channel: {ex.Message}");
            _logger.LogError(ex, "[RedisSyncBus][{AppId}][{InstanceId}] Error subscribing to Redis channel",
                _appId, _instanceId);
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
            var channel = $"{_channelPrefix}:{_appId}:{_messageTypeName}";
            await _subscriber.UnsubscribeAsync(RedisChannel.Pattern(channel));
            _isSubscribed = false;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RedisSyncBus][{AppId}][{InstanceId}] Error unsubscribing from Redis channel",
                _appId, _instanceId);
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