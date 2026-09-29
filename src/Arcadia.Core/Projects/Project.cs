using Arcadia.Core.Organizer;
namespace Arcadia.Core.Projects;

public enum ProjectStatus { Planning, Active, OnHold, Completed, Archived }

public enum ProjectPriority { Low, Normal, High, Critical }

public enum ProgressMode { Manual, TaskBased }

/// <summary>A personal project: its own small "world" with tasks, notes, files, links and history.</summary>
public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    /// <summary>The status to return to when an archived project is restored.</summary>
    public ProjectStatus? StatusBeforeArchive { get; set; }
    public ProjectPriority Priority { get; set; } = ProjectPriority.Normal;
    public DateOnly? StartDate { get; set; }
    public DateOnly? TargetDate { get; set; }
    /// <summary>Accent tone name from Arcadia's palette (Blue, Cyan, Green, Amber, Purple, Red, Gold, Steel).</summary>
    public string Accent { get; set; } = "Blue";
    /// <summary>Arcadia icon name.</summary>
    public string Icon { get; set; } = "Projects";
    /// <summary>Procedural scene used as the workspace banner.</summary>
    public string ArtworkTheme { get; set; } = "mountains-dusk";
    public int ArtworkSeed { get; set; }
    /// <summary>Comma separated.</summary>
    public string Tags { get; set; } = string.Empty;
    /// <summary>Optional primary Windows folder (referenced, never modified).</summary>
    public string? FolderPath { get; set; }
    /// <summary>New projects follow their tasks; the user can switch to a manual percentage.</summary>
    public ProgressMode ProgressMode { get; set; } = ProgressMode.TaskBased;
    public int ManualProgress { get; set; }
    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastOpenedAtUtc { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public bool IsSample { get; set; }

    public List<TaskItem> Tasks { get; set; } = [];
    public List<Note> Notes { get; set; } = [];
    public List<ProjectLink> Links { get; set; } = [];
    public List<ProjectActivity> Activity { get; set; } = [];

    public bool IsArchived => Status == ProjectStatus.Archived;
    public IReadOnlyList<string> TagList => TagText.Split(Tags);
}

public sealed class ProjectLink
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum ProjectActivityKind
{
    ProjectCreated, ProjectUpdated, StatusChanged, ProjectArchived, ProjectRestored,
    TaskCreated, TaskCompleted, TaskReopened, TaskDeleted,
    NoteAdded, NoteDeleted, FileAttached, FileRemoved, LinkAdded, LinkRemoved, FolderSet, FileRelocated,
}

/// <summary>A meaningful event in a project's history (never keystrokes).</summary>
public sealed class ProjectActivity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public ProjectActivityKind Kind { get; set; }
    /// <summary>The subject (task title, file name, new status…), shown with a localized template.</summary>
    public string? Subject { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Comma-separated tag lists (projects, tasks, notes, links).</summary>
public static class TagText
{
    public static IReadOnlyList<string> Split(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    public static string Join(IEnumerable<string> tags) => string.Join(", ", tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase));

    public static string Normalize(string? tags) => Join(Split(tags));
}
