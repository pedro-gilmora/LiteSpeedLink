using Application.Contracts;

using SourceCrafter.LiteSpeedLink;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IStreams>]
[ServiceProvider]
public sealed partial class StreamServiceClient;
