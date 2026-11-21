using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace WinChat;

[MemoryPackable]
public sealed partial record ChatMessage(string Room, string User, string Text);

public interface IChat : IServiceUnit
{
    void Post(ChatMessage message);

    /// <summary>Suscripcion a una sala: cada mensaje viaja en cuanto se publica (sin lotes).</summary>
    [Stream(Batch = 0, IdleTimeoutMs = Timeout.Infinite)]
    IAsyncEnumerable<ChatMessage> Join(string room, string user, CancellationToken token);
}

public static class ChatEndpoint
{
    public const int Port = 5310;
}
