using Application.Contracts;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.DependencyInjection.MsConfiguration.Metadata;
using SourceCrafter.LiteSpeedLink;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient]
[ServiceProvider]
[JsonConfiguration]
[JsonSetting<LocalOptions>("TextService")]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[ServiceUnit<IAuth>]
public sealed partial class TextServiceClient;
