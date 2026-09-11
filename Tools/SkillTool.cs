using Kite.Context;
using System.Text.Json;

namespace Kite.Tools;

public static class SkillTool {
    public const string DefaultName = "skill";

    public static readonly ToolDefinition Definition = new(
        DefaultName,
        "Load a specialized skill when the task at hand matches one of the available skills in the system context.",
        JsonDocument.Parse("""
                           {
                             "type": "object",
                             "properties": {
                               "name": { "type": "string", "description": "The name of the skill to load from available_skills." }
                             },
                             "required": ["name"],
                             "additionalProperties": false
                           }
                           """).RootElement.Clone());

    public static string Execute(ToolCall call, string workspace) {
        string name;
        try {
            using var doc = JsonDocument.Parse(call.Arguments);
            name = doc.RootElement.GetProperty("name").GetString() ?? string.Empty;
        } catch (Exception) {
            return "error: invalid arguments for skill tool; 'name' is required.";
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return "error: skill name cannot be empty.";
        }

        var skill = Skills.Find(workspace, name);
        if (skill is null || !skill.Auto) {
            return $"error: skill '{name}' not found or not available for automatic invocation.";
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