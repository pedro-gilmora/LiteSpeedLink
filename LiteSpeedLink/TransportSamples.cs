using Application.Contracts;

using LiteSpeedLink;
using LiteSpeedLink.Abstractions.Internals;

using SourceCrafter.DependencyInjection.Attributes;

namespace SourceCrafter.LiteSpeedLink;

// Mismo contrato y pipeline que TextService/TextServiceClient (Memory), por transporte.

[ServiceHost(ServiceConnectionType.Udp)]
[ServiceProvider]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Bracket>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[Scoped<IAuth, AuthService>]
public partial class UdpTextService;

[ServiceClient(ServiceConnectionType.Udp)]
[ServiceUnit<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
public sealed partial class UdpTextServiceClient;

[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Bracket>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[Scoped<IAuth, AuthService>]
public partial class TcpTextService;

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
public sealed partial class TcpTextServiceClient;

[ServiceHost(ServiceConnectionType.Quic)]
[ServiceProvider]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Bracket>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
[Scoped<IAuth, AuthService>]
public partial class QuicTextService;

[ServiceClient(ServiceConnectionType.Quic)]
[ServiceUnit<IAuth>]
[ServiceProvider]
[Singleton<Exclaim>]
[Singleton<TrimName>]
[Singleton<Upper>]
[Singleton<Tag>]
[Singleton<ParseInt>]
[Singleton<IntToString>]
public sealed partial class QuicTextServiceClient;
