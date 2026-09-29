using Arcadia.Core.Organizer;
using Arcadia.Core.Documents;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Text;
using Arcadia.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class ProjectRulesTests
{
    [Fact]
    public void Task_based_progress_ignores_cancelled_tasks()
    {
        Assert.Equal(0, ProjectRules.Progress(ProgressMode.TaskBased, 80, []));
        Assert.Equal(50, ProjectRules.Progress(ProgressMode.TaskBased, 80,
            [TaskItemStatus.Completed, TaskItemStatus.ToDo, TaskItemStatus.Cancelled]));
        Assert.Equal(67, ProjectRules.Progress(ProgressMode.TaskBased, 0,
            [TaskItemStatus.Completed, TaskItemStatus.Completed, TaskItemStatus.Blocked]));
        Assert.Equal(80, ProjectRules.Progress(ProgressMode.Manual, 80, [TaskItemStatus.ToDo]));
        Assert.Equal(100, ProjectRules.Progress(ProgressMode.Manual, 140, []));
        Assert.Equal(ProgressMode.TaskBased, new Project { Name = "New" }.ProgressMode); // new projects follow their tasks
    }

    [Fact]
    public void Completing_and_reopening_keeps_the_completion_stamp_consistent()
    {
        var now = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem { Status = TaskItemStatus.InProgress };
        ProjectRules.SetStatus(task, TaskItemStatus.Completed, now);
        Assert.Equal(now, task.CompletedAtUtc);
        Assert.True(task.IsDone);
        ProjectRules.SetStatus(task, ProjectRules.Toggle(task.Status), now.AddHours(1));
        Assert.Equal(TaskItemStatus.ToDo, task.Status);
        Assert.Null(task.CompletedAtUtc);
        ProjectRules.SetStatus(task, TaskItemStatus.Cancelled, now);
        Assert.Null(task.CompletedAtUtc);
        Assert.Equal(TaskItemStatus.ToDo, ProjectRules.Toggle(TaskItemStatus.Cancelled));
    }

    [Fact]
    public void Deadlines_and_restore()
    {
        var today = new DateOnly(2026, 9, 27);
        Assert.Equal(3, ProjectRules.DaysRemaining(today.AddDays(3), today));
        Assert.Equal(-2, ProjectRules.DaysRemaining(today.AddDays(-2), today));
        Assert.True(ProjectRules.IsOverdue(today.AddDays(-1), today, done: false));
        Assert.False(ProjectRules.IsOverdue(today.AddDays(-1), today, done: true));
        Assert.Equal(ProjectStatus.OnHold, ProjectRules.Restore(new Project { StatusBeforeArchive = ProjectStatus.OnHold }));
        Assert.Equal(ProjectStatus.Active, ProjectRules.Restore(new Project()));
    }

    [Theory]
    [InlineData("github.com/arcadia", "https://github.com/arcadia")]
    [InlineData(" https://learn.microsoft.com/de-de/dotnet ", "https://learn.microsoft.com/de-de/dotnet")]
    [InlineData("http://localhost:5000/x", "http://localhost:5000/x")]
    [InlineData("https://ar.wikipedia.org/wiki/دمشق", "https://ar.wikipedia.org/wiki/%D8%AF%D9%85%D8%B4%D9%82")]
    public void Links_are_normalised(string input, string expected)
    {
        Assert.True(LinkRules.TryNormalize(input, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("https://")]
    [InlineData("intranet")]
    public void Malformed_or_unsafe_links_are_rejected(string input) => Assert.False(LinkRules.TryNormalize(input, out _));

    [Fact]
    public void Tag_lists_are_trimmed_and_deduplicated() =>
        Assert.Equal("Work, arcadia, عمل", TagText.Normalize(" Work, arcadia ;Arcadia,, work, عمل ")); // first spelling wins
}

public sealed class PathAndFileKindTests
{
    [Theory]
    [InlineData(@"C:\Users\Test\Docs\report.pdf", @"C:\Users\Test\Docs\report.pdf")]
    [InlineData(@"  ""C:\Users\Test\Docs\sub\..\report.pdf""  ", @"C:\Users\Test\Docs\report.pdf")]
    [InlineData(@"C:\Projekte\Größenübersicht\", @"C:\Projekte\Größenübersicht")]
    [InlineData(@"D:\مشاريع\تقرير نهائي.docx", @"D:\مشاريع\تقرير نهائي.docx")]
    [InlineData(@"\\server\share\Team Docs\plan.xlsx", @"\\server\share\Team Docs\plan.xlsx")]
    [InlineData(@"C:\", @"C:\")]
    public void Paths_are_normalised_with_framework_apis(string input, string expected) => Assert.Equal(expected, PathRules.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("report.pdf")]
    [InlineData(@"..\report.pdf")]
    [InlineData("C:report.pdf")]
    [InlineData("C:\\bad\u0000name.txt")]
    public void Relative_or_invalid_paths_are_rejected(string input) => Assert.Null(PathRules.Normalize(input));

    [Fact]
    public void Long_paths_are_supported()
    {
        var deep = @"C:\" + string.Join('\\', Enumerable.Repeat("verylongfoldername", 20)) + @"\file.txt";
        Assert.True(deep.Length > 260);
        Assert.Equal(deep, PathRules.Normalize(deep));
    }

    [Fact]
    public void Kinds_previews_and_temporary_files()
    {
        Assert.Equal(FileKind.Spreadsheet, FileKinds.Classify(".XLSX"));
        Assert.Equal(FileKind.Folder, FileKinds.Classify("", isFolder: true));
        Assert.Equal(FileKind.Other, FileKinds.Classify(".xyz"));
        Assert.Equal(PreviewKind.Markdown, FileKinds.Preview(".md"));
        Assert.Equal(PreviewKind.Image, FileKinds.Preview(".png"));
        Assert.Equal(PreviewKind.None, FileKinds.Preview(".docx")); // no unreliable custom renderers
        Assert.Equal(PreviewKind.Text, FileKinds.Preview(".CSV")); // spreadsheet kind, but plain text is safe to show
        Assert.Equal(FileKind.Spreadsheet, FileKinds.Classify(".csv"));
        Assert.True(FileKinds.IsTemporary("~$Budget.xlsx"));
        Assert.True(FileKinds.IsTemporary("setup.exe.crdownload"));
        Assert.False(FileKinds.IsTemporary("Budget.xlsx"));
        Assert.True(PathRules.IsUnder(@"C:\Docs\Invoices\a.pdf", @"C:\Docs\"));
        Assert.False(PathRules.IsUnder(@"C:\Docs2\a.pdf", @"C:\Docs"));
    }
}

public sealed class SearchAndMarkdownTests
{
    [Theory]
    [InlineData("invoice 2024", "\"invoice\"* \"2024\"*")]
    [InlineData("\"; DROP TABLE x --", "\"DROP\"* \"TABLE\"* \"x\"*")]
    [InlineData("NEAR(a b) OR c*", "\"NEAR\"* \"a\"* \"b\"* \"OR\"* \"c\"*")]
    [InlineData("فاتورة كهرباء", "\"فاتورة\"* \"كهرباء\"*")]
    public void Fts_queries_are_built_from_quoted_prefix_terms(string input, string expected) => Assert.Equal(expected, FtsQuery.Build(input));

    [Fact]
    public void Empty_search_text_yields_no_query()
    {
        Assert.Null(FtsQuery.Build("   "));
        Assert.Null(FtsQuery.Build("*** ()"));
    }

    [Fact]
    public void Command_matching_prefers_prefixes_then_word_starts_then_initials()
    {
        Assert.True(CommandMatcher.Score("open pro", "Open Projects") > CommandMatcher.Score("pro", "Open Projects"));
        Assert.True(CommandMatcher.Score("proj", "Open Projects") > CommandMatcher.Score("jec", "Open Projects"));
        Assert.True(CommandMatcher.Score("np", "New Project") > 0);
        Assert.Equal(0, CommandMatcher.Score("xyz", "Open Games"));
        Assert.True(CommandMatcher.Score("مشاريع", "فتح المشاريع") > 0);
        Assert.True(CommandMatcher.Score("docs", "Open Documents", ["docs", "files"]) > 0);
    }

    [Fact]
    public void Markdown_subset_is_parsed_into_blocks()
    {
        var blocks = MarkdownLite.Parse("# Plan\nIntro with **bold**, *italic* and `code`.\n\n- [x] Done\n- [ ] Todo\n  - nested\n1. first\n```\nvar x = 1;\n```\n> quote\n---\nSee [docs](https://example.com).");
        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Bold && i.Text == "bold");
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Italic && i.Text == "italic");
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Code && i.Text == "code");
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Task && b.Checked);
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Task && !b.Checked);
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Bullet && b.Level == 1);
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Numbered && b.Number == 1);
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Code && b.Code == "var x = 1;");
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Quote);
        Assert.Contains(blocks, b => b.Kind == MdBlockKind.Rule);
        Assert.Contains(blocks[^1].Inlines, i => i.Kind == MdInlineKind.Link && i.Url == "https://example.com");
    }

    [Fact]
    public void Plain_text_stays_plain()
    {
        var blocks = MarkdownLite.Parse("Just a note_with_underscores and 2*3*4 maths.");
        var only = Assert.Single(blocks);
        Assert.Equal(MdBlockKind.Paragraph, only.Kind);
        Assert.DoesNotContain(only.Inlines, i => i.Kind == MdInlineKind.Italic);
    }
}

/// <summary>Projects and documents on the real migrations (in-memory SQLite with FTS5).</summary>
public sealed class ProjectDocumentStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private ProjectStore _projects = null!;
    private DocumentStore _documents = null!;
    private GlobalSearchService _search = null!;
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _projects = new ProjectStore(_factory, new Clock());
        _documents = new DocumentStore(_factory, new Clock());
        _search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(_factory), new Arcadia.Data.Search.GameSearchProvider(_factory)]);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static ProjectDraft Draft(string name, ProjectStatus status = ProjectStatus.Active, DateOnly? target = null) =>
        new(name, "Description", status, ProjectPriority.High, new DateOnly(2026, 9, 1), target, "Cyan", "Projects", "", "home, lab", null);

    private static FileFacts File(string path, bool exists = true, string? content = null) =>
        new(path, exists, false, 1234, Now.AddDays(-3), Now.AddDays(-1), content);

    [Fact]
    public async Task Project_crud_with_tasks_subtasks_notes_links_and_activity()
    {
        var p = await _projects.CreateAsync(Draft("Home Lab"));
        var parent = await _projects.AddTaskAsync(p.Id, new TaskDraft("Buy switch", null, TaskItemStatus.ToDo, ProjectPriority.Normal, new DateOnly(2026, 10, 1), null, "", null));
        var sub = await _projects.AddTaskAsync(p.Id, new TaskDraft("Compare prices", null, TaskItemStatus.ToDo, ProjectPriority.Low, null, null, "", parent.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.AddTaskAsync(p.Id, new TaskDraft("Too deep", null, TaskItemStatus.ToDo, ProjectPriority.Low, null, null, "", sub.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.AddTaskAsync(p.Id, new TaskDraft("  ", null, TaskItemStatus.ToDo, ProjectPriority.Low, null, null, "", null)));

        await _projects.SetTaskStatusAsync(sub.Id, TaskItemStatus.Completed);
        var note = await _projects.AddNoteAsync(p.Id, "Wiring");
        const string text = "# Rack\n- [x] Patch panel\nملاحظة بالعربية\nGrößen prüfen";
        await _projects.SaveNoteAsync(note.Id, "Wiring plan", text, "network");
        await _projects.AddLinkAsync(p.Id, new LinkDraft("", "github.com/arcadia/homelab", "Repo", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.AddLinkAsync(p.Id, new LinkDraft("x", "not a url", null, "")));
        await _projects.SetProgressAsync(p.Id, ProgressMode.TaskBased, 0);

        var loaded = await _projects.GetAsync(p.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Tasks.Count);
        Assert.Single(loaded.Tasks.Single(t => t.Id == parent.Id).Subtasks);
        Assert.Equal(text, loaded.Notes.Single().Content); // exactly as typed
        Assert.Equal("Wiring plan", loaded.Notes.Single().Title);
        Assert.Equal("https://github.com/arcadia/homelab", loaded.Links.Single().Url);
        Assert.Equal("github.com/arcadia", loaded.Links.Single().Title);
        Assert.Equal("home, lab", loaded.Tags);
        Assert.Contains(loaded.Activity, a => a.Kind == ProjectActivityKind.ProjectCreated);
        Assert.Contains(loaded.Activity, a => a.Kind == ProjectActivityKind.TaskCompleted && a.Subject == "Compare prices");
        Assert.Contains(loaded.Activity, a => a.Kind == ProjectActivityKind.NoteAdded);
        Assert.Contains(loaded.Activity, a => a.Kind == ProjectActivityKind.LinkAdded);

        var summary = Assert.Single(await _projects.ListAsync());
        Assert.Equal(50, summary.Progress); // 1 of 2 tasks
        Assert.Equal(1, summary.OpenTasks);

        await _projects.DeleteTaskAsync(parent.Id); // takes its subtask with it
        Assert.Empty((await _projects.GetAsync(p.Id))!.Tasks);
    }

    [Fact]
    public async Task Archiving_keeps_everything_and_restores_the_previous_status()
    {
        var p = await _projects.CreateAsync(Draft("Thesis", ProjectStatus.OnHold));
        await _projects.AddTaskAsync(p.Id, new TaskDraft("Outline", null, TaskItemStatus.ToDo, ProjectPriority.Normal, null, null, "", null));
        await _projects.AddNoteAsync(p.Id, "Sources", "Kant, Hegel");
        await _projects.ArchiveAsync(p.Id);

        var archived = await _projects.GetAsync(p.Id);
        Assert.Equal(ProjectStatus.Archived, archived!.Status);
        Assert.Single(archived.Tasks);
        Assert.Single(archived.Notes);
        Assert.NotEmpty(await _search.SearchAsync(new SearchRequest("Hegel"))); // still searchable

        await _projects.RestoreAsync(p.Id);
        Assert.Equal(ProjectStatus.OnHold, (await _projects.GetAsync(p.Id))!.Status);
        var activity = await _projects.GetActivityAsync(p.Id, 50);
        Assert.Contains(activity, a => a.Kind == ProjectActivityKind.ProjectArchived);
        Assert.Contains(activity, a => a.Kind == ProjectActivityKind.ProjectRestored);
    }

    [Fact]
    public async Task Stats_and_deadlines()
    {
        var today = DateOnly.FromDateTime(Now);
        var a = await _projects.CreateAsync(Draft("A", target: today.AddDays(5)));
        await _projects.CreateAsync(Draft("B", ProjectStatus.Completed, today.AddDays(1)));
        await _projects.CreateAsync(Draft("C", ProjectStatus.OnHold));
        await _projects.AddTaskAsync(a.Id, new TaskDraft("Overdue", null, TaskItemStatus.ToDo, ProjectPriority.Normal, today.AddDays(-2), null, "", null));
        await _projects.AddTaskAsync(a.Id, new TaskDraft("Later", null, TaskItemStatus.ToDo, ProjectPriority.Normal, today.AddDays(60), null, "", null));

        var stats = await _projects.GetStatsAsync(today, 14);
        Assert.Equal(1, stats.Active);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.OnHold);
        Assert.Equal(2, stats.TasksRemaining);
        var deadlines = await _projects.GetDeadlinesAsync(today, 14);
        Assert.Equal(["Overdue", "A"], deadlines.Select(d => d.Title));
    }

    [Fact]
    public async Task Search_finds_every_kind_including_arabic_and_german_text()
    {
        var p = await _projects.CreateAsync(Draft("Umzug Dresden"));
        await _projects.AddTaskAsync(p.Id, new TaskDraft("Kündigung schreiben", "Stromvertrag", TaskItemStatus.ToDo, ProjectPriority.Normal, null, null, "", null));
        var n = await _projects.AddNoteAsync(p.Id, "قائمة");
        await _projects.SaveNoteAsync(n.Id, "قائمة المشتريات", "شراء صناديق كرتون", "");
        await _documents.AddAsync([File(@"C:\Docs\Mietvertrag Dresden.pdf")], p.Id, null);

        var results = await _search.SearchAsync(new SearchRequest("dresden"));
        Assert.Contains(results, r => r.Kind == SearchResultKind.Project);
        Assert.Contains(results, r => r.Kind == SearchResultKind.Document);
        Assert.Contains(await _search.SearchAsync(new SearchRequest("kundig")), r => r.Kind == SearchResultKind.Task); // diacritics folded
        var arabic = await _search.SearchAsync(new SearchRequest("صناديق"));
        Assert.Equal(SearchResultKind.Note, Assert.Single(arabic).Kind);
        Assert.Equal(p.Id, arabic[0].ProjectId);
        var onlyTasks = await _search.SearchAsync(new SearchRequest("dresden strom", new HashSet<SearchResultKind> { SearchResultKind.Task }));
        Assert.All(onlyTasks, r => Assert.Equal(SearchResultKind.Task, r.Kind));
        Assert.Empty(await _search.SearchAsync(new SearchRequest("\"; DROP TABLE Projects --")));
    }

    [Fact]
    public async Task Documents_are_references_shared_between_library_and_projects()
    {
        var p = await _projects.CreateAsync(Draft("Taxes"));
        var q = await _projects.CreateAsync(Draft("Car"));
        var result = await _documents.AddAsync(
        [
            File(@"C:\Docs\Invoice 2024.pdf"),
            File(@"C:\Docs\invoice 2024.PDF"),          // same file, different case
            File(@"C:\Docs\~$Budget.xlsx"),             // Office lock file
            File(@"relative\path.txt"),                 // not a full path
            File(@"C:\Docs\notes.md", content: "tax deductible items"),
        ], p.Id, null);
        Assert.Equal(2, result.Added);
        Assert.Equal(3, result.Skipped);

        var invoice = (await _documents.FindByPathAsync(@"c:\docs\invoice 2024.pdf"))!;
        await _documents.AssociateAsync(invoice.Id, q.Id);
        Assert.Equal(["Car", "Taxes"], (await _documents.GetProjectsAsync(invoice.Id)).Select(x => x.Name).Order());
        Assert.Equal(2, (await _documents.ListForProjectAsync(p.Id)).Count);

        // adding it again from another project only associates
        var again = await _documents.AddAsync([File(@"C:\Docs\Invoice 2024.pdf")], q.Id, null);
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.AlreadyInLibrary);

        await _documents.DissociateAsync(invoice.Id, p.Id); // "Remove from project"
        Assert.Single(await _documents.ListForProjectAsync(p.Id));
        Assert.NotNull(await _documents.GetAsync(invoice.Id)); // still in the library

        Assert.Contains((await _search.SearchAsync(new SearchRequest("deductible"))), r => r.Kind == SearchResultKind.Document); // content indexed
        var page = await _documents.QueryAsync(new DocumentQuery(Text: "car"), Now);
        Assert.Contains(page.Rows, r => r.Id == invoice.Id); // found by associated project name

        await _documents.RemoveAsync([invoice.Id]);
        Assert.Null(await _documents.GetAsync(invoice.Id));
        Assert.Empty(await _search.SearchAsync(new SearchRequest("invoice", new HashSet<SearchResultKind> { SearchResultKind.Document })));
        var activity = await _projects.GetActivityAsync(q.Id, 20);
        Assert.Contains(activity, a => a.Kind == ProjectActivityKind.FileRemoved);
    }

    [Fact]
    public async Task Tags_are_reusable_and_filterable()
    {
        await _documents.AddAsync([File(@"C:\A\one.pdf"), File(@"C:\A\two.docx"), File(@"C:\A\three.png")], null, null);
        var docs = (await _documents.QueryAsync(new DocumentQuery(), Now)).Rows;
        await _documents.SetTagsAsync(docs[0].Id, ["Invoice", "Work"]);
        await _documents.SetTagsAsync(docs[1].Id, ["invoice", "فاتورة"]); // same tag, different case
        var tags = await _documents.GetTagsAsync();
        Assert.Equal(3, tags.Count);
        Assert.Equal(2, tags.Single(t => t.Name == "Invoice").Count);

        var byTag = await _documents.QueryAsync(new DocumentQuery(TagId: tags.Single(t => t.Name == "Invoice").Id), Now);
        Assert.Equal(2, byTag.Total);
        Assert.Equal("Invoice, Work", byTag.Rows.Single(r => r.Id == docs[0].Id).Tags);

        await _documents.SetTagsAsync(docs[0].Id, []);
        Assert.DoesNotContain(await _documents.GetTagsAsync(), t => t.Name == "Work"); // unused tags are cleaned up

        var images = await _documents.QueryAsync(new DocumentQuery(Kind: FileKind.Image), Now);
        Assert.Equal("three.png", Assert.Single(images.Rows).DisplayName);
        var counts = await _documents.GetCountsAsync(Now);
        Assert.Equal(3, counts.All);
        Assert.Equal(1, counts.ByKind[FileKind.Pdf]);
    }

    [Fact]
    public async Task Missing_files_can_be_relocated()
    {
        var project = await _projects.CreateAsync(Draft("Umzug"));
        await _documents.AddAsync([File(@"C:\Old\Plan.docx")], project.Id, null);
        var doc = (await _documents.FindByPathAsync(@"C:\Old\Plan.docx"))!;
        await _documents.UpdateAvailabilityAsync([(doc.Id, new FileFacts(doc.Path, false, false, null, null, null))], Now);
        var missing = await _documents.QueryAsync(new DocumentQuery(View: DocumentView.Missing), Now);
        Assert.Equal(doc.Id, Assert.Single(missing.Rows).Id);

        await _documents.RelocateAsync(doc.Id, File(@"E:\Neuer Ordner\Plan final.docx"));
        var moved = (await _documents.GetAsync(doc.Id))!;
        Assert.False(moved.IsMissing);
        Assert.Equal(@"E:\Neuer Ordner\Plan final.docx", moved.Path);
        Assert.Equal("Plan final.docx", moved.DisplayName); // default name follows the file
        var history = await _projects.GetActivityAsync(project.Id, 10);
        Assert.Contains(history, a => a.Kind == ProjectActivityKind.FileRelocated && a.Subject == "Plan final.docx");

        await _documents.AddAsync([File(@"C:\Other.docx")], null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _documents.RelocateAsync(doc.Id, File(@"C:\Other.docx")));
    }

    [Fact]
    public async Task Watched_folder_renames_follow_the_file()
    {
        var folder = await _documents.AddWatchedFolderAsync(@"C:\Watched", true);
        await _documents.AddAsync([File(@"C:\Watched\a.txt")], null, folder.Id);
        await _documents.RenamePathAsync(@"C:\Watched\a.txt", File(@"C:\Watched\b.txt"));
        Assert.NotNull(await _documents.FindByPathAsync(@"C:\Watched\b.txt"));
        await _documents.MarkMissingByPathAsync(@"C:\Watched\b.txt");
        Assert.True((await _documents.FindByPathAsync(@"C:\Watched\b.txt"))!.IsMissing);
        await _documents.RemoveWatchedFolderAsync(folder.Id, removeDocuments: true);
        Assert.Empty(await _documents.GetWatchedFoldersAsync());
        Assert.Null(await _documents.FindByPathAsync(@"C:\Watched\b.txt"));
    }

    [Fact]
    public async Task Deleting_a_project_keeps_its_documents_in_the_library()
    {
        var p = await _projects.CreateAsync(Draft("Temp"));
        await _documents.AddAsync([File(@"C:\Keep\me.pdf")], p.Id, null);
        await _projects.DeleteAsync(p.Id);
        Assert.NotNull(await _documents.FindByPathAsync(@"C:\Keep\me.pdf"));
        Assert.Empty(await _projects.ListAsync());
        Assert.Empty(await _search.SearchAsync(new SearchRequest("Temp", new HashSet<SearchResultKind> { SearchResultKind.Project })));
    }
}

/// <summary>The Projects/Documents migration on a database that already has games and diagnostic sessions.</summary>
public sealed class ProjectsDocumentsMigrationTests
{
    [Fact]
    public async Task Existing_games_and_diagnostics_survive_and_the_index_is_created()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-p4-{Guid.NewGuid():N}")).FullName; // own folder: the upgrade writes Backups\ next to the file
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("ItToolsDiagnostics");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Games (Title, Platform, Tags, Status, Priority, CompletionPercent, PlaytimeHours, EstimatedHours, AchievementsUnlocked, AchievementsTotal, AddedAtUtc, IsFeatured, ArtworkTheme, ArtworkSeed, IsSample) " +
                    "VALUES ('Elden Ring', 'PC', 'RPG', 'Playing', 'High', 45, 61.5, 100, 12, 42, '2026-01-01 00:00:00', 1, 'castle-dusk', 7, 0)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO DiagnosticSessions (Title, Reference, ComputerName, CreatedAtUtc, UpdatedAtUtc) VALUES ('Printer', 'T-1', 'PC', '2026-09-01 10:00:00', '2026-09-01 10:00:00')");
            }

            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);

            await using var after = new ArcadiaDbContext(options);
            Assert.Equal("Elden Ring", (await after.Games.SingleAsync()).Title);
            Assert.Equal("Printer", (await after.DiagnosticSessions.SingleAsync()).Title);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_ProjectsDocuments", StringComparison.Ordinal));
            Assert.Equal(0, await after.Projects.CountAsync());

            // the FTS table exists and works
            var store = new ProjectStore(factory, TimeProvider.System);
            await store.CreateAsync(new ProjectDraft("Garden", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Green", "Projects", "", "", null));
            var hits = await new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(factory)]).SearchAsync(new SearchRequest("gard"));
            Assert.Single(hits);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FileFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}
