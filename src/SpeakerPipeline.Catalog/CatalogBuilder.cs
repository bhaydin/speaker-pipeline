using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpeakerPipeline.Core;
using YamlDotNet.RepresentationModel;

namespace SpeakerPipeline.Catalog;

public static class CatalogBuilder
{
    public static string ContentHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string? AbstractSection(string text)
    {
        var match = Regex.Match(text, @"(?ims)^#{1,4}\s+Abstract\s*\r?\n(?<body>.*?)(?=^#{1,4}\s|\z)");
        return match.Success && match.Groups["body"].Value.Trim().Length >= 40 ? match.Groups["body"].Value.Trim() : null;
    }

    internal static IReadOnlyList<(RepositoryFile File, bool NeedsExtraction)> ContentSources(RepositorySnapshot repository)
    {
        var files = repository.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var result = new List<(RepositoryFile File, bool NeedsExtraction)>();
        foreach (var file in repository.Files.Where(f => Regex.IsMatch(f.Path, @"^talks/[^/]+/talk\.yaml$")).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var metadata = Metadata.Parse(file.Text!);
            var source = metadata.Text("content", "source") ?? "content/talk.md";
            if (source.Contains("..", StringComparison.Ordinal) || source.StartsWith('/')) throw new FormatException("Invalid content path.");
            // Only already-fetched, allowlisted documents are eligible; source declarations never cause arbitrary reads.
            if (files.TryGetValue(file.Path[..file.Path.LastIndexOf('/')] + "/" + source, out var content) && content.Text is { Length: >= 300 } text)
                result.Add((content, metadata.Text("description") is null && AbstractSection(text) is null));
        }
        return result;
    }

    public static CatalogSnapshot Build(RepositorySnapshot privateRepo, RepositorySnapshot publicRepo,
        CatalogSnapshot previous, IReadOnlyDictionary<string, CatalogExtraction> extractions, DateTimeOffset now)
    {
        var talks = new List<CatalogTalk>();
        var versions = previous.Versions.ToDictionary(v => v.VersionId, StringComparer.Ordinal);
        var deliveries = new List<CatalogDelivery>();
        var issues = new List<CatalogIssue>();
        var files = privateRepo.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seenTalkIds = new HashSet<string>(StringComparer.Ordinal);
        var explicitDescriptions = new HashSet<string>(StringComparer.Ordinal);
        var extractedLevels = new Dictionary<string, string?>(StringComparer.Ordinal);

        CatalogSource Source(RepositoryFile f) => new(privateRepo.Repository, f.Path, privateRepo.CommitSha, f.BlobSha);
        CatalogTalkVersion Version(string id, string title, string summary, DateTimeOffset? date, RepositoryFile f, bool extracted = false, bool substantive = false)
        {
            var key = ContentHash(id + "\n" + title + "\n" + summary)[..32];
            var version = new CatalogTalkVersion { TalkId = id, VersionId = key, Title = title, Abstract = summary, EffectiveDate = date, Source = Source(f), Extracted = extracted, SourceKind = substantive ? "abstract" : "metadata" };
            // Same substantive content is the same version: metadata-only date bumps cannot make it newer.
            if (!versions.ContainsKey(key)) versions[key] = version;
            return versions[key];
        }

        foreach (var f in privateRepo.Files.Where(f => Regex.IsMatch(f.Path, @"^talks/[^/]+/talk\.yaml$")))
        {
            var pathId = f.Path.Split('/')[1];
            seenTalkIds.Add(pathId);
            try
            {
                var m = Metadata.Parse(f.Text!);
                var id = m.Text("id");
                var title = m.Text("title");
                if (id != pathId || string.IsNullOrWhiteSpace(title)) throw new FormatException("Talk identity/title is invalid.");
                var source = m.Text("content", "source") ?? "content/talk.md";
                if (source.Contains("..", StringComparison.Ordinal) || source.StartsWith('/')) throw new FormatException("Content source escapes the talk directory.");
                files.TryGetValue($"talks/{id}/{source}", out var markdown);
                var summary = m.Text("description") ?? "";
                if (summary.Length > 0) explicitDescriptions.Add(id!);
                CatalogExtraction? cachedExtraction = null;
                var date = m.Date("updated");
                var extracted = false;
                var substantive = false;
                if (markdown?.Text is { } text)
                {
                    extractions.TryGetValue(ContentHash(text), out cachedExtraction);
                    var section = AbstractSection(text);
                    if (section is not null) { summary = section; substantive = true; }
                    else if (summary.Length == 0 && cachedExtraction is { Abstract.Length: > 0 })
                    { summary = cachedExtraction.Abstract; extracted = true; substantive = true; }
                    if (cachedExtraction?.ModelVersion.EndsWith(":invalid-response", StringComparison.Ordinal) == true && summary.Length == 0)
                        issues.Add(new("ExtractionNeedsReview", markdown.Path, "Metadata extraction did not yield a usable response; source content is retained for review."));
                    if (text.StartsWith("---", StringComparison.Ordinal))
                    {
                        var end = text.IndexOf("---", 3, StringComparison.Ordinal);
                        if (end > 3)
                        {
                            var fm = Metadata.Parse(text[3..end]);
                            if (fm.Date("updated") is { } updated && (date is null || updated >= date))
                            { title = fm.Text("title") ?? title; date = updated; }
                        }
                    }
                }
                var current = Version(id!, title!, summary, date, substantive ? markdown! : f, extracted, substantive);
                var topics = m.List("topics");
                extractedLevels[id!] = cachedExtraction?.AudienceLevel;
                talks.Add(new CatalogTalk
                {
                    TalkId = id!, CurrentVersionId = current.VersionId, Title = title!, Abstract = summary,
                    Topics = topics.Count > 0 ? topics : cachedExtraction?.Topics ?? [], Audiences = m.List("audiences"), Formats = m.List("formats"),
                    AudienceLevel = m.Text("audience_level"),
                    Status = m.Text("status") ?? "draft", PublicationStatus = m.Text("publication", "status") ?? "private",
                    Eligible = m.Text("status") != "retired", NeedsReview = summary.Length < 40, Source = Source(f)
                });
            }
            catch (Exception ex) when (ex is FormatException or YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                issues.Add(new("InvalidTalk", f.Path, "Metadata could not be parsed; previous valid record retained for review."));
                if (previous.Talks.FirstOrDefault(t => t.TalkId == pathId) is { } retained) talks.Add(retained with { NeedsReview = true });
            }
        }

        foreach (var f in privateRepo.Files.Where(f => f.Path.StartsWith("engagements/", StringComparison.Ordinal) && f.Path.EndsWith("/engagement.yaml", StringComparison.Ordinal)))
        {
            try
            {
                var m = Metadata.Parse(f.Text!);
                var id = m.Text("id");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(m.Text("event"))) throw new FormatException("Delivery identity is missing.");
                var talkId = m.Text("talk_id");
                var talk = talks.FirstOrDefault(t => t.TalkId == talkId);
                if (talkId is not null && talk is null) issues.Add(new("UnknownTalk", f.Path, "Engagement references a missing talk; retained as unresolved history."));
                var date = m.Date("date");
                var notes = m.Text("notes") ?? "";
                var precision = date is null ? "unknown" : m.Text("date_precision") ??
                    (Regex.IsMatch(notes, @"(?i)TBD|unknown|placeholder|exact date|date.*confirm") || date is { Month: 1, Day: 1 } ? "unknown" : "day");
                var eventUrl = SafeUrl(m.Text("event_url"));
                var delivery = new CatalogDelivery
                {
                    DeliveryId = id!, TalkId = talkId, EventName = m.Text("event")!, SessionTitle = m.Text("session_title"),
                    Date = date, DatePrecision = precision, Status = m.Text("status") ?? "planned", Role = m.Text("role") ?? "speaker",
                    City = m.Text("location", "city"), Region = m.Text("location", "region"), Country = m.Text("location", "country"),
                    DeliveryMode = m.Text("delivery_mode"), Organizer = m.Text("organizer", "name"), EventUrl = eventUrl,
                    EventSeriesId = EventSeriesKey(eventUrl, m.Text("event")!), ArtifactUrl = SafeUrl(m.Text("slides_url")),
                    AudienceLevel = m.Text("session", "level"), DurationMinutes = m.Integer("session", "duration_minutes"),
                    PreviousIds = m.List("previous_ids"), NeedsReview = precision != "day" || talkId is not null && talk is null,
                    Source = Source(f)
                };
                deliveries.Add(delivery);
                var abstractPath = f.Path[..f.Path.LastIndexOf('/')] + "/sources/session-abstract.md";
                if (talk is not null && files.TryGetValue(abstractPath, out var venue) && AbstractSection(venue.Text ?? "") is { } venueAbstract)
                    Version(talk.TalkId, delivery.SessionTitle ?? talk.Title, venueAbstract, date, venue, substantive: true);
            }
            catch (Exception ex) when (ex is FormatException or YamlDotNet.Core.YamlException or InvalidOperationException)
            { issues.Add(new("InvalidEngagement", f.Path, "Invalid engagement excluded from delivery counts.")); }
        }
        // Explicit previous_ids supersede old records. Uncertain look-alikes are never silently merged.
        var superseded = deliveries.SelectMany(d => d.PreviousIds).ToHashSet(StringComparer.Ordinal);
        deliveries.RemoveAll(d => superseded.Contains(d.DeliveryId));
        foreach (var duplicate in deliveries.GroupBy(d => d.DeliveryId).Where(g => g.Count() > 1))
            issues.Add(new("DuplicateDeliveryId", duplicate.Key, "Duplicate source identity requires review."));
        deliveries = deliveries.DistinctBy(d => d.DeliveryId).ToList();

        for (var i = 0; i < talks.Count; i++)
        {
            var talk = talks[i];
            var current = versions.Values.Where(v => v.TalkId == talk.TalkId && v.Abstract.Length >= 40 && (!explicitDescriptions.Contains(talk.TalkId) || !v.Extracted))
                .OrderByDescending(v => v.SourceKind == "abstract").ThenByDescending(v => v.EffectiveDate).ThenBy(v => v.VersionId, StringComparer.Ordinal).FirstOrDefault();
            var history = deliveries.Where(d => d.TalkId == talk.TalkId).ToArray();
            talks[i] = talk with
            {
                CurrentVersionId = current?.VersionId ?? talk.CurrentVersionId,
                Title = current?.Title ?? talk.Title, Abstract = current?.Abstract ?? talk.Abstract,
                PriorTitles = versions.Values.Where(v => v.TalkId == talk.TalkId && v.Title != (current?.Title ?? talk.Title)).Select(v => v.Title).Distinct().ToArray(),
                DurationsMinutes = history.Where(d => d.DurationMinutes > 0).Select(d => d.DurationMinutes!.Value).Distinct().Order().ToArray(),
                AudienceLevel = talk.AudienceLevel ?? history.OrderByDescending(d => d.Date).Select(d => d.AudienceLevel).FirstOrDefault(x => x is not null)
                    ?? extractedLevels.GetValueOrDefault(talk.TalkId)
            };
        }
        foreach (var missing in previous.Talks.Where(t => !seenTalkIds.Contains(t.TalkId)))
        {
            talks.Add(missing with { Eligible = false, NeedsReview = true });
            issues.Add(new("SourceMissing", missing.Source.Path, "Talk retained but excluded from scoring until its source is restored or reviewed."));
        }

        var publicArtifacts = publicRepo.Files.Where(f => f.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Select(f =>
        {
            var url = $"https://github.com/{publicRepo.Repository}/blob/{publicRepo.CommitSha}/{Uri.EscapeDataString(f.Path).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
            var folder = string.Join('/', f.Path.Split('/').Take(2));
            var existing = deliveries.FirstOrDefault(d => d.ArtifactUrl?.Contains("/" + folder, StringComparison.OrdinalIgnoreCase) == true);
            if (existing is null) issues.Add(new("UnlinkedPublicArtifact", f.Path, "Artifact available; explicit delivery identity review required before counting it."));
            return new CatalogArtifact(f.Path, url, existing?.DeliveryId, existing is null);
        }).ToArray();
        return CatalogReview.ApplyOverrides(new CatalogSnapshot
        {
            GenerationId = Guid.NewGuid().ToString("N"), SyncedAtUtc = now,
            RepositoryHeads = new Dictionary<string, string> { [privateRepo.Repository] = privateRepo.CommitSha, [publicRepo.Repository] = publicRepo.CommitSha },
            Talks = talks.OrderBy(t => t.TalkId).ToArray(), Versions = versions.Values.OrderBy(v => v.TalkId).ThenBy(v => v.VersionId).ToArray(),
            Deliveries = deliveries, SourceDeliveries = deliveries, Issues = issues, PublicArtifacts = publicArtifacts, LegacyTalkAliases = previous.LegacyTalkAliases,
            DeliveryCorrections = previous.DeliveryCorrections, EligibilityOverrides = previous.EligibilityOverrides
        });
    }

    public static string EventSeriesKey(string? eventUrl, string name)
    {
        // Use the path too: many distinct user groups share meetup.com or sessionize.com.
        if (Uri.TryCreate(eventUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var path = Regex.Replace(uri.AbsolutePath.TrimEnd('/').ToLowerInvariant(), @"(?<!\d)20\d{2}(?!\d)", "");
            return uri.IdnHost.ToLowerInvariant().Replace("www.", "", StringComparison.Ordinal) + path;
        }
        return "name:" + Regex.Replace(Regex.Replace(name.ToLowerInvariant(), @"\b20\d{2}\b", ""), @"[^\p{L}\p{N}]", "");
    }

    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? value : null;

    private sealed class Metadata(YamlMappingNode node)
    {
        public static Metadata Parse(string text)
        {
            if (text.Length > 256000 || Regex.IsMatch(text, @"(?m)(?:^|[\s:\[,])(?:&|\*)[A-Za-z]")) throw new FormatException("YAML aliases are not supported.");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            return yaml.Documents.Count == 1 && yaml.Documents[0].RootNode is YamlMappingNode map ? new Metadata(map) : throw new FormatException("Expected a YAML mapping.");
        }
        private YamlNode? Find(params string[] keys)
        {
            YamlNode? value = node;
            foreach (var key in keys) value = value is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var found) ? found : null;
            return value;
        }
        public string? Text(params string[] keys)
        {
            var text = (Find(keys) as YamlScalarNode)?.Value?.Trim();
            return string.IsNullOrWhiteSpace(text) || text is "null" or "~" ? null : text;
        }
        public IReadOnlyList<string> List(params string[] keys) => Find(keys) is YamlSequenceNode seq ? seq.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").Where(x => x.Length > 0).ToArray() : [];
        public DateTimeOffset? Date(params string[] keys) => DateTimeOffset.TryParse(Text(keys), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        public int? Integer(params string[] keys) => int.TryParse(Text(keys), CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
