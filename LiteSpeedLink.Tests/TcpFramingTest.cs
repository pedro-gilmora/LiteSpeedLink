using FluentAssertions;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Buffers.Binary;
using Xunit;

namespace LiteSpeedLink.Tests;

public partial class ServersTest
{
    /// <summary>P4: TCP fragmenta y coalesce. Dos peticiones enviadas byte a byte deben responderse bien.</summary>
    [Fact]
    public async Task TestTcpFragmentedFrames()
    {
        const int serverPort = 5005;

        using var server = Server.StartTcpServer(serverPort, HandleRequestAsync, () => { }, null, default);
        // Socket crudo: controlamos los bytes exactos. NoDelay evita que Nagle reagrupe los envios.
        using var socket = new System.Net.Sockets.TcpClient { NoDelay = true };
        await socket.ConnectAsync("localhost", serverPort);
        var stream = socket.GetStream();

        // Dos tramas contiguas (coalescencia) con corrId 1 y 2.
        byte[] wire = [.. Request(1, 3, 4), .. Request(2, 10, 20)];

        // Un byte por escritura: fuerza fragmentacion maxima en el servidor.
        foreach (var b in wire)
        {
            await stream.WriteAsync(new[] { b });
            await stream.FlushAsync();
        }

        // Respuesta: [len][corrId][status][cuerpo]. Pueden llegar en cualquier orden: se indexan por corrId.
        var responses = new Dictionary<int, int>();

        for (int i = 0; i < 2; i++)
        {
            // Longitud LEB128 (1 byte para respuestas tan cortas).
            var len = (await ReadExactly(stream, 1))[0];
            len.Should().BeLessThan(0x80);
            var frame = await ReadExactly(stream, len);

            frame[Framing.CorrelationIdSize].Should().Be((byte)ResponseStatus.Success);
            responses[BinaryPrimitives.ReadInt32LittleEndian(frame)] = MemoryPackSerializer.Deserialize<int>(frame.AsSpan(Framing.CorrelationIdSize + Framing.StatusSize));
        }

        responses.Should().Equal(new Dictionary<int, int> { [1] = 7, [2] = 30 });

        // Peticion: [len][corrId][opId=0 (suma)][(a, b, false) MemoryPack].
        static byte[] Request(int corrId, int a, int b) => TestFrames.Request(corrId, 0, MemoryPackSerializer.Serialize((a, b, false)));

        static async Task<byte[]> ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count];
            // Timeout: si el framing falla, el servidor no responde y el test no debe colgarse.
            await s.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return buffer;
        }
    }
}
