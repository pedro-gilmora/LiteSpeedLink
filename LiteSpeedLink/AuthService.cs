//Console.WriteLine("Test");

//Console.WriteLine("Test");
using Application.Contracts;

namespace LiteSpeedLink;

// Implementation layer
public partial class AuthService() : IAuthService
{
    public bool TryAuthenticate(Credentials credentials, out string token)
    {
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