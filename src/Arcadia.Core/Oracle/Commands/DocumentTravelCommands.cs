using System.Globalization;
using Arcadia.Core.Documents;
using Arcadia.Core.Organizer;
using Arcadia.Core.Travel;

namespace Arcadia.Core.Oracle.Commands;

// ─────────────────────────── documents ───────────────────────────

public static class DocumentCommands
{
    public static OracleItem Item(OracleCall c, DocumentRow d, string? group = null)
    {
        var r = new EntityRef(EntityKind.Document, d.Id);
        var item = new OracleItem
        {
            Title = d.DisplayName,
            Subtitle = d.IsMissing ? c.T("Orc_FileMissing") : d.Path,
            Value = d.Extension.TrimStart('.').ToUpperInvariant(),
            Icon = d.IsFolder ? "Projects" : "Documents",
            Accent = d.IsMissing ? "Red" : d.IsFavorite ? "Amber" : "Blue",
            Kind = OracleEntity.Document, Ref = r, Group = group, IsMuted = d.IsMissing,
        };
        item.Actions.Add(new OracleAction { Label = c.T("Orc_OpenFile"), Icon = "ArrowRight", Kind = OracleActionKind.OpenFile, Text = d.Path, Target = r, IsPrimary = true, IsEnabled = !d.IsMissing });
        item.Actions.Add(new OracleAction { Label = c.T("Orc_OpenFolder"), Icon = "Projects", Kind = OracleActionKind.OpenFolder, Text = d.Path, IsEnabled = !d.IsMissing });
        item.Actions.Add(new OracleAction { Label = c.T("Orc_CopyPath"), Icon = "Copy", Kind = OracleActionKind.Copy, Text = d.Path });
        return item;
    }
}

/// <summary>"show recent documents", "favorite documents", "missing files", "documents about invoice", "show documents for Home Server", "show its documents".</summary>
public sealed class DocumentListCommand : OracleCommandBase
{
    public override string Id => "documents.list";
    public override string Module => "documents";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Document];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["show recent documents", "favorite documents", "missing files", "documents about invoice", "show documents for Home Server"];
    public override string[] ExamplesDe => ["zeige letzte Dokumente", "Dokumente für Home Server"];
    public override string[] ExamplesAr => ["اعرض المستندات الاخيرة"];

    private static readonly EntityKind[] Scopes = [EntityKind.Project, EntityKind.Trip];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Document) && IsQuery(p) && !p.AllRelated ? (p.Subject.Length == 0 ? 0.85 : 0.6) : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (await UnresolvedTargetAsync(c, ct, x => $"show documents for {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\"", Scopes).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, Scopes).ConfigureAwait(false);
        var text = p.Topic ?? (p.Subject.Length > 0 ? p.Subject : null);
        IEnumerable<DocumentRow> rows;
        if (scope is { Ref.Kind: EntityKind.Project } sp) rows = await c.S.Documents.ListForProjectAsync(sp.Ref.Id, ct).ConfigureAwait(false);
        else if (scope is { Ref.Kind: EntityKind.Trip } st) rows = (await c.S.Travel.ListTripDocumentsAsync(st.Ref.Id, ct).ConfigureAwait(false)).Select(d => d.Document);
        else
        {
            var view = p.HasFilter("missing") ? DocumentView.Missing : p.HasFilter("favorite") ? DocumentView.Favorites : p.HasFilter("recent") ? DocumentView.Recent : DocumentView.All;
            var sort = view == DocumentView.Recent ? DocumentSort.Opened : DocumentSort.Name;
            rows = (await c.S.Documents.QueryAsync(new DocumentQuery(view, Text: text, Sort: sort, Descending: view == DocumentView.Recent, Take: 200), c.Now.ToUniversalTime(), ct).ConfigureAwait(false)).Rows;
            text = null;
        }
        if (text != null) rows = rows.Where(d => OracleFuzzy.Similarity(text, d.DisplayName) >= OracleFuzzy.Plausible || d.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (scope != null && p.HasFilter("missing")) rows = rows.Where(d => d.IsMissing);
        if (scope != null && p.HasFilter("favorite")) rows = rows.Where(d => d.IsFavorite);
        var list = rows.ToList();
        if (Counting(p)) return Count(c, "Orc_CountDocuments", list.Count, NavigateAction(c, "documents"));
        var items = list.Select(d => DocumentCommands.Item(c, d)).ToList();
        var headline = scope is { } s ? c.T("Orc_ListDocumentsFor", items.Count, s.Name) : c.T("Orc_ListDocuments", items.Count);
        return ListResult(c, headline, items, "Orc_NoDocuments", scope is { } sc ? OpenAction(c, sc.Ref, "Documents", false) : NavigateAction(c, "documents", label: c.T("Orc_OpenDocuments")), scope?.Ref, scope?.Name);
    }
}

/// <summary>"open document invoice.pdf", "open folder of router manual", "copy path of router manual".</summary>
public sealed class DocumentOpenCommand : OracleCommandBase
{
    public override string Id => "documents.open";
    public override string Module => "documents";
    public override OracleIntent Intent => OracleIntent.Open;
    public override OracleEntity[] Entities => [OracleEntity.Document];
    public override bool UsesFreeText => true;
    public override string[] Examples => ["open document router manual", "open folder of router manual", "copy path of router manual"];
    public override string[] ExamplesDe => ["öffne Dokument Handbuch"];
    public override string[] ExamplesAr => ["افتح ملف دليل الراوتر"];

    private static bool FolderWord(OracleParse p) => p.Input.Tokens.Any(t => t.Norm is "folder" or "ordner" or "مجلد" or "location");

    public override double Match(OracleParse p, OracleContext context)
    {
        if (NameOf(p).Length == 0 && p.Segments.Count == 0) return 0;
        if (p.Verb == "copy") return 0.85;
        if (p.Verb == "open" && (Mentions(p, OracleEntity.Document) || FolderWord(p))) return 0.9;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var raw = p.Payload ?? string.Join(' ', p.Segments);
        var name = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => OracleText.Fold(w) is not ("folder" or "ordner" or "مجلد" or "location" or "path" or "pfad")));
        var r = await c.ResolveAsync(name, ct, EntityKind.Document).ConfigureAwait(false);
        if (r.Single is not { } s) return r.IsNone ? c.NotFound(name) : c.Choose(r, x => $"{(p.Verb == "copy" ? "copy path of" : FolderWord(p) ? "open folder of" : "open document")} \"{x.Name}\"");
        var doc = await c.S.Documents.GetAsync(s.Ref.Id, ct).ConfigureAwait(false);
        if (doc == null) return c.NotFound(name);
        var kind = p.Verb == "copy" ? OracleActionKind.Copy : FolderWord(p) ? OracleActionKind.OpenFolder : OracleActionKind.OpenFile;
        var headline = kind switch { OracleActionKind.Copy => c.T("Orc_PathCopied", s.Name), OracleActionKind.OpenFolder => c.T("Orc_OpeningFolder", s.Name), _ => c.T("Orc_Opening", s.Name) };
        return new OracleResult
        {
            Kind = OracleResultKind.Navigated,
            Headline = headline,
            Detail = doc.Path,
            AutoAction = new OracleAction { Label = c.T("Orc_Open"), Kind = kind, Text = doc.Path, Target = s.Ref },
            Focus = s.Ref,
            FocusName = s.Name,
        };
    }
}

/// <summary>"link router manual to Home Server", "attach invoice.pdf to Italy trip".</summary>
public sealed class DocumentLinkCommand : OracleCommandBase
{
    public override string Id => "documents.link";
    public override string Module => "documents";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Document];
    public override OracleSafety Safety => OracleSafety.Update;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["link router manual to Home Server", "attach invoice.pdf to Italy trip"];
    public override string[] ExamplesDe => ["verknüpfe Handbuch mit Home Server"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb == "link" && (p.Payload ?? p.Subject).Length > 0 && p.Target is { Length: > 0 } ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var docName = (p.Payload ?? p.Subject).Trim();
        var doc = await c.ResolveAsync(docName, ct, EntityKind.Document).ConfigureAwait(false);
        if (doc.Single is not { } d) return doc.IsNone ? c.NotFound(docName) : c.Choose(doc, x => $"link \"{x.Name}\" to {c.Quote(p.Target!)}");
        var target = await c.ResolveAsync(p.Target, ct, EntityKind.Project, EntityKind.Trip).ConfigureAwait(false);
        if (target.Single is not { } t) return target.IsNone ? c.NotFound(p.Target!) : c.Choose(target, x => $"link \"{d.Name}\" to {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\"");
        return await c.ChangeAsync(new OraclePreview
        {
            Title = c.T("Orc_LinkTitle", d.Name, t.Name),
            Icon = "Link",
            Fields = [(c.T("Orc_Field_Document"), d.Name), (c.T("Orc_Field_LinkedTo"), t.Name)],
            Confirm = c.ConfirmAction(async x =>
            {
                if (t.Ref.Kind == EntityKind.Project) await c.S.Documents.AssociateAsync(d.Ref.Id, t.Ref.Id, x).ConfigureAwait(false);
                else await c.S.Travel.AssociateDocumentAsync(t.Ref.Id, d.Ref.Id, x).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_Linked", d.Name, t.Name), t.Ref, t.Name);
            }),
        }, false, ct).ConfigureAwait(false);
    }
}

// ─────────────────────────── travel ───────────────────────────

/// <summary>"show upcoming trips", "trips this year", "how many trips have I completed".</summary>
public sealed class TripListCommand : OracleCommandBase
{
    public override string Id => "travel.trips";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Trip];
    public override bool CanRefine => true;
    public override string[] Examples => ["show upcoming trips", "trips this year", "how many trips have I completed"];
    public override string[] ExamplesDe => ["zeige kommende Reisen"];
    public override string[] ExamplesAr => ["اعرض الرحلات القادمة"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Trip) && p.Entities.Count == 1 && IsQuery(p) && p.Question != "when" && p.Subject.Length == 0 && p.Metric == null ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        IEnumerable<TripSummary> trips = await c.S.Travel.ListTripsAsync(ct).ConfigureAwait(false);
        var today = c.Today;
        if (p.HasFilter("upcoming")) trips = trips.Where(t => t.Status is not (TripStatus.Completed or TripStatus.Cancelled) && (t.StartDate ?? DateOnly.MaxValue) >= today).OrderBy(t => t.StartDate ?? DateOnly.MaxValue);
        if (p.HasFilter("completed") || p.HasFilter("visited")) trips = trips.Where(t => t.Status == TripStatus.Completed);
        if (p.HasFilter("planning")) trips = trips.Where(t => t.Status is TripStatus.Planning or TripStatus.Idea);
        if (p.HasFilter("active") || p.HasFilter("playing")) trips = trips.Where(t => t.Status == TripStatus.Active);
        if (p.Period is { } per) trips = trips.Where(t => t.StartDate is { } s && s <= per.To && (t.EndDate ?? s) >= per.From);
        var list = trips.ToList();
        if (Counting(p)) return Count(c, "Orc_CountTrips", list.Count, NavigateAction(c, "travel", "trips"));
        var items = list.Select(t => Item(c, t)).ToList();
        return ListResult(c, c.T("Orc_ListTrips", items.Count), items, "Orc_NoTrips", NavigateAction(c, "travel", "trips", c.T("Orc_OpenTravel")));
    }

    public static OracleItem Item(OracleCall c, TripSummary t)
    {
        var r = new EntityRef(EntityKind.Trip, t.Id);
        var when = t.StartDate is { } a ? c.Env.Date(a) + (t.EndDate is { } b && b != a ? " – " + c.Env.Date(b) : "") : c.T("Orc_NoDates");
        return new OracleItem
        {
            Title = t.Name, Subtitle = c.T("Orc_TripStatus_" + t.Status) + " · " + c.T("Orc_Places", t.PlaceCount), Value = when,
            Icon = "Suitcase", Accent = t.Accent, Kind = OracleEntity.Trip, Ref = r,
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true },
                       new OracleAction { Label = c.T("Orc_Documents"), Icon = "Documents", Kind = OracleActionKind.Open, Target = r, Tab = "Documents" }],
        };
    }
}

/// <summary>"plan trip to Japan next month", "create trip Italy 2027".</summary>
public sealed class TripCreateCommand : OracleCommandBase
{
    public override string Id => "travel.createtrip";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Trip];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["plan trip to Japan next month", "create trip Italy"];
    public override string[] ExamplesDe => ["plane Reise nach Japan nächsten Monat"];
    public override string[] ExamplesAr => ["أنشئ رحلة Japan"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb == "create" && Is(p, OracleEntity.Trip) && NameOf(p).Length > 0 && !(p.Subject.Length > 0 && p.Target != null) ? 0.9 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var name = (p.Payload ?? (p.Subject.Length > 0 ? p.Subject : p.Target) ?? "").Trim();
        var (start, end) = p.Period is { } per ? ((DateOnly?)per.From, (DateOnly?)per.To) : (null, null);
        var preview = new OraclePreview
        {
            Title = c.T("Orc_CreateTripTitle"),
            Icon = "Suitcase",
            Fields = [(c.T("Orc_Field_Name"), name), (c.T("Orc_Field_Status"), c.T("Orc_TripStatus_Planning"))],
            Confirm = c.ConfirmAction(async t =>
            {
                var trip = await c.S.Travel.CreateTripAsync(new TripDraft(name, TripStatus.Planning, start, end), null, t).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_Created", name), new EntityRef(EntityKind.Trip, trip.Id), name);
            }),
        };
        if (start is { } s) preview.Fields.Add((c.T("Orc_Field_When"), c.Env.Date(s) + (end is { } e && e != s ? " – " + c.Env.Date(e) : "")));
        return c.ChangeAsync(preview, true, ct);
    }
}

/// <summary>"places I want to visit", "visited places", "places in Italy trip", "how many countries have I visited".</summary>
public sealed class PlaceListCommand : OracleCommandBase
{
    public override string Id => "travel.places";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Place];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["places I want to visit", "show visited places", "how many countries have I visited", "places for Italy trip"];
    public override string[] ExamplesDe => ["welche Länder habe ich besucht"];
    public override string[] ExamplesAr => ["كم عدد الدول التي زرتها"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Place) && IsQuery(p) && p.Metric == null && !p.AllRelated ? (p.Subject.Length == 0 ? 0.85 : 0.6) : 0;

    private static string CountryName(string code)
    {
        try { return new RegionInfo(code).DisplayName; }
        catch (ArgumentException) { return code; }
    }

    private static bool Countries(OracleParse p) => p.Input.Tokens.Any(t => t.Norm is "countries" or "country" or "lander" or "land" or "دول" or "الدول");

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (Countries(p) && (Counting(p) || p.HasFilter("visited")))
        {
            var stats = await c.S.Travel.GetStatsAsync(c.Today, ct).ConfigureAwait(false);
            if (Counting(p)) return Count(c, "Orc_CountCountries", stats.CountriesVisited, NavigateAction(c, "travel", "countries", c.T("Orc_OpenTravel")));
            var progress = await c.S.Travel.GetCountryProgressAsync(ct).ConfigureAwait(false);
            var visited = progress.Where(x => x.Visited || x.MarkedVisited).Select(x => new OracleItem { Title = CountryName(x.Code), Subtitle = x.Code, Icon = "Compass", Accent = "Green" }).ToList();
            return ListResult(c, c.T("Orc_CountCountries", visited.Count), visited, "Orc_NoPlaces", NavigateAction(c, "travel", "countries", c.T("Orc_OpenTravel")));
        }
        if (await UnresolvedTargetAsync(c, ct, x => $"show places for trip \"{x.Name}\"", EntityKind.Trip).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, EntityKind.Trip).ConfigureAwait(false);
        var status = p.HasFilter("visited") ? PlaceStatus.Visited : p.HasFilter("wanted") ? PlaceStatus.WantToVisit : p.HasFilter("planning") ? PlaceStatus.Planned : (PlaceStatus?)null;
        var text = p.Topic ?? (p.Subject.Length > 0 ? p.Subject : null);
        var rows = await c.S.Travel.ListPlacesAsync(new PlaceQuery(status, FavoritesOnly: p.HasFilter("favorite"), TripId: scope?.Ref.Id, Text: text), ct).ConfigureAwait(false);
        if (Counting(p)) return Count(c, "Orc_CountPlaces", rows.Count, NavigateAction(c, "travel", "places"));
        var items = rows.Select(x => new OracleItem
        {
            Title = x.Name,
            Subtitle = string.Join(" · ", new[] { x.City, x.Region, x.CountryCode }.Where(s => !string.IsNullOrEmpty(s))),
            Value = c.T("Orc_PlaceStatus_" + x.Status),
            Icon = "Pin", Accent = x.Status == PlaceStatus.Visited ? "Green" : x.IsFavorite ? "Amber" : "Cyan",
            Kind = OracleEntity.Place, Ref = new EntityRef(EntityKind.Place, x.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Place, x.Id), IsPrimary = true }],
        }).ToList();
        return ListResult(c, c.T("Orc_ListPlaces", items.Count), items, "Orc_NoPlaces", NavigateAction(c, "travel", "places", c.T("Orc_OpenTravel")), scope?.Ref, scope?.Name);
    }
}

/// <summary>"mark Rome as visited", "I visited Lisbon", "want to visit Kyoto" (existing places).</summary>
public sealed class PlaceUpdateCommand : OracleCommandBase
{
    public override string Id => "travel.placestatus";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Place];
    public override OracleSafety Safety => OracleSafety.Update;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["mark Rome as visited", "mark Kyoto as want to visit"];
    public override string[] ExamplesDe => ["markiere Rom als besucht"];
    public override string[] ExamplesAr => ["علم روما زرتها"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (NameOf(p).Length == 0 || !(p.HasFilter("visited") || p.HasFilter("wanted"))) return 0;
        if (p.Verb is "set" or "complete" && p.Entity is OracleEntity.None or OracleEntity.Place) return 0.8;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var name = NameOf(p);
        var r = await c.ResolveAsync(name, ct, EntityKind.Place).ConfigureAwait(false);
        var visited = p.HasFilter("visited");
        if (r.Single is not { } s) return r.IsNone ? c.NotFound(name) : c.Choose(r, x => $"mark place \"{x.Name}\" as {(visited ? "visited" : "want to visit")}");
        var status = visited ? PlaceStatus.Visited : PlaceStatus.WantToVisit;
        return await c.ChangeAsync(new OraclePreview
        {
            Title = c.T("Orc_SetStatusTitle", s.Name, c.T("Orc_PlaceStatus_" + status)),
            Icon = "Pin",
            Confirm = c.ConfirmAction(async t =>
            {
                if (visited) await c.S.Travel.AddVisitAsync(s.Ref.Id, p.Period?.From ?? c.Today, null, null, t).ConfigureAwait(false);
                await c.S.Travel.SetPlaceStatusAsync(s.Ref.Id, status, t).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_StatusSet", s.Name, c.T("Orc_PlaceStatus_" + status)), s.Ref, s.Name);
            }),
        }, r.IsExact, ct).ConfigureAwait(false);
    }
}

/// <summary>"add Rome to Italy trip", "add Northern Lights to my bucket list".</summary>
public sealed class TravelAddCommand : OracleCommandBase
{
    public override string Id => "travel.add";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Place, OracleEntity.Bucket];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["add Rome to Italy trip", "add Northern Lights to bucket list"];
    public override string[] ExamplesDe => ["füge Rom zur Reise Italien hinzu", "füge Nordlichter zur Wunschliste hinzu"];
    public override string[] ExamplesAr => ["أضف Northern Lights إلى قائمة الاحلام"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Verb is not ("create" or "link") || (p.Payload ?? p.Subject).Length == 0) return 0;
        if (Mentions(p, OracleEntity.Bucket)) return 0.9;
        if (p.Target is { Length: > 0 } && (p.TargetKind == OracleEntity.Trip || Mentions(p, OracleEntity.Trip)) && p.Entity is OracleEntity.None or OracleEntity.Place or OracleEntity.Trip) return 0.85;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var title = (p.Payload ?? p.Subject).Trim();
        if (Mentions(p, OracleEntity.Bucket))
            return await c.ChangeAsync(new OraclePreview
            {
                Title = c.T("Orc_CreateBucketTitle"),
                Icon = "Star",
                Fields = [(c.T("Orc_Field_Title"), title)],
                Confirm = c.ConfirmAction(async t =>
                {
                    await c.S.Travel.CreateBucketAsync(new BucketDraft(title, TargetYear: p.Period?.From.Year), t).ConfigureAwait(false);
                    return new OracleResult { Kind = OracleResultKind.Done, Headline = c.T("Orc_Created", title), Actions = [NavigateAction(c, "travel", "bucket", c.T("Orc_OpenBucket"))] };
                }),
            }, true, ct).ConfigureAwait(false);

        var trip = await c.ResolveAsync(p.Target, ct, EntityKind.Trip).ConfigureAwait(false);
        if (trip.Single is not { } tr) return trip.IsNone ? c.NotFound(p.Target!) : c.Choose(trip, x => $"add {c.Quote(title)} to trip \"{x.Name}\"");
        var place = await c.ResolveAsync(title, ct, EntityKind.Place).ConfigureAwait(false);
        if (place.Single is not { } pl)
            return place.IsNone
                ? new OracleResult { Kind = OracleResultKind.Message, Headline = c.T("Orc_PlaceUnknown", title), Actions = [OpenAction(c, tr.Ref, "Places")], Confidence = OracleConfidence.Medium }
                : c.Choose(place, x => $"add place \"{x.Name}\" to trip \"{tr.Name}\"");
        return await c.ChangeAsync(new OraclePreview
        {
            Title = c.T("Orc_AddPlaceTitle", pl.Name, tr.Name),
            Icon = "Pin",
            Fields = [(c.T("Orc_Field_Place"), pl.Name), (c.T("Orc_Field_Trip"), tr.Name)],
            Confirm = c.ConfirmAction(async t =>
            {
                await c.S.Travel.AddPlaceToTripAsync(tr.Ref.Id, pl.Ref.Id, t).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_Linked", pl.Name, tr.Name), tr.Ref, tr.Name);
            }),
        }, true, ct).ConfigureAwait(false);
    }
}

/// <summary>"add journal entry to Italy trip: amazing pasta in Rome", "new journal: …" (on a trip page).</summary>
public sealed class TravelJournalCommand : OracleCommandBase
{
    public override string Id => "travel.journal";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Journal];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["add journal entry to Italy trip: amazing pasta in Rome", "add journal entry to Cyberpunk: finished act 1"];
    public override string[] ExamplesDe => ["neuer Tagebucheintrag für Italien: tolle Pasta"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb == "create" && Is(p, OracleEntity.Journal) ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var text = (p.Payload ?? p.Subject).Trim();
        (EntityRef Ref, string Name)? trip = null;
        if (p.Target is { Length: > 0 } target)
        {
            EntityKind[] kinds = p.TargetKind == OracleEntity.Trip || Mentions(p, OracleEntity.Trip) ? [EntityKind.Trip] : p.TargetKind == OracleEntity.Game ? [EntityKind.Game] : [EntityKind.Trip, EntityKind.Game];
            var r = await c.ResolveAsync(target, ct, kinds).ConfigureAwait(false);
            if (r.Single is not { } s) return r.IsNone ? c.NotFound(target) : c.Choose(r, x => $"add journal entry to {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\": {text}");
            trip = (s.Ref, s.Name);
        }
        else if (c.Context.Current is { Kind: EntityKind.Trip or EntityKind.Game } page) trip = (page, c.Context.CurrentName ?? "");
        if (trip == null || text.Length == 0) return new OracleResult { Kind = OracleResultKind.Clarify, Headline = c.T("Orc_JournalNeeds"), Suggestions = [Examples[0]] };
        if (trip.Value.Ref.Kind == EntityKind.Game)
        {
            var game = trip.Value;
            return await c.ChangeAsync(new OraclePreview
            {
                Title = c.T("Orc_CreateJournalTitle"),
                Icon = "Journal",
                Fields = [(c.T("Orc_Field_Text"), text), (c.T("Orc_Field_LinkedTo"), game.Name)],
                Confirm = c.ConfirmAction(async t =>
                {
                    await c.S.Games.AddJournalEntryAsync(game.Ref.Id, text, t).ConfigureAwait(false);
                    return new OracleResult
                    {
                        Kind = OracleResultKind.Done, Headline = c.T("Orc_JournalAdded", game.Name), Focus = game.Ref, FocusName = game.Name,
                        Actions = [new OracleAction { Label = c.T("Orc_Journal"), Icon = "Journal", Kind = OracleActionKind.Open, Target = game.Ref, Tab = "Journal", IsPrimary = true }],
                    };
                }),
            }, true, ct).ConfigureAwait(false);
        }
        var title = text.Length > 50 ? text[..50].TrimEnd() + "…" : text;
        return await c.ChangeAsync(new OraclePreview
        {
            Title = c.T("Orc_CreateJournalTitle"),
            Icon = "Journal",
            Fields = [(c.T("Orc_Field_Text"), text), (c.T("Orc_Field_Trip"), trip.Value.Name), (c.T("Orc_Field_When"), c.Env.Date(p.Period?.From ?? c.Today))],
            Confirm = c.ConfirmAction(async t =>
            {
                var entry = await c.S.Travel.CreateJournalAsync(new JournalDraft(title, p.Period?.From ?? c.Today, text, trip.Value.Ref.Id), t).ConfigureAwait(false);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_JournalAdded", trip.Value.Name),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Journal, entry.Id), IsPrimary = true }],
                };
            }),
        }, true, ct).ConfigureAwait(false);
    }
}

/// <summary>"travel stats", "Reisestatistik".</summary>
public sealed class TravelStatsCommand : OracleCommandBase
{
    public override string Id => "travel.stats";
    public override string Module => "travel";
    public override OracleIntent Intent => OracleIntent.Calculate;
    public override string[] Examples => ["travel stats"];
    public override string[] ExamplesDe => ["Reise Statistik"];
    public override string[] ExamplesAr => ["احصائيات السفر"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Metric == "stats" && (p.Module == "travel" || Mentions(p, OracleEntity.Trip) || Mentions(p, OracleEntity.Place) || context.Module == "travel") ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var s = await c.S.Travel.GetStatsAsync(c.Today, ct).ConfigureAwait(false);
        string N(int n) => n.ToString(CultureInfo.CurrentCulture);
        return new OracleResult
        {
            Kind = OracleResultKind.Metric,
            Headline = c.T("Orc_TravelStatsHeadline", s.CountriesVisited, s.TripsCompleted),
            Metrics =
            [
                new(c.T("Orc_Stat_Countries"), N(s.CountriesVisited), "Cyan"),
                new(c.T("Orc_Stat_Continents"), N(s.ContinentsVisited)),
                new(c.T("Orc_Stat_Places"), N(s.PlacesVisited)),
                new(c.T("Orc_Stat_Upcoming"), N(s.TripsUpcoming), "Amber"),
                new(c.T("Orc_Stat_Days"), N(s.DaysTraveling)),
                new(c.T("Orc_Stat_Bucket"), $"{s.BucketCompleted}/{s.BucketTotal}"),
            ],
            Actions = [NavigateAction(c, "travel", label: c.T("Orc_OpenTravel"))],
        };
    }
}
