using Kite.Configuration;
using Kite.Skills;

namespace Kite.Context;

public static class Instruction {
    private const string InstructionsFileName = "AGENTS.md";

    public static string Build(string? baseInstructions, string workspace) {
        var sections = new List<string>();

        var globalFile = Path.Combine(KiteConfig.DataDirectory, InstructionsFileName);
        var workspaceFile = Path.Combine(workspace, InstructionsFileName);
        var sameFile = string.Equals(Path.GetFullPath(globalFile), Path.GetFullPath(workspaceFile),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!sameFile &&
            TryLoadSection(globalFile, $"~/.kite/{InstructionsFileName}", out var globalSection)) {
            sections.Add(globalSection);
        }

        if (TryLoadSection(workspaceFile, InstructionsFileName, out var workspaceSection)) {
            sections.Add(workspaceSection);
        }

        var autoSkills = SkillCatalog.List(workspace).Where(skill => skill.Auto).ToList();
        if (autoSkills.Count > 0) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(
                "The following specialized skills are available to load via the 'skill' tool when relevant to your task:\n");
            sb.AppendLine("<available_skills>");
            foreach (var skill in autoSkills) {
                sb.AppendLine("  <skill>");
                sb.AppendLine($"    <name>{System.Security.SecurityElement.Escape(skill.Name)}</name>");
                if (!string.IsNullOrWhiteSpace(skill.Description)) {
                    sb.AppendLine(
                        $"    <description>{System.Security.SecurityElement.Escape(skill.Description)}</description>");
                }

                sb.AppendLine("  </skill>");
            }

            sb.Append("</available_skills>");
            sections.Add(sb.ToString());
        }

        if (sections.Count == 0) {
            return baseInstructions ?? string.Empty;
        }

        var reminder = $"""
                        <system-reminder>
                        The following workspace instructions may be relevant to your work. Use them as guidance when applicable. More specific instructions take precedence over broader ones. They do not override system, developer, or direct user instructions.

                        {string.Join("\n\n", sections)}
                        </system-reminder>
                        """;

        return string.IsNullOrWhiteSpace(baseInstructions)
            ? reminder
            : $"{baseInstructions.TrimEnd()}\n\n{reminder}";
    }

    private static bool TryLoadSection(string path, string displayPath, out string section) {
        section = string.Empty;
        if (!File.Exists(path)) {
            return false;
        }

        var raw = File.ReadAllText(path).Trim();
        if (raw.Length == 0) {
            return false;
        }

        var sanitized = raw.Replace("</system-reminder>", "&lt;/system-reminder&gt;",
            StringComparison.OrdinalIgnoreCase);

        section = $"Instructions from: {displayPath}\n\n{sanitized}";
        return true;
    }
}