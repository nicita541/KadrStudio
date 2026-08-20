using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Kadr AI Server implementation of the model-agnostic agent contract.
/// The model chooses exactly one externally visible action per turn and never
/// receives direct access to project objects.
/// </summary>
public sealed class AiServerAgentModel :
    IAgentModel,
    IAgentTaskInterpreter,
    IAgentPlanCritic,
    IAgentVerificationReporter
{
    private const int MaximumPlanConstraints = 24;
    private const int MaximumPlanSteps = 24;
    private const int MaximumObservationPromptCharacters = 24_000;

    private static readonly JsonSerializerOptions TurnPayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly JsonElement InvestigationDecisionSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["use_tool", "ask_user", "publish_plan", "complete_read_only"] },
            "progress": { "type": "string", "maxLength": 600 },
            "tool_name": { "type": "string", "maxLength": 100 },
            "tool_arguments": { "type": "object", "additionalProperties": true },
            "question": { "type": "string", "maxLength": 1000 },
            "question_context": { "type": "string", "maxLength": 1600 },
            "completion_summary": { "type": "string", "maxLength": 3000 }
          },
          "required": ["action", "progress", "tool_name", "tool_arguments", "question", "question_context", "completion_summary"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement PublishedPlanSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "plan_objective": { "type": "string", "minLength": 1, "maxLength": 400 },
            "plan_summary": { "type": "string", "minLength": 1, "maxLength": 1200 },
            "plan_constraints": { "type": "array", "maxItems": 16, "items": { "type": "string", "maxLength": 300 } },
            "plan_steps": {
              "type": "array",
              "minItems": 1,
              "maxItems": 12,
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "minLength": 1, "maxLength": 200 },
                  "description": { "type": "string", "minLength": 1, "maxLength": 500 },
                  "expected_editing_tool": { "type": "string", "maxLength": 100 },
                  "expected_editing_arguments": { "type": "object", "additionalProperties": true },
                  "evidence_requirement": { "type": "string", "enum": ["timeline", "frames", "audio", "transcript", "all"] },
                  "evidence_observation_sequences": { "type": "array", "maxItems": 32, "items": { "type": "integer", "minimum": 1 } },
                  "expected_effect": { "type": "string", "maxLength": 500 },
                  "protected_invariants": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 300 } },
                  "verification_checks": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 300 } }
                },
                "required": ["title", "description", "expected_editing_tool", "expected_editing_arguments", "evidence_requirement", "evidence_observation_sequences", "expected_effect", "protected_invariants", "verification_checks"],
                "additionalProperties": false
              }
            }
          },
          "required": ["plan_objective", "plan_summary", "plan_constraints", "plan_steps"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ExecutionDecisionSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["use_tool", "ask_user", "begin_verification"] },
            "progress": { "type": "string" },
            "tool_name": { "type": "string" },
            "tool_arguments": { "type": "object", "additionalProperties": true },
            "question": { "type": "string" },
            "question_context": { "type": "string" }
          },
          "required": ["action", "progress", "tool_name", "tool_arguments", "question", "question_context"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement LegacyVerificationDecisionSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["use_tool", "ask_user", "complete_task"] },
            "progress": { "type": "string" },
            "tool_name": { "type": "string" },
            "tool_arguments": { "type": "object", "additionalProperties": true },
            "question": { "type": "string" },
            "question_context": { "type": "string" },
            "completion_summary": { "type": "string" }
          },
          "required": ["action", "progress", "tool_name", "tool_arguments", "question", "question_context", "completion_summary"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement TaskBriefSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "task_kind": { "type": "string", "enum": ["read_only", "edit", "mixed"] },
            "goal": { "type": "string", "minLength": 1, "maxLength": 240 },
            "scope": { "type": "string", "minLength": 1, "maxLength": 240 },
            "protected_elements": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 200 } },
            "constraints": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 200 } },
            "acceptance_criteria": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 200 } },
            "assumptions": { "type": "array", "maxItems": 6, "items": { "type": "string", "maxLength": 200 } },
            "missing_information": { "type": "array", "maxItems": 6, "items": { "type": "string", "maxLength": 200 } }
          },
          "required": ["task_kind", "goal", "scope", "protected_elements", "constraints", "acceptance_criteria", "assumptions", "missing_information"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement PlanReviewSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "accepted": { "type": "boolean" },
            "summary": { "type": "string" },
            "issues": { "type": "array", "items": { "type": "string" }, "maxItems": 12 }
          },
          "required": ["accepted", "summary", "issues"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement VerificationReportSchema = AgentToolJson.ParseObject(
        """
        {
          "type": "object",
          "properties": {
            "accepted": { "type": "boolean" },
            "summary": { "type": "string", "minLength": 1 },
            "issues": { "type": "array", "items": { "type": "string" }, "maxItems": 12 }
          },
          "required": ["accepted", "summary", "issues"],
          "additionalProperties": false
        }
        """);

    private readonly AiVideoAnalysisService _aiServer;
    private readonly IAgentDebugLog _debugLog;

    public AiServerAgentModel(
        AiVideoAnalysisService aiServer,
        IAgentDebugLog? debugLog = null)
    {
        _aiServer = aiServer ?? throw new ArgumentNullException(nameof(aiServer));
        _debugLog = debugLog ?? NullAgentDebugLog.Instance;
    }

    public async ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemPrompt = BuildSystemPrompt(request.Mode);
        var turnPayload = BuildTurnPayload(
            request,
            request.Mode == AgentModelTurnMode.Planning
                ? AgentToolAccess.ReadOnly
                : null);
        var startedAt = DateTimeOffset.UtcNow;

        _debugLog.Write(new AgentDebugLogEntry(
            startedAt,
            "ai_server_agent_model",
            "request",
            request.Task.Id,
            request.Task.Phase.ToString(),
            request.TurnIndex,
            $"Sending {request.Mode} turn to Kadr AI Server.",
            $"payload_characters={turnPayload.Length}; tools={request.AvailableTools.Length}; observations={request.Observations.Length}; conversation={request.Conversation.Length}"));

        try
        {
            var raw = await _aiServer.RunAgentStructuredTurnAsync(
                GetDecisionSchema(request.Mode),
                systemPrompt,
                turnPayload,
                cancellationToken,
                think: request.Mode == AgentModelTurnMode.Planning,
                maxTokens: 2048,
                reasoningTokens: request.Mode == AgentModelTurnMode.Planning
                    ? 1024
                    : null).ConfigureAwait(false);

            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ai_server_agent_model",
                "response",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                $"Kadr AI Server returned a structured response in {(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:0} ms.",
                $"response_characters={raw.Length}"));

            try
            {
                if (request.Mode == AgentModelTurnMode.Planning)
                {
                    using var actionDocument = JsonDocument.Parse(raw);
                    if (string.Equals(
                            ReadRequiredString(actionDocument.RootElement, "action"),
                            "publish_plan",
                            StringComparison.Ordinal))
                    {
                        return await GeneratePublishedPlanAsync(
                            request,
                            ReadString(actionDocument.RootElement, "progress"),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return ParseDecision(raw);
            }
            catch (Exception parseException)
            {
                _debugLog.Write(new AgentDebugLogEntry(
                    DateTimeOffset.UtcNow,
                    "ai_server_agent_model",
                    "response_parse_failed",
                    request.Task.Id,
                    request.Task.Phase.ToString(),
                    request.TurnIndex,
                    parseException.Message,
                    $"response_characters={raw.Length}",
                    parseException.ToString()));
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ai_server_agent_model",
                "cancelled",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                "Kadr AI Server agent turn was cancelled."));
            throw;
        }
        catch (Exception exception)
        {
            _debugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "ai_server_agent_model",
                "request_failed",
                request.Task.Id,
                request.Task.Phase.ToString(),
                request.TurnIndex,
                exception.Message,
                $"payload_characters={turnPayload.Length}",
                exception.ToString()));
            throw;
        }
    }

    private async ValueTask<AgentModelDecision> GeneratePublishedPlanAsync(
        AgentModelTurnRequest request,
        string progress,
        CancellationToken cancellationToken)
    {
        var prompt =
            """
            Сформируй компактный машинно проверяемый план уже исследованной задачи.
            Не вызывай инструменты и не добавляй новые действия сверх Task Brief.
            Для каждого editing-шага укажи точный tool_name из available_tools,
            нормализованные аргументы без reason, тип доказательства и номера фактических
            observations. Не придумывай IDs или таймкоды. Шаги без монтажа оставляй с
            expected_editing_tool="" и пустым объектом аргументов. Явно сохрани
            protected invariants. Если несколько диапазонов удаления измерены на одной
            revision, создай ровно один шаг ripple_delete_ranges со всеми диапазонами;
            не разбивай их на последовательные ripple-вызовы, меняющие координаты.
            Не раскрывай рассуждения. Верни только JSON по schema.
            """;
        var planPayload = BuildTurnPayload(request);
        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            PublishedPlanSchema,
            prompt,
            planPayload,
            cancellationToken,
            think: true,
            maxTokens: 4096,
            reasoningTokens: 1280).ConfigureAwait(false);
        _debugLog.Write(new AgentDebugLogEntry(
            DateTimeOffset.UtcNow,
            "ai_server_agent_model",
            "published_plan_response",
            request.Task.Id,
            request.Task.Phase.ToString(),
            request.TurnIndex,
            "Kadr AI Server returned the dedicated structured plan response.",
            $"response_characters={raw.Length}"));
        using var document = JsonDocument.Parse(raw);
        return ParsePlanDecision(document.RootElement, progress);
    }

    public async ValueTask<AgentTaskUnderstanding> UnderstandAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt =
            """
            Ты директор универсального монтажного агента Kadr Studio. Сначала преврати
            запрос в точный Task Brief. Не выбирай монтажные действия и не придумывай
            факты проекта. Используй переданный диалог и наблюдения редактора.
            task_kind означает намерение пользователя, а не текущую read-only стадию
            исследования: read_only — пользователь просит только ответ/анализ без
            изменения проекта; edit — пользователь просит любое изменение монтажа;
            mixed — одновременно ответ и изменение. Будущее утверждение плана не делает
            монтажный запрос read_only.
            Не задавай вопросы на этом шаге. Блокирующее намерение или предпочтение
            будет уточнено действием ask_user после доступного исследования проекта.
            Таймкоды, границы, clip IDs,
            содержание кадров и звука агент обязан исследовать tools: перечисли такие
            факты в missing_information, но не спрашивай их у пользователя. Агент всегда
            работает в отдельном Agent Draft, не перезаписывает source и не экспортирует,
            поэтому сохранение копии, исходные файлы, контейнер, битрейт и формат вывода
            не являются недостающей информацией для Task Brief.
            Пиши поля Task Brief компактно, без повторов и длинных объяснений.
            Никакой жанр, название или пример задачи не создаёт специального сценария.
            Не раскрывай внутренние рассуждения. Верни только JSON по schema.
            """;

        var payload = BuildTurnPayload(request);
        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            TaskBriefSchema,
            prompt,
            payload,
            cancellationToken,
            // This stage only serializes already available intent/context into a
            // compact brief. Keeping thinking enabled here made Qwen consume the
            // response budget before it emitted JSON; actual investigation and
            // plan criticism still use thinking.
            think: false,
            maxTokens: 1536,
            reasoningTokens: null).ConfigureAwait(false);
        using var briefDocument = JsonDocument.Parse(raw);
        var briefRoot = briefDocument.RootElement;
        var brief = ParseTaskBrief(briefRoot);
        return new AgentTaskUnderstanding(brief, []);
    }

    public async ValueTask<AgentPlanReview> ReviewPlanAsync(
        AgentPlanReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt =
            """
            Ты независимый критик плана универсального монтажного агента. Не исправляй
            план и не вызывай инструменты. Отклони его, если действие не поддерживается,
            аргументы неточны, evidence не доказывает действие, нарушены Task Brief или
            protected invariants, присутствуют выдуманные id/таймкоды, либо результат
            нельзя детерминированно проверить. Не требуй конкретного творческого решения:
            проверяй только соответствие запросу, доказательствам и безопасности.
            Не раскрывай внутренние рассуждения. Верни только JSON по schema.
            """;

        var reviewPlan = new
        {
            request.Plan.Objective,
            request.Plan.Summary,
            constraints = request.Plan.Constraints.IsDefault
                ? ImmutableArray<string>.Empty
                : request.Plan.Constraints,
            steps = request.Plan.Steps.Select(step => new
            {
                step.Title,
                step.Description,
                step.ExpectedEditingTool,
                step.ExpectedEditingArguments,
                evidence_observation_sequences = step.EvidenceObservationSequences.IsDefault
                    ? ImmutableArray<int>.Empty
                    : step.EvidenceObservationSequences,
                evidence_requirement = ToSchemaValue(step.EvidenceRequirement),
                step.ExpectedEffect,
                protected_invariants = step.ProtectedInvariants.IsDefault
                    ? ImmutableArray<string>.Empty
                    : step.ProtectedInvariants,
                verification_checks = step.VerificationChecks.IsDefault
                    ? ImmutableArray<string>.Empty
                    : step.VerificationChecks
            })
        };
        var payload = JsonSerializer.Serialize(new
        {
            task = new
            {
                request.Task.Id,
                request.Task.UserRequest,
                request.Task.SourceSequenceId,
                request.Task.SourceSequenceRevision,
                brief = request.Task.Brief
            },
            plan = reviewPlan,
            observations = request.Observations,
            conversation = request.Conversation
        }, TurnPayloadJsonOptions);

        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            PlanReviewSchema,
            prompt,
            payload,
            cancellationToken,
            think: true,
            maxTokens: 1536,
            reasoningTokens: 1024).ConfigureAwait(false);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var accepted = root.TryGetProperty("accepted", out var acceptedElement) &&
                       acceptedElement.ValueKind is JsonValueKind.True;
        var summary = ReadRequiredString(root, "summary");
        var issues = ReadStringArray(root, "issues", 12);
        return new AgentPlanReview(accepted, summary, issues);
    }

    public async ValueTask<AgentVerificationReport> ReportVerificationAsync(
        AgentVerificationReportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt =
            """
            Ты формируешь только итоговый отчёт детерминированной проверки Agent Draft.
            Ты не выбираешь tools, не предлагаешь скрытые исправления и не меняешь план.
            accepted=true допустимо только если edit log совпадает с утверждённым планом,
            source sequence не менялась, а проверки целостности и сравнения завершились
            успешно. Кратко опиши фактически выполненные изменения и проверки. Если
            observation сообщает ошибку или несовпадение, верни accepted=false и issues.
            Не раскрывай внутренние рассуждения. Верни только JSON по schema.
            """;
        var payload = JsonSerializer.Serialize(new
        {
            task = new
            {
                request.Task.Id,
                request.Task.UserRequest,
                request.Task.SourceSequenceId,
                request.Task.SourceSequenceRevision,
                request.Task.DraftSequenceId,
                brief = request.Task.Brief,
                plan = request.Task.Plan
            },
            verification_observations = request.VerificationObservations.Select(observation => new
            {
                observation.Sequence,
                observation.ToolName,
                status = observation.Status.ToString().ToLowerInvariant(),
                observation.Summary,
                observation.ErrorCode,
                data = CompactObservationForPrompt(observation.Data)
            })
        }, TurnPayloadJsonOptions);
        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            VerificationReportSchema,
            prompt,
            payload,
            cancellationToken,
            think: false,
            maxTokens: 1536,
            reasoningTokens: null).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new AgentVerificationReport(
            root.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == JsonValueKind.True,
            ReadRequiredString(root, "summary"),
            ReadStringArray(root, "issues", 12));
    }

    private static string BuildSystemPrompt(AgentModelTurnMode mode)
    {
        var phaseRules = mode switch
        {
            AgentModelTurnMode.Planning =>
                """
                Сейчас этап ИССЛЕДОВАНИЯ И ПЛАНА.
                Разрешённые итоговые действия: use_tool, ask_user, publish_plan, complete_read_only.
                Не выполняй монтаж. В action=use_tool используй только tools с access=read_only.
                Tools с access=editing показаны только как каталог допустимых будущих
                expected_editing_tool для плана и до утверждения не вызываются.
                publish_plan делай только когда данных достаточно для конкретного,
                проверяемого и понятного пользователю плана.
                Для каждого шага монтажа укажи точное имя editing tool и точные нормализованные
                аргументы в expected_editing_arguments. Не включай туда свободное поле reason.
                Укажи тип достаточного доказательства в evidence_requirement и номера observations,
                доказывающих действие, в evidence_observation_sequences. Для шага без монтажа
                верни expected_editing_tool="" и пустой объект аргументов. Выбирай канал доказательства
                по смыслу действия: timeline для геометрии, frames/audio/transcript либо all для содержания.
                Если Task Brief имеет task kind read_only, ответь доказанно через complete_read_only:
                не создавай план и Agent Draft.
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
                inspect_timeline_integrity и inspect_range для проверки фактического состояния,
                gaps/overlaps/link-групп и важных новых склеек/изменённых мест.
                Если обнаружена ошибка в рамках утверждённого плана, можешь исправить её
                editing tool и затем снова проверить. Если исправление выходит за рамки
                утверждённого плана, сначала спроси пользователя.
                complete_task выбирай только после фактической проверки результата.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        return
            """
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
        AgentModelTurnRequest request,
        AgentToolAccess? toolAccess = null)
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
                description = step.Description,
                expected_editing_tool = step.ExpectedEditingTool,
                expected_editing_arguments = step.ExpectedEditingArguments,
                evidence_requirement = ToSchemaValue(step.EvidenceRequirement),
                evidence_observation_sequences = step.EvidenceObservationSequences,
                expected_effect = step.ExpectedEffect,
                protected_invariants = step.ProtectedInvariants,
                verification_checks = step.VerificationChecks
                }).ToArray(),
                approved = plan.ApprovedAt is not null
            };

        var tools = request.AvailableTools
            .Where(tool => toolAccess is null || tool.Access == toolAccess)
            .Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                access = tool.Access.ToString().ToLowerInvariant(),
                input_schema = tool.InputSchema
            })
            .ToArray();

        var observations = SelectObservationsForPrompt(request)
            .Select(observation => new
            {
                sequence = observation.Sequence,
                tool_name = observation.ToolName,
                status = observation.Status.ToString().ToLowerInvariant(),
                summary = observation.Summary,
                error_code = observation.ErrorCode,
                data = CompactObservationForPrompt(observation.Data)
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
            task_brief = request.Task.Brief,
            evidence_ledger = request.Task.Evidence.Select(item => new
            {
                item.Id,
                item.Sequence,
                channel = item.Channel.ToString().ToLowerInvariant(),
                item.ToolName,
                item.TargetId,
                item.SourceRevision,
                item.StartSeconds,
                item.EndSeconds,
                item.Summary,
                item.Facts,
                item.ArtifactReference
            }),
            conversation,
            available_tools = tools,
            observations,
            instruction =
                "Choose exactly one next action that is valid for the current mode."
        };

        return JsonSerializer.Serialize(payload, TurnPayloadJsonOptions);
    }

    private static ImmutableArray<AgentModelObservation> SelectObservationsForPrompt(
        AgentModelTurnRequest request)
    {
        if (request.Observations.IsDefaultOrEmpty)
        {
            return [];
        }

        var pinned = request.Task.Plan?.Steps
            .SelectMany(step => step.EvidenceObservationSequences.IsDefault
                ? []
                : step.EvidenceObservationSequences)
            .ToHashSet() ?? [];
        var selected = new Dictionary<int, AgentModelObservation>();
        var characters = 0;

        void TryAdd(AgentModelObservation observation, bool required)
        {
            if (selected.ContainsKey(observation.Sequence))
            {
                return;
            }

            var size = EstimatePromptObservationCharacters(observation);
            if (!required && characters + size > MaximumObservationPromptCharacters)
            {
                return;
            }

            selected[observation.Sequence] = observation;
            characters += size;
        }

        foreach (var observation in request.Observations.Where(observation =>
                     pinned.Contains(observation.Sequence) ||
                     string.Equals(
                         observation.ToolName,
                         "inspect_editor_context",
                         StringComparison.OrdinalIgnoreCase)))
        {
            TryAdd(observation, required: true);
        }

        foreach (var observation in request.Observations
                     .OrderByDescending(observation => observation.Sequence))
        {
            TryAdd(observation, required: false);
        }

        return selected.Values
            .OrderBy(observation => observation.Sequence)
            .ToImmutableArray();
    }

    private static int EstimatePromptObservationCharacters(
        AgentModelObservation observation)
        => observation.ToolName.Length +
           observation.Summary.Length +
           (observation.ErrorCode?.Length ?? 0) +
           Math.Min(
               observation.Data?.GetRawText().Length ?? 0,
               20_000);

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
            "complete_read_only" => AgentModelDecision.CompleteReadOnly(
                ReadRequiredString(root, "completion_summary"),
                progress),
            "begin_verification" => AgentModelDecision.BeginVerification(progress),
            "complete_task" => AgentModelDecision.CompleteTask(
                ReadRequiredString(root, "completion_summary"),
                progress),
            _ => throw new InvalidOperationException(
                $"Agent model returned unknown action '{action}'.")
        };
    }

    private static JsonElement GetDecisionSchema(AgentModelTurnMode mode)
        => mode switch
        {
            AgentModelTurnMode.Planning => InvestigationDecisionSchema,
            AgentModelTurnMode.Execution => ExecutionDecisionSchema,
            AgentModelTurnMode.Verification => LegacyVerificationDecisionSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static JsonElement? CompactObservationForPrompt(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value ||
            value.GetRawText().Length <= 20_000)
        {
            return data?.Clone();
        }

        var retained = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "channel", "project_revision", "sequence_id", "sequence_revision",
                     "revision", "source_revision", "draft_revision", "target",
                     "start_seconds", "end_seconds", "detail", "truncated",
                     "artifact_reference", "recommended_next_inspection", "next_cursor",
                     "total_matches", "gap_count", "overlap_count", "link_issue_count",
                     "edit_count"
                 })
        {
            if (value.TryGetProperty(name, out var property))
            {
                retained[name] = property.Clone();
            }
        }

        retained["observation_data_omitted"] = true;
        retained["omitted_character_count"] = value.GetRawText().Length;
        retained["recommended_next_inspection"] = retained.GetValueOrDefault("recommended_next_inspection") ??
                                                   "Request a narrower or paginated inspection.";
        return AgentToolJson.ToElement(retained);
    }

    private static AgentTaskBrief ParseTaskBrief(JsonElement root)
    {
        var kind = ReadRequiredString(root, "task_kind") switch
        {
            "read_only" => AgentTaskKind.ReadOnly,
            "edit" => AgentTaskKind.Edit,
            "mixed" => AgentTaskKind.Mixed,
            var value => throw new InvalidOperationException(
                $"Agent model returned unknown task kind '{value}'.")
        };

        return AgentTaskBrief.Create(
            kind,
            ReadRequiredString(root, "goal"),
            ReadRequiredString(root, "scope"),
            ReadStringArray(root, "protected_elements", 24),
            ReadStringArray(root, "constraints", 24),
            ReadStringArray(root, "acceptance_criteria", 24),
            ReadStringArray(root, "assumptions", 24),
            ReadStringArray(root, "missing_information", 24));
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
                    description,
                    ReadString(item, "expected_editing_tool"),
                    item.TryGetProperty("evidence_observation_sequences", out var evidenceElement) &&
                    evidenceElement.ValueKind == JsonValueKind.Array
                        ? evidenceElement.EnumerateArray()
                            .Where(value => value.TryGetInt32(out _))
                            .Select(value => value.GetInt32())
                            .Where(value => value > 0)
                            .Distinct()
                            .ToImmutableArray()
                        : ImmutableArray<int>.Empty,
                    item.TryGetProperty("expected_editing_arguments", out var argumentsElement) &&
                    argumentsElement.ValueKind == JsonValueKind.Object
                        ? AgentActionApproval.NormalizeArguments(argumentsElement)
                        : AgentActionApproval.NormalizeArguments(AgentToolJson.ParseObject("{}")),
                    ParseEvidenceRequirement(ReadString(item, "evidence_requirement")),
                    ReadString(item, "expected_effect"),
                    ReadStringArray(item, "protected_invariants", 24),
                    ReadStringArray(item, "verification_checks", 24)));
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

    private static ImmutableArray<string> ReadStringArray(
        JsonElement element,
        string propertyName,
        int maximumItems)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .Take(maximumItems)
                .ToImmutableArray()
            : ImmutableArray<string>.Empty;

    private static AgentEvidenceRequirement ParseEvidenceRequirement(string value)
        => value.ToLowerInvariant() switch
        {
            "frames" => AgentEvidenceRequirement.Frames,
            "audio" => AgentEvidenceRequirement.Audio,
            "transcript" => AgentEvidenceRequirement.Transcript,
            "all" => AgentEvidenceRequirement.All,
            _ => AgentEvidenceRequirement.Timeline
        };

    private static string ToSchemaValue(AgentEvidenceRequirement requirement)
        => requirement.ToString().ToLowerInvariant();
}
