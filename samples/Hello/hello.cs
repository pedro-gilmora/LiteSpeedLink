// dotnet run hello.cs  -> RPC por memoria compartida: host y cliente en el mismo proceso
#:project ../../SourceCrafter.LiteSpeedLink.Server/SourceCrafter.LiteSpeedLink.Server.csproj
#:project ../../SourceCrafter.LiteSpeedLink.Client/SourceCrafter.LiteSpeedLink.Client.csproj
// Con los paquetes de nuget.org (quitar las ProjectReference de Directory.Build.props):
// #:package SourceCrafter.LiteSpeedLink.Server@2.26.282.164
// #:package SourceCrafter.LiteSpeedLink.Client@2.26.282.164

using System.Runtime.Versioning;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

using var server = GreeterServer.Start("hello-mmf");
var greeter = new GreeterServerClient("hello-mmf").Greeter;

Console.WriteLine(await greeter.GreetAsync("World"));

public interface IGreeter : IServiceUnit
{
    string Greet(string name);
}

public sealed class GreeterHandler : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}

[ServiceHost, ServiceProvider]
[Scoped<IGreeter, GreeterHandler>]
public partial class GreeterServer;

[ServiceClient, ServiceUnit<IGreeter>, ServiceProvider]
public sealed partial class GreeterServerClient;

[SupportedOSPlatform("windows")]
partial class Program;
