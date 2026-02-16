using System.Collections.Concurrent;

namespace EmailExtractor.Lib.Agent;

public enum JobStatus { Running, Completed, Failed, Cancelled }

public sealed class RunningJob
{
    public required string Name { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public required CancellationTokenSource Cts { get; init; }
}

public sealed class JobManager
{
    private readonly Dictionary<string, JobDefinition> _registry;
    private readonly Dictionary<string, string[]> _pipelines;
    private readonly Func<string, CancellationToken, Task> _notify;
    private readonly ConcurrentDictionary<string, RunningJob> _running = new(StringComparer.OrdinalIgnoreCase);

    public JobManager(
        Dictionary<string, JobDefinition> registry,
        Dictionary<string, string[]> pipelines,
        Func<string, CancellationToken, Task> notify)
    {
        _registry = registry;
        _pipelines = pipelines;
        _notify = notify;
    }

    public IEnumerable<(string Name, string Description)> GetJobDescriptions() =>
        _registry.Select(kvp => (kvp.Key, kvp.Value.Description));

    public IReadOnlyCollection<string> AvailablePipelineNames => _pipelines.Keys;

    public bool IsKnownJob(string name) => _registry.ContainsKey(name);
    public bool IsKnownPipeline(string name) => _pipelines.ContainsKey(name);

    public IReadOnlyList<RunningJob> GetRunningJobs() => _running.Values.ToList();

    /// <summary>Starts a single job in the background. Returns false if already running.</summary>
    public bool TryStartJob(string name, CancellationToken parentCt)
    {
        if (!_registry.TryGetValue(name, out var def)) return false;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var job = new RunningJob { Name = name, Cts = cts };

        if (!_running.TryAdd(name, job)) { cts.Dispose(); return false; }

        _ = Task.Run(async () =>
        {
            try
            {
                await _notify($"Job '{name}' started.", CancellationToken.None);
                var code = await def.Execute(cts.Token);
                job.ExitCode = code;
                job.Status = code == 0 ? JobStatus.Completed : JobStatus.Failed;
                job.Error = code != 0 ? $"exit code {code}" : null;
                await _notify(code == 0 ? $"Job '{name}' completed." : $"Job '{name}' failed (exit {code}).",
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Cancelled;
                await _notify($"Job '{name}' cancelled.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                await _notify($"Job '{name}' failed: {ex.Message}", CancellationToken.None);
            }
            finally
            {
                job.CompletedAt = DateTime.UtcNow;
                _running.TryRemove(name, out _);
                cts.Dispose();
            }
        });

        return true;
    }

    /// <summary>Starts a named pipeline in the background. Returns false if already running.</summary>
    public bool TryStartPipeline(string pipelineName, CancellationToken parentCt)
    {
        if (!_pipelines.TryGetValue(pipelineName, out var steps)) return false;

        var key = $"pipeline:{pipelineName}";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var job = new RunningJob { Name = key, Cts = cts };

        if (!_running.TryAdd(key, job)) { cts.Dispose(); return false; }

        _ = Task.Run(async () =>
        {
            try
            {
                await _notify($"Pipeline '{pipelineName}' started ({steps.Length} steps).", CancellationToken.None);
                await RunStepsAsync(steps, cts.Token);
                job.Status = JobStatus.Completed;
                await _notify($"Pipeline '{pipelineName}' completed.", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Cancelled;
                await _notify($"Pipeline '{pipelineName}' cancelled.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                await _notify($"Pipeline '{pipelineName}' failed: {ex.Message}", CancellationToken.None);
            }
            finally
            {
                job.CompletedAt = DateTime.UtcNow;
                _running.TryRemove(key, out _);
                cts.Dispose();
            }
        });

        return true;
    }

    /// <summary>
    /// Runs a named pipeline sequentially and awaits completion.
    /// Used by the heartbeat cycle so the caller can run analysis after the pipeline finishes.
    /// </summary>
    public async Task StartPipelineAndAwaitAsync(string pipelineName, CancellationToken ct)
    {
        if (!_pipelines.TryGetValue(pipelineName, out var steps))
        {
            await _notify($"Unknown pipeline: '{pipelineName}'.", ct);
            return;
        }
        await RunStepsAsync(steps, ct);
    }

    /// <summary>Cancels a running job or pipeline by name (or "pipeline:name").</summary>
    public bool TryCancel(string name)
    {
        if (_running.TryGetValue(name, out var job))
        {
            job.Cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task RunStepsAsync(string[] steps, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!_registry.TryGetValue(step, out var def))
            {
                await _notify($"  Step '{step}' not found, skipping.", ct);
                continue;
            }
            await _notify($"  Running '{step}'...", ct);
            var code = await def.Execute(ct);
            if (code != 0)
            {
                await _notify($"  Step '{step}' failed (exit {code}).", ct);
                throw new Exception($"Step '{step}' failed with exit code {code}");
            }
        }
    }
}
