using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arcadia.Data.Migrations
{
    /// <inheritdoc />
    public partial class Travel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TravelCountries",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    WantToVisit = table.Column<bool>(type: "INTEGER", nullable: false),
                    MarkedVisited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelCountries", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "TravelPlaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 50000, nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CoverPhotoId = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSample = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelPlaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CoverPhotoId = table.Column<int>(type: "INTEGER", nullable: true),
                    ArtworkTheme = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArtworkSeed = table.Column<int>(type: "INTEGER", nullable: false),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PrimaryPlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastOpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsSample = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trips_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Trips_TravelPlaces_PrimaryPlaceId",
                        column: x => x.PrimaryPlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BucketListItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TripId = table.Column<int>(type: "INTEGER", nullable: true),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetYear = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSample = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BucketListItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BucketListItems_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BucketListItems_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PlaceVisits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TripId = table.Column<int>(type: "INTEGER", nullable: true),
                    VisitedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceVisits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaceVisits_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaceVisits_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TravelJournalEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TripId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Mood = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsHighlight = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSample = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelJournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelJournalEntries_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TravelJournalEntries_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TripDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TripId = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripDays_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripDocuments",
                columns: table => new
                {
                    TripId = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsImportant = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripDocuments", x => new { x.TripId, x.DocumentId });
                    table.ForeignKey(
                        name: "FK_TripDocuments_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripDocuments_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripPlaces",
                columns: table => new
                {
                    TripId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripPlaces", x => new { x.TripId, x.PlaceId });
                    table.ForeignKey(
                        name: "FK_TripPlaces_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripPlaces_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TravelPhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    TripId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    JournalEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    TakenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Camera = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Orientation = table.Column<int>(type: "INTEGER", nullable: false),
                    PixelWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    PixelHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsHighlight = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelPhotos_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TravelPhotos_TravelJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "TravelJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TravelPhotos_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TravelPhotos_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ItineraryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DayId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    IsDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItineraryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItineraryItems_TravelPlaces_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "TravelPlaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ItineraryItems_TripDays_DayId",
                        column: x => x.DayId,
                        principalTable: "TripDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BucketListItems_PlaceId",
                table: "BucketListItems",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_BucketListItems_TripId",
                table: "BucketListItems",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_ItineraryItems_DayId_SortOrder",
                table: "ItineraryItems",
                columns: new[] { "DayId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ItineraryItems_PlaceId",
                table: "ItineraryItems",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisits_PlaceId",
                table: "PlaceVisits",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisits_TripId",
                table: "PlaceVisits",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisits_VisitedOn",
                table: "PlaceVisits",
                column: "VisitedOn");

            migrationBuilder.CreateIndex(
                name: "IX_TravelJournalEntries_EntryDate",
                table: "TravelJournalEntries",
                column: "EntryDate");

            migrationBuilder.CreateIndex(
                name: "IX_TravelJournalEntries_PlaceId",
                table: "TravelJournalEntries",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelJournalEntries_TripId",
                table: "TravelJournalEntries",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPhotos_DocumentId",
                table: "TravelPhotos",
                column: "DocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelPhotos_JournalEntryId",
                table: "TravelPhotos",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPhotos_PlaceId",
                table: "TravelPhotos",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPhotos_TakenAt",
                table: "TravelPhotos",
                column: "TakenAt");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPhotos_TripId",
                table: "TravelPhotos",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPlaces_CountryCode",
                table: "TravelPlaces",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPlaces_Source",
                table: "TravelPlaces",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_TravelPlaces_Status",
                table: "TravelPlaces",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TripDays_TripId_Index",
                table: "TripDays",
                columns: new[] { "TripId", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_TripDocuments_DocumentId",
                table: "TripDocuments",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_TripPlaces_PlaceId",
                table: "TripPlaces",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_PrimaryPlaceId",
                table: "Trips",
                column: "PrimaryPlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_ProjectId",
                table: "Trips",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_StartDate",
                table: "Trips",
                column: "StartDate");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_Status",
                table: "Trips",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BucketListItems");

            migrationBuilder.DropTable(
                name: "ItineraryItems");

            migrationBuilder.DropTable(
                name: "PlaceVisits");

            migrationBuilder.DropTable(
                name: "TravelCountries");

            migrationBuilder.DropTable(
                name: "TravelPhotos");

            migrationBuilder.DropTable(
                name: "TripDocuments");

            migrationBuilder.DropTable(
                name: "TripPlaces");

            migrationBuilder.DropTable(
                name: "TripDays");

            migrationBuilder.DropTable(
                name: "TravelJournalEntries");

            migrationBuilder.DropTable(
                name: "Trips");

            migrationBuilder.DropTable(
                name: "TravelPlaces");
        }
    }
}
