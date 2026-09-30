using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// PoC aislado (no toca el transporte real) de tres formas de entregar un stream sobre TCP loopback:
/// <list type="bullet">
/// <item><c>PerItem</c>: como hoy, una trama <c>[len][item]</c> y una entrada de Channel por item.</item>
/// <item><c>Coalesced</c>: mismo cable, pero el lector agrupa las tramas de cada ReadAsync en una sola entrada.</item>
/// <item><c>Batched</c>: opt-in de protocolo, una trama <c>[len][n][item...]</c> con hasta <see cref="BatchItems"/> items.</item>
/// </list>
/// Fin de stream: trama <c>len = 0</c>. Sin corrId: un stream a la vez por conexion.
/// </summary>
internal enum StreamMode : byte { PerItem, Coalesced, Batched }

internal sealed class StreamBatchPoc : IAsyncDisposable
{
    public const int BatchItems = 1024;
    private const int FlushBytes = 32 * 1024;

    private readonly Socket _client;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly Channel<Chunk> _chunks = Channel.CreateUnbounded<Chunk>(new() { SingleReader = true, SingleWriter = true });
    private readonly Task _server, _readLoop;
    private readonly StreamMode _mode;

    private readonly record struct Chunk(byte[] Buffer, int Count);

    private StreamBatchPoc(StreamMode mode, Socket client, Task server)
    {
        _mode = mode;
        _client = client;
        var stream = new NetworkStream(client, ownsSocket: false);
        _reader = PipeReader.Create(stream);
        _writer = PipeWriter.Create(stream);
        _server = server;
        _readLoop = ReadLoopAsync();
    }

    public static async Task<StreamBatchPoc> StartAsync(StreamMode mode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var peer = await accept;
        listener.Stop();
        peer.NoDelay = true;
        return new(mode, client, ServeAsync(peer, mode == StreamMode.Batched));
    }

    public async IAsyncEnumerable<int> RangeAsync(int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_writer.GetSpan(4), count);
        _writer.Advance(4);
        await _writer.FlushAsync();

        while (true)
        {
            var chunk = await _chunks.Reader.ReadAsync();
            if (chunk.Count < 0) yield break;

            try
            {
                for (int i = 0; i < chunk.Count; i++)
                    yield return BinaryPrimitives.ReadInt32LittleEndian(chunk.Buffer.AsSpan(i * 4));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }
    }

    private static async Task ServeAsync(Socket peer, bool batched)
    {
        using var stream = new NetworkStream(peer, ownsSocket: true);
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;

                while (buffer.Length >= 4)
                {
                    Span<byte> head = stackalloc byte[4];
                    buffer.Slice(0, 4).CopyTo(head);
                    buffer = buffer.Slice(4);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(head);

                    for (int i = 0; i < count;)
                    {
                        int n = batched ? Math.Min(BatchItems, count - i) : 1;
                        int size = batched ? 4 + n * 4 : 4;
                        var span = writer.GetSpan(4 + size);
                        BinaryPrimitives.WriteInt32LittleEndian(span, size);
                        span = span[4..];
                        if (batched) { BinaryPrimitives.WriteInt32LittleEndian(span, n); span = span[4..]; }
                        for (int k = 0; k < n; k++) BinaryPrimitives.WriteInt32LittleEndian(span[(k * 4)..], i + k);
                        writer.Advance(4 + size);
                        i += n;

                        if (writer.UnflushedBytes >= FlushBytes) await writer.FlushAsync();
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), 0);
                    writer.Advance(4);
                    await writer.FlushAsync();
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (IOException) { }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync();
                var buffer = result.Buffer;
                byte[]? group = null;
                int grouped = 0;

                while (TryReadFrame(ref buffer, out var frame))
                {
                    if (frame.Length == 0)
                    {
                        Publish(ref group, ref grouped);
                        _chunks.Writer.TryWrite(new([], -1));
                        continue;
                    }

                    switch (_mode)
                    {
                        case StreamMode.PerItem:
                            var one = ArrayPool<byte>.Shared.Rent(4);
                            frame.CopyTo(one);
                            _chunks.Writer.TryWrite(new(one, 1));
                            break;

                        case StreamMode.Batched:
                            var items = frame.Slice(4);
                            var batch = ArrayPool<byte>.Shared.Rent((int)items.Length);
                            items.CopyTo(batch);
                            _chunks.Writer.TryWrite(new(batch, (int)items.Length / 4));
                            break;

                        default:
                            // ponytail: el grupo se dimensiona con lo que quede en el buffer; techo = un ReadAsync.
                            group ??= ArrayPool<byte>.Shared.Rent((int)Math.Max(4, buffer.Length + 4));
                            frame.CopyTo(group.AsSpan(grouped * 4));
                            grouped++;
                            break;
                    }
                }

                Publish(ref group, ref grouped);
                _reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void Publish(ref byte[]? group, ref int grouped)
    {
        if (group is null) return;
        _chunks.Writer.TryWrite(new(group, grouped));
        group = null;
        grouped = 0;
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if (buffer.Length < 4) return false;
        Span<byte> head = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(head);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (buffer.Length < 4 + len) return false;
        frame = buffer.Slice(4, len);
        buffer = buffer.Slice(4 + len);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.CompleteAsync();
        _client.Shutdown(SocketShutdown.Both);
        try { await Task.WhenAll(_server, _readLoop); } catch { }
        _client.Dispose();
    }
}

/// <summary>Mide las tres formas del PoC con la misma carga; GlobalSetup valida que las tres entregan lo mismo.</summary>
[MemoryDiagnoser]
public class StreamBatchingBenchmarks
{
    [Params(1_000, 100_000)]
    public int Count;

    private StreamBatchPoc _perItem = null!, _coalesced = null!, _batched = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _perItem = await StreamBatchPoc.StartAsync(StreamMode.PerItem);
        _coalesced = await StreamBatchPoc.StartAsync(StreamMode.Coalesced);
        _batched = await StreamBatchPoc.StartAsync(StreamMode.Batched);

        long expected = (long)Count * (Count - 1) / 2;
        foreach (var (name, poc) in new[] { ("PerItem", _perItem), ("Coalesced", _coalesced), ("Batched", _batched) })
        {
            long sum = await Drain(poc);
            if (sum != expected) throw new InvalidOperationException($"{name}: suma {sum}, esperada {expected}.");
        }
    }

    [Benchmark(Baseline = true)] public Task<long> PerItem() => Drain(_perItem);
    [Benchmark] public Task<long> Coalesced() => Drain(_coalesced);
    [Benchmark] public Task<long> Batched() => Drain(_batched);

    private async Task<long> Drain(StreamBatchPoc poc)
    {
        long sum = 0;
        await foreach (var i in poc.RangeAsync(Count)) sum += i;
        return sum;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _perItem.DisposeAsync();
        await _coalesced.DisposeAsync();
        await _batched.DisposeAsync();
    }
}
