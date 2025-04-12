using System;

namespace GobanSource.Bus.Redis;

public abstract class BaseMessage : IMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string AppId { get; set; } = null!;
    public string InstanceId { get; set; } = null!;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public interface IMessage
{
    string MessageId { get; set; }
    string AppId { get; set; }
    string InstanceId { get; set; }
    DateTime Timestamp { get; set; }
}