//Console.WriteLine("Test");
using Application.Contracts;

using SourceCrafter.LiteSpeedLink;
using System.Diagnostics;
using System.Runtime.Versioning;


var rpcName = $"Test{Guid.CreateVersion7()}";

using var server = TextService.Start(rpcName);

TextServiceClient client = new (rpcName);

Console.WriteLine("Hola!");
var timestamp = Stopwatch.GetTimestamp();
try
{
    //Debugger.Launch();
    if (client.Auth.TryAuthenticate(new("pedro", "test!123"), out var token))
    {
        Console.WriteLine(token);
    }
    Console.WriteLine($"Sent a greeting: {await client.Auth.GreetAsync("  Pedro  ")}");
    Console.WriteLine($"Echo (all processors): {await client.Auth.EchoAsync("  hi  ")}");
    Console.WriteLine($"Echo (sync): {client.Auth.Echo("  hi  ")}");
    Console.WriteLine($"Square (server TOut): {await client.Auth.SquareAsync("7")}");
    Console.WriteLine($"Twice (client TOut): {client.Auth.Twice(21) + 1}");
    try { client.Auth.Echo("   "); }
    catch (PipelineRejectedException ex) { Console.WriteLine($"Rejected: {ex.Message}"); }
}
catch (System.Exception ex)
{
    Console.WriteLine(ex.ToString());
}
finally
{
    Console.WriteLine($"Took: ${Stopwatch.GetElapsedTime(timestamp)}");
}

[SupportedOSPlatform("windows")]
partial class Program;