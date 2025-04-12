namespace GobanSource.Bus.Redis;

/// <summary>
/// Defines a handler for processing specific message types in the messaging system.
/// </summary>
/// <typeparam name="TMessage">The type of message this handler processes.</typeparam>
public interface IMessageHandler<TMessage> where TMessage : IMessage
{
    /// <summary>
    /// Handles the processing of a message.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(TMessage message);
}