using System.Collections.Concurrent;
using System.Threading.Channels;
using MemoryPack;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

namespace Chat;

[MemoryPackable]
public sealed partial record ChatMessage(string Room, string User, string Text, string? To = null);

/// <summary>Contrato compartido: host y cliente se generan a partir de esta interfaz.</summary>
public interface IChat : IServiceUnit
{
    /// <summary>La moderacion corre en el host antes del handler; un rechazo no llega a la sala.</summary>
    bool Post([ServerProcessor<Moderate>] ChatMessage message);

    /// <summary>Suscripcion a una sala: cada mensaje viaja en cuanto se publica (sin lotes).</summary>
    [Stream(Batch = 0, IdleTimeoutMs = Timeout.Infinite)]
    IAsyncEnumerable<ChatMessage> Join(string room, string user, CancellationToken token);

    int Online(string room);
}

public sealed class Moderate : IPipeline<ChatMessage>
{
    static readonly string[] Banned = ["spam", "scam"];

    public (ResponseStatus, ChatMessage) Process(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > 500) return (ResponseStatus.Failed, message);

        var text = message.Text.Trim();
        foreach (var word in Banned) text = text.Replace(word, new string('*', word.Length), StringComparison.OrdinalIgnoreCase);

        return (ResponseStatus.Success, message with { Text = text });
    }
}

/// <summary>Estado compartido por todas las conexiones: una cola por suscriptor y sala.</summary>
public sealed class ChatRooms
{
    // ponytail: instancia estática y no [Singleton]: el generador 2.26.282.164 expondría Publish como RPC (saltando la moderación).
    public static readonly ChatRooms Shared = new();

    readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<ChatMessage>, string>> _rooms = new();

    public ConcurrentDictionary<Channel<ChatMessage>, string> Room(string name) => _rooms.GetOrAdd(name, static _ => new());

    public void Publish(ChatMessage message)
    {
        foreach (var (subscriber, user) in Room(message.Room))
            if (message.To is null || message.To == user) subscriber.Writer.TryWrite(message);
    }
}

public sealed class ChatHandler : IChat
{
    readonly ChatRooms rooms = ChatRooms.Shared;

    public bool Post(ChatMessage message)
    {
        rooms.Publish(message);
        return true;
    }

    public async IAsyncEnumerable<ChatMessage> Join(string room, string user, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var inbox = Channel.CreateUnbounded<ChatMessage>();
        var members = rooms.Room(room);
        members[inbox] = user;
        rooms.Publish(new(room, "system", $"{user} joined"));

        try
        {
            await foreach (var message in inbox.Reader.ReadAllAsync(token)) yield return message;
        }
        finally
        {
            members.TryRemove(inbox, out _);
            rooms.Publish(new(room, "system", $"{user} left"));
        }
    }

    public int Online(string room) => rooms.Room(room).Count;
}

[ServiceHost]
[ServiceProvider]
[Singleton<Moderate>]
[Scoped<IChat, ChatHandler>]
public partial class ChatServer;

[ServiceClient]
[ServiceUnit<IChat>]
[ServiceProvider]
public sealed partial class ChatServerClient;
