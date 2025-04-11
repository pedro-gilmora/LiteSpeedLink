using Application.Contracts;

using LiteSpeedLink;
using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

[ServiceHost(ServiceConnectionType.Udp)]
[ServiceContainer]
[Scoped<IAuthService, AuthService>]
public partial class TextService;