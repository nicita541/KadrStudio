using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Services.Agent;

internal static class AiServerAgentSchemas
{
    public static JsonElement InvestigationDecision { get; } = AgentToolJson.ParseObject(
        """
        {"type":"object","properties":{"action":{"type":"string","enum":["use_tool","ask_user","publish_plan","complete_read_only"]},"progress":{"type":["string","null"],"maxLength":600},"tool_name":{"type":["string","null"],"maxLength":100},"tool_arguments":{"type":["object","null"],"additionalProperties":true},"question":{"type":["string","null"],"maxLength":1000},"question_context":{"type":["string","null"],"maxLength":1600},"completion_summary":{"type":["string","null"],"maxLength":3000}},"required":["action","progress","tool_name","tool_arguments","question","question_context","completion_summary"],"additionalProperties":false}
        """);

    public static JsonElement PublishedPlan { get; } = AgentToolJson.ParseObject(
        """
        {
          "type":"object",
          "properties":{
            "plan_objective":{"type":"string","minLength":1,"maxLength":240},
            "plan_summary":{"type":"string","minLength":1,"maxLength":400},
            "plan_steps":{"type":"array","minItems":1,"maxItems":6,"items":{"type":"object","properties":{
              "title":{"type":"string","minLength":1,"maxLength":120},
              "description":{"type":"string","minLength":1,"maxLength":240},
              "expected_editing_tool":{"type":"string","maxLength":100},
              "expected_editing_arguments":{"type":"object","additionalProperties":true},
              "evidence_requirement":{"type":"string","enum":["timeline","frames","audio","transcript","all"]},
              "evidence_observation_sequences":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"integer","minimum":1}}
            },"required":["title","description","expected_editing_tool","expected_editing_arguments","evidence_requirement","evidence_observation_sequences"],"additionalProperties":false}}
          },
          "required":["plan_objective","plan_summary","plan_steps"],
          "additionalProperties":false
        }
        """);

    public static JsonElement CreatePublishedPlan(
        IEnumerable<string> editingToolNames)
    {
        var root = JsonNode.Parse(PublishedPlan.GetRawText())!.AsObject();
        var stepProperties = root["properties"]!["plan_steps"]!["items"]!["properties"]!.AsObject();
        var toolProperty = stepProperties["expected_editing_tool"]!.AsObject();
        toolProperty["enum"] = new JsonArray(
            editingToolNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());
        return JsonSerializer.SerializeToElement(root);
    }

    public static JsonElement TaskBrief { get; } = AgentToolJson.ParseObject(
        """
        {"type":"object","properties":{"task_kind":{"type":"string","enum":["read_only","edit","mixed"]},"investigation_strategy":{"type":"string","enum":["direct_target","timeline_only","content_discovery"]},"goal":{"type":"string","minLength":1,"maxLength":240},"scope":{"type":"string","minLength":1,"maxLength":240},"protected_elements":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":200}},"constraints":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":200}},"acceptance_criteria":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":200}},"assumptions":{"type":"array","maxItems":6,"items":{"type":"string","maxLength":200}},"missing_information":{"type":"array","maxItems":6,"items":{"type":"string","maxLength":200}}},"required":["task_kind","investigation_strategy","goal","scope","protected_elements","constraints","acceptance_criteria","assumptions","missing_information"],"additionalProperties":false}
        """);

    public static JsonElement PlanReview { get; } = AgentToolJson.ParseObject(
        """
        {"type":"object","properties":{"accepted":{"type":"boolean"},"summary":{"type":"string","minLength":1,"maxLength":1200},"issues":{"type":"array","items":{"type":"string","maxLength":500},"maxItems":12},"step_assessments":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object","properties":{"step_order":{"type":"integer","minimum":1},"evidence_supports_action":{"type":"boolean"},"protected_content_detected":{"type":"boolean"},"exact_arguments_supported":{"type":"boolean"},"counterevidence_checked":{"type":"boolean"},"adjacent_context_checked":{"type":"boolean"},"unresolved_contradictions":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":400}},"summary":{"type":"string","minLength":1,"maxLength":600}},"required":["step_order","evidence_supports_action","protected_content_detected","exact_arguments_supported","counterevidence_checked","adjacent_context_checked","unresolved_contradictions","summary"],"additionalProperties":false}}},"required":["accepted","summary","issues","step_assessments"],"additionalProperties":false}
        """);

    public static JsonElement VerificationReport { get; } = AgentToolJson.ParseObject(
        """
        {"type":"object","properties":{"accepted":{"type":"boolean"},"summary":{"type":"string","minLength":1},"issues":{"type":"array","items":{"type":"string"},"maxItems":12}},"required":["accepted","summary","issues"],"additionalProperties":false}
        """);
}
