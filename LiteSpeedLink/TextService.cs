using Application.Contracts;

using LiteSpeedLink;
using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

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
public partial class TextService;
