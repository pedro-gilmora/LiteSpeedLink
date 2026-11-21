using SourceCrafter.LiteSpeedLink;
using WinChat;

var builder = WebApplication.CreateSlimBuilder(args);

// Kestrel acepta los sockets y el host generado atiende el protocolo LiteSpeedLink (sin HTTP).
// HostServices: los [Hosted<T>] de ChatServer (ILogger, configuracion...) se piden a los servicios de la app.
builder.WebHost.UseLiteSpeedLink<ChatServer>(ChatEndpoint.Port);

builder.Build().Run();
