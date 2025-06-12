using GobanSource.Bus.Redis;

namespace GobanSource.Bus.Redis.Example.Messages;

public class NotificationMessage : BaseMessage
{
    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
}