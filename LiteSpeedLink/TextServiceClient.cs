using Application.Contracts;

using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient(ServiceConnectionType.Udp)]
[ServiceContainer]
public partial class TextServiceClient: IAuthService;
