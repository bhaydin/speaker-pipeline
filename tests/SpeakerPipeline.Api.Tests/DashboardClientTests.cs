using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpeakerPipeline.Core;
using SpeakerPipeline.Dashboard;

namespace SpeakerPipeline.Api.Tests;

public sealed class DashboardClientTests
{
    [Fact]
    public async Task Successful_action_refreshes_current_state_instead_of_rendering_an_older_replay_receipt()
    {
        var original = new OpportunityRecord
        {
            Event = new() { Slug = "fictional-event", Name = "Fictional event", EventType = EventType.Conference,
                Category = EventCategory.Monitor, Priority = Priority.Medium },
            ETag = "\"original\"",
        };
        var accepted = original with { ETag = "\"accepted\"", UserStatus = OpportunityStatus.Submitted };
        var current = accepted with { ETag = "\"later\"", UserStatus = OpportunityStatus.Dropped, DoNotResurface = true };
        var requests = new List<HttpMethod>();
        using var handler = new CallbackHandler(request =>
        {
            requests.Add(request.Method);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/opportunities/fictional-event/actions", request.RequestUri!.AbsolutePath);
                Assert.Equal(original.ETag, Assert.Single(request.Headers.GetValues("If-Match")));
                Assert.Equal("same-command", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
                return Json(accepted);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/opportunities/fictional-event", request.RequestUri!.AbsolutePath);
            return Json(current);
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };

        var displayed = await new DashboardClient(http).ActAsync(original,
            new() { Action = OpportunityAction.Submitted, IdempotencyKey = "same-command" });

        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, requests);
        Assert.Equal(current.ETag, displayed.ETag);
        Assert.Equal(OpportunityStatus.Dropped, displayed.Status);
        Assert.True(displayed.DoNotResurface);
    }

    private static HttpResponseMessage Json(OpportunityRecord value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } }),
    };

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
