using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Planning;
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

        if (request.Directive == AgentModelTurnDirective.PublishPlanFromExistingEvidence)
        {
            return await GeneratePublishedPlanAsync(
                request,
                "Формирую план по уже собранным доказательствам.",
                cancellationToken).ConfigureAwait(false);
        }

        var systemPrompt = BuildSystemPrompt();
        var turnPayload = BuildTurnPayload(request, AgentToolAccess.ReadOnly);
        var startedAt = DateTimeOffset.UtcNow;

        _debugLog.Write(new AgentDebugLogEntry(
            startedAt,
            "ai_server_agent_model",
            "request",
            request.Task.Id,
            request.Task.Phase.ToString(),
            request.TurnIndex,
            "Sending planning turn to Kadr AI Server.",
            $"payload_characters={turnPayload.Length}; tools={request.AvailableTools.Length}; observations={request.Observations.Length}; conversation={request.Conversation.Length}"));

        try
        {
            var raw = await _aiServer.RunAgentStructuredTurnAsync(
                AiServerAgentSchemas.InvestigationDecision,
                systemPrompt,
                turnPayload,
                cancellationToken,
                think: true,
                maxTokens: 2048,
                reasoningTokens: 1024).ConfigureAwait(false);

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
            Не вызывай инструменты и не добавляй действия сверх Task Brief. Верни только
            editing-шаги и только JSON по schema. Не придумывай IDs, observations или
            таймкоды. Для content_discovery одного timeline недостаточно: выбор содержимого
            должен поддерживаться всеми доступными каналами; если доступны frames, audio
            и transcript, evidence_requirement обязан быть all. Отличай сюжетные
            подписи времени/места/персонажей рядом с диалогом от производственных титров
            и отдельных карточек. Холодное вступление, эпилог и анонс могут менять порядок
            частей, поэтому края файла ничего не классифицируют. Приложение само добавит
            ограничения, защищаемые инварианты и проверки: не возвращай эти поля.
            Каждый текст короче 160 символов; не повторяй Task Brief, evidence и schema.
            Не раскрывай рассуждения.
            """ +
            AgentPromptPolicy.AuthorityAndEvidence +
            AgentPromptPolicy.PublishedPlan;
        var planObservations = SelectObservationsForPrompt(request);
        var referencableEvidence = request.Task.Evidence
            .Where(evidence =>
                evidence.SourceRevision == request.Task.SourceSequenceRevision &&
                evidence.Capabilities != AgentEvidenceCapabilities.None)
            .Select(evidence => evidence.Sequence)
            .ToArray();
        var editingTools = request.AvailableTools
            .Where(tool => tool.Access == AgentToolAccess.Editing)
            .Select(tool => tool.Name)
            .ToArray();
        var planPayload = BuildPlanPayload(request, planObservations);
        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            AiServerAgentSchemas.CreatePublishedPlan(editingTools),
            prompt,
            planPayload,
            cancellationToken,
            think: false,
            maxTokens: 4096,
            reasoningTokens: null).ConfigureAwait(false);
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
        var decision = ParsePlanDecision(document.RootElement, progress);
        if (decision.Plan is null)
        {
            return decision;
        }
        var structurallyNormalizedPlan = NormalizeAtomicRippleDeleteSteps(
            decision.Plan,
            editingTools.Contains("ripple_delete_ranges", StringComparer.OrdinalIgnoreCase));
        var allowedEvidence = referencableEvidence.ToHashSet();
        var brief = request.Task.Brief;
        var protectedInvariants = (brief?.ProtectedElements ?? [])
            .Add("Исходная последовательность не изменяется.")
            .Add("Материал вне утверждённых диапазонов сохраняется.")
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        var verificationChecks = ImmutableArray.Create(
            "Persistent receipt точно соответствует tool и нормализованным аргументам.",
            "Source revision, draft identity и целостность таймлайна сохранены.",
            "Обязательные post-edit probes завершены детерминированно.");
        var normalizedPlan = structurallyNormalizedPlan with
        {
            Constraints = structurallyNormalizedPlan.Constraints.IsDefaultOrEmpty
                ? brief?.Constraints ?? ImmutableArray<string>.Empty
                : structurallyNormalizedPlan.Constraints,
            Steps = structurallyNormalizedPlan.Steps
                .Select(step => step with
                {
                    EvidenceObservationSequences = step.EvidenceObservationSequences
                        .Where(allowedEvidence.Contains)
                        .Concat(FindRelevantEvidenceSequences(
                            step,
                            request.Task,
                            allowedEvidence))
                        .Distinct()
                        .ToImmutableArray(),
                    ExpectedEffect = string.IsNullOrWhiteSpace(step.ExpectedEffect)
                        ? $"Команда {step.ExpectedEditingTool ?? "editing tool"} выполняется один раз в Agent Draft."
                        : step.ExpectedEffect,
                    ProtectedInvariants = step.ProtectedInvariants.IsDefaultOrEmpty
                        ? protectedInvariants
                        : step.ProtectedInvariants,
                    VerificationChecks = step.VerificationChecks.IsDefaultOrEmpty
                        ? verificationChecks
                        : step.VerificationChecks
                })
                .ToImmutableArray()
        };
        return decision with { Plan = normalizedPlan };
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
            investigation_strategy: timeline_only — задача решается только структурой
            проекта/таймлайна; direct_target — пользователь уже дал точные координаты,
            стабильные IDs или просит применить действие ко всему явно заданному scope;
            content_discovery — надо найти названный смысловой блок, событие, момент,
            реплику или визуально/звуково определяемую часть, а её координаты неизвестны.
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
            AiServerAgentSchemas.TaskBrief,
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
            Ты независимый семантический критик плана универсального монтажного агента.
            Детерминированная проверка tool names, schemas, атомарности и evidence links
            уже пройдена. Не исправляй план и не вызывай инструменты. Не требуй, чтобы
            editing tool находился в evidence_ledger: поддержку команды уже проверил
            каталог tools. Не требуй research, создания Agent Draft или verification как
            plan_steps — приложение выполняет их автоматически.

            Не доверяй progress, названиям шагов, description, expected_effect и готовому
            смысловому ярлыку planner-а. Для каждого шага заново сопоставь Task Brief с
            measured factual summaries и заполни отдельный step_assessment. Одинаково ищи как
            подтверждающие, так и противоречащие факты; противоречия между перекрывающимися
            vision/transcript/audio observations нельзя усреднять или игнорировать.

            Отклони план, если фактическое содержание evidence не доказывает выбранные
            диапазоны, нарушены Task Brief/protected invariants, диапазон частично включает
            защищённый материал, выдуманы смысловые выводы либо выполнена лишь часть цели.
            Structural eligibility координаты необходима, но не доказывает её смысл.
            Сначала явно отметь, что проверены противоречащие факты и соседний защищаемый
            контекст. accepted=true допустим только когда для каждого step_assessment одновременно:
            evidence_supports_action=true, protected_content_detected=false,
            exact_arguments_supported=true, counterevidence_checked=true,
            adjacent_context_checked=true и unresolved_contradictions пуст.
            Не навязывай конкретное творческое решение сверх запроса. Не раскрывай
            внутренние рассуждения. Верни только JSON по schema.
            """ +
            AgentPromptPolicy.AuthorityAndEvidence +
            AgentPromptPolicy.PublishedPlan +
            AgentPromptPolicy.VerificationAuthority;

        var reviewPlan = new
        {
            request.Plan.Objective,
            request.Plan.Summary,
            constraints = request.Plan.Constraints.IsDefault
                ? ImmutableArray<string>.Empty
                : request.Plan.Constraints,
            steps = request.Plan.Steps.Select((step, index) => new
            {
                order = index + 1,
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
        var reviewEvidence = AgentEvidencePromptBuilder.ForReview(request);
        var reviewEvidenceSequences = reviewEvidence
            .Select(item => item.Evidence.Sequence)
            .ToHashSet();
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
            deterministic_validation = "passed",
            source_duration_seconds = BuildSourceDurationSeconds(request.Task),
            eligible_content_boundaries = BuildEligibleContentBoundaries(request.Task),
            evidence_ledger = reviewEvidence.Select(item => new
            {
                selection_reason = item.SelectionReason,
                sequence = item.Evidence.Sequence,
                tool_name = item.Evidence.ToolName,
                target_id = item.Evidence.TargetId,
                source_revision = item.Evidence.SourceRevision,
                capabilities = item.Evidence.Capabilities.ToString(),
                start_seconds = item.Evidence.StartSeconds,
                end_seconds = item.Evidence.EndSeconds,
                boundary_at_seconds = item.Evidence.BoundarySeconds,
                summary = item.Evidence.ToolName.Equals(
                    "inspect_boundary",
                    StringComparison.OrdinalIgnoreCase)
                    ? $"See eligible_content_boundaries observation {item.Evidence.Sequence}."
                    : item.Summary
            }),
            plan = reviewPlan,
            observations = request.Observations
                .Where(observation => reviewEvidenceSequences.Contains(observation.Sequence))
                .Select(observation => new
                {
                    observation.Sequence,
                    observation.ToolName,
                    status = observation.Status.ToString().ToLowerInvariant(),
                    observation.ErrorCode
                }),
            conversation = request.Conversation
        }, TurnPayloadJsonOptions);

        var raw = await _aiServer.RunAgentStructuredTurnAsync(
            AiServerAgentSchemas.PlanReview,
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
        var issues = ReadStringArray(root, "issues", 12).ToBuilder();
        var assessmentsByOrder = new Dictionary<int, JsonElement>();
        if (root.TryGetProperty("step_assessments", out var assessments) &&
            assessments.ValueKind == JsonValueKind.Array)
        {
            foreach (var assessment in assessments.EnumerateArray())
            {
                if (assessment.ValueKind == JsonValueKind.Object &&
                    assessment.TryGetProperty("step_order", out var orderValue) &&
                    orderValue.TryGetInt32(out var order) &&
                    order > 0)
                {
                    assessmentsByOrder[order] = assessment.Clone();
                }
            }
        }

        for (var order = 1; order <= request.Plan.Steps.Length; order++)
        {
            if (!assessmentsByOrder.TryGetValue(order, out var assessment))
            {
                accepted = false;
                issues.Add($"Independent review omitted step assessment {order}.");
                continue;
            }

            var evidenceSupports = ReadBoolean(assessment, "evidence_supports_action");
            var protectedContent = ReadBoolean(assessment, "protected_content_detected");
            var exactArguments = ReadBoolean(assessment, "exact_arguments_supported");
            var counterevidenceChecked = ReadBoolean(assessment, "counterevidence_checked");
            var adjacentContextChecked = ReadBoolean(assessment, "adjacent_context_checked");
            var contradictions = ReadStringArray(
                assessment,
                "unresolved_contradictions",
                8);
            if (!evidenceSupports || protectedContent || !exactArguments ||
                !counterevidenceChecked || !adjacentContextChecked ||
                !contradictions.IsEmpty)
            {
                accepted = false;
                var assessmentSummary = ReadRequiredString(assessment, "summary");
                issues.Add($"Step {order}: {assessmentSummary}");
                issues.AddRange(contradictions.Select(item => $"Step {order} contradiction: {item}"));
            }
        }

        return new AgentPlanReview(
            accepted,
            summary,
            issues.Distinct(StringComparer.Ordinal).Take(12).ToImmutableArray());
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
            Поле deterministic_result уже содержит окончательное решение политики:
            accepted обязано точно совпасть с deterministic_result.is_valid. Кратко опиши
            фактически выполненные изменения и проверки структуры, кадров, звука и текста.
            Не раскрывай внутренние рассуждения. Верни только JSON по schema.
            """ + AgentPromptPolicy.VerificationAuthority;
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
            deterministic_result = new
            {
                is_valid = request.DeterministicResult.IsValid,
                request.DeterministicResult.Summary,
                request.DeterministicResult.Issues
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
            AiServerAgentSchemas.VerificationReport,
            prompt,
            payload,
            cancellationToken,
            think: false,
            maxTokens: 1536,
            reasoningTokens: null).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new AgentVerificationReport(
            request.DeterministicResult.IsValid,
            ReadRequiredString(root, "summary"),
            ReadStringArray(root, "issues", 12));
    }

    private static string BuildSystemPrompt()
    {
        return
            """
            Ты универсальный монтажный AI-агент Kadr Studio. Сейчас только этап
            ИССЛЕДОВАНИЯ И ПЛАНА: монтаж запрещён. На каждом ходе выбери ровно одно
            действие use_tool, ask_user, publish_plan или complete_read_only и верни только
            JSON по schema. use_tool может вызывать только access=read_only; editing tools
            недоступны на этом ходе. После publish_plan отдельный издатель получит каталог
            editing tools и построит будущие действия по их точным schemas.

            Учитывай Task Brief, диалог и уже полученные observations. Не спрашивай то,
            что можно доказать tools. Не повторяй эквивалентные измерения. Если полного
            inspect_content_overview ещё нет для content_discovery, покрой весь scope
            coarse-to-fine; если coverage_complete уже есть, используй его и исследуй
            только сильные непроверенные кандидаты. Холодное вступление, эпилог и анонс
            могут менять обычный порядок частей.

            publish_plan разрешён только при достаточных typed evidence и точных аргументах.
            Для task kind read_only используй complete_read_only с доказанным ответом и не
            создавай план. progress описывает только следующее действие без найденных
            смысловых блоков, выводов или предполагаемых таймкодов; UI всё равно заменит
            его детерминированным статусом. Не объявляй исследование завершённым до
            публикации плана.
            Для неиспользуемых schema-полей верни пустую строку, объект или массив.
            """ +
            AgentPromptPolicy.AuthorityAndEvidence +
            AgentPromptPolicy.PublishedPlan;
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

        var evidenceContext = AgentEvidencePromptBuilder.ForPlanning(request.Task);
        var evidenceSequences = evidenceContext
            .Select(item => item.Evidence.Sequence)
            .ToHashSet();
        var recordedEvidenceSequences = request.Task.Evidence
            .Where(evidence =>
                evidence.SourceRevision == request.Task.SourceSequenceRevision ||
                evidence.SourceRevision is null)
            .Select(evidence => evidence.Sequence)
            .ToHashSet();
        var observations = SelectObservationsForPrompt(request)
            .Select(observation => new
            {
                sequence = observation.Sequence,
                tool_name = observation.ToolName,
                status = observation.Status.ToString().ToLowerInvariant(),
                summary = evidenceSequences.Contains(observation.Sequence)
                    ? $"See evidence_ledger E{observation.Sequence}."
                    : recordedEvidenceSequences.Contains(observation.Sequence)
                        ? CompactText(observation.Summary, 900)
                        : observation.Summary,
                error_code = observation.ErrorCode,
                data = recordedEvidenceSequences.Contains(observation.Sequence)
                    ? null
                    : CompactObservationForPrompt(observation.Data)
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
            mode = "planning",
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
            source_duration_seconds = BuildSourceDurationSeconds(request.Task),
            eligible_content_boundaries = BuildEligibleContentBoundaries(request.Task),
            observed_text_activity_regions = BuildObservedTextActivityRegions(request.Task.Evidence),
            evidence_ledger = evidenceContext.Select(entry => new
            {
                selection_reason = entry.SelectionReason,
                entry.Evidence.Id,
                entry.Evidence.Sequence,
                channel = entry.Evidence.Channel.ToString().ToLowerInvariant(),
                entry.Evidence.ToolName,
                entry.Evidence.TargetId,
                entry.Evidence.SourceRevision,
                capabilities = entry.Evidence.Capabilities.ToString(),
                entry.Evidence.StartSeconds,
                entry.Evidence.EndSeconds,
                entry.Evidence.BoundarySeconds,
                summary = entry.Summary,
                entry.Evidence.ArtifactReference
            }),
            conversation,
            available_tools = tools,
            observations,
            instruction =
                "Choose exactly one next action that is valid for the current mode."
        };

        return JsonSerializer.Serialize(payload, TurnPayloadJsonOptions);
    }

    private static string BuildPlanPayload(
        AgentModelTurnRequest request,
        ImmutableArray<AgentModelObservation> observations)
    {
        var evidenceContext = AgentEvidencePromptBuilder.ForPlanning(request.Task);
        var payload = new
        {
            task = new
            {
                request.Task.Id,
                request.Task.UserRequest,
                request.Task.SourceSequenceId,
                request.Task.SourceSequenceRevision,
                brief = request.Task.Brief
            },
            source_duration_seconds = BuildSourceDurationSeconds(request.Task),
            eligible_content_boundaries = BuildEligibleContentBoundaries(request.Task),
            observed_text_activity_regions = BuildObservedTextActivityRegions(request.Task.Evidence),
            editing_tools = request.AvailableTools
                .Where(tool => tool.Access == AgentToolAccess.Editing)
                .Select(tool => new
                {
                    name = tool.Name,
                    description = tool.Description,
                    input_schema = tool.InputSchema
                })
                .ToArray(),
            successful_evidence = evidenceContext
                .Select(entry => new
                {
                    sequence = entry.Evidence.Sequence,
                    tool_name = entry.Evidence.ToolName,
                    target_id = entry.Evidence.TargetId,
                    source_revision = entry.Evidence.SourceRevision,
                    start_seconds = entry.Evidence.StartSeconds,
                    end_seconds = entry.Evidence.EndSeconds,
                    boundary_at_seconds = entry.Evidence.BoundarySeconds,
                    capabilities = entry.Evidence.Capabilities.ToString(),
                    selected_for = entry.SelectionReason,
                    summary = entry.Evidence.ToolName.Equals(
                        "inspect_boundary",
                        StringComparison.OrdinalIgnoreCase)
                        ? $"See eligible_content_boundaries observation {entry.Evidence.Sequence}."
                        : entry.Summary
                })
                .ToArray(),
            prior_plan_feedback = observations
                .Where(observation =>
                    observation.Status == AgentToolResultStatus.Rejected &&
                    observation.ToolName is "review_plan" or "publish_plan")
                .OrderByDescending(observation => observation.Sequence)
                .Take(4)
                .Select(observation => new
                {
                    sequence = observation.Sequence,
                    error_code = observation.ErrorCode,
                    feedback = CompactText(observation.Summary, 6_000)
                })
                .ToArray()
        };
        return JsonSerializer.Serialize(payload, TurnPayloadJsonOptions);
    }

    private static string CompactText(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..(maximumCharacters - 1)].TrimEnd() + "…";

    private static string[] BuildObservedTextActivityRegions(
        IEnumerable<AgentEvidenceRecord> evidence)
        => AgentObservedTextActivityIndexer.Build(evidence)
            .Take(8)
            .Select(region =>
            {
                var start = region.StartSeconds.ToString(
                    "0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
                var end = region.EndSeconds.ToString(
                    "0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
                return $"{start}–{end}s: {region.Facts.Length} observed OCR facts: " +
                       string.Join("; ", region.Facts.Take(4).Select(item => CompactText(item.Text, 240)));
            })
            .ToArray();

    private static object[] BuildEligibleContentBoundaries(AgentTaskState task)
        => task.Evidence
            .Where(evidence =>
                evidence.SourceRevision == task.SourceSequenceRevision &&
                evidence.TargetId == task.SourceSequenceId &&
                evidence.ToolName.Equals("inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                evidence.Capabilities != AgentEvidenceCapabilities.None &&
                evidence.StartSeconds is not null &&
                evidence.EndSeconds is not null)
            // Automatic candidate probes are appended in deterministic signal
            // priority order. Preserve that provenance instead of biasing a
            // small planner toward the smallest timestamp.
            .OrderBy(evidence => evidence.Sequence)
            .Select(evidence => (object)new
            {
                observation_sequence = evidence.Sequence,
                at_seconds = Math.Round(
                    evidence.BoundarySeconds ??
                    (evidence.StartSeconds!.Value + evidence.EndSeconds!.Value) / 2d,
                    3),
                structural_eligibility_only = true,
                capabilities = evidence.Capabilities.ToString(),
                factual_summary = CompactText(evidence.Summary, 1_200)
            })
            .ToArray();

    private static double? BuildSourceDurationSeconds(AgentTaskState task)
    {
        var duration = task.Evidence
            .Where(evidence =>
                evidence.TargetId == task.SourceSequenceId &&
                evidence.SourceRevision == task.SourceSequenceRevision &&
                evidence.ToolName.Equals(
                    "inspect_content_overview",
                    StringComparison.OrdinalIgnoreCase) &&
                evidence.EndSeconds is not null)
            .Select(evidence => evidence.EndSeconds!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return duration > 0 ? Math.Round(duration, 3) : null;
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
                     IsStructuralPlanningObservation(observation.ToolName)))
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

    private static bool IsStructuralPlanningObservation(string toolName)
        => toolName is "inspect_editor_context" or
            "inspect_project" or
            "inspect_timeline" or
            "inspect_timeline_integrity" or
            "inspect_content_overview";

    private static int EstimatePromptObservationCharacters(
        AgentModelObservation observation)
        => observation.ToolName.Length +
           observation.Summary.Length +
           (observation.ErrorCode?.Length ?? 0) +
           Math.Min(
               observation.Data?.GetRawText().Length ?? 0,
               20_000);

    private static AgentPlanDraft NormalizeAtomicRippleDeleteSteps(
        AgentPlanDraft plan,
        bool multiRangeToolAvailable)
    {
        if (!multiRangeToolAvailable)
        {
            return plan;
        }

        var rippleSteps = plan.Steps
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => item.Step.ExpectedEditingTool is not null &&
                           (item.Step.ExpectedEditingTool.Equals(
                                "ripple_delete_range",
                                StringComparison.OrdinalIgnoreCase) ||
                            item.Step.ExpectedEditingTool.Equals(
                                "ripple_delete_ranges",
                                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (rippleSteps.Length <= 1)
        {
            return plan;
        }

        var ranges = new List<(double Start, double End)>();
        foreach (var item in rippleSteps)
        {
            if (!TryReadRippleDeleteRanges(item.Step, ranges))
            {
                // Malformed arguments remain untouched so the deterministic
                // validator can reject them instead of guessing coordinates.
                return plan;
            }
        }

        var mergedRanges = new List<(double Start, double End)>();
        foreach (var range in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (mergedRanges.Count == 0 ||
                range.Start > mergedRanges[^1].End + 0.000001d)
            {
                mergedRanges.Add(range);
                continue;
            }

            var previous = mergedRanges[^1];
            mergedRanges[^1] = (previous.Start, Math.Max(previous.End, range.End));
        }

        var requirements = rippleSteps
            .Select(item => item.Step.EvidenceRequirement)
            .Distinct()
            .ToArray();
        var nonTimelineRequirements = requirements
            .Where(requirement => requirement != AgentEvidenceRequirement.Timeline)
            .ToArray();
        var combinedRequirement = requirements.Contains(AgentEvidenceRequirement.All) ||
                                  nonTimelineRequirements.Length > 1
            ? AgentEvidenceRequirement.All
            : nonTimelineRequirements.SingleOrDefault();
        var first = rippleSteps[0].Step;
        var combined = first with
        {
            Title = "Атомарно удалить утверждённые диапазоны",
            Description =
                "Удалить все доказанные непересекающиеся диапазоны одной командой в исходных координатах Agent Draft.",
            ExpectedEditingTool = "ripple_delete_ranges",
            ExpectedEditingArguments = AgentToolJson.ToElement(new
            {
                ranges = mergedRanges.Select(range => new
                {
                    start_seconds = range.Start,
                    end_seconds = range.End
                }).ToArray()
            }),
            EvidenceObservationSequences = rippleSteps
                .SelectMany(item => item.Step.EvidenceObservationSequences.IsDefault
                    ? []
                    : item.Step.EvidenceObservationSequences)
                .Distinct()
                .ToImmutableArray(),
            EvidenceRequirement = combinedRequirement,
            ExpectedEffect = "Все утверждённые диапазоны удаляются одной транзакцией без сдвига последующих координат.",
            ProtectedInvariants = rippleSteps
                .SelectMany(item => item.Step.ProtectedInvariants.IsDefault
                    ? []
                    : item.Step.ProtectedInvariants)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray(),
            VerificationChecks = rippleSteps
                .SelectMany(item => item.Step.VerificationChecks.IsDefault
                    ? []
                    : item.Step.VerificationChecks)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray()
        };

        var rippleIndexes = rippleSteps.Select(item => item.Index).ToHashSet();
        var firstRippleIndex = rippleSteps[0].Index;
        var normalizedSteps = ImmutableArray.CreateBuilder<AgentPlanStepDraft>(
            plan.Steps.Length - rippleSteps.Length + 1);
        for (var index = 0; index < plan.Steps.Length; index++)
        {
            if (index == firstRippleIndex)
            {
                normalizedSteps.Add(combined);
            }
            else if (!rippleIndexes.Contains(index))
            {
                normalizedSteps.Add(plan.Steps[index]);
            }
        }

        return plan with { Steps = normalizedSteps.ToImmutable() };
    }

    private static IEnumerable<int> FindRelevantEvidenceSequences(
        AgentPlanStepDraft step,
        AgentTaskState task,
        IReadOnlySet<int> allowedEvidence)
    {
        var ranges = ReadPlanStepRanges(step);
        if (ranges.IsDefaultOrEmpty)
        {
            return [];
        }

        return task.Evidence
            .Where(evidence =>
                allowedEvidence.Contains(evidence.Sequence) &&
                evidence.TargetId == task.SourceSequenceId &&
                evidence.SourceRevision == task.SourceSequenceRevision &&
                (
                    evidence.BoundarySeconds is { } boundary &&
                    ranges.Any(range =>
                        Math.Abs(boundary - range.Start) <= 1d ||
                        Math.Abs(boundary - range.End) <= 1d)
                    ||
                    evidence.StartSeconds is { } evidenceStart &&
                    evidence.EndSeconds is { } evidenceEnd &&
                    ranges.Any(range =>
                        evidenceEnd >= range.Start &&
                        evidenceStart <= range.End)
                ))
            .OrderBy(evidence => evidence.Sequence)
            .Select(evidence => evidence.Sequence);
    }

    private static ImmutableArray<(double Start, double End)> ReadPlanStepRanges(
        AgentPlanStepDraft step)
    {
        if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            return [];
        }

        var ranges = ImmutableArray.CreateBuilder<(double Start, double End)>();
        if (TryReadFiniteRange(arguments, out var direct))
        {
            ranges.Add(direct);
        }
        if (arguments.TryGetProperty("ranges", out var rangeArray) &&
            rangeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rangeArray.EnumerateArray())
            {
                if (TryReadFiniteRange(item, out var range))
                {
                    ranges.Add(range);
                }
            }
        }
        return ranges.ToImmutable();
    }

    private static bool TryReadRippleDeleteRanges(
        AgentPlanStepDraft step,
        ICollection<(double Start, double End)> ranges)
    {
        if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            return false;
        }

        if (step.ExpectedEditingTool!.Equals(
                "ripple_delete_range",
                StringComparison.OrdinalIgnoreCase))
        {
            return TryReadFiniteRange(arguments, out var direct) && Add(direct);
        }

        if (!arguments.TryGetProperty("ranges", out var rangeArray) ||
            rangeArray.ValueKind != JsonValueKind.Array ||
            rangeArray.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var item in rangeArray.EnumerateArray())
        {
            if (!TryReadFiniteRange(item, out var range))
            {
                return false;
            }
            ranges.Add(range);
        }
        return true;

        bool Add((double Start, double End) range)
        {
            ranges.Add(range);
            return true;
        }
    }

    private static bool TryReadFiniteRange(
        JsonElement value,
        out (double Start, double End) range)
    {
        range = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("start_seconds", out var startValue) ||
            !startValue.TryGetDouble(out var start) ||
            !double.IsFinite(start) ||
            start < 0 ||
            !value.TryGetProperty("end_seconds", out var endValue) ||
            !endValue.TryGetDouble(out var end) ||
            !double.IsFinite(end) ||
            end <= start)
        {
            return false;
        }

        range = (start, end);
        return true;
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
            "complete_read_only" => AgentModelDecision.CompleteReadOnly(
                ReadRequiredString(root, "completion_summary"),
                progress),
            _ => throw new InvalidOperationException(
                $"Agent model returned unknown action '{action}'.")
        };
    }

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
        var strategy = ReadRequiredString(root, "investigation_strategy") switch
        {
            "direct_target" => AgentInvestigationStrategy.DirectTarget,
            "timeline_only" => AgentInvestigationStrategy.TimelineOnly,
            "content_discovery" => AgentInvestigationStrategy.ContentDiscovery,
            var value => throw new InvalidOperationException(
                $"Agent model returned unknown investigation strategy '{value}'.")
        };

        return AgentTaskBrief.Create(
            kind,
            ReadRequiredString(root, "goal"),
            ReadRequiredString(root, "scope"),
            ReadStringArray(root, "protected_elements", 24),
            ReadStringArray(root, "constraints", 24),
            ReadStringArray(root, "acceptance_criteria", 24),
            ReadStringArray(root, "assumptions", 24),
            ReadStringArray(root, "missing_information", 24),
            strategy);
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

    private static bool ReadBoolean(
        JsonElement element,
        string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.True;

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
