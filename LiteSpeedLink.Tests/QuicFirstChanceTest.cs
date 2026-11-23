using System.Net;
using System.Runtime.Versioning;

using FluentAssertions;

using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>
/// Cuenta las <c>QuicException</c> de primera oportunidad de todo el proceso: va sola, porque otros tests las provocan a proposito
/// (conexiones cerradas por los topes, reconexion) y en paralelo se sumarian a la cuenta.
/// </summary>
[Collection(nameof(QuicFirstChanceTest))]
public class QuicFirstChanceTest
{
    /// <summary>Optimizacion QUIC: cerrar la llamada drena el FIN y no aborta el stream (sin QuicException por llamada).</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestQuicCallDoesNotThrowInternally()
    {
        var cert = Constants.GetDevCert();

        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) =>
        {
            var (a, b, _) = ctx.Get<(int, int, bool)>();
            return ctx.ReturnAsync(a + b);
        }, () => { }, cert, default);
        await using var connection = new DnsEndPoint("localhost", server.LocalEndPoint.Port).AsQuicConnection(cert, unaryStreams: 0);
        await connection.GetAsync<(int, int, bool), int>(0, (1, 1, false));

        int quicExceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> h = (_, e) => { if (e.Exception is System.Net.Quic.QuicException) Interlocked.Increment(ref quicExceptions); };
        AppDomain.CurrentDomain.FirstChanceException += h;
        try
        {
            for (int i = 0; i < 50; i++) (await connection.GetAsync<(int, int, bool), int>(0, (i, i, false))).Should().Be(i * 2);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= h; }

        quicExceptions.Should().Be(0);
    }
}

[CollectionDefinition(nameof(QuicFirstChanceTest), DisableParallelization = true)]
public class QuicFirstChanceCollection;
