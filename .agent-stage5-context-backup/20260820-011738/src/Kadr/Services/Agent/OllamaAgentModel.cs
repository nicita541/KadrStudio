using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Current local/remote Ollama implementation of the model-agnostic agent model
/// contract. It emits exactly one externally visible action per turn and never
/// asks the model to expose hidden chain-of-thought.
/// </summary>
public sealed class OllamaAgentModel(
    OllamaVideoAnalysisService ollama) : IAgentModel
{
    private const int MaximumPlanConstraints = 24;
    private const int MaximumPlanSteps = 24;

    private static readonly JsonElement DecisionSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["use_tool", "ask_user", "publish_plan"]
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
            }
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
            "plan_steps"
          ],
          "additionalProperties": false
        }
        """);

    private const string SystemPrompt =
        """
        /no_think
        Ты монтажный AI-агент Kadr Studio на этапе исследования и подготовки плана.
        На каждом ходе выбери ровно ОДНО внешнее действие:
        use_tool, ask_user или publish_plan.

        Правила:
        - Не раскрывай chain-of-thought, скрытые рассуждения или внутренние рассуждения.
          Поле progress — только короткий понятный пользователю статус действия.
        - Сначала понимай задачу пользователя и учитывай ВСЕ уже данные им ответы и ограничения.
        - Не задавай вопрос, если ответ можно получить из сообщения пользователя, предыдущих ответов
          или доступными read-only tools.
        - Задавай ask_user только если существенная неопределённость реально не разрешается
          имеющимися данными и инструментами.
        - Не придумывай id, таймкоды, содержание видео или факты. Получай их инструментами.
        - Механические сигналы сами по себе не являются монтажным решением:
          тишина, чёрный кадр, смена сцены и похожие признаки — только наблюдения.
          При необходимости исследуй контекст вокруг них.
        - Используй инструменты целенаправленно. Не анализируй весь материал без необходимости.
          Если достаточно узкого диапазона, исследуй узкий диапазон.
        - Собирай достаточно фактов для задачи, но не вызывай один и тот же tool без новой причины.
        - publish_plan разрешён только когда данных достаточно для конкретного и проверяемого плана.
        - План описывает будущие изменения, но на этом этапе ничего не монтируется.
          Основной timeline должен оставаться нетронутым; выполнение позже пойдёт в отдельный Agent Draft.
        - Ограничения пользователя обязательны. Если будущая идея выходит за утверждённую задачу,
          она не должна молча попадать в план.
        - В tool_name используй только имя из available_tools.
        - tool_arguments должны строго соответствовать input_schema выбранного инструмента.
        - Для неиспользуемых полей верни: пустую строку, пустой объект или пустой массив.
        - Верни только JSON по переданной schema.
        """;

    private readonly OllamaVideoAnalysisService _ollama =
        ollama ?? throw new ArgumentNullException(nameof(ollama));

    public async ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userPrompt = BuildTurnPayload(request);
        var raw = await _ollama.RunAgentStructuredTurnAsync(
            DecisionSchema,
            SystemPrompt,
            userPrompt,
            cancellationToken).ConfigureAwait(false);

        return ParseDecision(raw);
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
            task = new
            {
                id = request.Task.Id,
                project_id = request.Task.ProjectId,
                source_sequence_id = request.Task.SourceSequenceId,
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
                "Choose exactly one next action. Prefer a useful tool over a user question when the tool can resolve the uncertainty."
        };

        return JsonSerializer.Serialize(payload);
    }

    private static AgentModelDecision ParseDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Agent model returned an empty structured response.");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var action = ReadRequiredString(root, "action");
        var progress = ReadString(root, "progress");

        return action switch
        {
            "use_tool" => ParseToolDecision(root, progress),
            "ask_user" => ParseQuestionDecision(root, progress),
            "publish_plan" => ParsePlanDecision(root, progress),
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
            throw new InvalidOperationException(
                "Agent model published a plan without valid steps.");

        var plan = AgentPlanDraft.Create(
            objective,
            summary,
            constraints,
            steps);

        return AgentModelDecision.PublishPlan(
            plan,
            progress);
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Agent model response field '{propertyName}' is required.");

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
