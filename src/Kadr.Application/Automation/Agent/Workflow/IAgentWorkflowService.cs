namespace KadrStudio.Application.Automation.Agent.Workflow;

public interface IAgentWorkflowService
{
    AgentTaskState? Current { get; }
    event EventHandler<AgentTaskChangedEventArgs>? Changed;
    Task<AgentTaskState> StartAsync(string userRequest, CancellationToken cancellationToken = default);
    Task<AgentTaskState> ContinueAsync(CancellationToken cancellationToken = default);
    Task<AgentTaskState> AcceptDraftAsync(CancellationToken cancellationToken = default);
    Task<AgentTaskState> ReviseDraftAsync(string feedback, CancellationToken cancellationToken = default);
    Task<AgentTaskState> DiscardDraftAsync(CancellationToken cancellationToken = default);
    Task<AgentTaskState> CancelAsync(string? reason = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The only host-facing workflow for Kadr AI Editor v2.1. There is no plan
/// approval or model/tool loop: users review an already isolated Agent Draft.
/// </summary>
public sealed class AgentWorkflowService : IAgentWorkflowService
{
    private readonly AiAgentOrchestrator _orchestrator;
    private readonly Func<string, AgentTaskState> _start;
    private readonly Func<CancellationToken, Task<AgentTaskState>> _continue;
    private readonly Func<AgentTaskState> _accept;
    private readonly Func<string, AgentTaskState> _revise;
    private readonly Func<AgentTaskState> _discard;
    private readonly Func<string?, AgentTaskState> _cancel;
    private readonly Action<AgentTaskState> _persist;

    public AgentWorkflowService(
        AiAgentOrchestrator orchestrator,
        Func<string, AgentTaskState> start,
        Func<CancellationToken, Task<AgentTaskState>> continueEditorial,
        Func<AgentTaskState> acceptDraft,
        Func<string, AgentTaskState> reviseDraft,
        Func<AgentTaskState> discardDraft,
        Func<string?, AgentTaskState> cancel,
        Action<AgentTaskState> persist)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _continue = continueEditorial ?? throw new ArgumentNullException(nameof(continueEditorial));
        _accept = acceptDraft ?? throw new ArgumentNullException(nameof(acceptDraft));
        _revise = reviseDraft ?? throw new ArgumentNullException(nameof(reviseDraft));
        _discard = discardDraft ?? throw new ArgumentNullException(nameof(discardDraft));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _orchestrator.TaskChanged += OnTaskChanged;
    }

    public AgentTaskState? Current => _orchestrator.CurrentTask;
    public event EventHandler<AgentTaskChangedEventArgs>? Changed;

    public async Task<AgentTaskState> StartAsync(string userRequest, CancellationToken cancellationToken = default)
    {
        var state = _start(userRequest);
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTaskState> ContinueAsync(CancellationToken cancellationToken = default)
    {
        var current = RequireCurrent();
        if (current.IsTerminal || current.Phase == AgentTaskPhase.ReviewingDraft) return current;
        var result = await _continue(cancellationToken).ConfigureAwait(false);
        _persist(result);
        return result;
    }

    public Task<AgentTaskState> AcceptDraftAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _accept();
        _persist(state);
        return Task.FromResult(state);
    }

    public async Task<AgentTaskState> ReviseDraftAsync(string feedback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedback))
            throw new ArgumentException("Draft revision feedback cannot be empty.", nameof(feedback));
        var state = _revise(feedback.Trim());
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentTaskState> DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _discard();
        _persist(state);
        return Task.FromResult(state);
    }

    public Task<AgentTaskState> CancelAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _cancel(reason);
        _persist(state);
        return Task.FromResult(state);
    }

    private AgentTaskState RequireCurrent()
        => Current ?? throw new AgentTaskTransitionException("There is no active Kadr AI Editor task.");

    private void OnTaskChanged(object? sender, AgentTaskChangedEventArgs args)
    {
        _persist(args.State);
        Changed?.Invoke(this, args);
    }
}
