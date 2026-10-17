using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram;

public sealed record TelegramMessageItem(string Reference, string EventSlug, string Name, string? Resolution = null,
    string? ExpectedETag = null, string? DecisionId = null);
public sealed record TelegramMessageState(long ChatId, long MessageId, string Text, IReadOnlyList<TelegramMessageItem> Items,
    DateTimeOffset ExpiresUtc, string? Picker = null, DateTimeOffset? LockedUntilUtc = null, IReadOnlyList<string>? TextChunks = null);
public sealed record TelegramCallbackState(string MessageKey, string EventSlug, long ChatId, DateTimeOffset ExpiresUtc);
public sealed record TelegramMessageTextChunk(string Text);

public sealed class TelegramInteractions(IOperationsApiClient operations, IOpportunityApiClient opportunities,
    TelegramClient client, IOptions<TelegramOptions> options, TimeProvider time, ILogger<TelegramInteractions> logger,
    IOptions<PipelineNotificationOptions> pipeline)
{
    public async Task<TelegramKeyboard?> PrepareAsync(string key, NotificationPart part, CancellationToken ct)
    {
        if (!pipeline.Value.OpportunitiesEnabled) return null;
        var items = new List<TelegramMessageItem>();
        foreach (var item in (part.Items ?? []).Where(i => i.Actions && i.EventSlug is not null))
        {
            var current = string.IsNullOrWhiteSpace(item.ExpectedETag) ? await opportunities.GetOpportunityAsync(item.EventSlug!, ct) : null;
            var expectedETag = item.ExpectedETag ?? current?.ETag;
            if (string.IsNullOrWhiteSpace(expectedETag)) continue;
            items.Add(new(OperationState.Key(key + ":" + item.Id)[..24], item.EventSlug!, item.Name,
                ExpectedETag: expectedETag, DecisionId: item.ExpectedETag is null ? current?.LatestDecisionId : item.DecisionId));
        }
        if (items.Count == 0) return null;
        var existing = await operations.GetAsync("messages", key, ct);
        var state = OperationState.Read<TelegramMessageState>(existing);
        if (state is null)
        {
            var chunks = await StoreTextAsync(key, part.Text!, ct);
            state = new TelegramMessageState(options.Value.ChatId, 0, "", items, time.GetUtcNow().AddDays(30), TextChunks: chunks);
            var saved = await operations.SaveAsync(OperationState.Document("messages", key, state, time.GetUtcNow()), ct: ct);
            if (saved is null)
                state = OperationState.Read<TelegramMessageState>(await operations.GetAsync("messages", key, ct))
                    ?? throw new InvalidOperationException("Message identity could not be persisted.");
        }
        foreach (var item in state.Items)
            await operations.SaveAsync(OperationState.Document("callbacks", item.Reference,
                new TelegramCallbackState(key, item.EventSlug, state.ChatId, state.ExpiresUtc), time.GetUtcNow()), ct: ct);
        return Keyboard(state);
    }

    public async Task BindAsync(string key, TelegramSentMessage sent, CancellationToken ct)
    {
        var document = await operations.GetAsync("messages", key, ct);
        var state = OperationState.Read<TelegramMessageState>(document);
        if (state is null) return;
        if (state.MessageId == sent.MessageId) return;
        if (state.MessageId != 0) throw new InvalidOperationException("A different message receipt is already bound.");
        if (await operations.SaveAsync(OperationState.Document("messages", key, state with { MessageId = sent.MessageId }, time.GetUtcNow()), document!.Version, ct) is null)
            throw new InvalidOperationException("Message receipt changed concurrently.");
    }

    public async Task HandleAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var expectedUser = options.Value.UserId == 0 ? options.Value.ChatId : options.Value.UserId;
        if (!pipeline.Value.OpportunitiesEnabled || !options.Value.Enabled || callback.Message?.Chat?.Type != "private"
            || callback.Message.Chat.Id != options.Value.ChatId || callback.From?.Id != expectedUser)
        {
            await client.AnswerAsync(callback.Id, "Not authorized.", true, ct);
            return;
        }
        var data = callback.Data?.Split(':');
        if (data is not { Length: 3 } || data[0] != "p" || data[1].Length != 24 || !data[1].All(char.IsAsciiHexDigit))
        { await client.AnswerAsync(callback.Id, "Invalid action.", true, ct); return; }
        var action = data[2];
        var reference = OperationState.Read<TelegramCallbackState>(await operations.GetAsync("callbacks", data[1], ct));
        if (reference is null || reference.ChatId != options.Value.ChatId || reference.ExpiresUtc <= time.GetUtcNow())
        { await client.AnswerAsync(callback.Id, "This action expired. Use /status.", true, ct); return; }
        var document = await operations.GetAsync("messages", reference.MessageKey, ct);
        var state = OperationState.Read<TelegramMessageState>(document);
        var item = state?.Items.FirstOrDefault(i => i.Reference == data[1]);
        if (state is null || item is null || state.MessageId != callback.Message!.MessageId)
        { await client.AnswerAsync(callback.Id, "This message cannot be changed.", true, ct); return; }
        if (state.LockedUntilUtc > time.GetUtcNow())
        { await client.AnswerAsync(callback.Id, "An action is already being applied. Try again shortly.", false, ct); return; }
        var locked = await operations.SaveAsync(OperationState.Document("messages", reference.MessageKey,
            state with { LockedUntilUtc = time.GetUtcNow().AddSeconds(45) }, time.GetUtcNow()), document!.Version, ct);
        if (locked is null) { await client.AnswerAsync(callback.Id, "Message changed. Try again.", false, ct); return; }
        var updated = state;
        try
        {
            if (item.Resolution is not null)
            {
                await AnswerValidatedAsync(callback.Id, item.Resolution, false, ct);
                if (item.Resolution != "Changed elsewhere; use /status")
                    updated = await RefreshResolutionAsync(state, item, ct);
            }
            else if (action == "d")
            {
                updated = state with { Picker = item.Reference };
                await AnswerValidatedAsync(callback.Id, "Choose a reason below.", false, ct);
            }
            else if (action == "cancel")
            {
                updated = state with { Picker = null };
                await AnswerValidatedAsync(callback.Id, "Cancelled.", false, ct);
            }
            else
            {
                var reason = action.StartsWith('r') && int.TryParse(action.AsSpan(1), out var number) && Enum.IsDefined((FeedbackReason)number)
                    ? (FeedbackReason?)number : null;
                var selected = action switch { "s" => OpportunityAction.Submitted, "z" => OpportunityAction.Snooze7Days,
                    _ when reason is not null && state.Picker == item.Reference => OpportunityAction.Drop, _ => (OpportunityAction?)null };
                if (selected is null) { await AnswerValidatedAsync(callback.Id, "Invalid action.", true, ct); return; }
                await AnswerValidatedAsync(callback.Id, "Updating…", false, ct);
                if (string.IsNullOrWhiteSpace(item.ExpectedETag))
                {
                    updated = state with { Items = state.Items.Select(i => i.Reference == item.Reference
                        ? i with { Resolution = "Changed elsewhere; use /status" } : i).ToArray(), Picker = null };
                    await client.EditAsync(state.ChatId, state.MessageId, await RenderAsync(updated, ct), Keyboard(updated), ct);
                    return;
                }
                await opportunities.ApplyOpportunityActionAsync(item.EventSlug, new OpportunityActionRequest
                {
                    Action = selected.Value, ReasonCode = reason, IdempotencyKey = $"telegram-{item.Reference}-{action}",
                    DecisionId = item.DecisionId,
                }, item.ExpectedETag, ct);
                var resolution = selected == OpportunityAction.Snooze7Days ? "Snoozed 7 days" : selected == OpportunityAction.Drop ? "Dropped" : "Submitted";
                updated = state with { Picker = null, Items = state.Items.Select(i => i.Reference == item.Reference ? i with { Resolution = resolution } : i).ToArray() };
                // Record acceptance before refreshing: a failed read/edit must never repeat the mutation.
                updated = await RefreshResolutionAsync(updated, item, ct);
            }
            await client.EditAsync(state.ChatId, state.MessageId, await RenderAsync(updated, ct), Keyboard(updated), ct);
        }
        catch (OpportunityConflictException)
        {
            logger.LogWarning("Telegram action conflicted with newer opportunity state.");
            // No blind retry of a business action against a different revision.
            updated = state with { Picker = null, Items = state.Items.Select(i => i.Reference == item.Reference
                ? i with { Resolution = "Changed elsewhere; use /status" } : i).ToArray() };
            await client.EditAsync(state.ChatId, state.MessageId, await RenderAsync(updated, ct), Keyboard(updated), ct);
        }
        finally
        {
            // Persist accepted actions even when editing fails. A replay retries only the edit.
            await operations.SaveAsync(OperationState.Document("messages", reference.MessageKey,
                updated with { LockedUntilUtc = null }, time.GetUtcNow()), locked.Version, CancellationToken.None);
        }
    }

    private async Task<TelegramMessageState> RefreshResolutionAsync(TelegramMessageState state, TelegramMessageItem item, CancellationToken ct)
    {
        // Command replay returns the original receipt. Rendering explicitly reads today's state,
        // including a later dashboard move between the accepted action and message-edit repair.
        var current = await opportunities.GetOpportunityAsync(item.EventSlug, ct);
        if (current is null) return state;
        var resolution = current.DoNotResurface ? "Dropped"
            : current.SnoozedUntilUtc > time.GetUtcNow() ? "Snoozed 7 days"
            : current.Status switch { OpportunityStatus.SubmitNow => "Submit now", OpportunityStatus.NotPursuing => "Not pursuing", _ => current.Status.ToString() };
        return state with { Items = state.Items.Select(i => i.Reference == item.Reference ? i with { Resolution = resolution } : i).ToArray() };
    }

    private async Task AnswerValidatedAsync(string callbackId, string text, bool alert, CancellationToken ct)
    {
        try { await client.AnswerAsync(callbackId, text, alert, ct); }
        catch (TelegramApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Telegram may expire the acknowledgement token before a webhook replay.
            // Authorization, message binding and CAS still protect the business action;
            // an expired UI acknowledgement must not block a saved action's edit repair.
            logger.LogWarning("Telegram rejected an acknowledgement for a validated callback; continuing its idempotent action or edit repair.");
        }
    }

    private async Task<IReadOnlyList<string>> StoreTextAsync(string key, string text, CancellationToken ct)
    {
        const int chunkSize = 3500; // At most 21,000 JSON characters even when every character is escaped.
        if (text.Length > chunkSize * 256) throw new InvalidOperationException("Message HTML exceeds the bounded interaction storage size.");
        var chunks = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(chunkSize, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
            var value = text.Substring(offset, count);
            var id = OperationState.Key(key + ":" + offset + ":" + value);
            await operations.SaveAsync(OperationState.Document("messagechunks", id, new TelegramMessageTextChunk(value), time.GetUtcNow()), ct: ct);
            chunks.Add(id);
            offset += count;
        }
        return chunks;
    }

    private async Task<string> RenderAsync(TelegramMessageState state, CancellationToken ct)
    {
        if (state.TextChunks is null) return Render(state); // Existing single-payload message state remains readable.
        var text = new System.Text.StringBuilder();
        foreach (var id in state.TextChunks)
        {
            var chunk = OperationState.Read<TelegramMessageTextChunk>(await operations.GetAsync("messagechunks", id, ct))
                ?? throw new InvalidOperationException("Message text payload is incomplete.");
            text.Append(chunk.Text);
        }
        return Render(state with { Text = text.ToString() });
    }

    internal static string Render(TelegramMessageState state)
    {
        var text = state.Text;
        foreach (var item in state.Items.Where(i => i.Resolution is not null))
        {
            var number = item.Name.Split('.')[0];
            text = text.Replace($"<i>Item {number}: Awaiting action</i>", $"<i>Item {number}: {TelegramText.Escape(item.Resolution)}</i>", StringComparison.Ordinal);
        }
        return text;
    }

    internal static TelegramKeyboard Keyboard(TelegramMessageState state)
    {
        if (state.Picker is { } picker)
        {
            var rows = Enum.GetValues<FeedbackReason>().Select(reason => (IReadOnlyList<TelegramButton>)
                new[] { new TelegramButton(ReasonLabel(reason), $"p:{picker}:r{(int)reason}") }).ToList();
            rows.Add([new("Cancel", $"p:{picker}:cancel")]);
            return new(rows);
        }
        return new(state.Items.Where(i => i.Resolution is null).Select(i => (IReadOnlyList<TelegramButton>)new[]
        {
            new TelegramButton($"{i.Name.Split('.')[0]} Submitted", $"p:{i.Reference}:s"),
            new TelegramButton("Snooze 7d", $"p:{i.Reference}:z"),
            new TelegramButton("Drop", $"p:{i.Reference}:d"),
        }).ToArray());
    }

    private static string ReasonLabel(FeedbackReason reason) => reason switch
    {
        FeedbackReason.WrongAudience => "Wrong audience", FeedbackReason.TooFar => "Too far", FeedbackReason.BadTiming => "Bad timing",
        FeedbackReason.AlreadySpokeThere => "Already spoke there", FeedbackReason.NotMyTopic => "Not my topic", _ => "Other",
    };
}
