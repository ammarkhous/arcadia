using Arcadia.Core.Finance;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Arcadia.Core.Organizer;
using Arcadia.Core.Games;
using Arcadia.Core.Documents;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.Projects;
using Arcadia.Core.Travel;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed class ArcadiaDbContext(DbContextOptions<ArcadiaDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Screenshot> Screenshots => Set<Screenshot>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<DiagnosticSession> DiagnosticSessions => Set<DiagnosticSession>();
    public DbSet<DiagnosticResult> DiagnosticResults => Set<DiagnosticResult>();
    public DbSet<DiagnosticNote> DiagnosticNotes => Set<DiagnosticNote>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<ProjectLink> ProjectLinks => Set<ProjectLink>();
    public DbSet<ProjectActivity> ProjectActivity => Set<ProjectActivity>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();
    public DbSet<DocumentProject> DocumentProjects => Set<DocumentProject>();
    public DbSet<WatchedFolder> WatchedFolders => Set<WatchedFolder>();
    public DbSet<TravelPlace> TravelPlaces => Set<TravelPlace>();
    public DbSet<PlaceVisit> PlaceVisits => Set<PlaceVisit>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripPlace> TripPlaces => Set<TripPlace>();
    public DbSet<TripDay> TripDays => Set<TripDay>();
    public DbSet<ItineraryItem> ItineraryItems => Set<ItineraryItem>();
    public DbSet<TripDocument> TripDocuments => Set<TripDocument>();
    public DbSet<TravelJournalEntry> TravelJournalEntries => Set<TravelJournalEntry>();
    public DbSet<TravelPhoto> TravelPhotos => Set<TravelPhoto>();
    public DbSet<BucketListItem> BucketListItems => Set<BucketListItem>();
    public DbSet<TravelCountry> TravelCountries => Set<TravelCountry>();
    public DbSet<NoteRevision> NoteRevisions => Set<NoteRevision>();
    public DbSet<CalendarCategory> CalendarCategories => Set<CalendarCategory>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarEventException> CalendarEventExceptions => Set<CalendarEventException>();
    public DbSet<FinanceAccount> FinanceAccounts => Set<FinanceAccount>();
    public DbSet<FinanceCategory> FinanceCategories => Set<FinanceCategory>();
    public DbSet<FinanceTransaction> FinanceTransactions => Set<FinanceTransaction>();
    public DbSet<TransactionDocument> TransactionDocuments => Set<TransactionDocument>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<RecurringItem> RecurringItems => Set<RecurringItem>();
    public DbSet<RecurringOccurrence> RecurringOccurrences => Set<RecurringOccurrence>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<Arcadia.Core.Automation.AutomationEntity> Automations => Set<Arcadia.Core.Automation.AutomationEntity>();
    public DbSet<Arcadia.Core.Automation.AutomationRunEntity> AutomationRuns => Set<Arcadia.Core.Automation.AutomationRunEntity>();
    public DbSet<Arcadia.Core.Automation.WorkSession> WorkSessions => Set<Arcadia.Core.Automation.WorkSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Game>(e =>
        {
            e.ToTable("Games");
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Platform).HasMaxLength(64);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.CurrentObjective).HasMaxLength(256);
            e.Property(x => x.ArtworkTheme).HasMaxLength(64);
            e.Property(x => x.CoverImagePath).HasMaxLength(1024);
            e.Property(x => x.CoverThumbnailPath).HasMaxLength(1024);
            e.Property(x => x.HeroImagePath).HasMaxLength(1024);
            e.Property(x => x.LaunchTarget).HasMaxLength(1024);
            e.Property(x => x.InstallDirectory).HasMaxLength(1024);
            e.Property(x => x.Executables).HasMaxLength(2048);
            e.Property(x => x.BoxArtPath).HasMaxLength(1024);
            e.Property(x => x.LogoImagePath).HasMaxLength(1024);
            e.Property(x => x.Developer).HasMaxLength(256);
            e.Property(x => x.Publisher).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.TagList);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.LastPlayedAtUtc);
            e.HasMany(x => x.JournalEntries).WithOne(x => x.Game).HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Screenshots).WithOne(x => x.Game).HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Sessions).WithOne(x => x.Game).HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("JournalEntries");
            e.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<Screenshot>(e =>
        {
            e.ToTable("Screenshots");
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Location).HasMaxLength(256);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.ArtworkTheme).HasMaxLength(64);
            e.Property(x => x.ImagePath).HasMaxLength(1024);
            e.Property(x => x.ThumbnailPath).HasMaxLength(1024);
            e.Property(x => x.SourcePath).HasMaxLength(1024);
            e.HasIndex(x => x.CapturedAtUtc);
            e.HasIndex(x => x.SourcePath);
        });

        b.Entity<GameSession>(e =>
        {
            e.ToTable("GameSessions");
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Ignore(x => x.Hours);
            e.HasIndex(x => x.StartedAtUtc);
        });

        // IT Tools: troubleshooting sessions (independent of the games tables).
        b.Entity<DiagnosticSession>(e =>
        {
            e.ToTable("DiagnosticSessions");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(200);
            e.Property(x => x.ComputerName).HasMaxLength(128);
            e.Ignore(x => x.IsClosed);
            e.HasIndex(x => x.UpdatedAtUtc);
            e.HasMany(x => x.Results).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Notes).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DiagnosticResult>(e =>
        {
            e.ToTable("DiagnosticResults");
            e.Property(x => x.Tool).HasMaxLength(32).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.SectionJson).IsRequired();
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<DiagnosticNote>(e =>
        {
            e.ToTable("DiagnosticNotes");
            e.Property(x => x.Text).HasMaxLength(8000).IsRequired();
            e.HasIndex(x => x.CreatedAtUtc);
        });

        // ── Projects ────────────────────────────────────────────────
        b.Entity<Project>(e =>
        {
            e.ToTable("Projects");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Description).HasMaxLength(20000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.StatusBeforeArchive).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ProgressMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.ArtworkTheme).HasMaxLength(64);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.FolderPath).HasMaxLength(2048);
            e.Ignore(x => x.IsArchived);
            e.Ignore(x => x.TagList);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.UpdatedAtUtc);
            e.HasMany(x => x.Tasks).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Notes).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Links).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Activity).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        // Phase 6: the Phase 4 project tasks and notes became Arcadia-wide tasks and notes (same rows, renamed tables).
        b.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ContextKind).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.IsDone);
            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.Context);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ReminderAt);
            e.HasIndex(x => new { x.ContextKind, x.ContextId });
            e.HasMany(x => x.Subtasks).WithOne(x => x.ParentTask).HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Note>(e =>
        {
            e.ToTable("Notes");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.ContextKind).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.Context);
            e.HasIndex(x => x.UpdatedAtUtc);
            e.HasIndex(x => new { x.ContextKind, x.ContextId });
        });

        b.Entity<NoteRevision>(e =>
        {
            e.ToTable("NoteRevisions");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.HasOne(x => x.Note).WithMany().HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.NoteId, x.SavedAtUtc });
        });

        b.Entity<CalendarCategory>(e =>
        {
            e.ToTable("CalendarCategories");
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.HasData(
                new CalendarCategory { Id = 1, Key = "personal", Icon = "Home", Accent = "Blue", SortOrder = 1 },
                new CalendarCategory { Id = 2, Key = "work", Icon = "Suitcase", Accent = "Steel", SortOrder = 2 },
                new CalendarCategory { Id = 3, Key = "gaming", Icon = "Games", Accent = "Purple", SortOrder = 3 },
                new CalendarCategory { Id = 4, Key = "travel", Icon = "Travel", Accent = "Green", SortOrder = 4 },
                new CalendarCategory { Id = 5, Key = "project", Icon = "Projects", Accent = "Cyan", SortOrder = 5 },
                new CalendarCategory { Id = 6, Key = "appointment", Icon = "Calendar", Accent = "Gold", SortOrder = 6 },
                new CalendarCategory { Id = 7, Key = "important", Icon = "Warning", Accent = "Red", SortOrder = 7 });
        });

        b.Entity<CalendarEvent>(e =>
        {
            e.ToTable("CalendarEvents");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Weekdays).HasConversion<int>();
            e.Property(x => x.ContextKind).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.IsRecurring);
            e.Ignore(x => x.Duration);
            e.Ignore(x => x.Context);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<TravelPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Exceptions).WithOne(x => x.Event).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Start);
            e.HasIndex(x => x.End);
            e.HasIndex(x => x.RecursUntil);
            e.HasIndex(x => new { x.ContextKind, x.ContextId });
        });

        b.Entity<CalendarEventException>(e =>
        {
            e.ToTable("CalendarEventExceptions");
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(8000);
            e.HasIndex(x => new { x.EventId, x.OriginalDate }).IsUnique();
        });

        b.Entity<ProjectLink>(e =>
        {
            e.ToTable("ProjectLinks");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Tags).HasMaxLength(512);
        });

        b.Entity<ProjectActivity>(e =>
        {
            e.ToTable("ProjectActivity");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Subject).HasMaxLength(400);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAtUtc });
        });

        // ── Documents ───────────────────────────────────────────────
        b.Entity<Document>(e =>
        {
            e.ToTable("Documents");
            e.Property(x => x.DisplayName).HasMaxLength(400).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Path).HasMaxLength(4096).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Extension).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.HasIndex(x => x.Path).IsUnique();
            e.HasIndex(x => x.Extension);
            e.HasIndex(x => x.AddedAtUtc);
            e.HasIndex(x => x.LastOpenedAtUtc);
            e.HasOne(x => x.WatchedFolder).WithMany().HasForeignKey(x => x.WatchedFolderId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Tag>(e =>
        {
            e.ToTable("Tags");
            e.Property(x => x.Name).HasMaxLength(60).IsRequired().UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<DocumentTag>(e =>
        {
            e.ToTable("DocumentTags");
            e.HasKey(x => new { x.DocumentId, x.TagId });
            e.HasOne(x => x.Document).WithMany(x => x.Tags).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(x => x.Documents).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DocumentProject>(e =>
        {
            e.ToTable("DocumentProjects");
            e.HasKey(x => new { x.DocumentId, x.ProjectId });
            e.HasOne(x => x.Document).WithMany(x => x.Projects).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<WatchedFolder>(e =>
        {
            e.ToTable("WatchedFolders");
            e.Property(x => x.Path).HasMaxLength(4096).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.Path).IsUnique();
        });

        // ── Travel ──────────────────────────────────────────────────
        // Deleting a trip or place never deletes photos, journal entries or documents: they are unlinked (SetNull).
        b.Entity<TravelPlace>(e =>
        {
            e.ToTable("TravelPlaces");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.Region).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(200);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.Notes).HasMaxLength(50000);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Ignore(x => x.HasPosition);
            e.Ignore(x => x.TagList);
            e.HasIndex(x => x.CountryCode);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Source);
            e.HasMany(x => x.Visits).WithOne(x => x.Place).HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlaceVisit>(e =>
        {
            e.ToTable("PlaceVisits");
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.VisitedOn);
        });

        b.Entity<Trip>(e =>
        {
            e.ToTable("Trips");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Description).HasMaxLength(20000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ArtworkTheme).HasMaxLength(64);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Ignore(x => x.TagList);
            e.HasIndex(x => x.StartDate);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.PrimaryPlace).WithMany().HasForeignKey(x => x.PrimaryPlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Days).WithOne(x => x.Trip).HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TripPlace>(e =>
        {
            e.ToTable("TripPlaces");
            e.HasKey(x => new { x.TripId, x.PlaceId });
            e.HasOne(x => x.Trip).WithMany(x => x.Places).HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Place).WithMany(x => x.Trips).HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PlaceId);
        });

        b.Entity<TripDay>(e =>
        {
            e.ToTable("TripDays");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.HasIndex(x => new { x.TripId, x.Index });
            e.HasMany(x => x.Items).WithOne(x => x.Day).HasForeignKey(x => x.DayId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ItineraryItem>(e =>
        {
            e.ToTable("ItineraryItems");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Slot).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(24);
            e.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.DayId, x.SortOrder });
        });

        b.Entity<TripDocument>(e =>
        {
            e.ToTable("TripDocuments");
            e.HasKey(x => new { x.TripId, x.DocumentId });
            e.HasOne(x => x.Trip).WithMany(x => x.Documents).HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DocumentId);
        });

        b.Entity<TravelJournalEntry>(e =>
        {
            e.ToTable("TravelJournalEntries");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Text).IsRequired();
            e.Property(x => x.Mood).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.EntryDate);
        });

        b.Entity<TravelPhoto>(e =>
        {
            e.ToTable("TravelPhotos");
            e.Property(x => x.Camera).HasMaxLength(120);
            e.Property(x => x.Caption).HasMaxLength(1000);
            e.Ignore(x => x.HasGps);
            e.HasIndex(x => x.DocumentId).IsUnique();
            e.HasIndex(x => x.TakenAt);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.JournalEntry).WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BucketListItem>(e =>
        {
            e.ToTable("BucketListItems");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.SetNull);
        });

        // ── Finance (Phase 7) ───────────────────────────────────────
        // Money is decimal in code and stored as exact INTEGER ten-thousandths (MoneyConverter): exact sums, correct
        // ordering, no floating point anywhere. Balances are never stored; they are derived from the transactions.
        b.Entity<FinanceAccount>(e =>
        {
            e.ToTable("FinanceAccounts");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.OpeningBalance).HasConversion<MoneyConverter>();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.Accent).HasMaxLength(16);
        });

        b.Entity<FinanceCategory>(e =>
        {
            e.ToTable("FinanceCategories");
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ParentId);
            e.HasData(FinanceDefaults.Categories.Select(d => new FinanceCategory
            {
                Id = d.Id, Key = d.Key, Kind = d.Kind, ParentId = d.ParentId, Icon = d.Icon, Accent = d.Accent, SortOrder = d.Id,
            }));
        });

        b.Entity<FinanceTransaction>(e =>
        {
            e.ToTable("FinanceTransactions");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Amount).HasConversion<MoneyConverter>();
            e.Property(x => x.ToAmount).HasConversion<MoneyConverter>();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Payee).HasMaxLength(200);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.ImportBatch).HasMaxLength(40);
            e.Ignore(x => x.IsTransfer);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToAccount).WithMany().HasForeignKey(x => x.ToAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<RecurringItem>().WithMany().HasForeignKey(x => x.RecurringItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Documents).WithOne(x => x.Transaction).HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AccountId, x.Date });
            e.HasIndex(x => x.ToAccountId);
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.TripId);
            e.HasIndex(x => x.RecurringItemId);
            e.HasIndex(x => x.ImportBatch);
        });

        b.Entity<TransactionDocument>(e =>
        {
            e.ToTable("TransactionDocuments");
            e.HasKey(x => new { x.TransactionId, x.DocumentId });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DocumentId);
        });

        b.Entity<Budget>(e =>
        {
            e.ToTable("Budgets");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Amount).HasConversion<MoneyConverter>();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Period).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.TripId);
        });

        b.Entity<RecurringItem>(e =>
        {
            e.ToTable("RecurringItems");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Amount).HasConversion<MoneyConverter>();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Payee).HasMaxLength(200);
            e.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Ignore(x => x.TransactionType);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Occurrences).WithOne(x => x.RecurringItem).HasForeignKey(x => x.RecurringItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringOccurrence>(e =>
        {
            e.ToTable("RecurringOccurrences");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // deleting the payment makes the due date open again
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RecurringItemId, x.DueDate }).IsUnique();
            e.HasIndex(x => x.TransactionId);
        });

        b.Entity<SavingsGoal>(e =>
        {
            e.ToTable("SavingsGoals");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TargetAmount).HasConversion<MoneyConverter>();
            e.Property(x => x.CurrentAmount).HasConversion<MoneyConverter>();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Icon).HasMaxLength(32);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        // ── Phase 9: automation ──
        b.Entity<Arcadia.Core.Automation.AutomationEntity>(e =>
        {
            e.ToTable("Automations");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.TriggerType).HasMaxLength(64).IsRequired();
            e.Property(x => x.ConditionMode).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.LastStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.HasIndex(x => x.TriggerType);
        });
        b.Entity<Arcadia.Core.Automation.AutomationRunEntity>(e =>
        {
            e.ToTable("AutomationRuns");
            e.Property(x => x.AutomationName).HasMaxLength(200);
            e.Property(x => x.TriggerType).HasMaxLength(64);
            e.Property(x => x.EventKey).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.HasOne<Arcadia.Core.Automation.AutomationEntity>().WithMany().HasForeignKey(x => x.AutomationId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.StartedAtUtc);
            e.HasIndex(x => new { x.AutomationId, x.EventKey });
        });
        b.Entity<Arcadia.Core.Automation.WorkSession>(e =>
        {
            e.ToTable("WorkSessions");
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.EndedAtUtc);
        });

        b.Entity<TravelCountry>(e =>
        {
            e.ToTable("TravelCountries");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(8);
            e.Property(x => x.Notes).HasMaxLength(4000);
        });
    }
}

/// <summary>
/// decimal ⇄ INTEGER ten-thousandths (like SQL Server's money type). Finance validates amounts to at most four decimals,
/// so the conversion is exact both ways.
/// </summary>
public sealed class MoneyConverter() : ValueConverter<decimal, long>(v => (long)decimal.Round(v * 10000m, 0, MidpointRounding.AwayFromZero), v => v / 10000m);

