using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;

namespace SourceCrafter.LiteSpeedLink
{
    public interface IRequestContext
    {
        TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>();
        public ValueTask YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn payload, CancellationToken token = default);
        ValueTask EndStreamingAsync(CancellationToken token = default);
        ValueTask ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload, CancellationToken token = default);
    }
}