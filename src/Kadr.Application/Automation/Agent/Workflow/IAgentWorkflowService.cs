using KadrStudio.Application.Automation.Agent.Runtime;

namespace KadrStudio.Application.Automation.Agent.Workflow;

public interface IAgentWorkflowService
{
    AgentTaskState? Current { get; }

    event EventHandler<AgentTaskChangedEventArgs>? Changed;

    Task<AgentTaskState> StartAsync(
        string userRequest,
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> ContinueAsync(
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> AnswerAsync(
        string answer,
        Guid? questionId = null,
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> RequestRevisionAsync(
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> ApproveAsync(
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> RetryAsync(
        CancellationToken cancellationToken = default);

    Task<AgentTaskState> StopAsync(string? reason = null);

    Task<AgentTaskState> FailAsync(string reason);
}

/// <summary>
/// Single host-facing façade for the agent lifecycle. Presentation code no
/// longer chooses a planning/execution loop or persists runtime state itself.
/// </summary>
public sealed class AgentWorkflowService : IAgentWorkflowService
{
    private readonly AiAgentOrchestrator _orchestrator;
    private readonly AgentPlanningLoop _planning;
    private readonly AgentExecutionLoop _execution;
    private readonly Func<string, AgentTaskState> _start;
    private readonly Func<string, Guid?, AgentTaskState> _answer;
    private readonly Func<AgentTaskState> _requestRevision;
    private readonly Func<AgentTaskState> _approve;
    private readonly Func<AgentTaskState> _retry;
    private readonly Func<string?, AgentTaskState> _stop;
    private readonly Action<AgentTaskState> _persist;

    public AgentWorkflowService(
        AiAgentOrchestrator orchestrator,
        AgentPlanningLoop planning,
        AgentExecutionLoop execution,
        Func<string, AgentTaskState> start,
        Func<string, Guid?, AgentTaskState> answer,
        Func<AgentTaskState> requestRevision,
        Func<AgentTaskState> approve,
        Func<AgentTaskState> retry,
        Func<string?, AgentTaskState> stop,
        Action<AgentTaskState> persist)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _planning = planning ?? throw new ArgumentNullException(nameof(planning));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));
        _requestRevision = requestRevision ?? throw new ArgumentNullException(nameof(requestRevision));
        _approve = approve ?? throw new ArgumentNullException(nameof(approve));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _orchestrator.TaskChanged += OnTaskChanged;
    }

    public AgentTaskState? Current => _orchestrator.CurrentTask;

    public event EventHandler<AgentTaskChangedEventArgs>? Changed;

    public async Task<AgentTaskState> StartAsync(
        string userRequest,
        CancellationToken cancellationToken = default)
    {
        var state = _start(userRequest);
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTaskState> ContinueAsync(
        CancellationToken cancellationToken = default)
    {
        var current = RequireCurrent();
        AgentTaskState result;
        if (current.Phase is AgentTaskPhase.Understanding or
            AgentTaskPhase.Investigating or AgentTaskPhase.Planning)
        {
            result = await _planning.RunUntilPauseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else if (current.Phase is AgentTaskPhase.Executing or AgentTaskPhase.Verifying)
        {
            result = await _execution.RunUntilPauseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            result = current;
        }

        _persist(result);
        return result;
    }

    public async Task<AgentTaskState> AnswerAsync(
        string answer,
        Guid? questionId = null,
        CancellationToken cancellationToken = default)
    {
        var state = _answer(answer, questionId);
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTaskState> RequestRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        var state = _requestRevision();
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTaskState> ApproveAsync(
        CancellationToken cancellationToken = default)
    {
        var state = _approve();
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTaskState> RetryAsync(
        CancellationToken cancellationToken = default)
    {
        var state = _retry();
        _planning.PrepareRetryFromExistingEvidence();
        _persist(state);
        return await ContinueAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentTaskState> StopAsync(string? reason = null)
    {
        var state = _stop(reason);
        _persist(state);
        return Task.FromResult(state);
    }

    public Task<AgentTaskState> FailAsync(string reason)
    {
        var current = RequireCurrent();
        var state = current.IsTerminal
            ? current
            : _orchestrator.Fail(reason);
        _persist(state);
        return Task.FromResult(state);
    }

    private AgentTaskState RequireCurrent()
        => Current ?? throw new AgentTaskTransitionException(
            "There is no active AI agent task.");

    private void OnTaskChanged(object? sender, AgentTaskChangedEventArgs args)
    {
        if (args.State.Phase is AgentTaskPhase.Understanding or
            AgentTaskPhase.Investigating or
            AgentTaskPhase.Planning or
            AgentTaskPhase.WaitingForUserInput or
            AgentTaskPhase.WaitingForApproval)
        {
            // Planning evidence has no draft checkpoint, so persist it as the
            // state machine changes. Approved/executing state is persisted by
            // the atomic draft-creation transaction instead.
            _persist(args.State);
        }

        Changed?.Invoke(this, args);
    }
}
