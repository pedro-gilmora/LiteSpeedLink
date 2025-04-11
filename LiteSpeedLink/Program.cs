//Console.WriteLine("Test");
using Application.Contracts;

using SourceCrafter.LiteSpeedLink;

const int port = 5000;

using var server = TextService.Start(port);

TextServiceClient client = new("localhost", port);

Credentials creds = new("pedro", "test!123");

if (await client.TryAuthenticateAsync(creds) is (true, { } token))
{
    Console.WriteLine(token);
}
