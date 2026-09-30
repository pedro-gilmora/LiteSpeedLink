using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Reflection.Metadata;

namespace Application.Contracts;


// Contracts layer
public interface IAuth : IServiceUnit
{
    bool TryAuthenticate(Credentials credentials, out string token);
    bool TryAuth(string user, string password, out string token);
    [return: ClientPostProcessor<Exclaim>]
    string Greet([ServerPreProcessor<TrimName>] string name);

    // Cliente: ClientPre, Pre, Processor -> red -> Servidor: ServerPre, Pre, Processor -> Echo
    // Servidor: ServerPost, Post, Processor -> red -> Cliente: ClientPost, Post, Processor
    [return: ServerPostProcessor<Bracket>, PostProcessor<Upper>, ClientPostProcessor<Exclaim>, Processor<Tag>]
    string Echo([ClientPreProcessor<TrimName>, PreProcessor<Upper>, ServerPreProcessor<Bracket>, Processor<Tag>] string text);

    // Servidor cambia tipos: red string -> ParseInt -> int Square(int) -> IntToString -> red string. Cliente: string Square(string)
    [return: ServerPostProcessor<IntToString>]
    int Square([ServerPreProcessor<ParseInt>] int value);

    // Cliente cambia tipos: int -> IntToString -> red string -> string Twice(string) -> red string -> ParseInt -> int. Cliente: int Twice(int)
    [return: ClientPostProcessor<ParseInt>]
    string Twice([ClientPreProcessor<IntToString>] string value);

    // Task sin resultado: raw (#12), el cuerpo de respuesta se ignora.
    Task TouchAsync(string user);
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
