using System.Collections.Concurrent;
using System.Threading.Channels;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

namespace WinChat;

/// <summary>Estado compartido por todas las conexiones: una cola por suscriptor y sala.</summary>
public sealed class ChatRooms
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<ChatMessage>, string>> _rooms = new();

    public ConcurrentDictionary<Channel<ChatMessage>, string> Room(string name) => _rooms.GetOrAdd(name, static _ => new());

    public void Publish(ChatMessage message)
    {
        foreach (var subscriber in Room(message.Room).Keys) subscriber.Writer.TryWrite(message);
    }
}

public sealed class ChatHandler(ChatRooms rooms, ILogger<ChatHandler> logger) : IChat
{
    public void Post(ChatMessage message) => rooms.Publish(message);

    public async IAsyncEnumerable<ChatMessage> Join(string room, string user, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var inbox = Channel.CreateUnbounded<ChatMessage>();
        var members = rooms.Room(room);
        members[inbox] = user;
        logger.LogInformation("{User} joined {Room}", user, room);
        rooms.Publish(new(room, "system", $"{user} joined"));

        try
        {
            await foreach (var message in inbox.Reader.ReadAllAsync(token)) yield return message;
        }
        finally
        {
            members.TryRemove(inbox, out _);
            logger.LogInformation("{User} left {Room}", user, room);
            rooms.Publish(new(room, "system", $"{user} left"));
        }
    }
}

[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Singleton<ChatRooms>]
[Singleton<ILogger<ChatHandler>>(source: nameof(GetLogger))]
[Scoped<IChat, ChatHandler>]
public partial class ChatServer
{
    static ILogger<ChatHandler> GetLogger([Root] ChatServer host) => host.HostService<ILogger<ChatHandler>>();
}
