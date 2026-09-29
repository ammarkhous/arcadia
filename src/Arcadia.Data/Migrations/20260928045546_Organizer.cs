using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Arcadia.Data.Migrations
{
    /// <inheritdoc />
    public partial class Organizer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 4 project tasks and notes become Arcadia-wide tasks and notes. EF cannot rename a table and relax a column in
            // one step on SQLite, so the new tables are created and every row is copied with its id (search-index entries and
            // links stay valid); the old tables are dropped only after the copy.
            migrationBuilder.CreateTable(
                name: "CalendarCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContextKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ContextId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContextKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ContextId = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DueTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    ReminderAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Tasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                "INSERT INTO \"Tasks\" (\"Id\", \"ProjectId\", \"ParentTaskId\", \"Title\", \"Description\", \"Status\", \"Priority\", \"DueDate\", \"IsPinned\", \"Notes\", \"Tags\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"CompletedAtUtc\") " +
                "SELECT \"Id\", \"ProjectId\", \"ParentTaskId\", \"Title\", \"Description\", \"Status\", \"Priority\", \"DueDate\", 0, \"Notes\", \"Tags\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"CompletedAtUtc\" " +
                "FROM \"ProjectTasks\" ORDER BY (\"ParentTaskId\" IS NOT NULL), \"Id\";");
            migrationBuilder.Sql(
                "INSERT INTO \"Notes\" (\"Id\", \"ProjectId\", \"Title\", \"Content\", \"IsPinned\", \"IsFavorite\", \"Tags\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
                "SELECT \"Id\", \"ProjectId\", \"Title\", \"Content\", \"IsPinned\", 0, \"Tags\", \"CreatedAtUtc\", \"UpdatedAtUtc\" FROM \"ProjectNotes\" ORDER BY \"Id\";");
            migrationBuilder.DropTable(name: "ProjectNotes");
            migrationBuilder.DropTable(name: "ProjectTasks");

            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: false),
                    End = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReminderMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    Weekdays = table.Column<int>(type: "INTEGER", nullable: false),
                    RecursUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ContextKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ContextId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_CalendarCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "CalendarCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NoteRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NoteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SavedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteRevisions_Notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEventExceptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsCancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: true),
                    End = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarEventExceptions_CalendarEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CalendarCategories",
                columns: new[] { "Id", "Accent", "Icon", "Key", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, "Blue", "Home", "personal", null, 1 },
                    { 2, "Steel", "Suitcase", "work", null, 2 },
                    { 3, "Purple", "Games", "gaming", null, 3 },
                    { 4, "Green", "Travel", "travel", null, 4 },
                    { 5, "Cyan", "Projects", "project", null, 5 },
                    { 6, "Gold", "Calendar", "appointment", null, 6 },
                    { 7, "Red", "Warning", "important", null, 7 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventExceptions_EventId_OriginalDate",
                table: "CalendarEventExceptions",
                columns: new[] { "EventId", "OriginalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CategoryId",
                table: "CalendarEvents",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_ContextKind_ContextId",
                table: "CalendarEvents",
                columns: new[] { "ContextKind", "ContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_End",
                table: "CalendarEvents",
                column: "End");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_PlaceId",
                table: "CalendarEvents",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecursUntil",
                table: "CalendarEvents",
                column: "RecursUntil");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_Start",
                table: "CalendarEvents",
                column: "Start");

            migrationBuilder.CreateIndex(
                name: "IX_NoteRevisions_NoteId_SavedAtUtc",
                table: "NoteRevisions",
                columns: new[] { "NoteId", "SavedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_ContextKind_ContextId",
                table: "Notes",
                columns: new[] { "ContextKind", "ContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_ProjectId",
                table: "Notes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_UpdatedAtUtc",
                table: "Notes",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ContextKind_ContextId",
                table: "Tasks",
                columns: new[] { "ContextKind", "ContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_DueDate",
                table: "Tasks",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId",
                table: "Tasks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ReminderAt",
                table: "Tasks",
                column: "ReminderAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Status",
                table: "Tasks",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventExceptions");

            migrationBuilder.DropTable(
                name: "NoteRevisions");

            migrationBuilder.DropTable(
                name: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "CalendarCategories");

            migrationBuilder.CreateTable(
                name: "ProjectNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectNotes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParentTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTasks_ProjectTasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotes_ProjectId",
                table: "ProjectNotes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotes_UpdatedAtUtc",
                table: "ProjectNotes",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_DueDate",
                table: "ProjectTasks",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_ParentTaskId",
                table: "ProjectTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_ProjectId",
                table: "ProjectTasks",
                column: "ProjectId");

            // back to project-only tasks and notes: rows without a project cannot be represented in Phase 5 and are dropped
            migrationBuilder.Sql(
                "INSERT INTO \"ProjectTasks\" (\"Id\", \"ProjectId\", \"ParentTaskId\", \"Title\", \"Description\", \"Status\", \"Priority\", \"DueDate\", \"Notes\", \"Tags\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"CompletedAtUtc\") " +
                "SELECT \"Id\", \"ProjectId\", \"ParentTaskId\", \"Title\", \"Description\", \"Status\", \"Priority\", \"DueDate\", \"Notes\", \"Tags\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"CompletedAtUtc\" " +
                "FROM \"Tasks\" WHERE \"ProjectId\" IS NOT NULL AND (\"ParentTaskId\" IS NULL OR \"ParentTaskId\" IN (SELECT \"Id\" FROM \"Tasks\" WHERE \"ProjectId\" IS NOT NULL)) " +
                "ORDER BY (\"ParentTaskId\" IS NOT NULL), \"Id\";");
            migrationBuilder.Sql(
                "INSERT INTO \"ProjectNotes\" (\"Id\", \"ProjectId\", \"Title\", \"Content\", \"IsPinned\", \"Tags\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
                "SELECT \"Id\", \"ProjectId\", \"Title\", \"Content\", \"IsPinned\", \"Tags\", \"CreatedAtUtc\", \"UpdatedAtUtc\" FROM \"Notes\" WHERE \"ProjectId\" IS NOT NULL;");
            migrationBuilder.DropTable(name: "Tasks");
            migrationBuilder.DropTable(name: "Notes");
        }
    }
}
