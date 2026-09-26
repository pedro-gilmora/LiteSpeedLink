
using Application.Contracts;

namespace LiteSpeedLink;

// Implementation layer
public partial class AuthService() : IAuth
{
    public string Greet(string name) => $"Hello, {name}!";

    public string Echo(string text) => text;

    public int Square(int value) => value * value;

    public string Twice(string value) => value + value;

    public bool TryAuthenticate(Credentials credentials, out string token)
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
