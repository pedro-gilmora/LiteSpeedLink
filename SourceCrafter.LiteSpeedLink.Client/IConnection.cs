using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client
{
    public interface IConnection
    {
        ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
        IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    }
}