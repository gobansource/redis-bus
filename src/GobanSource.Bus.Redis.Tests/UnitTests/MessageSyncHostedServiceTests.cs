using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace GobanSource.Bus.Redis.Tests.UnitTests;

[TestClass]
public class MessageSyncHostedServiceTests
{
    private const string TestCacheInstanceId = "test-cache";
    private Mock<IRedisSyncBus<TestMessage>> _mockSyncBus = null!;
    private Mock<IMessageHandler<TestMessage>> _mockHandler = null!;
    private Mock<ILogger<MessageSyncHostedService<TestMessage>>> _mockLogger = null!;
    private MessageSyncHostedService<TestMessage> _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _mockSyncBus = new Mock<IRedisSyncBus<TestMessage>>();
        _mockHandler = new Mock<IMessageHandler<TestMessage>>();
        _mockLogger = new Mock<ILogger<MessageSyncHostedService<TestMessage>>>();

        _service = new MessageSyncHostedService<TestMessage>(
            _mockSyncBus.Object,
            _mockHandler.Object,
            _mockLogger.Object);
    }

    [TestMethod]
    public async Task StartAsync_SubscribesToSyncBus()
    {
        // Act
        await _service.StartAsync(default);

        // Assert
        _mockSyncBus.Verify(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()), Times.Once);
    }

    [TestMethod]
    public async Task StopAsync_UnsubscribesFromSyncBus()
    {
        // Act
        await _service.StopAsync(default);

        // Assert
        _mockSyncBus.Verify(x => x.UnsubscribeAsync(), Times.Once);
    }

    [TestMethod]
    public async Task MessageHandler_WhenMessageIsCorrectType_CallsHandler()
    {
        // Arrange
        Func<TestMessage, Task>? messageHandler = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((handler, _) => messageHandler = handler)
            .Returns(Task.CompletedTask);

        await _service.StartAsync(default);

        var message = new TestMessage
        {
            Message = "testMessage"
        };

        _mockHandler.Setup(h => h.HandleAsync(It.IsAny<TestMessage>()))
            .Returns(Task.CompletedTask);

        // Act
        await messageHandler!(message);

        // Assert
        _mockHandler.Verify(h => h.HandleAsync(message), Times.Once);
    }

    // [TestMethod]
    // public async Task MessageHandler_WhenMessageIsWrongType_DoesNotCallHandler()
    // {
    //     // Arrange
    //     Func<CacheMessage, Task>? messageHandler = null;
    //     _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<CacheMessage, Task>>(), It.IsAny<Func<string, CacheMessage>>()))
    //         .Callback<Func<CacheMessage, Task>, Func<string, CacheMessage>>((handler, _) => messageHandler = handler)
    //         .Returns(Task.CompletedTask);

    //     await _service.StartAsync(default);

    //     // Create a different message type
    //     var message = new OtherMessage { SomeProperty = "test" };

    //     // Act
    //     await messageHandler!(messag e);

    //     // Assert
    //     _mockHandler.Verify(h => h.HandleAsync(It.IsAny<CacheMessage>()), Times.Never);
    // }

    [TestMethod]
    public async Task MessageHandler_WhenHandlerThrowsException_ShouldNotThrowException()
    {
        // Arrange
        Func<TestMessage, Task>? messageHandler = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((handler, _) => messageHandler = handler)
            .Returns(Task.CompletedTask);

        await _service.StartAsync(default);

        var message = new TestMessage
        {
            Message = "testMessage"
        };

        _mockHandler.Setup(h => h.HandleAsync(It.IsAny<TestMessage>()))
            .ThrowsAsync(new Exception("Test exception"));

        // Act
        await messageHandler!(message);

        // Assert - The exception should be handled gracefully without crashing
        Assert.IsTrue(true, "Handler exception should be caught and handled gracefully");
    }

    [TestMethod]
    public async Task SubscribeAsync_DeserializerCorrectlyDeserializesJsonToCacheMessage()
    {
        // Arrange
        Func<string, IMessage>? deserializer = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((_, deserializeFunc) => deserializer = deserializeFunc)
            .Returns(Task.CompletedTask);

        // Start the service which will set up the deserializer
        await _service.StartAsync(default);

        // Ensure deserializer was captured
        Assert.IsNotNull(deserializer, "Deserializer function should have been provided");

        // Create a JSON string representing a CacheMessage
        var jsonMessage = @"{
            ""MessageId"": ""test-id"",
            ""AppId"": ""test-app"",
            ""InstanceId"": ""test-instance"",
            ""Timestamp"": ""2023-01-01T12:00:00Z"",
            ""Message"": ""test-message""
           
        }";

        // Act
        var result = deserializer!(jsonMessage);

        // Assert
        Assert.IsInstanceOfType(result, typeof(TestMessage));
        var testMessage = (TestMessage)result;
        Assert.AreEqual("test-id", (result as BaseMessage)?.MessageId);
        Assert.AreEqual("test-app", (result as BaseMessage)?.AppId);
        Assert.AreEqual("test-instance", (result as BaseMessage)?.InstanceId);
        Assert.AreEqual("test-message", testMessage.Message);
    }

    [TestMethod]
    public async Task SubscribeAsync_DeserializerThrowsExceptionForInvalidJson()
    {
        // Arrange
        Func<string, TestMessage>? deserializer = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((_, deserializeFunc) => deserializer = deserializeFunc)
            .Returns(Task.CompletedTask);

        // Start the service which will set up the deserializer
        await _service.StartAsync(default);

        // Ensure deserializer was captured
        Assert.IsNotNull(deserializer, "Deserializer function should have been provided");

        // Create invalid JSON
        var invalidJson = "{ this is not valid JSON }";

        // Act & Assert
        Assert.ThrowsException<System.Text.Json.JsonException>(() => deserializer!(invalidJson));
    }

    [TestMethod]
    public async Task SubscribeAsync_DeserializerThrowsExceptionForMissingRequiredProperties()
    {
        // Arrange
        Func<string, TestMessage>? deserializer = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((_, deserializeFunc) => deserializer = deserializeFunc)
            .Returns(Task.CompletedTask);

        // Start the service which will set up the deserializer
        await _service.StartAsync(default);

        // Ensure deserializer was captured
        Assert.IsNotNull(deserializer, "Deserializer function should have been provided");

        // Create JSON with missing required properties
        var incompleteJson = @"{
            ""MessageId"": ""test-id"",
            ""AppId"": ""test-app""
            // Missing other required properties
        }";

        // Act & Assert
        Assert.ThrowsException<System.Text.Json.JsonException>(() => deserializer!(incompleteJson));
    }

    [TestMethod]
    public async Task SubscribeAsync_DeserializerThrowsExceptionForNullDeserialization()
    {
        // Arrange
        Func<string, TestMessage>? deserializer = null;
        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((_, deserializeFunc) => deserializer = deserializeFunc)
            .Returns(Task.CompletedTask);

        // Start the service which will set up the deserializer
        await _service.StartAsync(default);

        // Ensure deserializer was captured
        Assert.IsNotNull(deserializer, "Deserializer function should have been provided");

        // Create JSON that deserializes to null
        var nullJson = "null";

        // Act & Assert
        var exception = Assert.ThrowsException<InvalidOperationException>(() => deserializer!(nullJson));
        Assert.AreEqual("Failed to deserialize message as TestMessage", exception.Message);
    }

    [TestMethod]
    public async Task EndToEnd_JsonToHandlerExecution()
    {
        // Arrange
        Func<TestMessage, Task>? messageHandler = null;
        Func<string, TestMessage>? deserializer = null;

        _mockSyncBus.Setup(x => x.SubscribeAsync(It.IsAny<Func<TestMessage, Task>>(), It.IsAny<Func<string, TestMessage>>()))
            .Callback<Func<TestMessage, Task>, Func<string, TestMessage>>((handler, deserializeFunc) =>
            {
                messageHandler = handler;
                deserializer = deserializeFunc;
            })
            .Returns(Task.CompletedTask);

        await _service.StartAsync(default);

        // Ensure both functions were captured
        Assert.IsNotNull(messageHandler, "Message handler should have been provided");
        Assert.IsNotNull(deserializer, "Deserializer function should have been provided");

        // Create a JSON string representing a CacheMessage
        var jsonMessage = @"{
            ""MessageId"": ""test-id"",
            ""AppId"": ""test-app"",
            ""InstanceId"": ""test-instance"",
            ""Timestamp"": ""2023-01-01T12:00:00Z"",
            ""Message"": ""test-message""
        }";

        // Setup handler expectation
        _mockHandler.Setup(h => h.HandleAsync(It.IsAny<TestMessage>()))
            .Returns(Task.CompletedTask);

        // Act - First deserialize, then handle
        var deserializedMessage = deserializer!(jsonMessage);
        await messageHandler!(deserializedMessage);

        // Assert - Verify the handler was called with the deserialized message
        _mockHandler.Verify(h => h.HandleAsync(It.Is<TestMessage>(m =>
            m.Message == "test-message")), Times.Once);
    }
}

// A different message type for testing
public class OtherMessage : BaseMessage
{
    public string SomeProperty { get; set; } = null!;

    public OtherMessage()
    {

    }
}