using Jobs;

// Worker Service: se instala tal cual como servicio de Windows o unidad systemd.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentService>();
builder.Build().Run();

sealed class AgentService(ILogger<AgentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var host = JobsAgent.Start(Agent.Name, stoppingToken);
        logger.LogInformation("Jobs agent listening on local channel '{Name}'", Agent.Name);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}
