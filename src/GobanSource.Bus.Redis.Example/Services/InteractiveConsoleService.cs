using GobanSource.Bus.Redis;
using GobanSource.Bus.Redis.Example.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GobanSource.Bus.Redis.Example.Services;

public class InteractiveConsoleService : BackgroundService
{
    private readonly IRedisSyncBus<OrderMessage> _orderBus;
    private readonly IRedisSyncBus<NotificationMessage> _notificationBus;
    private readonly IRedisSyncBus<HeartbeatMessage> _heartbeatBus;
    private readonly ILogger<InteractiveConsoleService> _logger;
    private readonly string _instanceName;
    private readonly ConsoleColor _instanceColor;

    public InteractiveConsoleService(
        IRedisSyncBus<OrderMessage> orderBus,
        IRedisSyncBus<NotificationMessage> notificationBus,
        IRedisSyncBus<HeartbeatMessage> heartbeatBus,
        ILogger<InteractiveConsoleService> logger)
    {
        _orderBus = orderBus;
        _notificationBus = notificationBus;
        _heartbeatBus = heartbeatBus;
        _logger = logger;

        var instanceId = _orderBus.GetInstanceId();
        _instanceName = GetInstanceName(instanceId);
        _instanceColor = GetInstanceColor(instanceId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ShowWelcomeMessage();
        ShowHelp();

        while (!stoppingToken.IsCancellationRequested)
        {
            Console.ForegroundColor = _instanceColor;
            Console.Write($"[{_instanceName}] > ");
            Console.ResetColor();

            var input = await Task.Run(() => Console.ReadLine(), stoppingToken);
            if (string.IsNullOrWhiteSpace(input)) continue;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLower();

            try
            {
                switch (command)
                {
                    case "order":
                        await HandleOrderCommand(parts);
                        break;
                    case "notify":
                        await HandleNotifyCommand(parts);
                        break;
                    case "ping":
                        await HandlePingCommand();
                        break;
                    case "help":
                        ShowHelp();
                        break;
                    case "quit":
                    case "exit":
                        return;
                    default:
                        Console.WriteLine("Unknown command. Type 'help' for available commands.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private async Task HandleOrderCommand(string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: order <product> <quantity>");
            return;
        }

        var product = parts[1];
        if (!int.TryParse(parts[2], out var quantity))
        {
            Console.WriteLine("Quantity must be a number");
            return;
        }

        var message = new OrderMessage
        {
            Product = product,
            Quantity = quantity
        };

        await _orderBus.PublishAsync(message);
        Console.WriteLine($"Sent OrderMessage: {product} (qty: {quantity}) [OrderId: {message.OrderId}]");
    }

    private async Task HandleNotifyCommand(string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: notify <title> <content>");
            return;
        }

        var title = parts[1];
        var content = string.Join(" ", parts.Skip(2));

        var message = new NotificationMessage
        {
            Title = title,
            Content = content
        };

        await _notificationBus.PublishAsync(message);
        Console.WriteLine($"Sent NotificationMessage: {title} - {content}");
    }

    private async Task HandlePingCommand()
    {
        var message = new HeartbeatMessage();
        await _heartbeatBus.PublishAsync(message);
        Console.WriteLine($"Sent HeartbeatMessage: {message.Status} (ping: {message.PingTime:HH:mm:ss})");
    }

    private void ShowWelcomeMessage()
    {
        Console.ForegroundColor = _instanceColor;
        Console.WriteLine($"=== Redis Bus Demo - {_instanceName} ===");
        Console.ResetColor();
        Console.WriteLine("This instance will send and receive messages through Redis.");
        Console.WriteLine("Start another instance in a different terminal to see distributed messaging.");
        Console.WriteLine();
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  order <product> <quantity>  - Send an order message");
        Console.WriteLine("  notify <title> <content>    - Send a notification message");
        Console.WriteLine("  ping                        - Send a heartbeat message");
        Console.WriteLine("  help                        - Show this help");
        Console.WriteLine("  quit                        - Exit the application");
        Console.WriteLine();
    }

    private static ConsoleColor GetInstanceColor(string instanceId)
    {
        var colors = new[] { ConsoleColor.Red, ConsoleColor.Green, ConsoleColor.Blue, ConsoleColor.Yellow, ConsoleColor.Cyan, ConsoleColor.Magenta };
        return colors[Math.Abs(instanceId.GetHashCode()) % colors.Length];
    }

    private static string GetInstanceName(string instanceId)
    {
        var names = new[] { "Instance-A", "Instance-B", "Instance-C", "Instance-D", "Instance-E", "Instance-F" };
        return names[Math.Abs(instanceId.GetHashCode()) % names.Length];
    }
}