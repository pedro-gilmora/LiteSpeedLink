//Console.WriteLine("Test");
using Application.Contracts;

using SourceCrafter.LiteSpeedLink;
using System.Diagnostics;
using System.Runtime.Versioning;

Console.WriteLine("Wait for debbuger...");
Console.ReadLine();

var rpcName = $"Test{Guid.CreateVersion7()}";

using var server = TextService.Start(rpcName);

TextServiceClient client = new (rpcName);

Console.WriteLine("Hola!");
var timestamp = Stopwatch.GetTimestamp();
try
{
    //Debugger.Launch();
    if (await client.TryAuthenticateAsync(new("pedro", "test!123")) is (true, var token))
    {
        Console.WriteLine(token);
    }

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