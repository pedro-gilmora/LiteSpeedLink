using Jab;

using Microsoft.Extensions.Logging;

namespace SourceCrafter.MemLink;

[ServiceHost]
[ServiceProvider]
[Transient<SumService>]
public partial class TextService;
