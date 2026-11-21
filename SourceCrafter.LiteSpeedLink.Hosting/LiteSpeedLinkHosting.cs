using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Endpoint de un host generado: el generador emite <c>THost.Endpoint</c> con miembros <c>required</c> segun el transporte y el TLS
/// fijados en compilacion (puerto o nombre, certificado, <c>MaxAmplification</c>), asi que lo que falta no compila.
/// </summary>
public interface IHostedEndpoint<THost> where THost : class
{
    /// <summary>El host con <c>HostServices</c> = servicios de la app: sus <c>[Hosted&lt;T&gt;]</c> salen de ellos.</summary>
    THost Create(IServiceProvider services);

    /// <summary>Empieza a escuchar; lo que devuelve deja de escuchar al liberarse.</summary>
    ValueTask<IAsyncDisposable> StartAsync(THost host, CancellationToken token);
}

public static class LiteSpeedLinkHosting
{
    /// <summary>
    /// Cualquier host de .NET (<c>IHost</c>: Generic Host, Worker Service, ASP.NET Core): <typeparamref name="THost"/> es un singleton de la app
    /// y escucha como servicio hospedado, hasta <c>ApplicationStopping</c>. <c>builder.Services.AddLiteSpeedLink(new ChatServer.Endpoint { Port = 5310 })</c>.
    /// </summary>
    public static IServiceCollection AddLiteSpeedLink<THost>(this IServiceCollection services, IHostedEndpoint<THost> endpoint)
        where THost : class
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        services.AddSingleton<THost>(endpoint.Create);
        services.AddHostedService(sp => new LiteSpeedLinkService<THost>(sp.GetRequiredService<THost>(), endpoint, sp.GetRequiredService<IHostApplicationLifetime>()));

        return services;
    }

    private sealed class LiteSpeedLinkService<THost>(THost host, IHostedEndpoint<THost> endpoint, IHostApplicationLifetime lifetime) : IHostedService
        where THost : class
    {
        private IAsyncDisposable? _listener;

        // Fin del host de la app: ApplicationStopping cancela conexiones y streams; un fallo al arrancar detiene el arranque de la app.
        public async Task StartAsync(CancellationToken cancellationToken)
            => _listener = await endpoint.StartAsync(host, lifetime.ApplicationStopping).ConfigureAwait(false);

        public Task StopAsync(CancellationToken cancellationToken)
            => _listener?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }
}

/// <summary>
/// Endpoint de un host con <c>[JsonSetting&lt;RemoteOptions|LocalOptions&gt;]</c> que no lo fija en codigo: se lee en ejecucion de la
/// configuracion de la app si esta cargada (<c>IConfiguration</c> en sus servicios), antes que la del propio contenedor.
/// </summary>
public static class HostSettings
{
    public static int? Port(IServiceProvider? services, string section)
        => Value(services, section, "Port") is not { } value
            ? null
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= ushort.MaxValue
                ? port
                : throw new InvalidOperationException($"'{section}:Port' = '{value}' in the app settings is not a valid port.");

    public static string? Name(IServiceProvider? services, string section)
        => Value(services, section, "Name") is { Length: > 0 } name ? name : null;

    private static string? Value(IServiceProvider? services, string section, string key)
        => (services?.GetService(typeof(IConfiguration)) as IConfiguration)?[section + ":" + key];
}
