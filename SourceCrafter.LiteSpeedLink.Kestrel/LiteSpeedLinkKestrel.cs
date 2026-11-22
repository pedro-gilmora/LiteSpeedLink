using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SourceCrafter.LiteSpeedLink;

public static class LiteSpeedLinkKestrel
{
    /// <summary>
    /// Kestrel acepta las conexiones TCP de <paramref name="endpoint"/> (sin HTTP) y <typeparamref name="THost"/> las atiende.
    /// El host es un singleton de la app con <see cref="IHostedLiteSpeedLink.HostServices"/> = sus servicios.
    /// De Kestrel se toma su configuracion y su conexion: TLS con su adaptador HTTPS (sus valores por defecto: certificado, protocolos,
    /// <c>HandshakeTimeout</c>) mas el modo y la validacion que <typeparamref name="THost"/> fija en compilacion (<c>[ServiceHost(Tls, Check)]</c>);
    /// tope de conexiones (<c>Limits.MaxConcurrentConnections</c>); fin de conexion (cierre del cliente o su peticion de cierre al parar);
    /// plazos de una trama a medias (<c>Limits.RequestHeadersTimeout</c> sin bytes nuevos y ritmo minimo <c>Limits.MinRequestBodyDataRate</c>).
    /// Kestrel no limita por direccion: <paramref name="maxConnectionsPerAddress"/> (IPv6 por /64) se aplica antes del handshake TLS.
    /// </summary>
    public static IWebHostBuilder UseLiteSpeedLink<THost>(this IWebHostBuilder builder, IPEndPoint endpoint, X509Certificate2? certificate = null, int maxConnectionsPerAddress = Server.MaxConnectionsPerAddress)
        where THost : class, IHostedLiteSpeedLink, new()
        => builder
            .ConfigureServices(services => services
                .AddSingleton(sp => new THost { HostServices = sp })
                .AddHostedService(sp => new HostDisposer<THost>(sp.GetRequiredService<THost>())))
            .ConfigureKestrel(kestrel => kestrel.Listen(endpoint, listen =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(maxConnectionsPerAddress, 1);

                // Primero: una direccion por encima de su limite se corta sin gastar un handshake TLS.
                var addresses = new AddressLimiter(maxConnectionsPerAddress);

                listen.Use(next => async connection =>
                {
                    if (!addresses.TryEnter(connection.RemoteEndPoint, out var address))
                    {
                        connection.Abort();
                        return;
                    }

                    try { await next(connection); }
                    finally { addresses.Exit(address); }
                });

                if (THost.Tls is TlsMode.Server or TlsMode.Mutual || (THost.Tls is TlsMode.Default && certificate is not null))
                    listen.UseHttps(https =>
                    {
                        // Parte de ConfigureHttpsDefaults; el certificado explicito gana.
                        if (certificate is not null) https.ServerCertificate = certificate;

                        https.CheckCertificateRevocation = false;

                        if (THost.Tls is TlsMode.Server)
                        {
                            https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                            return;
                        }

                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                        // Validador construido una vez (pin SHA-256 precalculado) para todas las conexiones.
                        var validate = TlsPolicy.Validator(THost.Check is CertificateCheck.Chain ? null : https.ServerCertificate
                            ?? throw new InvalidOperationException($"CertificateCheck.{THost.Check} pins the server certificate: pass it to UseLiteSpeedLink or set it in ConfigureHttpsDefaults."), THost.Check);

                        https.ClientCertificateValidation = (remote, chain, errors) => validate(https, remote, chain, errors);
                    });

                var services = listen.ApplicationServices;
                var host = services.GetRequiredService<THost>();
                // Sin la peticion de cierre de Kestrel (no deberia faltar): ApplicationStopping, para no agotar ShutdownTimeout con streams abiertos.
                var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
                var limits = listen.KestrelServerOptions.Limits;

                listen.Use(_ => async connection =>
                {
                    var closing = connection.Features.Get<IConnectionLifetimeNotificationFeature>()?.ConnectionClosedRequested ?? stopping;

                    using var token = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed, closing);
                    // Leido por conexion: la configuracion de Kestrel puede cambiar despues de UseLiteSpeedLink.
                    var rate = limits.MinRequestBodyDataRate;
                    var bodySize = limits.MaxRequestBodySize;
                    await host.ServeAsync(connection.Transport.Input, connection.Transport.Output, token.Token, TimeoutMs(limits.RequestHeadersTimeout),
                        rate is null ? 0 : (int)Math.Ceiling(rate.BytesPerSecond), rate is null ? 0 : TimeoutMs(rate.GracePeriod),
                        bodySize is null ? Framing.MaxMessageSize : (int)Math.Min(bodySize.Value, Framing.MaxMessageSize),
                        TimeoutMs(limits.KeepAliveTimeout));
                });
            }));

    private static int TimeoutMs(TimeSpan timeout)
        => timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds >= int.MaxValue ? Timeout.Infinite : (int)timeout.TotalMilliseconds;

    /// <inheritdoc cref="UseLiteSpeedLink{THost}(IWebHostBuilder, IPEndPoint, X509Certificate2?, int)"/>
    public static IWebHostBuilder UseLiteSpeedLink<THost>(this IWebHostBuilder builder, int port, X509Certificate2? certificate = null, int maxConnectionsPerAddress = Server.MaxConnectionsPerAddress)
        where THost : class, IHostedLiteSpeedLink, new()
        => builder.UseLiteSpeedLink<THost>(new IPEndPoint(IPAddress.Any, port), certificate, maxConnectionsPerAddress);

    /// <summary>
    /// Libera el host tras vaciar Kestrel y antes de que la app libere sus servicios: los propios del host aun pueden usarlos.
    /// La liberacion posterior del contenedor de la app es un no-op.
    /// </summary>
    private sealed class HostDisposer<THost>(THost host) : IHostedLifecycleService
    {
        public async Task StoppedAsync(CancellationToken cancellationToken)
        {
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (host is IDisposable disposable) disposable.Dispose();
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
