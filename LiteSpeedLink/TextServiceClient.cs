using Application.Contracts;

using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient]
[ServiceProvider]
#if !NETSTANDARD
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public partial class TextServiceClient: IAuthService;
