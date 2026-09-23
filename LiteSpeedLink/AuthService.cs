//Console.WriteLine("Test");

//Console.WriteLine("Test");
using Application.Contracts;

namespace LiteSpeedLink;

// Implementation layer
public partial class AuthService() : IAuthService
{
    public string Greet(string name) => $"Hello, {name}!";

    public bool TryAuthenticate(Credentials credentials, out string token)
    {
        Console.WriteLine("Are credentials correct {0}", credentials is ("pedro", "test!123"));
        //authServiceLogger.Log(LogLevel.Information, "testing logging");
        if (credentials is ("pedro", "test!123"))
        {
            token = "Token";
            return true;
        }
        token = default!;
        return false;
    }
}