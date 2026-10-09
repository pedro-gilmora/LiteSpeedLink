using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

namespace Jobs;

/// <summary>Cola y estado de los trabajos; procesa uno a la vez en segundo plano.</summary>
public sealed class JobQueue
{
    readonly ConcurrentDictionary<int, JobInfo> _jobs = new();
    readonly Channel<int> _pending = Channel.CreateUnbounded<int>();
    int _lastId;

    // ponytail: ProcessAsync estático y parámetro jobId porque el generador 2.26.282.164 expone como RPC todos los métodos de instancia
    // de cualquier servicio registrado (y su switch ya declara 'id'); corregido en el generador (solo IServiceUnit), simplificar al publicarlo.
    public JobQueue() => _ = Task.Run(() => ProcessAsync(_jobs, _pending.Reader));

    public int Enqueue(string path)
    {
        var id = Interlocked.Increment(ref _lastId);
        _jobs[id] = new(id, path, JobState.Queued, 0, 0, null);
        _pending.Writer.TryWrite(id);
        return id;
    }

    public JobInfo[] Snapshot() => [.. _jobs.Values.OrderBy(j => j.Id)];

    public JobInfo? Get(int jobId) => _jobs.GetValueOrDefault(jobId);

    static async Task ProcessAsync(ConcurrentDictionary<int, JobInfo> jobs, ChannelReader<int> pending)
    {
        await foreach (var id in pending.ReadAllAsync())
        {
            var job = jobs[id];
            try
            {
                await using var file = File.OpenRead(job.Path);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;

                while ((read = await file.ReadAsync(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    jobs[id] = job with { State = JobState.Running, Done = done += read, Total = file.Length };
                    await Task.Delay(20); // ponytail: ritmo artificial para que el progreso se vea; quitarlo en un agente real.
                }

                jobs[id] = job with { State = JobState.Done, Done = done, Total = done, Result = Convert.ToHexString(sha.GetHashAndReset()) };
            }
            catch (Exception ex)
            {
                jobs[id] = job with { State = JobState.Failed, Result = ex.Message };
            }
        }
    }
}

public sealed class JobsHandler(JobQueue queue) : IJobs
{
    public int Submit(string path) => queue.Enqueue(path);

    public JobInfo[] List() => queue.Snapshot();

    public JobInfo? Get(int jobId) => queue.Get(jobId);
}

/// <summary>Local: el generador elige shared memory (Windows) o Unix domain socket (Linux/macOS) en compilacion.</summary>
[ServiceHost(ServiceConnectionType.Local)]
[ServiceProvider]
[Singleton<JobQueue>]
[Scoped<IJobs, JobsHandler>]
public partial class JobsAgent;
