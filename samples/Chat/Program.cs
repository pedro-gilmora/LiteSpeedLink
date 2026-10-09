using Chat;
using System.Runtime.Versioning;

// dotnet run                          -> demo: servidor + dos usuarios en el mismo proceso
// dotnet run -- server [name]         -> solo servidor
// dotnet run -- join <user> [room] [name] -> cliente interactivo
const string DefaultPort = "my-chat-test";

var port = args switch
{
    ["server", var name, ..] => name,
    ["join", _, _, var name, ..] => name,
    _ => DefaultPort,
};

switch (args)
{
    case ["server", ..]:
        var server = ChatServer.Start(port);
        Console.WriteLine($"Chat server on mmf://{port}. Ctrl+C to stop.");
        await WaitForCtrlC();
        server.Dispose();
        return 0;

    case ["join", var user, ..]:
        var room = args.ElementAtOrDefault(2) ?? "lobby";
        var chat = new ChatServerClient(port).Chat;
        return await JoinInteractive(chat, user, room);

    default:
        return await Demo();
}

async Task<int> Demo()
{
    using var server = ChatServer.Start(port);

    var alice = new ChatServerClient(port).Chat;
    var bob = new ChatServerClient(port).Chat;

    using var stop = new CancellationTokenSource();
    var bobInbox = Listen(bob, "bob", "lobby", stop.Token);
    var aliceInbox = Listen(alice, "alice", "lobby", stop.Token);
    await WaitUntil(async () => await alice.OnlineAsync("lobby") == 2);

    await alice.PostAsync(new("lobby", "alice", "hi bob! no spam here"));
    await bob.PostAsync(new("lobby", "bob", "hey alice"));

    try { await bob.PostAsync(new("lobby", "bob", "   ")); }
    catch (Exception ex) { Console.WriteLine($"[moderation] empty message rejected by the server: {ex.Message}"); }

    await Task.Delay(300);
    stop.Cancel();
    var received = (await bobInbox).Concat(await aliceInbox).ToList();

    var ok = received.Any(m => m.User == "alice" && m.Text == "hi bob! no **** here")
          && received.Any(m => m.User == "bob" && m.Text == "hey alice")
          && received.All(m => !string.IsNullOrWhiteSpace(m.Text));
    Console.WriteLine(ok ? "OK" : "FAIL");

    return ok ? 0 : 1;
}

static async Task<List<ChatMessage>> Listen(ChatServerClient.ChatClient chat, string user, string room, CancellationToken token)
{
    var seen = new List<ChatMessage>();
    try
    {
        await foreach (var m in chat.Join(room, user, token))
        {
            Console.WriteLine($"[{user}'s screen] {m.User}: {m.Text}");
            seen.Add(m);
        }
    }
    catch (OperationCanceledException) { }
    return seen;
}

static async Task<int> JoinInteractive(ChatServerClient.ChatClient chat, string user, string room)
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

    var inbox = Task.Run(async () =>
    {
        await foreach (var m in chat.Join(room, user, stop.Token))
            if (m.User != user) Console.WriteLine(m.To is null ? $"[{m.User} said]: {m.Text}" : $"[From {m.User}]: {m.Text}");
    });

    Console.WriteLine($"Joined '{room}' as {user}. Type and press Enter; Ctrl+C to leave.");
    while (!stop.IsCancellationRequested && await Task.Run(Console.ReadLine, stop.Token) is { } line)
    {
        // "bob: hola" -> privado para bob; sin "destino:" -> a toda la sala
        var colon = line.IndexOf(':');
        var to = colon > 0 && !line.AsSpan(0, colon).Contains(' ') ? line[..colon] : null;
        try { await chat.PostAsync(new(room, user, to is null ? line : line[(colon + 1)..].Trim(), to)); }
        catch (Exception ex) { Console.WriteLine($"(rejected: {ex.Message})"); }
    }

    stop.Cancel();
    try { await inbox; } catch (OperationCanceledException) { }
    return 0;
}

static async Task WaitUntil(Func<Task<bool>> condition)
{
    for (var i = 0; i < 50 && !await condition(); i++) await Task.Delay(50);
}

static Task WaitForCtrlC()
{
    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    return done.Task;
}

[SupportedOSPlatform("windows")]
partial class Program;