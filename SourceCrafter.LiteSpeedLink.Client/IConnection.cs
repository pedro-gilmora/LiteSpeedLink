using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client;

public interface IConnection
{
    bool Send(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    bool Send<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    IEnumerable<TOut?> Enumerate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    IEnumerable<TOut?> Enumerate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    /// <summary>Unaria en bytes: <paramref name="request"/> es el cuerpo ya serializado; devuelve el cuerpo de la respuesta (lanza si el estado no es Success).</summary>
    ReadOnlyMemory<byte> GetRaw(long op, ReadOnlyMemory<byte> request, CancellationToken token = default);
}

/// <summary>Cuerpo ya serializado: el transporte lo copia tal cual tras su cabecera. <c>typeof</c> sobre él se pliega en JIT.</summary>
internal readonly record struct RawBody(ReadOnlyMemory<byte> Bytes);

public interface IAsyncConnection
{
    /// <summary>Unaria en bytes: <paramref name="request"/> es el cuerpo ya serializado; devuelve el cuerpo de la respuesta (lanza si el estado no es Success).</summary>
    ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default);
    ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
}