using Arcadia.Core.Organizer;
namespace Arcadia.Core.Projects;

/// <summary>Progress, deadlines and task state rules — pure logic, unit tested.</summary>
public static class ProjectRules
{
    /// <summary>
    /// Progress 0–100. Task-based progress counts top-level tasks and subtasks alike, ignores cancelled tasks,
    /// and is 0 when there is nothing to do yet.
    /// </summary>
    public static int Progress(ProgressMode mode, int manual, IEnumerable<TaskItemStatus> taskStatuses)
    {
        if (mode == ProgressMode.Manual) return Math.Clamp(manual, 0, 100);
        var counted = taskStatuses.Where(s => s != TaskItemStatus.Cancelled).ToList();
        if (counted.Count == 0) return 0;
        return (int)Math.Round(counted.Count(s => s == TaskItemStatus.Completed) * 100.0 / counted.Count, MidpointRounding.AwayFromZero);
    }

    /// <summary>Days until the target date (negative when overdue), null without a target.</summary>
    public static int? DaysRemaining(DateOnly? target, DateOnly today) => target is { } t ? t.DayNumber - today.DayNumber : null;

    public static bool IsOverdue(DateOnly? due, DateOnly today, bool done) => !done && due is { } d && d < today;

    /// <summary>
    /// Moves a task to a new status and keeps the completion stamp consistent:
    /// completing stamps the time, reopening (any open status) clears it.
    /// </summary>
    public static void SetStatus(TaskItem task, TaskItemStatus status, DateTime nowUtc)
    {
        if (task.Status == status) return;
        task.Status = status;
        task.CompletedAtUtc = status == TaskItemStatus.Completed ? nowUtc : null;
        task.UpdatedAtUtc = nowUtc;
    }

    /// <summary>Checkbox behaviour: open → completed, done → to do.</summary>
    public static TaskItemStatus Toggle(TaskItemStatus current) =>
        current is TaskItemStatus.Completed or TaskItemStatus.Cancelled ? TaskItemStatus.ToDo : TaskItemStatus.Completed;

    /// <summary>Only one level of subtasks: the parent must be a top-level task of the same project.</summary>
    public static bool CanBeParent(TaskItem candidate, int? projectId) => candidate.ProjectId == projectId && candidate.ParentTaskId == null;

    public static ProjectStatus Restore(Project project) =>
        project.StatusBeforeArchive is { } s && s != ProjectStatus.Archived ? s : ProjectStatus.Active;
}

/// <summary>URL validation for project links: absolute http(s) only, with a real host.</summary>
public static class LinkRules
{
    public static bool TryNormalize(string? input, out string url)
    {
        url = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 2048 || text.Any(char.IsWhiteSpace)) return false;
        // "github.com/x" → https://github.com/x
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrEmpty(uri.Host) || (!uri.Host.Contains('.') && uri.Host != "localhost" && uri.HostNameType == UriHostNameType.Dns)) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>A readable default title: host + first path segment.</summary>
    public static string DefaultTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var segment = uri.Segments.Skip(1).FirstOrDefault()?.Trim('/');
        return string.IsNullOrEmpty(segment) ? host : $"{host}/{Uri.UnescapeDataString(segment)}";
    }
}
