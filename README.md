# GobanSource.Bus.Redis

[![NuGet](https://img.shields.io/nuget/v/GobanSource.Bus.Redis.svg)](https://www.nuget.org/packages/GobanSource.Bus.Redis/)
[![License](https://img.shields.io/github/license/gobansource/redis-bus)](LICENSE)

A lightweight, Redis-based message bus library for .NET applications. Enables communication between distributed application instances using Redis Pub/Sub.

## Features

- **True Fan-Out Messaging**: Each message is delivered to all active subscribers across different application instances
- **Instance Filtering**: Messages from the same instance are automatically skipped
- **Type-Safe Message Handling**: Generic interfaces for strongly-typed message processing
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
    "myapp",     // Your application ID
    "messages",  // Channel prefix
    sp.GetRequiredService<ILogger<RedisSyncBus<SimpleMessage>>>()));

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

## How It Works

GobanSource.Bus.Redis uses Redis Pub/Sub channels to publish and subscribe to messages between different application instances. When a message is published:

1. The message is serialized and published to a Redis channel with a pattern: `{prefix}:{appId}:{messageType}`
2. Redis broadcasts the message to all subscribers of that channel
3. Each subscriber receives and processes the message if it's from a different instance
4. Messages from the same instance (identified by InstanceId) are automatically skipped

## License

This project is licensed under the [MIT License](LICENSE).

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
