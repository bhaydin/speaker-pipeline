namespace SpeakerPipeline.Catalog;

public static class CatalogSchedule
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public static string? NightlyDeliveryId(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, Chicago);
        return local.Hour == 3 ? "scheduled-" + local.ToString("yyyy-MM-dd") : null;
    }
}
