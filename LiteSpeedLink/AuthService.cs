
using Application.Contracts;

namespace LiteSpeedLink;

// Implementation layer
public partial class AuthService() : IAuth
{
    public string Greet(string name) => $"Hello, {name}!";

    public string Echo(string text) => text;

    public int Square(int value) => value * value;

    public string Twice(string value) => value + value;

    public int Touched;

    public Task TouchAsync(string user)
    {
        if (user != "pedro") throw new ArgumentException(user);
        Interlocked.Increment(ref Touched);
        return Task.CompletedTask;
    }

    public void Bump(ref int counter, out string label) => label = $"#{++counter}";

    public string Shout(in string text) => text + "!";

    public string Normalize(ref string text, out int length)
    {
        length = text.Length;
        text = $"<{text}>";
        return "ok";
    }

    public byte[] Download(int size) => new byte[size];

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

    public bool TryAuth(string user, string password, out string token)
    {
        if (user == "pedro" && password == "test!123")
        {
            token = "Token";
            return true;
        }
        token = default!;
        return false;
    }
}
