using SourceCrafter.LiteSpeedLink;

namespace Application.Contracts;


// Contracts layer
public interface IAuthService : IServiceUnit
{
    bool TryAuthenticate(Credentials credentials, out string token);
}

[MemoryPack.MemoryPackable]
public readonly partial record struct Credentials(string UserName, string Password);
