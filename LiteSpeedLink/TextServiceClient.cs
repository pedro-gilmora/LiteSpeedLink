using Application.Contracts;

using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient]
[ClientService<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
#if !NETSTANDARD
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public partial class TextServiceClient;
