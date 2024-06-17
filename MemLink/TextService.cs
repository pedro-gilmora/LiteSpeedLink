using Jab;

namespace SourceCrafter.MemLink;

[ServiceHost]
[ServiceProvider]
[Transient<SumService>(Name = "sum")]
public partial class TextService;
