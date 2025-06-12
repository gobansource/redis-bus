using GobanSource.Bus.Redis;

namespace GobanSource.Bus.Redis.Example.Messages;

public class OrderMessage : BaseMessage
{
    public string OrderId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Product { get; set; } = null!;
    public int Quantity { get; set; }
}