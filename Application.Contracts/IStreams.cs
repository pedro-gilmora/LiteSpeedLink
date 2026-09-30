using SourceCrafter.LiteSpeedLink;

namespace Application.Contracts;

/// <summary>Estudio #1: una operacion por cada combinacion secuencia x politica que emite el host generado.</summary>
public interface IStreams : IServiceUnit
{
    IEnumerable<int> SyncDefault(int count);
    [Stream(Batch = 0)] IEnumerable<int> SyncUnbatched(int count);
    [Stream(Batch = 16)] IEnumerable<int> SyncBatched(int count);
    [Stream(Batch = 16, MaxDelayMs = 1)] IEnumerable<int> SyncTimed(int count);

    IAsyncEnumerable<int> AsyncDefault(int count);
    [Stream(Batch = 0)] IAsyncEnumerable<int> AsyncUnbatched(int count);
    [Stream(Batch = 16)] IAsyncEnumerable<int> AsyncBatched(int count);
    [Stream(Batch = 16, MaxDelayMs = 1)] IAsyncEnumerable<int> AsyncTimed(int count);
}

public sealed class Streams : IStreams
{
    public IEnumerable<int> SyncDefault(int count) => Enumerable.Range(0, count);
    public IEnumerable<int> SyncUnbatched(int count) => Enumerable.Range(0, count);
    public IEnumerable<int> SyncBatched(int count) => Enumerable.Range(0, count);
    public IEnumerable<int> SyncTimed(int count) => Enumerable.Range(0, count);

    public IAsyncEnumerable<int> AsyncDefault(int count) => Range(count);
    public IAsyncEnumerable<int> AsyncUnbatched(int count) => Range(count);
    public IAsyncEnumerable<int> AsyncBatched(int count) => Range(count);
    public IAsyncEnumerable<int> AsyncTimed(int count) => Range(count);

    private static async IAsyncEnumerable<int> Range(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (i % 100 == 99) await Task.Yield();
            yield return i;
        }
    }
}
