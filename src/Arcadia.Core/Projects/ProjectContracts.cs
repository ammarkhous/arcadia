using Arcadia.Core.Organizer;
namespace Arcadia.Core.Projects;

/// <summary>Editable project fields (create and edit).</summary>
public sealed record ProjectDraft(
    string Name,
    string? Description,
    ProjectStatus Status,
    ProjectPriority Priority,
    DateOnly? StartDate,
    DateOnly? TargetDate,
    string Accent,
    string Icon,
    string ArtworkTheme,
    string Tags,
    string? FolderPath);

public sealed record TaskDraft(string Title, string? Description, TaskItemStatus Status, ProjectPriority Priority, DateOnly? DueDate, string? Notes, string Tags, int? ParentTaskId);

public sealed record LinkDraft(string Title, string Url, string? Description, string Tags);

/// <summary>A project row for lists: everything the home screen needs without loading children.</summary>
public sealed record ProjectSummary(
    int Id,
    string Name,
    string? Description,
    ProjectStatus Status,
    ProjectPriority Priority,
    DateOnly? StartDate,
    DateOnly? TargetDate,
    string Accent,
    string Icon,
    string ArtworkTheme,
    int ArtworkSeed,
    string Tags,
    bool IsPinned,
    int Progress,
    int TotalTasks,
    int OpenTasks,
    DateTime UpdatedAtUtc,
    DateTime? LastOpenedAtUtc,
    string? FolderPath);

public sealed record ProjectStats(int Planning, int Active, int OnHold, int Completed, int Archived, int TasksRemaining, int UpcomingDeadlines);

/// <summary>A due project or task within the look-ahead window (overdue items included).</summary>
public sealed record Deadline(int ProjectId, string ProjectName, string Accent, int? TaskId, string Title, DateOnly Due);

public interface IProjectStore
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProjectStats> GetStatsAsync(DateOnly today, int deadlineDays, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Deadline>> GetDeadlinesAsync(DateOnly today, int days, CancellationToken cancellationToken = default);

    /// <summary>The project with tasks, notes, links and the most recent activity.</summary>
    Task<Project?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(ProjectDraft draft, CancellationToken cancellationToken = default);
    Task UpdateAsync(int id, ProjectDraft draft, CancellationToken cancellationToken = default);
    Task SetStatusAsync(int id, ProjectStatus status, CancellationToken cancellationToken = default);
    Task ArchiveAsync(int id, CancellationToken cancellationToken = default);
    Task RestoreAsync(int id, CancellationToken cancellationToken = default);
    Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default);
    Task SetProgressAsync(int id, ProgressMode mode, int manualProgress, CancellationToken cancellationToken = default);
    Task SetDescriptionAsync(int id, string? description, CancellationToken cancellationToken = default);
    Task MarkOpenedAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Deletes the project and its tasks, notes, links and history. Referenced files are never touched.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<TaskItem> AddTaskAsync(int projectId, TaskDraft draft, CancellationToken cancellationToken = default);
    Task UpdateTaskAsync(int taskId, TaskDraft draft, CancellationToken cancellationToken = default);
    Task SetTaskStatusAsync(int taskId, TaskItemStatus status, CancellationToken cancellationToken = default);
    /// <summary>Deletes a task and its subtasks.</summary>
    Task DeleteTaskAsync(int taskId, CancellationToken cancellationToken = default);

    Task<Note> AddNoteAsync(int projectId, string title, string content = "", CancellationToken cancellationToken = default);
    /// <summary>Autosave target: stores title, content and tags; returns the new modification time.</summary>
    Task<DateTime> SaveNoteAsync(int noteId, string title, string content, string tags, CancellationToken cancellationToken = default);
    Task SetNotePinnedAsync(int noteId, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteNoteAsync(int noteId, CancellationToken cancellationToken = default);

    Task<ProjectLink> AddLinkAsync(int projectId, LinkDraft draft, CancellationToken cancellationToken = default);
    Task UpdateLinkAsync(int linkId, LinkDraft draft, CancellationToken cancellationToken = default);
    Task SetLinkPinnedAsync(int linkId, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteLinkAsync(int linkId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectActivity>> GetActivityAsync(int projectId, int take, CancellationToken cancellationToken = default);
}
