using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Current Ollama implementation of the model-agnostic agent contract.
/// The model chooses exactly one externally visible action per turn and never
/// receives direct access to project objects.
/// </summary>
public sealed class OllamaAgentModel : IAgentModel
{
    private const int MaximumPlanConstraints = 24;
    private const int MaximumPlanSteps = 24;

    private static readonly JsonSerializerOptions TurnPayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly JsonElement DecisionSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "use_tool",
                "ask_user",
                "publish_plan",
                "begin_verification",
                "complete_task"
              ]
            },
            "progress": { "type": "string" },
            "tool_name": { "type": "string" },
            "tool_arguments": {
              "type": "object",
              "additionalProperties": true
            },
            "question": { "type": "string" },
            "question_context": { "type": "string" },
            "plan_objective": { "type": "string" },
            "plan_summary": { "type": "string" },
            "plan_constraints": {
              "type": "array",
              "items": { "type": "string" }
            },
            "plan_steps": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "description": { "type": "string" }
                },
                "required": ["title", "description"],
                "additionalProperties": false
              }
            },
            "completion_summary": { "type": "string" }
          },
          "required": [
            "action",
            "progress",
            "tool_name",
            "tool_arguments",
            "question",
            "question_context",
            "plan_objective",
            "plan_summary",
            "plan_constraints",
            "plan_steps",
            "completion_summary"
          ],
          "additionalProperties": false
        }
        """);

    private readonly OllamaVideoAnalysisService _ollama;
    private readonly IAgentDebugLog _debugLog;

    public OllamaAgentModel(
        OllamaVideoAnalysisService ollama,
        IAgentDebugLog? debugLog = null)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _debugLog = debugLog ?? NullAgentDebugLog.Instance;
    }

    public async ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemPrompt = BuildSystemPrompt(request.Mode);
        var turnPayload = BuildTurnPayload(request);
        var startedAt = DateTimeOffset.UtcNow;

        _debugLog.Write(new AgentDebugLogEntry(
            startedAt,
            "ollama_agent_model",
            "request",
            request.Task.Id,
            request.Task.Phase.ToString(),
            request.TurnIndex,
            $"Sending {request.Mode} turn to Ollama.",
            $"system_prompt:\n{systemPrompt}\n\nturn_payload:\n{turnPayload}"));

        try
        {
            var raw = await _ollama.RunAgentStructuredTurnAsync(
                DecisionSchema,
                systemPrompt,
                turnPayload,
                cancellationToken).ConfigureAwait(false);

            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ollama_agent_model",
                "response",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                $"Ollama returned a structured response in {(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:0} ms.",
                raw));

            try
            {
                return ParseDecision(raw);
            }
            catch (Exception parseException)
            {
                _debugLog.Write(new AgentDebugLogEntry(
                    DateTimeOffset.UtcNow,
                    "ollama_agent_model",
                    "response_parse_failed",
                    request.Task.Id,
                    request.Task.Phase.ToString(),
                    request.TurnIndex,
                    parseException.Message,
                    raw,
                    parseException.ToString()));
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ollama_agent_model",
                "cancelled",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                "Ollama agent turn was cancelled."));
            throw;
        }
        catch (Exception exception)
        {
            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ollama_agent_model",
                "request_failed",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                exception.Message,
                $"turn_payload:\n{turnPayload}",
                exception.ToString()));
            throw;
        }
    }

    private static string BuildSystemPrompt(AgentModelTurnMode mode)
    {
        var phaseRules = mode switch
        {
            AgentModelTurnMode.Planning =>
                """
                Сейчас этап ИССЛЕДОВАНИЯ И ПЛАНА.
                Разрешённые итоговые действия: use_tool, ask_user, publish_plan.
                Не выполняй монтаж. Используй только read-only tools.
                publish_plan делай только когда данных достаточно для конкретного,
                проверяемого и понятного пользователю плана.
                """,
            AgentModelTurnMode.Execution =>
                """
                Сейчас этап ВЫПОЛНЕНИЯ УТВЕРЖДЁННОГО ПЛАНА на отдельном Agent Draft.
                Разрешённые итоговые действия: use_tool, ask_user, begin_verification.
                Выполняй только утверждённый план и явно одобренные пользователем уточнения.
                Не меняй исходную последовательность. Не делай лишних улучшений "заодно".
                Если нужно удалить несколько диапазонов, измеренных на одном состоянии таймлайна,
                предпочитай ripple_delete_ranges. Если используешь отдельные ripple-delete вызовы,
                удаляй справа налево либо заново inspect_timeline после каждого сдвига.
                Когда все запланированные изменения выполнены, выбери begin_verification.
                """,
            AgentModelTurnMode.Verification =>
                """
                Сейчас этап ПРОВЕРКИ Agent Draft.
                Разрешённые итоговые действия: use_tool, ask_user, complete_task.
                Сначала проверь фактические изменения read-only инструментами.
                Обязательно вызови inspect_agent_edits после последнего изменения, затем
                inspect_timeline и/или inspect_range для проверки фактического состояния
                черновика и важных новых склеек/изменённых мест.
                Если обнаружена ошибка в рамках утверждённого плана, можешь исправить её
                editing tool и затем снова проверить. Если исправление выходит за рамки
                утверждённого плана, сначала спроси пользователя.
                complete_task выбирай только после фактической проверки результата.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        return
            """
            /no_think
            Ты монтажный AI-агент Kadr Studio.

            Общие правила:
            - На каждом ходе выбери ровно ОДНО внешнее действие.
            - Не раскрывай chain-of-thought, скрытые рассуждения или внутренние рассуждения.
              progress — только короткий понятный пользователю статус текущего действия.
            - Учитывай исходный запрос, весь доступный диалог, уже данные пользователем ответы,
              утверждённый план и ограничения.
            - Не спрашивай пользователя, если ответ уже есть в сообщениях либо его можно
              надёжно получить доступным инструментом.
            - ask_user используй только при существенной неопределённости, которую нельзя
              безопасно разрешить имеющимися данными и tools.
            - Не придумывай id, таймкоды, содержание видео или факты. Получай их tools.
            - Механические признаки сами по себе не являются монтажным решением:
              тишина, чёрный кадр, смена сцены и похожие сигналы — только наблюдения.
              При необходимости исследуй смысловой контекст вокруг них.
            - Работай целенаправленно. Не анализируй весь материал, когда достаточно
              конкретного диапазона.
            - В tool_name используй только имя из available_tools.
            - tool_arguments должны строго соответствовать input_schema выбранного tool.
            - Никогда не пытайся изменить source sequence: editing tools предназначены
              только для Agent Draft и дополнительно проверяются приложением.
            - Для неиспользуемых полей schema верни пустую строку, пустой объект или пустой массив.
            - Верни только JSON по переданной schema.

            """ + phaseRules;
    }

    private static string BuildTurnPayload(
        AgentModelTurnRequest request)
    {
        var answeredQuestions = request.Task.Questions
            .Where(question => question.IsAnswered)
            .Select(question => new
            {
                question = question.Prompt,
                answer = question.Answer
            })
            .ToArray();

        var plan = request.Task.Plan;
        var currentPlan = plan is null
            ? null
            : new
            {
                version = plan.Version,
                objective = plan.Objective,
                summary = plan.Summary,
                constraints = plan.Constraints,
                steps = plan.Steps.Select(step => new
                {
                    order = step.Order,
                    title = step.Title,
                    description = step.Description
                }).ToArray(),
                approved = plan.ApprovedAt is not null
            };

        var tools = request.AvailableTools
            .Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                access = tool.Access.ToString().ToLowerInvariant(),
                input_schema = tool.InputSchema
            })
            .ToArray();

        var observations = request.Observations
            .Select(observation => new
            {
                sequence = observation.Sequence,
                tool_name = observation.ToolName,
                status = observation.Status.ToString().ToLowerInvariant(),
                summary = observation.Summary,
                error_code = observation.ErrorCode,
                data = observation.Data
            })
            .ToArray();

        var conversation = request.Conversation
            .Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                text = message.Text,
                created_at = message.CreatedAt
            })
            .ToArray();

        var payload = new
        {
            turn = request.TurnIndex,
            mode = request.Mode.ToString().ToLowerInvariant(),
            task = new
            {
                id = request.Task.Id,
                project_id = request.Task.ProjectId,
                source_sequence_id = request.Task.SourceSequenceId,
                source_sequence_revision = request.Task.SourceSequenceRevision,
                draft_sequence_id = request.Task.DraftSequenceId,
                phase = request.Task.Phase.ToString().ToLowerInvariant(),
                user_request = request.Task.UserRequest,
                answered_questions = answeredQuestions,
                current_plan = currentPlan
            },
            conversation,
            available_tools = tools,
            observations,
            instruction =
                "Choose exactly one next action that is valid for the current mode."
        };

        return JsonSerializer.Serialize(payload, TurnPayloadJsonOptions);
    }

    private static AgentModelDecision ParseDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "Agent model returned an empty structured response.");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var action = ReadRequiredString(root, "action");
        var progress = ReadString(root, "progress");

        return action switch
        {
            "use_tool" => ParseToolDecision(root, progress),
            "ask_user" => ParseQuestionDecision(root, progress),
            "publish_plan" => ParsePlanDecision(root, progress),
            "begin_verification" => AgentModelDecision.BeginVerification(progress),
            "complete_task" => AgentModelDecision.CompleteTask(
                ReadRequiredString(root, "completion_summary"),
                progress),
            _ => throw new InvalidOperationException(
                $"Agent model returned unknown action '{action}'.")
        };
    }

    private static AgentModelDecision ParseToolDecision(
        JsonElement root,
        string progress)
    {
        var toolName = ReadRequiredString(root, "tool_name");

        if (!root.TryGetProperty("tool_arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Agent model returned invalid tool_arguments.");
        }

        return AgentModelDecision.UseTool(
            toolName,
            arguments,
            progress);
    }

    private static AgentModelDecision ParseQuestionDecision(
        JsonElement root,
        string progress)
    {
        var question = ReadRequiredString(root, "question");
        var context = ReadString(root, "question_context");

        return AgentModelDecision.AskUser(
            question,
            context,
            progress);
    }

    private static AgentModelDecision ParsePlanDecision(
        JsonElement root,
        string progress)
    {
        var objective = ReadRequiredString(root, "plan_objective");
        var summary = ReadRequiredString(root, "plan_summary");

        var constraints = root.TryGetProperty(
                "plan_constraints",
                out var constraintsElement) &&
            constraintsElement.ValueKind == JsonValueKind.Array
            ? constraintsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Take(MaximumPlanConstraints)
                .ToArray()
            : [];

        var steps = new List<AgentPlanStepDraft>();
        if (root.TryGetProperty("plan_steps", out var stepsElement) &&
            stepsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stepsElement
                         .EnumerateArray()
                         .Take(MaximumPlanSteps))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = ReadString(item, "title");
                var description = ReadString(item, "description");
                if (string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                steps.Add(new AgentPlanStepDraft(
                    title,
                    description));
            }
        }

        if (steps.Count == 0)
        {
            throw new InvalidOperationException(
                "Agent model published a plan without valid steps.");
        }

        return AgentModelDecision.PublishPlan(
            AgentPlanDraft.Create(
                objective,
                summary,
                constraints,
                steps),
            progress);
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Agent model response field '{propertyName}' is required.");
        }

        return value;
    }

    private static string ReadString(
        JsonElement element,
        string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
