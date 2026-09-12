namespace Phantom.Workspaces.Llm;

internal static class AgentInputQueueSnapshotValidator
{
    internal static void Validate(AgentInputQueuesSnapshot snapshot)
    {
        if (snapshot.Revision < 0)
        {
            throw new ArgumentException("Aggregate revision must be nonnegative.", nameof(snapshot));
        }

        var queueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queue in snapshot.Queues)
        {
            if (string.IsNullOrWhiteSpace(queue.QueueId)
                || string.IsNullOrWhiteSpace(queue.Name)
                || queue.Revision < 0
                || queue.Priority < 0
                || !Enum.IsDefined(queue.Immediacy)
                || (queue.IsDefault && queue.IsImmediate)
                || (queue.IsImmediate && queue.Immediacy != AgentInputQueueImmediacy.Immediate)
                || !queueIds.Add(queue.QueueId))
            {
                throw new ArgumentException("Queue snapshot contains invalid identity, role, revision, or priority.", nameof(snapshot));
            }

            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in queue.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId)
                    || item.Messages.IsDefaultOrEmpty
                    || item.Messages.Any(static message => message is null)
                    || !itemIds.Add(item.ItemId))
                {
                    throw new ArgumentException("Queue snapshot contains invalid item identity or messages.", nameof(snapshot));
                }
            }
        }
    }
}
