# GobanSource.Bus.Redis

![build](https://github.com/gobansource/redis-bus/workflows/build/badge.svg)
![coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fgobansource.github.io%2Fcode-coverage%2Fgobansource%2Fredis-bus%2Fbadge.txt)
[![NuGet](https://img.shields.io/nuget/v/GobanSource.Bus.Redis.svg)](https://www.nuget.org/packages/GobanSource.Bus.Redis/)
![MyGet Version](https://img.shields.io/myget/gobansource/v/GobanSource.Bus.Redis)
[![License](https://img.shields.io/github/license/gobansource/redis-bus)](LICENSE)

A lightweight, Redis-based message bus library for .NET applications. Enables communication between distributed application instances using Redis Pub/Sub.

## Features

- **True Fan-Out Messaging**: Each message is delivered to all active subscribers across different application instances
- **Instance Filtering**: Messages from the same instance are automatically skipped
- **Type-Safe Message Handling**: Generic interfaces for strongly-typed message processing
- **Optional LZ4 Compression**: Reduce bandwidth usage with fast LZ4 compression (backward compatible)
- **No Message Persistence**: Only active subscribers receive messages (suitable for cache synchronization)
- **Easy Integration**: Works with .NET's dependency injection and hosted services

## Installation

```shell
dotnet add package GobanSource.Bus.Redis
```

## Requirements

- .NET Standard 2.0+
- Redis server

## Quick Start

### 1. Define your message type

```csharp
public class SimpleMessage : BaseMessage
{
    public string Data { get; set; } = null!;
}
```

### 2. Create a message handler

```csharp
public class SimpleMessageHandler : IMessageHandler<SimpleMessage>
{
    private readonly ILogger<SimpleMessageHandler> _logger;

    public SimpleMessageHandler(ILogger<SimpleMessageHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(SimpleMessage message)
    {
        _logger.LogInformation("Received message: {Data}", message.Data);
        // Process the message here
        await Task.CompletedTask;
    }
}
```

### 3. Register services in your application

```csharp
// Add Redis connection
services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect("localhost:6379"));

// Register the message bus for SimpleMessage
services.AddTransient<IRedisSyncBus<SimpleMessage>>(sp => new RedisSyncBus<SimpleMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "messages",  // Channel prefix
    sp.GetRequiredService<ILogger<RedisSyncBus<SimpleMessage>>>(),
    false));     // Enable compression (optional, defaults to false)

// Register the message handler
services.AddTransient<IMessageHandler<SimpleMessage>, SimpleMessageHandler>();

// Register the hosted service that connects the bus to the handler
services.AddHostedService<MessageSyncHostedService<SimpleMessage>>();
```

### 4. Publish messages

```csharp
public class MessagePublisher
{
    private readonly IRedisSyncBus<SimpleMessage> _bus;

    public MessagePublisher(IRedisSyncBus<SimpleMessage> bus)
    {
        _bus = bus;
    }

    public async Task SendMessageAsync(string data)
    {
        await _bus.PublishAsync(new SimpleMessage
        {
            Data = data
        });
    }
}
```

## Compression

GobanSource.Bus.Redis supports optional LZ4 compression to reduce bandwidth usage and improve performance for large messages.

### Enabling Compression

```csharp
// Enable compression when creating the bus
services.AddTransient<IRedisSyncBus<SimpleMessage>>(sp => new RedisSyncBus<SimpleMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "messages",
    sp.GetRequiredService<ILogger<RedisSyncBus<SimpleMessage>>>(),
    enableCompression: true));  // Enable LZ4 compression
```

### Features

- **LZ4 Frame Format**: Fast compression/decompression with good compression ratios
- **Automatic Detection**: Compressed and uncompressed messages are automatically detected
- **Backward Compatibility**: Applications with compression enabled can communicate with those without
- **Performance Benefits**: Especially effective for repetitive content or large messages
- **Transparent Operation**: No changes needed to message handlers or publishers

### When to Use Compression

- **Large Messages**: Messages over 1KB typically benefit from compression
- **Repetitive Content**: JSON with repeated structures compress very well
- **High-Volume Applications**: Reduces Redis bandwidth and network traffic
- **Mixed Environments**: Safe to enable during gradual rollouts

## How It Works

GobanSource.Bus.Redis uses Redis Pub/Sub channels to publish and subscribe to messages between different application instances. When a message is published:

1. The message is serialized to JSON
2. If compression is enabled, the JSON is compressed using LZ4 Frame format
3. The message (compressed or uncompressed) is published to a Redis channel: `{prefix}:{messageType}`
4. Redis broadcasts the message to all subscribers of that channel
5. Each subscriber automatically detects and decompresses the message if needed
6. Messages from the same instance (identified by InstanceId) are automatically skipped

## License

This project is licensed under the [MIT License](LICENSE).

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Acknowledgments

- Built and maintained by [Goban Source](https://gobansource.com)
