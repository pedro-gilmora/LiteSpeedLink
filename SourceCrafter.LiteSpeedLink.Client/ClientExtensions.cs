using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using static System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

public static partial class ClientExtensions
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QuicConnection AsQuicConnection(this EndPoint ip, X509Certificate2? cert = default, int unaryStreams = QuicConnection.DefaultUnaryStreams, TlsMode tls = TlsMode.Default, CertificateCheck check = CertificateCheck.Pinned,
        Func<long, int>? maxMessageSize = null)
    {
        // QUIC siempre cifra: Default es TLS mutuo; el servidor se valida siempre (antes se aceptaba cualquier certificado).
        if (tls is TlsMode.None) throw new ArgumentException("QUIC always uses TLS.", nameof(tls));

        return new(new()
        {
            RemoteEndPoint = ip,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ClientAuthenticationOptions = TlsPolicy.ForClient(ip, cert, tls is TlsMode.Default ? TlsMode.Mutual : tls, check,
                System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, [Constants.protocol])!
        }, unaryStreams, maxMessageSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UdpConnection AsUdpConnection(this EndPoint ip) => new(ip);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SupportedOSPlatform("windows")]
    public static MemoryConnection AsMemoryConnection(this string ctxName, int timeout = 5000, System.Text.Encoding? encoding = null) => new(ctxName, timeout, encoding);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TcpConnection AsTcpConnection(this EndPoint ip, X509Certificate2? cert = null, TlsMode tls = TlsMode.Default, CertificateCheck check = CertificateCheck.Pinned,
        Func<long, int>? maxMessageSize = null)
        => new(ip, cert, tls: tls, check: check, maxMessageSize: maxMessageSize);

    // Miembros sincronos del contrato sobre transportes de red (Tcp/Udp/Quic): comparten socket con un read loop
    // async y correlacion por corrId, asi que no hay ruta sincrona nativa. Solo Memory (RpcBuffer) es sincrono de
    // verdad: implementa IConnection como instancia y gana en la resolucion de sobrecargas.
    // ponytail: sync-over-async bloquea un hilo por llamada; en red usar las variantes *Async generadas.
    public static bool Send(this IAsyncConnection c, long op, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        c.SendAsync(op, token, name).AsTask().GetAwaiter().GetResult();
        return true;
    }

    public static bool Send<[DynamicallyAccessedMembers(All)] TIn>(this IAsyncConnection c, long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        c.SendAsync(op, payload, token, name).AsTask().GetAwaiter().GetResult();
        return true;
    }

    public static TOut? Get<[DynamicallyAccessedMembers(All)] TIn, [DynamicallyAccessedMembers(All)] TOut>(this IAsyncConnection c, long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
        => c.GetAsync<TIn, TOut>(op, payload, token, name).AsTask().GetAwaiter().GetResult();

    public static TOut? Get<[DynamicallyAccessedMembers(All)] TOut>(this IAsyncConnection c, long op, CancellationToken token = default, [CallerMemberName] string name = "")
        => c.GetAsync<TOut>(op, token, name).AsTask().GetAwaiter().GetResult();

    public static IEnumerable<TOut?> Enumerate<[DynamicallyAccessedMembers(All)] TIn, [DynamicallyAccessedMembers(All)] TOut>(this IAsyncConnection c, long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
        => c.EnumerateAsync<TIn, TOut>(op, payload, token, name).ToBlockingEnumerable(token);

    public static IEnumerable<TOut?> Enumerate<[DynamicallyAccessedMembers(All)] TOut>(this IAsyncConnection c, long op, CancellationToken token = default, [CallerMemberName] string name = "")
        => c.EnumerateAsync<TOut>(op, token, name).ToBlockingEnumerable(token);

    public static TOut GetRaw<TState, TOut>(this IAsyncConnection c, long op, TState state, Action<System.Buffers.IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token = default)
        => c.GetRawAsync(op, state, write, read, token).AsTask().GetAwaiter().GetResult();
}
