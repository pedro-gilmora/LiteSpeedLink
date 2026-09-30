using Application.Contracts;

using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>Host TCP con todas las combinaciones de stream x politica; valida lo que emite el generador.</summary>
[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Scoped<IStreams, Streams>]
public partial class StreamService;
