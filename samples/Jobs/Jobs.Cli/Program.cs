using Jobs;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

// jobs hash <file>...  -> encola y muestra el progreso en vivo
// jobs list            -> estado de todos los trabajos
await using var agent = new JobsAgentClient(Agent.Name);
var jobs = agent.Jobs;

try
{
    switch (args)
    {
        case ["hash", .. var files] when files.Length > 0:
            var ids = new List<int>();
            foreach (var file in files) ids.Add(await jobs.SubmitAsync(Path.GetFullPath(file)));
            await Task.WhenAll(ids.Select(id => Follow(jobs, id)));
            return 0;

        case ["list"]:
            foreach (var j in await jobs.ListAsync())
                Console.WriteLine($"#{j.Id,-3} {j.State,-8} {Percent(j),4} {j.Path} {j.Result}");
            return 0;

        default:
            Console.WriteLine("usage: jobs hash <file>... | jobs list");
            return 1;
    }
}
catch (TimeoutException)
{
    Console.Error.WriteLine("The agent is not running. Start it with: dotnet run --project samples/Jobs/Jobs.Agent");
    return 2;
}

static async Task Follow(JobsAgentClient.JobsClient jobs, int id)
{
    JobInfo? last = null;
    while (await jobs.GetAsync(id) is { } j)
    {
        if (last is null || last.State != j.State || Percent(j) != Percent(last))
            Console.WriteLine($"#{j.Id} {j.State,-8} {Percent(j),4} {Path.GetFileName(j.Path)}");
        last = j;
        if (j.State is JobState.Done or JobState.Failed) break;
        await Task.Delay(50);
    }

    if (last is { State: JobState.Done or JobState.Failed }) Console.WriteLine($"#{id} {last.State}: {last.Result}");
}

static string Percent(JobInfo j) => j.Total == 0 ? "" : $"{j.Done * 100 / j.Total}%";

[ServiceClient(ServiceConnectionType.Local)]
[ServiceUnit<IJobs>]
[ServiceProvider]
sealed partial class JobsAgentClient;
