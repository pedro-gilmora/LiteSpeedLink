using Application.Contracts;

using LiteSpeedLink;
using SourceCrafter.LiteSpeedLink;

using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.DependencyInjection.MsConfiguration.Metadata;

namespace SourceCrafter.LiteSpeedLink;


[ServiceHost]
[ServiceProvider]
[JsonConfiguration]
[JsonSetting<LocalOptions>("TextService")]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Bracket>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[Scoped<IAuth, AuthService>]
public partial class TextService;
