using GobanSource.Bus.Redis;
using GobanSource.Bus.Redis.Example.Handlers;
using GobanSource.Bus.Redis.Example.Messages;
using GobanSource.Bus.Redis.Example.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning); // Keep it quiet

// Add Redis connection
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    try
    {
        return ConnectionMultiplexer.Connect("localhost:6379");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to connect to Redis: {ex.Message}");
        Console.WriteLine("Make sure Redis is running on localhost:6379");
        Environment.Exit(1);
        return null!;
    }
});

// Register message buses
builder.Services.AddSingleton<IRedisSyncBus<OrderMessage>>(sp => new RedisSyncBus<OrderMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "redis-bus-demo",
    "demo",
    sp.GetRequiredService<ILogger<RedisSyncBus<OrderMessage>>>()));

builder.Services.AddSingleton<IRedisSyncBus<NotificationMessage>>(sp => new RedisSyncBus<NotificationMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "redis-bus-demo",
    "demo",
    sp.GetRequiredService<ILogger<RedisSyncBus<NotificationMessage>>>()));

builder.Services.AddSingleton<IRedisSyncBus<HeartbeatMessage>>(sp => new RedisSyncBus<HeartbeatMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "redis-bus-demo",
    "demo",
    sp.GetRequiredService<ILogger<RedisSyncBus<HeartbeatMessage>>>()));

// Register message handlers
builder.Services.AddTransient<IMessageHandler<OrderMessage>, OrderMessageHandler>();
builder.Services.AddTransient<IMessageHandler<NotificationMessage>, NotificationMessageHandler>();
builder.Services.AddTransient<IMessageHandler<HeartbeatMessage>, HeartbeatMessageHandler>();

// Register hosted services
builder.Services.AddHostedService<MessageSyncHostedService<OrderMessage>>();
builder.Services.AddHostedService<MessageSyncHostedService<NotificationMessage>>();
builder.Services.AddHostedService<MessageSyncHostedService<HeartbeatMessage>>();
builder.Services.AddHostedService<InteractiveConsoleService>();

var host = builder.Build();

// Graceful shutdown
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    host.StopAsync().Wait();
};

try
{
    await host.RunAsync();
}
catch (OperationCanceledException)
{
    // Expected when cancellation is requested
}
catch (Exception ex)
{
    Console.WriteLine($"Application error: {ex.Message}");
}
