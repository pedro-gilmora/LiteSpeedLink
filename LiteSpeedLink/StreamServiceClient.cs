using Application.Contracts;

using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IStreams>]
[ServiceProvider]
public sealed partial class StreamServiceClient;
