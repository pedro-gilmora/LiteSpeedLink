using SourceCrafter.LiteSpeedLink;
using WinChat;

var builder = WebApplication.CreateSlimBuilder(args);

// Kestrel acepta los sockets y el host generado atiende el protocolo LiteSpeedLink (sin HTTP).
// HostServices: los metodos factoria de ChatServer toman servicios de la app (ILogger, configuracion...).
builder.WebHost.UseLiteSpeedLink<ChatServer>(ChatEndpoint.Port);

builder.Build().Run();
