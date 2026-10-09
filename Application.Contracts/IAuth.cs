using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Reflection.Metadata;

namespace Application.Contracts;


// Contracts layer
public interface IAuth : IServiceUnit
{
    bool TryAuthenticate(Credentials credentials, out string token);
    bool TryAuth(string user, string password, out string token);
    [return: ClientProcessor<Exclaim>]
    string Greet([ServerProcessor<TrimName>] string name);

    // Cliente: TrimName, Upper, Tag -> red -> Servidor: Upper, Bracket, Tag -> Echo
    // Servidor: Bracket, Upper, Tag -> red -> Cliente: Upper, Exclaim, Tag
    [return: ServerProcessor<Bracket>, ServerProcessor<Upper>, ClientProcessor<Upper>, ClientProcessor<Exclaim>, ServerProcessor<Tag>, ClientProcessor<Tag>]
    string Echo([ClientProcessor<TrimName>, ClientProcessor<Upper>, ServerProcessor<Upper>, ServerProcessor<Bracket>, ClientProcessor<Tag>, ServerProcessor<Tag>] string text);

    // Servidor cambia tipos: red string -> ParseInt -> int Square(int) -> IntToString -> red string. Cliente: string Square(string)
    [return: ServerProcessor<IntToString>]
    int Square([ServerProcessor<ParseInt>] int value);

    // Cliente cambia tipos: int -> IntToString -> red string -> string Twice(string) -> red string -> ParseInt -> int. Cliente: int Twice(int)
    [return: ClientProcessor<ParseInt>]
    string Twice([ClientProcessor<IntToString>] string value);

    // Task sin resultado: raw (#12), el cuerpo de respuesta se ignora.
    Task TouchAsync(string user);

    // void con ref + out: el lector de respuesta escribe ambos (6e).
    void Bump(ref int counter, out string label);

    // 'in' con procesadores en ambos lados: cliente TrimName, servidor Upper.
    string Shout([ClientProcessor<TrimName>, ServerProcessor<Upper>] in string text);

    // ref con procesadores que preservan el tipo (ida) + out (vuelta) + post en el retorno.
    [return: ClientProcessor<Exclaim>]
    string Normalize([ClientProcessor<TrimName>, ServerProcessor<Upper>] ref string text, out int length);

    // Respuesta grande: en QUIC va por stream propio y no bloquea las unarias del pool (#14); otros transportes lo ignoran.
    [DedicatedStream]
    byte[] Download(int size);
}

public sealed class ParseInt : IPipeline<string, int>
{
    public (ResponseStatus, int) Process(string input) =>
        int.TryParse(input, out var n) ? (ResponseStatus.Success, n) : (ResponseStatus.Failed, 0);
}

public sealed class IntToString : IPipeline<int, string>
{
    public (ResponseStatus, string) Process(int input) => (ResponseStatus.Success, input.ToString());
}

public sealed class TrimName : IPipeline<string>
{
    public (ResponseStatus, string) Process(string input) =>
        string.IsNullOrWhiteSpace(input) ? (ResponseStatus.Failed, input) : (ResponseStatus.Success, input.Trim());
}

public sealed class Exclaim : IAsyncValuePipeline<string>
{
    public ValueTask<(ResponseStatus, string)> ProcessAsync(string input) => new((ResponseStatus.Success, input + "!"));
}

public sealed class Upper : IPipeline<string>
{
    public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.ToUpperInvariant());
}

public sealed class Bracket : IAsyncPipeline<string>
{
    public Task<(ResponseStatus, string)> ProcessAsync(string input) => Task.FromResult((ResponseStatus.Success, "[" + input + "]"));
}

public sealed class Tag : IPipeline<string>
{
    public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, "#" + input);
}

[MemoryPackable]
public readonly partial record struct Credentials(string UserName, string Password);
