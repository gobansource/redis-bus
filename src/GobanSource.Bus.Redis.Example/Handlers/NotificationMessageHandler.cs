using GobanSource.Bus.Redis;
using GobanSource.Bus.Redis.Example.Messages;
using Microsoft.Extensions.Logging;

namespace GobanSource.Bus.Redis.Example.Handlers;

public class NotificationMessageHandler : IMessageHandler<NotificationMessage>
{
    private readonly ILogger<NotificationMessageHandler> _logger;

    public NotificationMessageHandler(ILogger<NotificationMessageHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(NotificationMessage message)
    {
        var instanceColor = GetInstanceColor(message.InstanceId);
        Console.ForegroundColor = instanceColor;
        Console.WriteLine($"[Received from {GetInstanceName(message.InstanceId)}] NOTIFICATION: {message.Title} - {message.Content}");
        Console.ResetColor();

        await Task.CompletedTask;
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