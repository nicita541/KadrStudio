using System.Text.Json;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Inference;

public sealed class RoleStructuredReasoningService(
    AiServerOptions options,
    IWorkerGateway workers,
    ModelCapabilityGate modelGate)
{
    public async Task<RoleReasoningResult> RunAsync(
        RoleStructuredReasoningRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Schema.ValueKind != JsonValueKind.Object ||
            request.Context.ValueKind != JsonValueKind.Object)
            return RoleReasoningResult.Failure("invalid_request", "schema and context must be JSON objects.");
        if (string.IsNullOrWhiteSpace(request.Instruction))
            return RoleReasoningResult.Failure("invalid_request", "instruction is required.");
        var model = options.ResolvePlannerModel(request.Model ?? options.PlannerPublicModelAlias);
        var gate = await modelGate.CheckAsync(
            model.BackendModel,
            request.Role.ToString(),
            request.Profile.Trim(),
            request.RequireProduction,
            cancellationToken).ConfigureAwait(false);
        if (!gate.IsAllowed)
            return RoleReasoningResult.Failure("model_not_qualified", gate.Error);
        var modelWindow = Math.Min(
            gate.Manifest?.ContextWindowTokens ?? options.MaxPlannerContextTokens,
            options.MaxPlannerContextTokens);
        var window = Math.Clamp(request.ContextWindowTokens, 2_048, modelWindow);
        // Reserve a real 8K structured-output lane on the 32K workstation
        // window.  Input is measured with the model tokenizer before inference;
        // an oversized request is rejected for deterministic compaction and can
        // therefore never overflow llama.cpp's physical context.
        var outputCeiling = Math.Min(8_192, (int)Math.Floor(window * 0.25));
        var reserve = Math.Max(256, (int)Math.Floor(window * 0.10));
        var inputBudget = window - outputCeiling - reserve;
        var system = BuildRolePrompt(request.Role);
        var user = JsonSerializer.Serialize(new
        {
            instruction = request.Instruction.Trim(),
            context = request.Context
        });
        var schemaInstruction = "Return JSON matching this schema: " + request.Schema.GetRawText();
        int inputTokens;
        try
        {
            inputTokens = await workers.CountTokensAsync(
                model.BackendModel,
                system + "\n" + user + "\n" + schemaInstruction,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerUnavailableException exception)
        {
            return RoleReasoningResult.Failure("tokenizer_unavailable", exception.Message);
        }
        if (inputTokens > inputBudget)
            return RoleReasoningResult.Failure(
                "invalid_context_budget",
                $"Measured input uses {inputTokens} model tokens; stage budget is {inputBudget}.",
                inputTokens, inputBudget, outputCeiling, reserve);

        var outputBudget = Math.Min(
            outputCeiling,
            Math.Max(32, window - inputTokens - reserve));

        var analyzer = request.Role == ReasoningRole.Critic ? "critic" : "director";
        var parameters = JsonSerializer.SerializeToElement(new
        {
            schema = request.Schema,
            system,
            user,
            contextWindowTokens = window,
            maxTokens = Math.Min(Math.Clamp(request.MaxTokens, 32, 8_192), outputBudget),
            model = model.BackendModel,
            role = request.Role.ToString(),
            profile = request.Profile
        });
        WorkerJobResult? workerResult = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            workerResult = await workers.ExecuteAsync(
                new WorkerJob(Guid.NewGuid(), analyzer, "2", [], parameters),
                null,
                cancellationToken).ConfigureAwait(false);
            if (workerResult.IsSuccess) break;
            if (attempt == 3)
                return RoleReasoningResult.Failure(
                    string.IsNullOrWhiteSpace(workerResult.ErrorCode) ? "worker_failed" : workerResult.ErrorCode,
                    string.IsNullOrWhiteSpace(workerResult.Error) ? "Structured reasoning worker failed." : workerResult.Error,
                    inputTokens, inputBudget, outputBudget, reserve);
        }
        try
        {
            using var document = JsonDocument.Parse(workerResult!.ResultJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) ||
                !kind.ValueEquals("structured-reasoning") ||
                !root.TryGetProperty("content", out var content))
                return RoleReasoningResult.Failure(
                    "worker_invalid_json", "Structured reasoning worker returned an incompatible artifact.",
                    inputTokens, inputBudget, outputBudget, reserve);
            var raw = content.GetRawText();
            if (!StructuredOutputValidator.TryValidate(raw, request.Schema, out var normalized, out var errors))
                return RoleReasoningResult.Failure(
                    "worker_schema_violation",
                    "Constrained worker output failed server-side schema validation: " + string.Join(' ', errors.Take(3)),
                    inputTokens, inputBudget, outputBudget, reserve);
            return RoleReasoningResult.Success(
                normalized, inputTokens, inputBudget, outputBudget, reserve,
                root.TryGetProperty("attemptCount", out var attempts) ? attempts.GetInt32() : 1);
        }
        catch (JsonException exception)
        {
            return RoleReasoningResult.Failure(
                "worker_invalid_json", "Structured reasoning worker returned damaged JSON: " + exception.Message,
                inputTokens, inputBudget, outputBudget, reserve);
        }
    }

    private static string BuildRolePrompt(ReasoningRole role)
        => role switch
        {
            ReasoningRole.Director =>
                "You are the Director. Produce an EditorialBrief only. Separate measured facts, hypotheses and editorial intent.",
            ReasoningRole.RoughCut =>
                "You are the Rough Cut editor. Select semantic Keep/Remove/Reorder/SelectTake/InsertBroll decisions from supplied evidence only.",
            ReasoningRole.Critic =>
                "You are an independent montage Critic. Search for counterevidence, protected-content violations and continuity failures. Do not rewrite the graph.",
            _ =>
                $"You are the independent {role} editorial pass. Return only a bounded DraftPatch and do not perform unrelated passes."
        } +
        " Heavy artifacts and embeddings are external; treat only the supplied working set as context. " +
        "Never infer local video content from internet knowledge. Return constrained JSON only.";
}

public sealed record RoleReasoningResult(
    bool IsSuccess,
    string? Content,
    string? ErrorCode,
    string? Error,
    int InputTokens,
    int InputBudgetTokens,
    int OutputBudgetTokens,
    int ReserveTokens,
    int AttemptCount)
{
    public static RoleReasoningResult Success(
        string content, int input, int inputBudget, int outputBudget, int reserve, int attempts)
        => new(true, content, null, null, input, inputBudget, outputBudget, reserve, attempts);

    public static RoleReasoningResult Failure(
        string code, string error, int input = 0, int inputBudget = 0,
        int outputBudget = 0, int reserve = 0)
        => new(false, null, code, error, input, inputBudget, outputBudget, reserve, 0);
}
