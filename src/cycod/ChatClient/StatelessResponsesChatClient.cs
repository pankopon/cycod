using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

/// <summary>
/// Makes a Responses-API chat client stateless by suppressing server-side
/// conversation chaining.
/// </summary>
/// <remarks>
/// OpenAI's Responses API can chain turns server-side: the client sends only the
/// new messages plus a previous_response_id, and the service supplies the rest of
/// the history. Microsoft.Extensions.AI drives that through
/// ChatOptions.ConversationId, and FunctionInvokingChatClient copies the id from
/// each response into the follow-up turn automatically.
///
/// GitHub Copilot's /responses implementation does not store responses, and
/// rejects the chained request:
///
///   "Request body is badly formatted: unknown field 'previous_response_id'"
///
/// The first turn therefore succeeds and the second fails, which shows up as tool
/// calls working right up until the model tries to use the result.
///
/// Clearing the id on the way in (so no previous_response_id is sent) and on the
/// way out (so nothing downstream re-attaches one) makes every turn carry the full
/// conversation, which is how /chat/completions already behaves. cycod maintains
/// the message list itself, so no history is lost.
/// </remarks>
public sealed class StatelessResponsesChatClient : DelegatingChatClient
{
    public StatelessResponsesChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    private static ChatOptions? WithoutConversationId(ChatOptions? options)
    {
        if (options?.ConversationId == null) return options;

        // Clone rather than mutate: the caller's options may be reused.
        var copy = options.Clone();
        copy.ConversationId = null;
        return copy;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, WithoutConversationId(options), cancellationToken);
        response.ConversationId = null;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = base.GetStreamingResponseAsync(messages, WithoutConversationId(options), cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            update.ConversationId = null;
            yield return update;
        }
    }
}
