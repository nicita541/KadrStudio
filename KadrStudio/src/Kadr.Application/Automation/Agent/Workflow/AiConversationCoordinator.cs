using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Workflow;

public sealed class AiConversationCoordinator
{
    public AiConversation Append(AiConversation conversation, AiChatMessage message)
        => conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = conversation.Messages.Add(message)
        };

    public AiConversation Replace(AiConversation conversation, AiChatMessage message)
        => conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = conversation.Messages
                .Select(item => item.Id == message.Id ? message : item)
                .ToImmutableArray()
        };
}
