using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SourceCrafter.LiteSpeedLink;

public static class LiteSpeedLinkKestrel
{
    /// <summary>
    /// Kestrel acepta las conexiones TCP de <paramref name="endpoint"/> (sin HTTP) y <typeparamref name="THost"/> las atiende.
    /// El host es un singleton de la app con <see cref="IHostedLiteSpeedLink.HostServices"/> = sus servicios.
    /// Con <paramref name="certificate"/>: TLS mutuo de Kestrel, como <c>StartTcpServer</c> (el cliente generado pasa el mismo certificado).
    /// </summary>
    public static IWebHostBuilder UseLiteSpeedLink<THost>(this IWebHostBuilder builder, IPEndPoint endpoint, X509Certificate2? certificate = null)
        where THost : class, IHostedLiteSpeedLink, new()
        => builder
            .ConfigureServices(services => services
                .AddSingleton(sp => new THost { HostServices = sp })
                .AddHostedService(sp => new HostDisposer<THost>(sp.GetRequiredService<THost>())))
            .ConfigureKestrel(kestrel => kestrel.Listen(endpoint, listen =>
            {
                if (certificate is not null)
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = certificate,
                        SslProtocols = SslProtocols.Tls12,
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                        CheckCertificateRevocation = false,
                        // Pinning: cadena valida o exactamente el certificado configurado (autofirmado de desarrollo).
                        ClientCertificateValidation = (remote, _, errors) =>
                            errors == SslPolicyErrors.None || remote.GetCertHashString() == certificate.GetCertHashString()
                    });

                var services = listen.ApplicationServices;
                var host = services.GetRequiredService<THost>();
                // Solo ConnectionClosed: Kestrel esperaria ShutdownTimeout con streams abiertos y luego abortaria.
                var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

                listen.Use(_ => async connection =>
                {
                    using var token = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed, stopping);
                    await host.ServeAsync(connection.Transport.Input, connection.Transport.Output, token.Token);
                });
            }));

    /// <inheritdoc cref="UseLiteSpeedLink{THost}(IWebHostBuilder, IPEndPoint, X509Certificate2?)"/>
    public static IWebHostBuilder UseLiteSpeedLink<THost>(this IWebHostBuilder builder, int port, X509Certificate2? certificate = null)
        where THost : class, IHostedLiteSpeedLink, new()
        => builder.UseLiteSpeedLink<THost>(new IPEndPoint(IPAddress.Any, port), certificate);

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
