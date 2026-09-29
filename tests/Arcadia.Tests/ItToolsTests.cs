using Arcadia.Core.Organizer;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Arcadia.Core.Games;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.ItTools.SystemInfo;
using Arcadia.Data;
using Arcadia.ItTools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class SubnetCalculatorTests
{
    private static Ipv4Subnet Parse(string address, string prefix)
    {
        Assert.True(Ipv4Subnet.TryParse(address, prefix, out var s, out var error), error);
        return s;
    }

    [Fact]
    public void Class_c_network_has_254_usable_hosts()
    {
        var s = Parse("192.168.1.130", "24");
        Assert.Equal("192.168.1.0", s.NetworkAddress.ToString());
        Assert.Equal("192.168.1.255", s.BroadcastAddress.ToString());
        Assert.Equal("255.255.255.0", s.SubnetMask.ToString());
        Assert.Equal("0.0.0.255", s.WildcardMask.ToString());
        Assert.Equal("192.168.1.1", s.FirstUsable.ToString());
        Assert.Equal("192.168.1.254", s.LastUsable.ToString());
        Assert.Equal(256UL, s.TotalAddresses);
        Assert.Equal(254UL, s.UsableHosts);
        Assert.Equal("192.168.1.0/24", s.Cidr);
    }

    [Fact]
    public void Odd_prefix_splits_octets_correctly()
    {
        var s = Parse("172.17.48.77", "/20");
        Assert.Equal("172.17.48.0", s.NetworkAddress.ToString());
        Assert.Equal("172.17.63.255", s.BroadcastAddress.ToString());
        Assert.Equal("255.255.240.0", s.SubnetMask.ToString());
        Assert.Equal(4094UL, s.UsableHosts);
        Assert.True(s.Contains(IPAddress.Parse("172.17.60.1")));
        Assert.False(s.Contains(IPAddress.Parse("172.17.64.1")));
    }

    [Theory]
    [InlineData("10.0.0.7", "31", "10.0.0.6", "10.0.0.7", 2UL)]
    [InlineData("10.0.0.7", "32", "10.0.0.7", "10.0.0.7", 1UL)]
    public void Point_to_point_and_host_routes_use_every_address(string ip, string prefix, string first, string last, ulong usable)
    {
        var s = Parse(ip, prefix);
        Assert.Equal(first, s.FirstUsable.ToString());
        Assert.Equal(last, s.LastUsable.ToString());
        Assert.Equal(usable, s.UsableHosts);
    }

    [Fact]
    public void Whole_address_space_does_not_overflow()
    {
        var s = Parse("8.8.8.8", "0");
        Assert.Equal("0.0.0.0", s.NetworkAddress.ToString());
        Assert.Equal("255.255.255.255", s.BroadcastAddress.ToString());
        Assert.Equal(4_294_967_296UL, s.TotalAddresses);
    }

    [Fact]
    public void Dotted_masks_and_cidr_in_the_address_field_are_accepted()
    {
        Assert.Equal(24, Parse("192.168.0.9", "255.255.255.0").Prefix);
        Assert.Equal(8, Parse("10.1.2.3/8", "").Prefix);
    }

    [Theory]
    [InlineData("192.168.1", "24", "address")]
    [InlineData("300.1.1.1", "24", "address")]
    [InlineData("fe80::1", "64", "address")]
    [InlineData("192.168.1.1", "33", "prefix")]
    [InlineData("192.168.1.1", "255.0.255.0", "prefix")] // not contiguous
    [InlineData("192.168.1.1", "abc", "prefix")]
    public void Invalid_input_is_rejected_with_the_offending_field(string address, string prefix, string field)
    {
        Assert.False(Ipv4Subnet.TryParse(address, prefix, out _, out var error));
        Assert.Equal(field, error);
    }

    [Fact]
    public void Small_subnets_enumerate_their_hosts()
    {
        var hosts = Parse("192.168.5.9", "29").Hosts().Select(h => h.ToString()).ToList();
        Assert.Equal(6, hosts.Count);
        Assert.Equal("192.168.5.9", hosts[0]);
        Assert.Equal("192.168.5.14", hosts[^1]);
    }
}

public sealed class NetworkTargetTests
{
    [Theory]
    [InlineData("  example.com  ", "example.com", NetworkTargetKind.HostName)]
    [InlineData("https://Example.COM:8443/status?x=1", "example.com", NetworkTargetKind.HostName)]
    [InlineData("server01.", "server01", NetworkTargetKind.HostName)]
    [InlineData("192.168.1.1", "192.168.1.1", NetworkTargetKind.IPv4)]
    [InlineData("[fe80::1]", "fe80::1", NetworkTargetKind.IPv6)]
    [InlineData("::1", "::1", NetworkTargetKind.IPv6)]
    [InlineData("münchen.de", "xn--mnchen-3ya.de", NetworkTargetKind.HostName)]
    public void Pasted_hosts_are_normalised(string input, string host, NetworkTargetKind kind)
    {
        Assert.True(NetworkTarget.TryParse(input, out var target, out _));
        Assert.Equal(host, target.Host);
        Assert.Equal(kind, target.Kind);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("exa mple.com", "invalid")]
    [InlineData("1.2", "invalid")]           // legacy numeric forms are not accepted as addresses
    [InlineData("-bad-.com", "invalid")]
    [InlineData("a..b", "invalid")]
    public void Invalid_hosts_are_explained(string input, string error)
    {
        Assert.False(NetworkTarget.TryParse(input, out _, out var actual));
        Assert.Equal(error, actual);
    }

    [Fact]
    public void Overlong_labels_are_rejected()
    {
        Assert.False(NetworkTarget.TryParse(new string('a', 64) + ".com", out _, out _));
        Assert.True(NetworkTarget.TryParse(new string('a', 63) + ".com", out _, out _));
    }

    [Fact]
    public void Reverse_lookup_names_follow_the_arpa_conventions()
    {
        Assert.Equal("4.3.2.1.in-addr.arpa", NetworkTarget.ReverseLookupName(IPAddress.Parse("1.2.3.4")));
        Assert.StartsWith("1.0.0.0.", NetworkTarget.ReverseLookupName(IPAddress.Parse("2001:db8::1")));
        Assert.EndsWith(".8.b.d.0.1.0.0.2.ip6.arpa", NetworkTarget.ReverseLookupName(IPAddress.Parse("2001:db8::1")));
    }

    [Theory]
    [InlineData(" 443 ", true)]
    [InlineData("65535", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("-1", false)]
    [InlineData("80a", false)]
    public void Ports_must_be_in_range(string text, bool valid) => Assert.Equal(valid, ToolInput.TryParsePort(text, out _));
}

public sealed class HashAndFormattingTests
{
    [Theory]
    [InlineData("AB CD-ef:01", "abcdef01")]
    [InlineData("SHA256: ABCD", "abcd")]
    [InlineData("sha256:abcd", "abcd")]
    [InlineData("  ", "")]
    public void Published_checksums_are_normalised(string input, string expected) => Assert.Equal(expected, HashText.Normalize(input));

    [Fact]
    public void Comparison_distinguishes_every_case()
    {
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(HashComparison.Match, HashText.Compare(hash, hash.ToUpperInvariant()));
        Assert.Equal(HashComparison.Match, HashText.Compare(hash, "SHA256: " + string.Join(' ', hash.Chunk(8).Select(c => new string(c)))));
        Assert.Equal(HashComparison.Mismatch, HashText.Compare(hash, hash[..^1] + "e"));
        Assert.Equal(HashComparison.DifferentLength, HashText.Compare(hash, "900150983cd24fb0d6963f7d28e17f72"));
        Assert.Equal(HashComparison.NotAHash, HashText.Compare(hash, "not a checksum"));
        Assert.Equal(HashComparison.Empty, HashText.Compare(hash, null));
    }

    [Fact]
    public void Md5_is_flagged_as_not_secure()
    {
        Assert.False(HashText.IsSecure(HashAlgorithmKind.Md5));
        Assert.True(HashText.IsSecure(HashAlgorithmKind.Sha256));
        Assert.Equal(128, HashText.HexLength(HashAlgorithmKind.Sha512));
    }

    [Fact]
    public void Ping_statistics_only_count_answered_requests_for_latency()
    {
        var stats = PingStatistics.From(
        [
            new PingReplyInfo(1, "1.1.1.1", PingOutcome.Success, 10, 57, 32),
            new PingReplyInfo(2, "1.1.1.1", PingOutcome.TimedOut, null, null, 32),
            new PingReplyInfo(3, "1.1.1.1", PingOutcome.Success, 30, 57, 32),
            new PingReplyInfo(4, "1.1.1.1", PingOutcome.Unreachable, null, null, 32),
        ]);
        Assert.Equal(4, stats.Sent);
        Assert.Equal(2, stats.Received);
        Assert.Equal(2, stats.Lost);
        Assert.Equal(50, stats.LossPercent);
        Assert.Equal(10, stats.Minimum);
        Assert.Equal(30, stats.Maximum);
        Assert.Equal(20, stats.Average);
        Assert.Null(PingStatistics.From([]).Average);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10.5 * 1024 * 1024, "10.5 MB")]
    [InlineData(500L * 1024 * 1024 * 1024, "500 GB")]
    public void Byte_sizes_use_binary_units(double bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Link_speeds_and_uptime_are_formatted()
    {
        Assert.Equal("1 Gbps", ByteSize.FormatBits(1_000_000_000, CultureInfo.InvariantCulture));
        Assert.Equal("100 Mbps", ByteSize.FormatBits(100_000_000, CultureInfo.InvariantCulture));
        Assert.Equal("3 d 04:05:06", ByteSize.FormatUptime(new TimeSpan(3, 4, 5, 6)));
        Assert.Equal("00:59:59", ByteSize.FormatUptime(TimeSpan.FromSeconds(3599)));
    }

    [Fact]
    public void Critical_processes_are_recognised()
    {
        Assert.True(ProcessSafety.IsCritical("csrss", 612, 0));
        Assert.True(ProcessSafety.IsCritical("lsass.exe", 700, 0));
        Assert.True(ProcessSafety.IsCritical("anything", 4, 0));
        Assert.False(ProcessSafety.IsCritical("notepad", 1234, 1));
    }

    [Fact]
    public void Event_log_queries_only_ask_for_the_chosen_levels()
    {
        Assert.Equal("*", EventLogService.BuildXPath(new HashSet<EventSeverity>()));
        Assert.Equal("*[System[(Level=1 or Level=2)]]", EventLogService.BuildXPath(new HashSet<EventSeverity> { EventSeverity.Error, EventSeverity.Critical }));
        Assert.Contains("Level=0", EventLogService.BuildXPath(new HashSet<EventSeverity> { EventSeverity.Information }));
    }

    [Fact]
    public void Quick_commands_are_a_fixed_catalogue()
    {
        Assert.Equal(QuickCommands.All.Count, QuickCommands.All.Select(c => c.Key).Distinct().Count());
        Assert.All(QuickCommands.All, c => Assert.Null(c.Arguments));
        Assert.All(QuickCommands.All.Where(c => c.RunAsAdministrator), c => Assert.EndsWith("Admin", c.Key));
        Assert.Null(QuickCommands.Find("rm -rf"));
    }
}

public sealed class ReportTests
{
    [Fact]
    public void Plain_text_aligns_fields_tables_and_multiline_values()
    {
        var section = new ReportSection("Ping: example.com")
            .Add("Sent", "4")
            .Add("Average", "12 ms")
            .Add("DNS servers", "1.1.1.1\n8.8.8.8");
        section.Table = new ReportTable("Hop", "Address").AddRow("1", "192.168.1.1").AddRow("2", null);

        var text = TextReportExporter.Render(section);

        Assert.Contains("Ping: example.com", text);
        Assert.Contains("Sent:        4", text);
        Assert.Contains("DNS servers: 1.1.1.1", text);
        Assert.Contains(new string(' ', 13) + "8.8.8.8", text);
        Assert.Contains("Hop  Address", text);
        Assert.Contains("2    —", text);
    }

    [Fact]
    public async Task Json_export_keeps_arabic_and_german_readable()
    {
        var doc = new ReportDocument { Title = "Sitzung – جلسة", Sections = [new ReportSection("Notiz") { Preformatted = "الشبكة لا تعمل · Größe" }] };
        using var ms = new MemoryStream();
        await new JsonReportExporter().WriteAsync(doc, ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("الشبكة لا تعمل · Größe", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void Sections_round_trip_through_json()
    {
        var section = new ReportSection("DNS") { Table = new ReportTable("Type", "Value").AddRow("MX", "10 mail.example.com") }.Add("Result", "1 record");
        var back = JsonReportExporter.DeserializeSection(JsonReportExporter.Serialize(section));
        Assert.NotNull(back);
        Assert.Equal("DNS", back.Heading);
        Assert.Equal("10 mail.example.com", back.Table!.Rows[0][1]);
        Assert.Null(JsonReportExporter.DeserializeSection("{ broken"));
    }

    [Fact]
    public void Session_report_lists_results_and_notes_in_time_order()
    {
        var t0 = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var session = new DiagnosticSession
        {
            Title = "Printer offline",
            Reference = "TICKET-42",
            ComputerName = "PC1",
            CreatedAtUtc = t0,
            Results = [new DiagnosticResult { Title = "Ping printer", Summary = "0/4 replies", CreatedAtUtc = t0.AddMinutes(2), SectionJson = JsonReportExporter.Serialize(new ReportSection("x").Add("Loss", "100%")) }],
            Notes = [new DiagnosticNote { Text = "Kabel geprüft\nالطابعة مطفأة", CreatedAtUtc = t0.AddMinutes(1) }],
        };
        var labels = new DiagnosticReport.Labels("Session", "Reference", "Computer", "Started", "Closed", "Open", "Note", "Result", "Entries");

        var doc = DiagnosticReport.Build(session, labels, "Arcadia", t0.AddHours(1));
        var text = TextReportExporter.Render(doc);

        Assert.Equal(3, doc.Sections.Count);
        Assert.Contains("Note", doc.Sections[1].Heading);
        Assert.Equal("Kabel geprüft\nالطابعة مطفأة", doc.Sections[1].Preformatted); // exactly as typed
        Assert.Contains("Ping printer", doc.Sections[2].Heading);
        Assert.Contains("Result:", text);
        Assert.Contains("0/4 replies", text);
        Assert.Contains("TICKET-42", text);
    }
}

/// <summary>Diagnostic sessions on the real migrations (in-memory SQLite).</summary>
public sealed class DiagnosticSessionStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DiagnosticSessionStore _store = null!;
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _store = new DiagnosticSessionStore(_factory, new SteppingClock());
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Session_keeps_results_and_notes_exactly()
    {
        var session = await _store.CreateAsync("  Wi-Fi drops  ", "Kunde Müller", "PC1");
        await _store.AddResultAsync(session.Id, new NewDiagnosticResult("ping", "Ping router", "4/4", DiagnosticOutcome.Success, new ReportSection("Ping").Add("Avg", "3 ms")));
        const string arabic = "  الاتصال ينقطع كل ساعة  \n(مع الشاحن)";
        await _store.AddNoteAsync(session.Id, arabic);
        await _store.AddNoteAsync(session.Id, "Treiber aktualisiert — keine Änderung");

        var loaded = await _store.GetAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Wi-Fi drops", loaded.Title);
        Assert.Equal("Kunde Müller", loaded.Reference);
        Assert.Single(loaded.Results);
        Assert.Equal(DiagnosticOutcome.Success, loaded.Results[0].Outcome);
        Assert.Equal("3 ms", JsonReportExporter.DeserializeSection(loaded.Results[0].SectionJson)!.Fields[0].Value);
        Assert.Equal(arabic, loaded.Notes[0].Text); // stored exactly as entered, whitespace included
        Assert.Equal(2, loaded.Notes.Count);
        Assert.True(loaded.UpdatedAtUtc > loaded.CreatedAtUtc);

        var summary = Assert.Single(await _store.ListAsync());
        Assert.Equal(1, summary.ResultCount);
        Assert.Equal(2, summary.NoteCount);
    }

    [Fact]
    public async Task Closing_editing_and_deleting_sessions()
    {
        var session = await _store.CreateAsync("Slow disk", null, "PC1");
        var note = await _store.AddNoteAsync(session.Id, "first");
        await _store.UpdateNoteAsync(note.Id, "second");
        await _store.UpdateDetailsAsync(session.Id, "Slow disk C:", "INC-7");
        await _store.SetClosedAsync(session.Id, true);

        var closed = await _store.GetAsync(session.Id);
        Assert.True(closed!.IsClosed);
        Assert.Equal("Slow disk C:", closed.Title);
        Assert.Equal("second", closed.Notes.Single().Text);

        await _store.SetClosedAsync(session.Id, false);
        Assert.False((await _store.GetAsync(session.Id))!.IsClosed);

        await _store.DeleteAsync(session.Id);
        Assert.Null(await _store.GetAsync(session.Id));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.DiagnosticNotes.CountAsync()); // cascade
    }

    [Fact]
    public async Task Empty_or_oversized_notes_are_refused()
    {
        var session = await _store.CreateAsync("x", null, "PC1");
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AddNoteAsync(session.Id, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AddNoteAsync(session.Id, new string('a', DiagnosticSessionStore.MaxNoteLength + 1)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.AddNoteAsync(999, "note"));
    }
}

/// <summary>The IT Tools migration must leave an existing library untouched.</summary>
public sealed class ItToolsMigrationTests
{
    [Fact]
    public async Task Upgrading_an_existing_database_keeps_games_and_adds_session_tables()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-mig-{Guid.NewGuid():N}")).FullName; // own folder: the upgrade writes Backups\ next to the file
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                // The database as it was before IT Tools existed…
                await db.GetService<IMigrator>().MigrateAsync("Phase4MetadataLengths");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Games (Title, Platform, Tags, Status, Priority, CompletionPercent, PlaytimeHours, EstimatedHours, AchievementsUnlocked, AchievementsTotal, AddedAtUtc, IsFeatured, ArtworkTheme, ArtworkSeed, IsSample) " +
                    "VALUES ('Elden Ring', 'PC', 'RPG', 'Playing', 'High', 45, 61.5, 100, 12, 42, '2026-01-01 00:00:00', 1, 'castle-dusk', 7, 0)");
            }

            // …then the normal startup path runs the new migration.
            var factory = new PooledFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);

            await using var after = new ArcadiaDbContext(options);
            var game = await after.Games.SingleAsync();
            Assert.Equal("Elden Ring", game.Title);
            Assert.Equal(61.5, game.PlaytimeHours);
            Assert.True(game.IsFeatured);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_ItToolsDiagnostics", StringComparison.Ordinal));
            Assert.Equal(0, await after.DiagnosticSessions.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class PooledFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}

/// <summary>Real Windows services, local only (loopback, temp files).</summary>
public sealed class ItToolsServiceTests
{
    [Fact]
    public async Task Hashes_match_known_test_vectors_and_report_progress()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "abc", new UTF8Encoding(false));
            var service = new HashService();
            double last = 0;
            var sha = await service.ComputeAsync(file, HashAlgorithmKind.Sha256, new Progress<double>(p => last = p));
            var md5 = await service.ComputeAsync(file, HashAlgorithmKind.Md5, null);
            var sha512 = await service.ComputeAsync(file, HashAlgorithmKind.Sha512, null);

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sha.Hash);
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", md5.Hash);
            Assert.StartsWith("ddaf35a193617aba", sha512.Hash);
            Assert.Equal(3, sha.SizeBytes);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Missing_files_are_explained()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new HashService().ComputeAsync(@"C:\definitely\not\here.bin", HashAlgorithmKind.Sha256, null));
        Assert.Equal(ToolFailure.NotFound, ex.Failure);
    }

    [Fact]
    public async Task Large_file_finder_scans_only_the_chosen_folder_and_sorts_by_size()
    {
        var root = Directory.CreateTempSubdirectory("arcadia-large-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            await File.WriteAllBytesAsync(Path.Combine(root, "small.bin"), new byte[10_000]);
            await File.WriteAllBytesAsync(Path.Combine(root, "big.bin"), new byte[3_000_000]);
            await File.WriteAllBytesAsync(Path.Combine(root, "sub", "bigger.bin"), new byte[5_000_000]);

            var found = await new LargeFileFinder(NullLogger<LargeFileFinder>.Instance).FindAsync(root, 1_000_000, null);

            Assert.Equal(["bigger.bin", "big.bin"], found.Select(f => f.Name));
            Assert.True(File.Exists(Path.Combine(root, "small.bin"))); // nothing is ever deleted
            await Assert.ThrowsAsync<ToolException>(() => new LargeFileFinder(NullLogger<LargeFileFinder>.Instance).FindAsync(Path.Combine(root, "nope"), 1, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Port_test_tells_open_from_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            NetworkTarget.TryParse("127.0.0.1", out var target, out _);
            var tester = new PortTestService();
            var reachable = await tester.TestAsync(target, open, 2000);
            Assert.Equal(PortTestOutcome.Reachable, reachable.Outcome);
            Assert.NotNull(reachable.LatencyMs);

            listener.Stop();
            // Windows retries a refused SYN for about two seconds before reporting it.
            var refused = await tester.TestAsync(target, open, 6000);
            Assert.Equal(PortTestOutcome.Refused, refused.Outcome);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ping_to_loopback_answers()
    {
        NetworkTarget.TryParse("127.0.0.1", out var target, out _);
        var replies = new List<PingReplyInfo>();
        await foreach (var r in new PingService().PingAsync(target, 2, 1000)) replies.Add(r);
        Assert.Equal(2, replies.Count);
        Assert.All(replies, r => Assert.Equal(PingOutcome.Success, r.Outcome));
    }

    [Fact]
    public async Task Unknown_host_names_are_a_dns_failure()
    {
        NetworkTarget.TryParse("no-such-host.invalid", out var target, out _);
        var ex = await Assert.ThrowsAsync<ToolException>(() => new PingService().ResolveAsync(target));
        Assert.Equal(ToolFailure.DnsFailure, ex.Failure);
    }

    [Fact]
    public void Quick_status_and_drives_describe_this_computer()
    {
        var quick = new SystemInformationService(NullLogger<SystemInformationService>.Instance).GetQuickStatus();
        Assert.Equal(Environment.MachineName, quick.ComputerName);
        Assert.True(quick.MemoryTotal > 0 && quick.MemoryUsed > 0 && quick.MemoryUsed <= quick.MemoryTotal);
        Assert.StartsWith("Windows", quick.WindowsName);

        var drives = new StorageService(NullLogger<StorageService>.Instance).GetDrives();
        Assert.Contains(drives, d => d.IsSystemDrive && d.TotalBytes > 0);
    }

    [Fact]
    public void Performance_samples_are_within_range()
    {
        using var monitor = new PerformanceMonitor(NullLogger<PerformanceMonitor>.Instance);
        monitor.Sample();
        Thread.Sleep(200);
        var s = monitor.Sample();
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.True(s.MemoryTotal > 0);
        Assert.True(s.ProcessCount > 0);
    }
}
