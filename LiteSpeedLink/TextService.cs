using Application.Contracts;

using LiteSpeedLink;
using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;


[ServiceHost]
[ServiceProvider]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Bracket>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[Scoped<IAuth, AuthService>]
[SupportedOSPlatform("windows")]
public partial class TextService;
