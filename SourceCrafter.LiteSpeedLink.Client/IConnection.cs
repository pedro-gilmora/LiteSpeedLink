using System.Buffers;
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
    /// <summary>
    /// Unaria raw sin copias: <paramref name="write"/> serializa <paramref name="state"/> directamente en la trama del transporte y
    /// <paramref name="read"/> decodifica el cuerpo de la respuesta sobre el buffer del transporte (lanza si el estado no es Success).
    /// </summary>
    TOut GetRaw<TState, TOut>(long op, TState state, Action<IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token = default);
}

/// <summary>Lector de un cuerpo de respuesta; el span apunta al buffer del transporte y solo es valido durante la llamada.</summary>
public delegate TOut RawReader<TOut>(ReadOnlySpan<byte> body);

/// <summary>Cuerpo raw escrito por el transporte en su trama. Struct: el <c>is</c> sobre un <c>TIn</c> generico se pliega en JIT sin box.</summary>
internal interface IRawBody { void WriteTo(IBufferWriter<byte> writer); }

internal readonly struct RawBody<TState>(TState state, Action<IBufferWriter<byte>, TState> write) : IRawBody
{
    public void WriteTo(IBufferWriter<byte> writer) => write(writer, state);
}

public interface IAsyncConnection
{
    /// <inheritdoc cref="IConnection.GetRaw{TState, TOut}"/>
    ValueTask<TOut> GetRawAsync<TState, TOut>(long op, TState state, Action<IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token = default);
    ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
    IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
    IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, CancellationToken token = default, [CallerMemberName] string name = "");
}
