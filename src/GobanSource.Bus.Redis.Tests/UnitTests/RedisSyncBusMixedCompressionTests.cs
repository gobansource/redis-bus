using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using K4os.Compression.LZ4.Streams;
using System.Buffers;
using ZstdSharp;

namespace GobanSource.Bus.Redis.Tests.UnitTests;

[TestClass]
public class RedisSyncBusMixedCompressionTests
{
    private Mock<IConnectionMultiplexer> _mockRedis = null!;
    private Mock<ISubscriber> _mockSubscriber = null!;
    private ILogger<RedisSyncBus<TestMessage>> _logger = null!;
    private const string ChannelPrefix = "test-prefix";

    [TestInitialize]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockSubscriber = new Mock<ISubscriber>();
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _logger = NullLogger<RedisSyncBus<TestMessage>>.Instance;
    }

    [TestMethod]
    public async Task SubscribeAsync_MixedCompressionTypes_ShouldHandleAll()
    {
        // Arrange - bus can be any compression option (publish side irrelevant)
        var bus = new RedisSyncBus<TestMessage>(_mockRedis.Object, ChannelPrefix, _logger, CompressionAlgo.None);

        var originalPlain = new TestMessage { Message = "plain", InstanceId = Guid.NewGuid().ToString() };
        var originalLz4 = new TestMessage { Message = "lz4", InstanceId = Guid.NewGuid().ToString() };
        var originalZstd = new TestMessage { Message = "zstd", InstanceId = Guid.NewGuid().ToString() };

        var received = new List<string>();
        var handler = new Func<TestMessage, Task>(msg => { received.Add(msg.Message!); return Task.CompletedTask; });

        Action<RedisChannel, RedisValue> subscriberCallback = null!;
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, cb, _) => subscriberCallback = cb)
            .Returns(Task.CompletedTask);

        await bus.SubscribeAsync(handler, json => JsonSerializer.Deserialize<TestMessage>(json)!);

        // Helper to trigger callback with bytes
        void Trigger(TestMessage msg, byte[] bytes)
        {
            subscriberCallback(new RedisChannel("test", RedisChannel.PatternMode.Auto), bytes);
        }

        // Plain JSON bytes
        var plainBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalPlain));
        Trigger(originalPlain, plainBytes);

        // LZ4 compressed bytes
        var lz4Json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalLz4));
        var bufferWriter = new ArrayBufferWriter<byte>();
        LZ4Frame.Encode(lz4Json.AsSpan(), bufferWriter);
        Trigger(originalLz4, bufferWriter.WrittenMemory.ToArray());

        // Zstd compressed bytes
        var zstdJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalZstd));
        var zstdBytes = new Compressor(3).Wrap(zstdJson).ToArray();
        Trigger(originalZstd, zstdBytes);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "plain", "lz4", "zstd" }, received);
    }
}