using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm.Provider.Llama;

public sealed class OllamaStreamLlmProvider : ILlmProvider
{
    private readonly Stream stream;

    public OllamaStreamLlmProvider(
        Stream stream)
    {
        this.stream = stream;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var line in ReadLinesAsync(this.stream, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var streamEvent in OllamaStreamEventParser.ParseLine(line))
            {
                yield return streamEvent;
            }
        }
    }

    internal static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            stream,
            leaveOpen: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
        }
    }
}
