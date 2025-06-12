using GobanSource.Bus.Redis;

namespace GobanSource.Bus.Redis.Example.Messages;

public class HeartbeatMessage : BaseMessage
{
    public string Status { get; set; } = "ALIVE";
    public DateTime PingTime { get; set; } = DateTime.UtcNow;
}