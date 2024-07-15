using Jab;

namespace SourceCrafter.LiteSpeedLink;

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceProvider]
public partial class TextServiceClient : IAuthService
{
}
