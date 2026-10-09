using Application.Contracts;

using SourceCrafter.LiteSpeedLink;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient]
[ServiceUnit<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
public sealed partial class TextServiceClient;
