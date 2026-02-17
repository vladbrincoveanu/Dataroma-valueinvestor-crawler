using System.Collections.Concurrent;

namespace ValueInvestorCrawler.Lib.Agent;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class RunningJob
{
    public string Name { get; }
    public JobStatus Status { get; internal set; } = JobStatus.Pending;
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; internal set; }
    public int? ExitCode { get; internal set; }
    public string? Error { get; internal set; }
    public CancellationTokenSource Cts { get; }
    public Task Task { get; internal set; } = Task.CompletedTask;

    internal RunningJob(string name, CancellationTokenSource cts)
    {
        Name = name;
        Cts = cts;
    }
}

public sealed class JobManager
{
    private readonly Dictionary<string, JobDefinition> _jobs;
    private readonly Dictionary<string, string[]> _pipelines;
    private readonly Func<string, CancellationToken, Task> _notify;
    private readonly ConcurrentDictionary<string, RunningJob> _running = new(StringComparer.OrdinalIgnoreCase);

    public JobManager(
        Dictionary<string, JobDefinition> jobs,
        Dictionary<string, string[]> pipelines,
        Func<string, CancellationToken, Task> notify)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    public IReadOnlyList<string> AvailableJobNames => [.. _jobs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
    public IReadOnlyList<string> AvailablePipelineNames => [.. _pipelines.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

    public bool TryStartJob(string name, CancellationToken parentCt)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!_jobs.TryGetValue(name.Trim(), out var def)) return false;
        return TryStartBackground(def.Name, parentCt, token => RunSingleJobAsync(def, token));
    }

    public bool TryStartPipeline(string name, CancellationToken parentCt)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var pipelineName = name.Trim();
        if (!_pipelines.TryGetValue(pipelineName, out var steps)) return false;
        var key = $"pipeline:{pipelineName}";
        return TryStartBackground(key, parentCt, token => RunPipelineAsync(pipelineName, steps, token));
    }

    public async Task<bool> StartPipelineAndAwaitAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var key = $"pipeline:{name.Trim()}";

        if (!TryStartPipeline(name, ct))
        {
            if (_running.TryGetValue(key, out var existing))
            {
                await existing.Task.ConfigureAwait(false);
                return existing.Status == JobStatus.Completed;
            }
            return false;
        }

        if (_running.TryGetValue(key, out var running))
        {
            await running.Task.ConfigureAwait(false);
            return running.Status == JobStatus.Completed;
        }

        return false;
    }

    public bool TryCancel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!_running.TryGetValue(name.Trim(), out var running)) return false;
        running.Cts.Cancel();
        return true;
    }

    public IReadOnlyList<RunningJob> GetRunningJobs()
    {
        return [.. _running.Values.OrderBy(j => j.StartedAtUtc)];
    }

    private bool TryStartBackground(
        string key,
        CancellationToken parentCt,
        Func<CancellationToken, Task<RunOutcome>> run)
    {
        if (_running.ContainsKey(key)) return false;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var running = new RunningJob(key, linkedCts);
        if (!_running.TryAdd(key, running))
        {
            linkedCts.Dispose();
            return false;
        }

        running.Task = Task.Run(async () =>
        {
            running.Status = JobStatus.Running;
            try
            {
                var outcome = await run(linkedCts.Token).ConfigureAwait(false);
                running.Status = outcome.Status;
                running.ExitCode = outcome.ExitCode;
                running.Error = outcome.Error;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                running.Status = JobStatus.Cancelled;
                running.Error = "Cancelled";
            }
            catch (Exception ex)
            {
                running.Status = JobStatus.Failed;
                running.Error = ex.Message;
            }
            finally
            {
                running.CompletedAtUtc = DateTime.UtcNow;
                _running.TryRemove(key, out _);
                linkedCts.Dispose();
            }
        }, CancellationToken.None);

        return true;
    }

    private async Task<RunOutcome> RunSingleJobAsync(JobDefinition def, CancellationToken ct)
    {
        await NotifySafeAsync($"Started job: {def.Name}", ct).ConfigureAwait(false);
        try
        {
            var exitCode = await def.Execute(ct).ConfigureAwait(false);
            if (exitCode == 0)
            {
                await NotifySafeAsync($"Completed job: {def.Name}", ct).ConfigureAwait(false);
                return new RunOutcome(JobStatus.Completed, exitCode, null);
            }

            await NotifySafeAsync($"Failed job: {def.Name} (exit code {exitCode})", ct).ConfigureAwait(false);
            return new RunOutcome(JobStatus.Failed, exitCode, $"exit code {exitCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await NotifySafeAsync($"Cancelled job: {def.Name}", CancellationToken.None).ConfigureAwait(false);
            return new RunOutcome(JobStatus.Cancelled, null, "Cancelled");
        }
        catch (Exception ex)
        {
            await NotifySafeAsync($"Failed job: {def.Name} ({ex.Message})", ct).ConfigureAwait(false);
            return new RunOutcome(JobStatus.Failed, null, ex.Message);
        }
    }

    private async Task<RunOutcome> RunPipelineAsync(string pipelineName, string[] steps, CancellationToken ct)
    {
        var key = $"pipeline:{pipelineName}";
        await NotifySafeAsync($"Started pipeline: {pipelineName}", ct).ConfigureAwait(false);

        for (var i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var stepName = steps[i];
            if (!_jobs.TryGetValue(stepName, out var def))
            {
                var error = $"unknown step '{stepName}'";
                await NotifySafeAsync($"Failed {key}: {error}", ct).ConfigureAwait(false);
                return new RunOutcome(JobStatus.Failed, null, error);
            }

            await NotifySafeAsync($"[{pipelineName}] Step {i + 1}/{steps.Length}: {stepName}", ct).ConfigureAwait(false);

            var exitCode = await def.Execute(ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                var error = $"step '{stepName}' failed with exit code {exitCode}";
                await NotifySafeAsync($"Failed {key}: {error}", ct).ConfigureAwait(false);
                return new RunOutcome(JobStatus.Failed, exitCode, error);
            }
        }

        await NotifySafeAsync($"Completed pipeline: {pipelineName}", ct).ConfigureAwait(false);
        return new RunOutcome(JobStatus.Completed, 0, null);
    }

    private async Task NotifySafeAsync(string text, CancellationToken ct)
    {
        try
        {
            await _notify(text, ct).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: notifications should not break job execution.
        }
    }

    private sealed record RunOutcome(JobStatus Status, int? ExitCode, string? Error);
}
