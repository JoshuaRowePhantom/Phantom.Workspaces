using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoTestDatabaseCollection.CollectionName)]
public sealed class MongoDbChatHistoryProviderSlowTests
{
    private readonly MongoTestDatabaseFixture _fixture;

    public MongoDbChatHistoryProviderSlowTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ChatHistoryProvider_StoresAndReloadsMessages()
    {
        await _fixture.ResetCollectionAsync();

        var provider = new MongoDbChatHistoryProvider(
            _fixture.Database,
            MongoTestDatabaseFixture.ChatHistoryCollectionName);
        var client = new RecordingChatClient();
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
                ChatHistoryProvider = provider,
            });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions());

        await agent.RunAsync(
            new ChatMessage(
                ChatRole.User,
                [
                    new TextContent("hello"),
                    new DataContent(new byte[] { 0x01, 0x02 }, "image/png"),
                ]),
            session,
            runOptions,
            CancellationToken.None);

        Assert.Single(client.Invocations);
        var firstInvocation = client.Invocations.First();
        Assert.Equal(["hello"], GetUserTexts(firstInvocation));

        var historyCollection = _fixture.Database.GetCollection<BsonDocument>(MongoTestDatabaseFixture.ChatHistoryCollectionName);
        Assert.Equal(2, await historyCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var firstStoredMessage = await historyCollection.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync();
        Assert.Equal(BsonType.Document, firstStoredMessage["Payload"].BsonType);

        await agent.RunAsync("world", session, runOptions, CancellationToken.None);

        Assert.Equal(2, client.Invocations.Count);
        var secondInvocation = client.Invocations.Last();
        Assert.Equal(["hello", "world"], GetUserTexts(secondInvocation));

        var restoredUserMessage = Assert.Single(
            secondInvocation,
            message => message.Role == ChatRole.User && message.Contents.OfType<DataContent>().Any());
        Assert.Equal("hello", Assert.Single(restoredUserMessage.Contents.OfType<TextContent>()).Text);
        Assert.Equal("image/png", Assert.Single(restoredUserMessage.Contents.OfType<DataContent>()).MediaType);

        Assert.Equal(4, await historyCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    private static IReadOnlyList<string> GetUserTexts(IReadOnlyList<ChatMessage> messages)
    {
        return messages
            .Where(static message => message.Role == ChatRole.User)
            .SelectMany(static message => message.Contents.OfType<TextContent>())
            .Select(static content => content.Text)
            .ToArray();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _invocations = new();

        public ConcurrentQueue<IReadOnlyList<ChatMessage>> Invocations => _invocations;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            _invocations.Enqueue(snapshot);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, BuildResponse(snapshot))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            _invocations.Enqueue(snapshot);
            yield return new ChatResponseUpdate(ChatRole.Assistant, BuildResponse(snapshot))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }

        private static string BuildResponse(IEnumerable<ChatMessage> messages)
        {
            var userTexts = messages
                .Where(static message => message.Role == ChatRole.User)
                .SelectMany(static message => message.Contents.OfType<TextContent>())
                .Select(static content => content.Text);

            return string.Join("|", userTexts);
        }
    }
}
