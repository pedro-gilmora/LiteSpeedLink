using BenchmarkDotNet.Attributes;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Descompone el coste fijo por llamada de QUIC: stream crudo (msquic/.NET) vs stream crudo + pipes
/// vs RPC LiteSpeedLink de 1 item. La diferencia entre filas es lo que aporta cada capa.
/// </summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
#pragma warning disable CA2252
public class QuicOverheadBenchmarks
{
    private static readonly SslApplicationProtocol RawAlpn = new("lsl-raw-bench");
    private QuicListener _rawServer = null!, _lslServer = null!;
    private System.Net.Quic.QuicConnection _raw = null!;
    private SourceCrafter.LiteSpeedLink.Client.QuicConnection _lsl = null!;
    private readonly byte[] _one = [42];
    private readonly byte[] _buf = new byte[16];

    [GlobalSetup]
    public async Task Setup()
    {
        var cert = Constants.GetDevCert();
        _rawServer = await QuicListener.ListenAsync(new()
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [RawAlpn],
            ConnectionOptionsCallback = (_, _, _) => new(new QuicServerConnectionOptions
            {
                MaxInboundBidirectionalStreams = 1000,
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 2,
                ServerAuthenticationOptions = new() { ApplicationProtocols = [RawAlpn], ServerCertificate = cert }
            })
        });
        _ = EchoLoop(_rawServer);

        _raw = await System.Net.Quic.QuicConnection.ConnectAsync(new()
        {
            RemoteEndPoint = _rawServer.LocalEndPoint,
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 2,
            ClientAuthenticationOptions = new()
            {
                ApplicationProtocols = [RawAlpn],
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        });

        _lslServer = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, 1)), () => { }, cert);
        _lsl = new DnsEndPoint("localhost", _lslServer.LocalEndPoint.Port).AsQuicConnection(cert);

        await RawStream(); await RawPipes(); await Lsl();
    }

    private static async Task EchoLoop(QuicListener l)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptConnectionAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var s = await c.AcceptInboundStreamAsync();
                            _ = Task.Run(async () =>
                            {
                                await using (s)
                                {
                                    var b = new byte[16];
                                    int n, total = 0;
                                    while ((n = await s.ReadAsync(b.AsMemory(total))) > 0) total += n;
                                    await s.WriteAsync(b.AsMemory(0, total), completeWrites: true);
                                }
                            });
                        }
                    }
                    catch { }
                });
            }
        }
        catch { }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _raw.DisposeAsync(); await _rawServer.DisposeAsync();
        await _lsl.DisposeAsync(); await _lslServer.DisposeAsync();
    }

    /// <summary>Suelo de msquic/.NET: abrir stream, 1 byte ida, 1 byte vuelta, cerrar.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> RawStream()
    {
        await using var s = await _raw.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        await s.WriteAsync(_one, completeWrites: true);
        int n, total = 0;
        while ((n = await s.ReadAsync(_buf.AsMemory(total))) > 0) total += n;
        return total == 1 ? total : throw new InvalidOperationException($"{total}");
    }

    /// <summary>Igual que RawStream pero con PipeReader/PipeWriter por stream (lo que hace LSL).</summary>
    [Benchmark]
    public async Task<int> RawPipes()
    {
        await using var s = await _raw.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        var w = PipeWriter.Create(s, new(leaveOpen: true));
        var r = PipeReader.Create(s, new(leaveOpen: true));
        await w.WriteAsync(_one);
        await w.CompleteAsync();
        s.CompleteWrites();
        ReadResult res;
        while (!(res = await r.ReadAsync()).IsCompleted) r.AdvanceTo(res.Buffer.Start, res.Buffer.End);
        int total = (int)res.Buffer.Length;
        await r.CompleteAsync();
        return total == 1 ? total : throw new InvalidOperationException($"{total}");
    }

    /// <summary>RPC LiteSpeedLink completo con 1 item.</summary>
    [Benchmark]
    public async Task<int> Lsl()
    {
        int n = 0;
        await foreach (var _ in _lsl.EnumerateAsync<int, int>(0, 1)) n++;
        return n == 1 ? n : throw new InvalidOperationException($"{n}");
    }
}
#pragma warning restore CA2252
