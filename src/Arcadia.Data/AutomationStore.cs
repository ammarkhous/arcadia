using System.Text.Json;
using Arcadia.Core.Automation;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

/// <summary>Automations and their bounded activity log (SQLite, Phase 9).</summary>
public sealed class AutomationStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : IAutomationStore
{
    public async Task<IReadOnlyList<AutomationDefinition>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Automations.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct).ConfigureAwait(false)).Select(AutomationDefinition.From).ToList();
    }

    public async Task<AutomationDefinition?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Automations.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) is { } e ? AutomationDefinition.From(e) : null;
    }

    public async Task<AutomationDefinition> SaveAsync(AutomationDefinition definition, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;
        AutomationEntity entity;
        if (definition.Id == 0)
        {
            entity = new AutomationEntity { CreatedAtUtc = now };
            db.Automations.Add(entity);
        }
        else entity = await db.Automations.FirstOrDefaultAsync(a => a.Id == definition.Id, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Automation no longer exists.");
        definition.CopyTo(entity);
        entity.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return AutomationDefinition.From(entity);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;
        await db.Automations.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsEnabled, enabled).SetProperty(a => a.UpdatedAtUtc, now), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Automations.Where(a => a.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> BeginRunAsync(AutomationRunEntity run, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        run.Summary = Clip(run.Summary, 1000);
        db.AutomationRuns.Add(run);
        if (run.AutomationId is { } id && !run.IsSimulation && run.Status is RunStatus.Blocked or RunStatus.Skipped)
            if (await db.Automations.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) is { } a) a.LastStatus = run.Status;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run.Id;
    }

    public async Task CompleteRunAsync(int runId, RunStatus status, int durationMs, string summary, string detailsJson, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.AutomationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run == null) return;
        run.Status = status;
        run.DurationMs = durationMs;
        run.Summary = Clip(summary, 1000);
        run.DetailsJson = detailsJson.Length > 20000 ? detailsJson[..20000] : detailsJson;
        if (run.AutomationId is { } id && !run.IsSimulation && await db.Automations.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) is { } a)
        {
            a.LastRunAtUtc = run.StartedAtUtc;
            a.RunCount++;
            a.LastStatus = status;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasRunAsync(int automationId, string eventKey, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AutomationRuns.AnyAsync(r => r.AutomationId == automationId && r.EventKey == eventKey && !r.IsSimulation, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutomationRunRecord>> GetRunsAsync(int take, int? automationId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.AutomationRuns.AsNoTracking();
        if (automationId is { } id) q = q.Where(r => r.AutomationId == id);
        var rows = await q.OrderByDescending(r => r.Id).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new AutomationRunRecord(r.Id, r.AutomationId, r.AutomationName, r.TriggerType, r.StartedAtUtc, r.DurationMs, r.Status, r.Summary,
            Details(r.DetailsJson), r.IsSimulation)).ToList();
    }

    private static RunDetails Details(string json)
    {
        try { return JsonSerializer.Deserialize<RunDetails>(json) ?? RunDetails.Empty; }
        catch (JsonException) { return RunDetails.Empty; }
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AutomationRuns.Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Incomplete).SetProperty(r => r.Summary, "Interrupted (Arcadia closed or crashed); not re-run."), ct).ConfigureAwait(false);
    }

    public async Task PruneRunsAsync(int keepDays, int keepMax, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, keepDays));
        // runs that carry a once-per-occurrence key are kept, so a bill or a trip start never fires twice
        await db.AutomationRuns.Where(r => r.StartedAtUtc < cutoff && r.EventKey == null).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var count = await db.AutomationRuns.CountAsync(r => r.EventKey == null, ct).ConfigureAwait(false);
        if (count > keepMax)
        {
            var edge = await db.AutomationRuns.Where(r => r.EventKey == null).OrderByDescending(r => r.Id).Skip(keepMax).Select(r => r.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            await db.AutomationRuns.Where(r => r.Id <= edge && r.EventKey == null).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>Project work sessions (Phase 9).</summary>
public sealed class WorkSessionStore(IDbContextFactory<ArcadiaDbContext> factory) : IWorkSessionStore
{
    public async Task<WorkSession?> GetOpenAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.WorkSessions.AsNoTracking().Where(w => w.EndedAtUtc == null).OrderByDescending(w => w.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<WorkSession> StartAsync(int projectId, DateTime startUtc, bool automatic, CancellationToken ct = default)
    {
        await StopAsync(startUtc, null, ct).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct).ConfigureAwait(false)) throw new InvalidOperationException("Project not found.");
        var s = new WorkSession { ProjectId = projectId, StartedAtUtc = startUtc, LastSeenAtUtc = startUtc, IsAutomatic = automatic };
        db.WorkSessions.Add(s);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return s;
    }

    public async Task<WorkSession?> StopAsync(DateTime endUtc, string? note = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var open = await db.WorkSessions.Where(w => w.EndedAtUtc == null).ToListAsync(ct).ConfigureAwait(false);
        WorkSession? last = null;
        foreach (var s in open)
        {
            var end = endUtc < s.StartedAtUtc ? s.StartedAtUtc : endUtc;
            s.EndedAtUtc = end;
            s.LastSeenAtUtc = end;
            s.Minutes = (int)Math.Round((end - s.StartedAtUtc).TotalMinutes);
            if (!string.IsNullOrWhiteSpace(note)) s.Note = note.Trim();
            last = s;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return last;
    }

    public async Task HeartbeatAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.WorkSessions.Where(w => w.EndedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeenAtUtc, nowUtc), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(WorkSession Session, string Project)>> RecentAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.WorkSessions.AsNoTracking().OrderByDescending(w => w.StartedAtUtc).Take(take)
            .Join(db.Projects, w => w.ProjectId, p => p.Id, (w, p) => new { w, p.Name }).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => (r.w, r.Name)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> MinutesByProjectAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.WorkSessions.Where(w => w.EndedAtUtc != null).GroupBy(w => w.ProjectId).Select(g => new { g.Key, M = g.Sum(w => w.Minutes) })
            .ToDictionaryAsync(x => x.Key, x => x.M, ct).ConfigureAwait(false);
    }
}
