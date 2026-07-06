using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery.Tests;

public class ConfsTechSourceTests
{
    // A fixed "now" so date filters are deterministic.
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);

    private static ConfsTechOptions Options() => new() { Enabled = true };

    private static ConfsTechEntry Entry(
        string name = "Great Lakes Cloud Conf 2026",
        string url = "https://greatlakescloud.example/2026",
        string? start = "2026-10-01",
        string? end = "2026-10-03",
        string? city = "Milwaukee, WI",
        string? country = "U.S.A.",
        bool? online = false,
        string? cfpUrl = null,
        string? cfpEnd = null) => new()
        {
            Name = name,
            Url = url,
            StartDate = start,
            EndDate = end,
            City = city,
            Country = country,
            Online = online,
            CfpUrl = cfpUrl,
            CfpEndDate = cfpEnd,
        };

    // --- Mapping -------------------------------------------------------------

    [Fact]
    public void Maps_structured_entry_to_pre_extracted_candidate()
    {
        var entry = Entry(cfpUrl: "https://greatlakescloud.example/cfp", cfpEnd: "2026-08-15");

        var candidates = ConfsTechSource.BuildCandidates([entry], Now, Options());

        var c = Assert.Single(candidates);
        Assert.Equal(SourceSeenOn.ConfsTech, c.Source);
        Assert.Null(c.Page);                       // structured: no page needing extraction
        Assert.NotNull(c.Extracted);
        Assert.Equal("https://greatlakescloud.example/cfp", c.Url); // provenance points at the CFP

        var e = c.Extracted!;
        Assert.True(e.IsEvent);
        Assert.Equal("Great Lakes Cloud Conf 2026", e.EventName);
        Assert.Equal(ConfsTechSource.StructuredConfidence, e.Confidence);
        Assert.Equal(EventFormat.InPerson, e.Format);
        Assert.Equal(CfpStatus.Open, e.CfpStatus);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), e.CfpDeadline);
        Assert.Equal("Milwaukee, WI, U.S.A.", e.Location);
    }

    [Fact]
    public void Cfp_without_deadline_stays_unknown()
    {
        var entry = Entry(cfpUrl: "https://x.example/cfp", cfpEnd: null);

        var e = Assert.Single(ConfsTechSource.BuildCandidates([entry], Now, Options())).Extracted!;

        Assert.Equal(CfpStatus.Unknown, e.CfpStatus);
        Assert.Equal("https://x.example/cfp", e.CfpUrl);
    }

    [Theory]
    [InlineData(true, "Austin, TX", EventFormat.Hybrid)]   // has a physical city + online option
    [InlineData(true, null, EventFormat.Virtual)]           // online only
    [InlineData(false, "Austin, TX", EventFormat.InPerson)]
    public void Derives_format_from_online_and_city(bool online, string? city, EventFormat expected)
    {
        var entry = Entry(city: city, country: "U.S.A.", online: online);

        var e = Assert.Single(ConfsTechSource.BuildCandidates([entry], Now, Options())).Extracted!;

        Assert.Equal(expected, e.Format);
    }

    // --- Date filters --------------------------------------------------------

    [Fact]
    public void Drops_events_that_already_started()
    {
        var past = Entry(start: "2026-01-10", end: "2026-01-12");

        Assert.Empty(ConfsTechSource.BuildCandidates([past], Now, Options()));
    }

    [Fact]
    public void Drops_events_whose_cfp_already_closed()
    {
        var closed = Entry(cfpUrl: "https://x/cfp", cfpEnd: "2026-05-01"); // before Now

        Assert.Empty(ConfsTechSource.BuildCandidates([closed], Now, Options()));
    }

    [Fact]
    public void Drops_entries_missing_name_or_url()
    {
        var noName = Entry(name: "");
        var noUrl = Entry(url: "");

        Assert.Empty(ConfsTechSource.BuildCandidates([noName, noUrl], Now, Options()));
    }

    // --- Geo filter ----------------------------------------------------------

    [Fact]
    public void Drops_non_us_events_without_an_online_option()
    {
        var foreign = Entry(city: "Frankfurt am Main", country: "Germany", online: false);

        Assert.Empty(ConfsTechSource.BuildCandidates([foreign], Now, Options()));
    }

    [Fact]
    public void Keeps_non_us_events_that_offer_an_online_option()
    {
        var foreignOnline = Entry(city: "Frankfurt am Main", country: "Germany", online: true);

        Assert.Single(ConfsTechSource.BuildCandidates([foreignOnline], Now, Options()));
    }

    [Fact]
    public void Us_only_can_be_disabled()
    {
        var foreign = Entry(city: "Frankfurt am Main", country: "Germany", online: false);
        var options = Options();
        options.UsOnly = false;

        Assert.Single(ConfsTechSource.BuildCandidates([foreign], Now, options));
    }

    // --- Ordering ------------------------------------------------------------

    [Fact]
    public void Surfaces_midwest_events_before_others()
    {
        var coast = Entry(name: "Coastal Conf 2026", url: "https://coastal/2026",
            start: "2026-08-01", city: "Seattle, WA", country: "U.S.A.");
        var midwest = Entry(name: "Driftless AI Days 2026", url: "https://driftless/2026",
            start: "2026-11-01", city: "Madison, WI", country: "U.S.A.");

        var ordered = ConfsTechSource.BuildCandidates([coast, midwest], Now, Options());

        Assert.Equal(2, ordered.Count);
        Assert.Equal("Driftless AI Days 2026", ordered[0].Extracted!.EventName); // Midwest leads despite later date
    }

    [Fact]
    public void Respects_max_events_per_run()
    {
        var many = Enumerable.Range(0, 5)
            .Select(i => Entry(name: $"Conf {i}", url: $"https://c/{i}", city: "Austin, TX"))
            .ToArray();
        var options = Options();
        options.MaxEventsPerRun = 2;

        Assert.Equal(2, ConfsTechSource.BuildCandidates(many, Now, options).Count);
    }
}
