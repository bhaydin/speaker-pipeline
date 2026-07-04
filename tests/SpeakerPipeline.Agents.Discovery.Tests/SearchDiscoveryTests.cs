using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery.Tests;

public class SearchDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string Agent = "discovery-agent-v1";

    // --- URL canonicalization + source identification ------------------------

    [Theory]
    [InlineData("https://sessionize.com/foo-conf-2027/", "https://sessionize.com/foo-conf-2027")]
    [InlineData("https://Sessionize.com/Foo?utm_source=x#cfp", "https://sessionize.com/Foo")]
    [InlineData("http://example.org/e/", "http://example.org/e")]
    [InlineData("https://sessionize.com", "https://sessionize.com/")]
    public void Canonicalize_drops_query_fragment_trailing_slash_and_lowercases_host(string input, string expected)
        => Assert.Equal(expected, UrlCanonicalizer.Canonicalize(input));

    [Theory]
    [InlineData("https://sessionize.com/foo", true)]
    [InlineData("https://api.sessionize.com/x", true)]
    [InlineData("https://notsessionize.com/foo", false)]
    [InlineData("https://sessionize.com.evil.com/foo", false)]
    [InlineData("not-a-url", false)]
    public void IsSessionize_identifies_only_real_sessionize_hosts(string url, bool expected)
        => Assert.Equal(expected, UrlCanonicalizer.IsSessionize(url));

    [Theory]
    [InlineData("https://sessionize.com/foo", SourceSeenOn.Sessionize)]
    [InlineData("https://contoso-conf.org/cfp", SourceSeenOn.Direct)]
    public void InferSource_maps_sessionize_vs_official(string url, SourceSeenOn expected)
        => Assert.Equal(expected, UrlCanonicalizer.InferSource(url));

    // --- Duplicate URL detection --------------------------------------------

    [Fact]
    public void Dedupe_collapses_same_canonical_url_keeping_first_query()
    {
        var hits = new[]
        {
            new SearchHit("https://sessionize.com/foo-conf/", "T1", "S1", "query-A"),
            new SearchHit("https://sessionize.com/foo-conf?utm=x", "T2", "S2", "query-B"), // dup of foo-conf
            new SearchHit("https://sessionize.com/bar-conf", "T3", "S3", "query-A"),
        };

        var deduped = SearchSource.Dedupe(hits);

        Assert.Equal(2, deduped.Count);
        var foo = deduped.Single(h => h.Url == "https://sessionize.com/foo-conf");
        Assert.Equal("query-A", foo.Query); // first query that surfaced it wins (traceability)
    }

    // --- Google response normalization --------------------------------------

    [Fact]
    public void ParseResponse_reads_items_into_hits()
    {
        var json = """
            {"kind":"customsearch#search","items":[
              {"title":"Foo Conf CFP","link":"https://sessionize.com/foo","snippet":"Call for Speakers"},
              {"title":"Bar","link":"https://sessionize.com/bar","snippet":"..."}
            ]}
            """;

        var hits = GoogleProgrammableSearchAdapter.ParseResponse(json, "q");

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://sessionize.com/foo", hits[0].Url);
        Assert.Equal("q", hits[0].Query);
    }

    [Theory]
    [InlineData("{\"searchInformation\":{\"totalResults\":\"0\"}}")] // valid, no items
    [InlineData("not json")]                                          // malformed
    [InlineData("{\"items\":[{\"title\":\"no link\"}]}")]            // item missing link
    public void ParseResponse_is_tolerant_of_empty_or_bad_payloads(string json)
        => Assert.Empty(GoogleProgrammableSearchAdapter.ParseResponse(json, "q"));

    // --- Reconcile: search-specific behavior --------------------------------

    [Fact]
    public void Reconcile_new_closed_cfp_is_skipped()
    {
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "Old Conf 2024", CfpStatus = CfpStatus.Closed, Confidence = 9 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(
            existing: null, extracted, "old-conf-2024", SourceSeenOn.Sessionize, Now, Agent, discoveredVia: "q");

        Assert.Null(upsert);
        Assert.Contains("closed", summary);
    }

    [Fact]
    public void Reconcile_new_event_records_the_discovering_query()
    {
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "Fresh Conf 2027", CfpStatus = CfpStatus.Open, Confidence = 9 };

        var (upsert, _, isNew) = DiscoveryAgent.Reconcile(
            existing: null, extracted, "fresh-conf-2027", SourceSeenOn.Sessionize, Now, Agent,
            discoveredVia: "site:sessionize.com \"Call for Speakers\" \"Azure\"");

        Assert.True(isNew);
        Assert.NotNull(upsert);
        Assert.Contains("Discovered via search", upsert!.StatusDetail);
        Assert.Contains("Azure", upsert.StatusDetail);
    }

    [Fact]
    public void Reconcile_search_never_overwrites_existing_human_or_scoring_decision()
    {
        // A search re-surfaces an event Brian already skipped.
        var existing = new EventRecord
        {
            Slug = "known", Name = "Known Conf", EventType = EventType.Conference,
            Category = EventCategory.Pass, Priority = Priority.Low, DoNotResurface = true,
        };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "Known Conf", CfpStatus = CfpStatus.Open, Confidence = 9 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(existing, extracted, "known", SourceSeenOn.Sessionize, Now, Agent, "q");

        Assert.Null(upsert); // do-not-resurface stands; search can't resurrect it
        Assert.Contains("do-not-resurface", summary);
    }
}
