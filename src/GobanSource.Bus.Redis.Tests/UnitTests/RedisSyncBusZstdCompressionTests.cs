using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using ZstdSharp;

namespace GobanSource.Bus.Redis.Tests.UnitTests;

[TestClass]
public class RedisSyncBusZstdCompressionTests
{
    private Mock<IConnectionMultiplexer> _mockRedis = null!;
    private Mock<ISubscriber> _mockSubscriber = null!;
    private ILogger<RedisSyncBus<TestMessage>> _logger = null!;
    private const string ChannelPrefix = "test-prefix";

    // Zstd frame magic number
    private static readonly byte[] ZstdFrameMagicBytes = { 0x28, 0xB5, 0x2F, 0xFD };

    [TestInitialize]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockSubscriber = new Mock<ISubscriber>();
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _logger = NullLogger<RedisSyncBus<TestMessage>>.Instance;
    }

    [TestMethod]
    public async Task PublishAsync_ZstdCompressionEnabled_ShouldCompressMessage()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, ChannelPrefix, _logger, CompressionAlgo.Zstd);
        var message = new TestMessage
        {
            Message = "This message should be compressed using Zstd."
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
        Assert.IsTrue(capturedBytes.Length >= ZstdFrameMagicBytes.Length, "Compressed message should be at least as long as magic number");

        // Verify Zstd magic number at start
        var magicBytes = capturedBytes.Take(ZstdFrameMagicBytes.Length).ToArray();
        CollectionAssert.AreEqual(ZstdFrameMagicBytes, magicBytes, "Compressed message should start with Zstd magic number");

        // Verify we can decompress back to original JSON
        var decompressedBytes = new Decompressor().Unwrap(capturedBytes);
        var decompressedJson = Encoding.UTF8.GetString(decompressedBytes);
        var deserializedMessage = JsonSerializer.Deserialize<TestMessage>(decompressedJson);
        Assert.IsNotNull(deserializedMessage);
        Assert.AreEqual(message.Message, deserializedMessage.Message);
    }

    [TestMethod]
    public async Task SubscribeAsync_ShouldDecompressZstdMessages()
    {
        // Arrange
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, ChannelPrefix, _logger, CompressionAlgo.Zstd);
        var original = new TestMessage
        {
            Message = "Zstd compressed inbound message",
            InstanceId = Guid.NewGuid().ToString()
        };

        TestMessage? received = null;
        var handler = new Func<TestMessage, Task>(m => { received = m; return Task.CompletedTask; });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, cb, _) => subscriberCallback = cb)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Craft compressed payload
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(original));
        var compressedBytes = new Compressor(3).Wrap(jsonBytes).ToArray();

        // Act – trigger subscriber
        subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), compressedBytes);

        // Assert
        Assert.IsNotNull(received, "Message should be received and decompressed");
        Assert.AreEqual(original.Message, received!.Message);
        Assert.AreEqual(original.InstanceId, received.InstanceId);
    }
}