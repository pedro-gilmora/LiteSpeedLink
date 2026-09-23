using Application.Contracts;

using LiteSpeedLink;
using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Connection info that allows clients to connect back to the server's DI scope.
/// </summary>
public sealed record TextServiceConnectionInfo(string Port);

[ServiceHost]
[ServiceProvider]
[Scoped<IAuthService, AuthService>]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class TextService;
