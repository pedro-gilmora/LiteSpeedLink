using Jab;

using Microsoft.Extensions.Logging;

namespace SourceCrafter.LiteSpeedLink;

[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Singleton<AuthService>]
public partial class TextService;
