//Console.WriteLine("Test");
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

const int port = 5000;

using var server = TextService.Start(port);
await using TextServiceClient client = new("localhost", port);

Credentials creds = new("pedro", "test!123");

if (((IAuthService)client).Authenticate(creds, out var token))
{
    Console.WriteLine(token);
}

if (await client.AuthenticateAsync(creds) is (true, { } token2))
{
    Console.WriteLine(token2);
}

// Implementation layer
public partial class AuthService : IAuthService
{
    public bool Authenticate(Credentials credentials, out string token)
    {
        if (credentials is ("pedro", "test!123"))
        {
            token = "Token";
            return true;
        }
        token = default!;
        return false;
    }
}

// Contracts layer
public interface IAuthService : IServiceUnit
{
    bool Authenticate(Credentials credentials, out string token);
}

[MemoryPackable]
public readonly partial record struct Credentials(string UserName, string Password);