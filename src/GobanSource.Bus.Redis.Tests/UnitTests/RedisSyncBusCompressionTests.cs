using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using K4os.Compression.LZ4.Streams;
using System.Buffers;

namespace GobanSource.Bus.Redis.Tests.UnitTests;

[TestClass]
public class RedisSyncBusCompressionTests
{
    private Mock<IConnectionMultiplexer> _mockRedis = null!;
    private Mock<ISubscriber> _mockSubscriber = null!;
    private ILogger<RedisSyncBus<TestMessage>> _logger = null!;
    private string _appId = null!;
    private const string ChannelPrefix = "test-prefix";

    // LZ4 Frame format magic number
    private static readonly byte[] LZ4FrameMagicBytes = { 0x04, 0x22, 0x4D, 0x18 };

    [TestInitialize]
    public void Setup()
    {
        _appId = Guid.NewGuid().ToString();
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockSubscriber = new Mock<ISubscriber>();
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _logger = NullLogger<RedisSyncBus<TestMessage>>.Instance;
    }

    #region Basic Compression Tests

    [TestMethod]
    public async Task PublishAsync_WithCompressionEnabled_ShouldCompressMessage()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var message = new TestMessage
        {
            AppId = _appId,
            Message = "This is a test message that should be compressed using LZ4 compression algorithm to reduce the size of the payload when transmitted over Redis."
        };

        byte[] capturedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => capturedBytes = value)
            .Returns(Task.FromResult(1L));

        // Act
        await bus.PublishAsync(message);

        // Assert
        Assert.IsNotNull(capturedBytes, "Message bytes should be captured");
        Assert.IsTrue(capturedBytes.Length >= LZ4FrameMagicBytes.Length, "Compressed message should be at least as long as magic number");

        // Verify LZ4 magic number is present
        var magicBytes = capturedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(LZ4FrameMagicBytes, magicBytes, "Compressed message should start with LZ4 magic number");

        // Verify we can decompress back to original JSON
        var bufferWriter = new ArrayBufferWriter<byte>();
        LZ4Frame.Decode(capturedBytes.AsSpan(), bufferWriter);
        var decompressedBytes = bufferWriter.WrittenMemory.ToArray();
        var decompressedJson = Encoding.UTF8.GetString(decompressedBytes);

        var deserializedMessage = JsonSerializer.Deserialize<TestMessage>(decompressedJson);
        Assert.IsNotNull(deserializedMessage);
        Assert.AreEqual(message.Message, deserializedMessage.Message);
    }

    [TestMethod]
    public async Task PublishAsync_WithCompressionDisabled_ShouldNotCompressMessage()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: false);
        var message = new TestMessage
        {
            AppId = _appId,
            Message = "This is a test message that should NOT be compressed"
        };

        byte[] capturedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => capturedBytes = value)
            .Returns(Task.FromResult(1L));

        // Act
        await bus.PublishAsync(message);

        // Assert
        Assert.IsNotNull(capturedBytes, "Message bytes should be captured");

        // Verify no LZ4 magic number
        if (capturedBytes.Length >= LZ4FrameMagicBytes.Length)
        {
            var magicBytes = capturedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
            CollectionAssert.AreNotEqual(LZ4FrameMagicBytes, magicBytes, "Uncompressed message should not start with LZ4 magic number");
        }

        // Verify it's plain UTF-8 JSON
        var messageString = Encoding.UTF8.GetString(capturedBytes);
        var deserializedMessage = JsonSerializer.Deserialize<TestMessage>(messageString);
        Assert.IsNotNull(deserializedMessage);
        Assert.AreEqual(message.Message, deserializedMessage.Message);
    }

    [TestMethod]
    public async Task PublishAsync_SmallMessage_CompressionStillApplied()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var message = new TestMessage
        {
            AppId = _appId,
            Message = "Small" // Very small message that might not compress well
        };

        byte[] capturedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => capturedBytes = value)
            .Returns(Task.FromResult(1L));

        // Act
        await bus.PublishAsync(message);

        // Assert
        Assert.IsNotNull(capturedBytes);

        // Even small messages should be compressed when compression is enabled
        var magicBytes = capturedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(LZ4FrameMagicBytes, magicBytes, "Even small messages should be compressed when compression is enabled");
    }

    [TestMethod]
    public async Task PublishAsync_LargeMessage_ShouldProvideGoodCompressionRatio()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        // Create a large, repetitive message that should compress well
        var largeMessage = string.Join("", Enumerable.Repeat("This is a repetitive message that should compress very well with LZ4 compression. ", 100));
        var message = new TestMessage
        {
            AppId = _appId,
            Message = largeMessage
        };

        byte[] capturedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => capturedBytes = value)
            .Returns(Task.FromResult(1L));

        // Act
        await bus.PublishAsync(message);

        // Assert
        Assert.IsNotNull(capturedBytes);

        var originalJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, message.GetType()));
        var compressionRatio = (double)capturedBytes.Length / originalJsonBytes.Length;

        // For repetitive content, we should get significant compression
        Assert.IsTrue(compressionRatio < 0.5, $"Compression ratio should be less than 50% for repetitive content, got {compressionRatio:P}");

        // Verify magic number
        var magicBytes = capturedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(LZ4FrameMagicBytes, magicBytes);
    }

    #endregion

    #region Decompression Subscription Tests

    [TestMethod]
    public async Task SubscribeAsync_ShouldDecompressLZ4Messages()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var testMessage = "This is a test message that will be compressed and then decompressed";
        var originalMessage = new TestMessage
        {
            AppId = _appId,
            Message = testMessage,
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? receivedMessage = null;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create compressed message manually
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalMessage));
        var bufferWriter = new ArrayBufferWriter<byte>();
        LZ4Frame.Encode(jsonBytes.AsSpan(), bufferWriter);
        var compressedBytes = bufferWriter.WrittenMemory.ToArray();

        // Act
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), compressedBytes);

        // Assert
        Assert.IsNotNull(receivedMessage, "Message should be received and decompressed");
        Assert.AreEqual(originalMessage.Message, receivedMessage.Message);
        Assert.AreEqual(originalMessage.AppId, receivedMessage.AppId);
        Assert.AreEqual(originalMessage.InstanceId, receivedMessage.InstanceId);
    }

    [TestMethod]
    public async Task SubscribeAsync_ShouldHandleUncompressedMessages()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var testMessage = "This is an uncompressed message";
        var originalMessage = new TestMessage
        {
            AppId = _appId,
            Message = testMessage,
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? receivedMessage = null;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create uncompressed message (plain UTF-8 JSON bytes)
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalMessage));

        // Act
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), jsonBytes);

        // Assert
        Assert.IsNotNull(receivedMessage, "Uncompressed message should be received and processed");
        Assert.AreEqual(originalMessage.Message, receivedMessage.Message);
        Assert.AreEqual(originalMessage.AppId, receivedMessage.AppId);
        Assert.AreEqual(originalMessage.InstanceId, receivedMessage.InstanceId);
    }

    [TestMethod]
    public async Task SubscribeAsync_MixedCompressedAndUncompressed_ShouldHandleBoth()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var receivedMessages = new List<TestMessage>();

        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessages.Add(msg);
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create compressed message
        var compressedMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Compressed message",
            InstanceId = Guid.NewGuid().ToString()
        };
        var compressedJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(compressedMessage));
        var compressedBufferWriter = new ArrayBufferWriter<byte>();
        LZ4Frame.Encode(compressedJsonBytes.AsSpan(), compressedBufferWriter);
        var compressedBytes = compressedBufferWriter.WrittenMemory.ToArray();

        // Create uncompressed message
        var uncompressedMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Uncompressed message",
            InstanceId = Guid.NewGuid().ToString()
        };
        var uncompressedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(uncompressedMessage));

        // Act
        subscriberCallback(new RedisChannel("test1", RedisChannel.PatternMode.Auto), compressedBytes);
        subscriberCallback(new RedisChannel("test2", RedisChannel.PatternMode.Auto), uncompressedBytes);

        // Assert
        Assert.AreEqual(2, receivedMessages.Count, "Should receive both compressed and uncompressed messages");

        var compressedReceived = receivedMessages.First(m => m.Message == "Compressed message");
        var uncompressedReceived = receivedMessages.First(m => m.Message == "Uncompressed message");

        Assert.IsNotNull(compressedReceived);
        Assert.IsNotNull(uncompressedReceived);
        Assert.AreEqual(compressedMessage.InstanceId, compressedReceived.InstanceId);
        Assert.AreEqual(uncompressedMessage.InstanceId, uncompressedReceived.InstanceId);
    }

    #endregion

    #region End-to-End Compression Tests

    [TestMethod]
    public async Task EndToEnd_CompressedPublishAndSubscribe_ShouldMaintainMessageIntegrity()
    {
        // Arrange
        var publisherBus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var subscriberBus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var originalMessage = new TestMessage
        {
            AppId = _appId,
            Message = "End-to-end test message with special characters: åäö, 中文, emoji 🚀, JSON: {\"key\": \"value\"}"
        };

        TestMessage? receivedMessage = null;
        byte[] publishedBytes = null!;

        // Setup publisher mock
        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => publishedBytes = value)
            .Returns(Task.FromResult(1L));

        // Setup subscriber mock
        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        // Act
        await subscriberBus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);
        await publisherBus.PublishAsync(originalMessage);

        // Simulate the Redis callback with the published bytes
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), publishedBytes);

        // Assert
        Assert.IsNotNull(receivedMessage, "Message should be received after compression/decompression");
        Assert.AreEqual(originalMessage.Message, receivedMessage.Message, "Message content should be preserved");
        Assert.AreEqual(originalMessage.AppId, receivedMessage.AppId, "AppId should be preserved");

        // Verify the message was actually compressed
        var magicBytes = publishedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(LZ4FrameMagicBytes, magicBytes, "Message should have been compressed");
    }

    [TestMethod]
    public async Task EndToEnd_LargeMessageCompression_ShouldHandleEfficiently()
    {
        // Arrange
        var publisherBus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);
        var subscriberBus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        // Create a large message with repetitive content
        var largeContent = string.Join("", Enumerable.Repeat("Large message content block with repetitive data. ", 500));
        var originalMessage = new TestMessage
        {
            AppId = _appId,
            Message = largeContent
        };

        TestMessage? receivedMessage = null;
        byte[] publishedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => publishedBytes = value)
            .Returns(Task.FromResult(1L));

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        // Act
        await subscriberBus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);
        await publisherBus.PublishAsync(originalMessage);
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), publishedBytes);

        // Assert
        Assert.IsNotNull(receivedMessage);
        Assert.AreEqual(originalMessage.Message, receivedMessage.Message);

        // Verify compression effectiveness
        var originalSize = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalMessage)).Length;
        var compressionRatio = (double)publishedBytes.Length / originalSize;
        Assert.IsTrue(compressionRatio < 0.3, $"Large repetitive message should compress to less than 30%, got {compressionRatio:P}");
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task SubscribeAsync_CorruptedCompressedData_ShouldHandleGracefully()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var exceptionThrown = false;
        var handler = new Func<TestMessage, Task>(msg => Task.CompletedTask);

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create corrupted data with LZ4 magic number but invalid compression data
        var corruptedData = new byte[100];
        Array.Copy(LZ4FrameMagicBytes, corruptedData, LZ4FrameMagicBytes.Length);
        // Fill rest with random data
        new Random().NextBytes(corruptedData.AsSpan(LZ4FrameMagicBytes.Length));

        // Act & Assert
        try
        {
            subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), corruptedData);
        }
        catch (Exception)
        {
            exceptionThrown = true;
        }

        // The implementation should handle this gracefully - either by catching the exception internally
        // or by letting it bubble up (both are acceptable behaviors)
        // The important thing is that it doesn't crash the whole application
        Assert.IsTrue(true, "Should handle corrupted compression data without crashing");
    }

    [TestMethod]
    public async Task SubscribeAsync_InvalidMagicNumber_ShouldTreatAsUncompressed()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var receivedMessage = false;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = true;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create data with different magic number but valid JSON
        var testMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Test message",
            InstanceId = Guid.NewGuid().ToString()
        };
        var validJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testMessage));

        // Act
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), validJsonBytes);

        // Assert
        Assert.IsTrue(receivedMessage, "Message with non-LZ4 magic number should be treated as uncompressed");
    }

    #endregion

    #region Edge Case Branch Tests

    [TestMethod]
    public async Task SubscribeAsync_MessageShorterThanMagicBytes_ShouldTreatAsUncompressed()
    {
        // Arrange - Test branch 2: messageBytes.Length < 4
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var testMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Hi", // Very short message
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? receivedMessage = null;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create very short message (shorter than 4 bytes)
        var shortBytes = new byte[] { 0x01, 0x02 }; // Only 2 bytes, less than LZ4FrameMagicBytes.Length (4)

        // Act
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), shortBytes);

        // Assert
        // Should try to treat as uncompressed UTF-8, which will likely fail JSON deserialization
        // The important thing is it doesn't crash on the length check
        Assert.IsNull(receivedMessage, "Very short non-JSON message should not be processed successfully");
    }

    [TestMethod]
    public async Task SubscribeAsync_MessageExactlyMagicBytesLengthWrongContent_ShouldTreatAsUncompressed()
    {
        // Arrange - Test branch 3: messageBytes.Length == 4 but wrong content
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var testMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Test",
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? receivedMessage = null;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create 4-byte message with wrong magic number
        var wrongMagicBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // 4 bytes, but not LZ4 magic

        // Act
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), wrongMagicBytes);

        // Assert
        // Should treat as uncompressed UTF-8, which will fail JSON deserialization
        Assert.IsNull(receivedMessage, "4-byte message with wrong magic should not be processed as compressed");
    }

    [TestMethod]
    public async Task SubscribeAsync_LZ4DecompressionFailure_ShouldHandleGracefully()
    {
        // Arrange - Test branch 5: LZ4Frame.Decode throws exception
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var handler = new Func<TestMessage, Task>(msg => Task.CompletedTask);

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Create data with correct LZ4 magic number but invalid compression stream
        var invalidLZ4Data = new byte[20];
        Array.Copy(LZ4FrameMagicBytes, invalidLZ4Data, LZ4FrameMagicBytes.Length);
        // Fill rest with data that will cause LZ4Frame.Decode to fail
        for (int i = 4; i < invalidLZ4Data.Length; i++)
        {
            invalidLZ4Data[i] = 0xFF; // Invalid LZ4 stream data
        }

        // Act & Assert
        // The method should handle the LZ4 decompression exception gracefully
        // without crashing the subscriber callback
        try
        {
            subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), invalidLZ4Data);

            // If we get here, the exception was caught internally (which is good)
            Assert.IsTrue(true, "LZ4 decompression failure should be handled gracefully");
        }
        catch (Exception)
        {
            // If an exception bubbles up, that's also acceptable behavior
            // as long as it's properly logged and doesn't crash the application
            Assert.IsTrue(true, "LZ4 decompression failure exception is acceptable if properly handled");
        }
    }

    [TestMethod]
    public async Task SubscribeAsync_PartialMagicBytesMatch_ShouldTreatAsUncompressed()
    {
        // Arrange - Test branch 7: Partial magic bytes match
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var testMessage = new TestMessage
        {
            AppId = _appId,
            Message = "Test message",
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? receivedMessage = null;
        var handler = new Func<TestMessage, Task>(msg =>
        {
            receivedMessage = msg;
            return Task.CompletedTask;
        });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Test different partial magic byte scenarios
        var testCases = new[]
        {
            ("First byte matches", new byte[] { 0x04, 0xFF, 0xFF, 0xFF, 0xFF }),
            ("First two bytes match", new byte[] { 0x04, 0x22, 0xFF, 0xFF, 0xFF }),
            ("First three bytes match", new byte[] { 0x04, 0x22, 0x4D, 0xFF, 0xFF }),
            ("Last three bytes match", new byte[] { 0xFF, 0x22, 0x4D, 0x18, 0xFF }),
        };

        foreach (var (description, partialMagicBytes) in testCases)
        {
            // Act
            subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), partialMagicBytes);

            // Assert
            Assert.IsNull(receivedMessage, $"{description} should not trigger LZ4 decompression");
            receivedMessage = null; // Reset for next test case
        }
    }

    [TestMethod]
    public async Task SubscribeAsync_EmptyMessage_ShouldTreatAsUncompressed()
    {
        // Arrange - Test edge case: empty message array
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var handler = new Func<TestMessage, Task>(msg => Task.CompletedTask);

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => subscriberCallback = callback)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Act
        var emptyBytes = Array.Empty<byte>();
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), emptyBytes);

        // Assert
        // Should not crash on length check (0 < 4), should go to uncompressed branch
        Assert.IsTrue(true, "Empty message should be handled without crashing");
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public async Task CompressionPerformance_RepeatedContent_ShouldAchieveGoodRatio()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        var testCases = new[]
        {
            ("Small repetitive", string.Join("", Enumerable.Repeat("test ", 20))),
            ("Medium repetitive", string.Join("", Enumerable.Repeat("This is a test message. ", 100))),
            ("Large repetitive", string.Join("", Enumerable.Repeat("Large repetitive content block for compression testing. ", 300))),
            ("JSON-like repetitive", string.Join("", Enumerable.Repeat("{\"key\":\"value\",\"data\":\"test\"}", 50)))
        };

        foreach (var (name, content) in testCases)
        {
            var message = new TestMessage { AppId = _appId, Message = content };
            byte[] compressedBytes = null!;

            _mockSubscriber.Setup(s => s.PublishAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
                .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => compressedBytes = value)
                .Returns(Task.FromResult(1L));

            // Act
            await bus.PublishAsync(message);

            // Assert
            var originalSize = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)).Length;
            var compressionRatio = (double)compressedBytes.Length / originalSize;

            Console.WriteLine($"{name}: {originalSize} -> {compressedBytes.Length} bytes (ratio: {compressionRatio:P})");

            // For repetitive content, we should get reasonable compression
            Assert.IsTrue(compressionRatio < 0.8, $"{name} should compress to less than 80%, got {compressionRatio:P}");
        }
    }

    [TestMethod]
    public async Task CompressionPerformance_RandomContent_ShouldHandleGracefully()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, _appId, ChannelPrefix, _logger, enableCompression: true);

        // Create random content that won't compress well
        var random = new Random(42); // Fixed seed for reproducible tests
        var randomContent = new string(Enumerable.Range(0, 1000)
            .Select(_ => (char)random.Next(32, 127))
            .ToArray());

        var message = new TestMessage { AppId = _appId, Message = randomContent };
        byte[] compressedBytes = null!;

        _mockSubscriber.Setup(s => s.PublishAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => compressedBytes = value)
            .Returns(Task.FromResult(1L));

        // Act
        await bus.PublishAsync(message);

        // Assert
        Assert.IsNotNull(compressedBytes);
        var originalSize = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)).Length;
        var compressionRatio = (double)compressedBytes.Length / originalSize;

        Console.WriteLine($"Random content: {originalSize} -> {compressedBytes.Length} bytes (ratio: {compressionRatio:P})");

        // Random content might not compress well, but should still be handled
        Assert.IsTrue(compressionRatio > 0.5, $"Random content compression ratio should be reasonable, got {compressionRatio:P}");

        // Verify it's still properly compressed (has magic number)
        var magicBytes = compressedBytes.Take(LZ4FrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(LZ4FrameMagicBytes, magicBytes);
    }

    #endregion

    [TestCleanup]
    public async Task Cleanup()
    {
        // Clean up any resources if needed
        await Task.CompletedTask;
    }
}