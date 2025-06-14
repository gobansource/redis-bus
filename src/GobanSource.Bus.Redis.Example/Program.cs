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

// Parse command line arguments
var enableCompression = args.Contains("--enable-compression", StringComparer.OrdinalIgnoreCase);
var verboseLogging = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
var showHelp = args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase);

// Show help and exit if requested
if (showHelp)
{
    Console.WriteLine("GobanSource Redis Bus Example");
    Console.WriteLine("=============================");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet run -- [options]");
    Console.WriteLine("       <executable> [options]");
    Console.WriteLine();
    Console.WriteLine("Note: Use '--' separator when running with dotnet run to pass args to the application");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --enable-compression    Enable LZ4 compression for Redis messages");
    Console.WriteLine("  --verbose              Enable verbose logging (Debug level)");
    Console.WriteLine("  --help, -h             Show this help information");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run");
    Console.WriteLine("  dotnet run -- --help");
    Console.WriteLine("  dotnet run -- --enable-compression");
    Console.WriteLine("  dotnet run -- --verbose");
    Console.WriteLine("  dotnet run -- --enable-compression --verbose");
    Console.WriteLine();
    return;
}

// Display configuration status
Console.WriteLine("GobanSource Redis Bus Example - Configuration");
Console.WriteLine("============================================");
Console.WriteLine($"Redis Bus Compression: {(enableCompression ? "ENABLED" : "DISABLED")}");
Console.WriteLine($"Verbose Logging: {(verboseLogging ? "ENABLED" : "DISABLED")}");
if (enableCompression)
{
    Console.WriteLine("  → Messages will be compressed using LZ4 compression");
}
if (verboseLogging)
{
    Console.WriteLine("  → Debug-level logging enabled for detailed operation tracking");
}
Console.WriteLine();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (verboseLogging)
{
    builder.Logging.SetMinimumLevel(LogLevel.Debug); // Verbose logging
    Console.WriteLine("Logging configured for DEBUG level - you'll see detailed Redis Bus operations");
}
else
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning); // Keep it quiet
}

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
    sp.GetRequiredService<ILogger<RedisSyncBus<OrderMessage>>>(),
    enableCompression));

builder.Services.AddSingleton<IRedisSyncBus<NotificationMessage>>(sp => new RedisSyncBus<NotificationMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "redis-bus-demo",
    "demo",
    sp.GetRequiredService<ILogger<RedisSyncBus<NotificationMessage>>>(),
    enableCompression));

builder.Services.AddSingleton<IRedisSyncBus<HeartbeatMessage>>(sp => new RedisSyncBus<HeartbeatMessage>(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    "redis-bus-demo",
    "demo",
    sp.GetRequiredService<ILogger<RedisSyncBus<HeartbeatMessage>>>(),
    enableCompression));

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
