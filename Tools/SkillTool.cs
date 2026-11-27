using Kite.Agent;
using Kite.Context;

namespace Kite.Tools;

internal static class SkillTool {
    public const string DefaultName = "skill";

    public static readonly ToolDefinition Definition = new(
        DefaultName,
        "Load a specialized skill when the task at hand matches one of the available skills in the system context.",
        ToolArgs.Schema("""
                        {
                          "type": "object",
                          "properties": {
                            "name": { "type": "string", "description": "The name of the skill to load from available_skills." }
                          },
                          "required": ["name"],
                          "additionalProperties": false
                        }
                        """));

    public static string Execute(ToolCall call, IReadOnlyList<Skill> skills) {
        using var document = ToolArgs.ParseObject(call.Arguments, DefaultName);
        var root = document.RootElement;
        ToolArgs.RejectUnknownProperties(root, DefaultName, "name");
        var name = ToolArgs.RequiredString(root, DefaultName, "name");

        if (string.IsNullOrWhiteSpace(name)) {
            throw new InvalidOperationException("skill name cannot be empty");
        }

        var skill = skills.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null || !skill.Auto) {
            throw new InvalidOperationException($"skill '{name}' not found or not available for automatic invocation");
        }

        return $"""
                <skill_content name="{skill.Name}">
                # Skill: {skill.Name}

                {skill.Content}

                Base directory for this skill: {skill.Directory}
                Relative paths in this skill are relative to this base directory.
                </skill_content>
                """;
    }
}