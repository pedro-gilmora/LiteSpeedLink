using Jab;

namespace SourceCrafter.MemLink;

[ServiceHost]
[ServiceProvider]
[Transient<SumService>]
public partial class TextService
{
}
