using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

[TestClass]
public class CopilotResponsesTests
{
    [TestMethod]
    public async Task RefusalIsDisplayedReturnedAndSavedAsync()
    {
        var refusal = new ChatResponseUpdate(ChatRole.Assistant,
            [new ErrorContent("I cannot help with that request.") { ErrorCode = "Refusal" }]);
        using var client = new ScriptedClient([refusal]);
        await using var chat = new FunctionCallingChat(client, "system", new FunctionFactory(), null);
        var displayed = "";
        var result = await chat.CompleteChatStreamingAsync("question", streamingCallback: update => displayed += update.Text);

        Assert.AreEqual("I cannot help with that request.", result);
        Assert.AreEqual(result, displayed);
        Assert.AreEqual(result, chat.Conversation.Messages.Last().Text);
        Assert.IsTrue(refusal.Contents.Single() is ErrorContent, "Do not mutate the provider's update.");
    }

    [TestMethod]
    public async Task StreamErrorStopsToolsAndDoesNotLeakCallsIntoNextTurnAsync()
    {
        using var client = new ScriptedClient(
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "NeverRun", new Dictionary<string, object?>())]),
             new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("Service unavailable") { ErrorCode = "server_error" }])],
            [new ChatResponseUpdate(ChatRole.Assistant, "Recovered")]);
        await using var chat = new FunctionCallingChat(client, "system", new FunctionFactory(), null);
        var approvals = 0;
        InvalidOperationException? failure = null;
        try
        {
            await chat.CompleteChatStreamingAsync("question", approveFunctionCall: (_, _) => { approvals++; return false; });
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }

        Assert.IsTrue(failure != null, "A streamed service error must fail the turn.");
        Assert.IsTrue(failure!.Message.Contains("server_error"));
        Assert.IsTrue(failure.Message.Contains("Service unavailable"));
        Assert.AreEqual(0, approvals);
        Assert.IsFalse(chat.Conversation.Messages.Any(message => message.Role == ChatRole.Assistant));
        var result = await chat.CompleteChatStreamingAsync("retry", approveFunctionCall: (_, _) => { approvals++; return false; });
        Assert.AreEqual("Recovered", result);
        Assert.AreEqual(0, approvals);
    }

    [TestMethod]
    public async Task StatelessWrapperClearsIdsWithoutMutatingOptionsAsync()
    {
        using var inner = new ScriptedClient(
            [new ChatResponseUpdate(ChatRole.Assistant, "streamed") { ConversationId = "server-id", ResponseId = "response-id" }]);
        using var client = new StatelessResponsesChatClient(inner);
        var messages = new List<ChatMessage> { new(ChatRole.User, "question") };
        var options = new ChatOptions { ConversationId = "previous-id", MaxOutputTokens = 123 };

        var response = await client.GetResponseAsync(messages, options);
        Assert.IsTrue(response.ConversationId == null);
        Assert.AreEqual("response-id", response.ResponseId);
        Assert.IsTrue(inner.LastOptions?.ConversationId == null);
        Assert.AreEqual(123, inner.LastOptions?.MaxOutputTokens);
        Assert.AreEqual("previous-id", options.ConversationId);
        Assert.IsTrue(ReferenceEquals(messages[0], inner.Requests[0][0]));

        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            Assert.IsTrue(update.ConversationId == null);
            Assert.AreEqual("response-id", update.ResponseId);
            Assert.AreEqual("streamed", update.Text);
        }
        Assert.IsTrue(inner.LastOptions?.ConversationId == null);
        Assert.AreEqual(123, inner.LastOptions?.MaxOutputTokens);
        Assert.AreEqual("previous-id", options.ConversationId);
    }

    [TestMethod]
    public async Task ToolFollowUpReplaysHistoryWithoutConversationIdAsync()
    {
        using var inner = new ScriptedClient(
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "DeniedTool", new Dictionary<string, object?>())]) { ConversationId = "server-id" }],
            [new ChatResponseUpdate(ChatRole.Assistant, "Done")],
            [new ChatResponseUpdate(ChatRole.Assistant, "Next turn")]);
        using var client = new StatelessResponsesChatClient(inner);
        await using var chat = new FunctionCallingChat(client, "system", new FunctionFactory(), null);

        Assert.AreEqual("Done", await chat.CompleteChatStreamingAsync("question", approveFunctionCall: (_, _) => false));
        Assert.AreEqual(2, inner.Requests.Count);
        var replay = inner.Requests[1];
        Assert.AreEqual(ChatRole.System, replay[0].Role);
        Assert.AreEqual("question", replay[1].Text);
        Assert.IsTrue(replay[2].Contents.Any(content => content is FunctionCallContent));
        Assert.IsTrue(replay[3].Contents.Any(content => content is FunctionResultContent));
        Assert.IsTrue(inner.LastOptions?.ConversationId == null);
        Assert.AreEqual("Next turn", await chat.CompleteChatStreamingAsync("another question"));
        Assert.IsTrue(inner.Requests[2].Any(message => message.Text == "Done"));
    }

    private sealed class ScriptedClient(params ChatResponseUpdate[][] turns) : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = new();
        public ChatOptions? LastOptions { get; private set; }
        private readonly Queue<ChatResponseUpdate[]> _turns = new(turns);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            LastOptions = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")])
            {
                ConversationId = "server-id",
                ResponseId = "response-id"
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            LastOptions = options;
            await Task.CompletedTask;
            foreach (var update in _turns.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }
}
