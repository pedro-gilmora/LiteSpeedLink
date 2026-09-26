using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

public sealed class TcpConnection(EndPoint endpoint, X509Certificate2? cert = default) : IConnectionAsync, IAsyncDisposable
{
    internal TcpClient? connection;
    internal Stream? stream;
    private MultiplexedChannel? _channel;
    private readonly SemaphoreSlim _init = new(1, 1);

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();

        if (stream is not null)
            await stream.DisposeAsync();

        connection?.Dispose();
    }

    internal async ValueTask<MultiplexedChannel> TryInitializeAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _channel) is { } ready) return ready;

        await _init.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_channel is not null) return _channel;

            connection ??= new TcpClient { NoDelay = true };

            string targetHost;

            switch (endpoint)
            {
                case DnsEndPoint { Host: string host, Port: int port }:

                    await connection.ConnectAsync(host, port, token).ConfigureAwait(false);
                    targetHost = host;
                    break;

                case IPEndPoint ip:

                    await connection.ConnectAsync(ip, token).ConfigureAwait(false);
                    targetHost = ip.Address.ToString();
                    break;

                default:

                    throw new Exception($"Unsupported endpoint: [{endpoint}]");
            }

            stream = connection.GetStream();

            if (cert is not null)
            {
                var ssl = new SslStream(stream, false);

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    ClientCertificates = [cert],
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, token).ConfigureAwait(false);

                stream = ssl;
            }

            var channel = new MultiplexedChannel(PipeReader.Create(stream), PipeWriter.Create(stream), endpoint.ToString()!);

            Volatile.Write(ref _channel, channel);

            return channel;
        }
        finally
        {
            _init.Release();
        }
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await TryInitializeAsync(token).ConfigureAwait(false)).GetAsync<TIn, TOut>(op, payload, true, token).ConfigureAwait(false);

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await TryInitializeAsync(token).ConfigureAwait(false)).GetAsync<byte, TOut>(op, default, false, token).ConfigureAwait(false);

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await TryInitializeAsync(token).ConfigureAwait(false)).SendAsync(op, payload, true, token).ConfigureAwait(false);

    public async ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await TryInitializeAsync(token).ConfigureAwait(false)).SendAsync<byte>(op, default, false, token).ConfigureAwait(false);

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var channel = await TryInitializeAsync(token).ConfigureAwait(false);

        await foreach (var item in channel.EnumerateAsync<TIn, TOut>(op, payload, true, token).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var channel = await TryInitializeAsync(token).ConfigureAwait(false);

        await foreach (var item in channel.EnumerateAsync<byte, TOut>(op, default, false, token).ConfigureAwait(false))
            yield return item;
    }
}
