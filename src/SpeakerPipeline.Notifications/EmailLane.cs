using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Sends notifications as email via Microsoft Graph <c>sendMail</c>, authenticating
/// to the Haydin.ai tenant with client credentials. No-ops (rather than throwing)
/// when the lane isn't configured, so an unconfigured environment simply sends
/// nothing.
/// </summary>
public sealed class EmailLane(
    HttpClient http,
    IOptions<NotificationOptions> options,
    ILogger<EmailLane> logger) : INotificationLane
{
    private static readonly string[] GraphScope = ["https://graph.microsoft.com/.default"];

    private readonly EmailLaneOptions _options = options.Value.Email;

    public NotificationChannel Channel => NotificationChannel.Email;

    public bool IsEnabled => _options.IsConfigured;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            logger.LogWarning("Email lane skipped: Notifications:Email is not fully configured.");
            return;
        }

        var credential = new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScope), ct);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_options.Sender!)}/sendMail")
        {
            Content = new StringContent(BuildSendMailPayload(notification, _options.Recipient!), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var resp = await http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Email send failed: {Status}", (int)resp.StatusCode);
            resp.EnsureSuccessStatusCode();
        }

        logger.LogInformation("Email sent: '{Subject}' -> {Recipient}", notification.Subject, _options.Recipient);
    }

    /// <summary>Builds the Graph sendMail request body for a notification.</summary>
    internal static string BuildSendMailPayload(Notification n, string recipient)
    {
        var message = new
        {
            message = new
            {
                subject = n.Subject,
                body = new { contentType = "HTML", content = Render(n) },
                toRecipients = new[] { new { emailAddress = new { address = recipient } } },
            },
            saveToSentItems = true,
        };
        return JsonSerializer.Serialize(message);
    }

    private static string Render(Notification notification)
    {
        if (notification.Groups.Count == 0) return notification.HtmlBody;
        var body = new StringBuilder();
        foreach (var group in notification.Groups)
        {
            body.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(group.Title)).Append("</h2>");
            foreach (var item in group.Items)
            {
                var name = System.Net.WebUtility.HtmlEncode(item.Name);
                if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    name = $"<a href=\"{System.Net.WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{name}</a>";
                body.Append("<p><strong>").Append(name).Append("</strong><br>")
                    .Append(System.Net.WebUtility.HtmlEncode(item.Detail).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            }
        }
        return body.ToString();
    }
}
