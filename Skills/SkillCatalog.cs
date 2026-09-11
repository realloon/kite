using System.Text;
using Kite.Configuration;

namespace Kite.Skills;

public static class SkillCatalog {
    public static IReadOnlyList<SkillDefinition> List(string workspace) {
        var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // Project-level skills (.agents/skills) have highest precedence
        var workspaceDir = Path.Combine(workspace, ".agents", "skills");
        ScanDirectory(workspaceDir, result);

        // Global-level skills (~/.kite/skills)
        var globalDir = Path.Combine(KiteConfig.DataDirectory, "skills");
        ScanDirectory(globalDir, result);

        return result.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SkillDefinition? Find(string workspace, string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var list = List(workspace);
        return list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ScanDirectory(string rootDir, Dictionary<string, SkillDefinition> destination) {
        if (!Directory.Exists(rootDir)) return;

        // Only support <rootDir>/<name>/SKILL.md format
        try {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir)) {
                var dirName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(dirName)) continue;

                var skillFile = Path.Combine(subDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;

                var skill = ParseSkillFile(skillFile, subDir, dirName);
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Name)) {
                    destination.TryAdd(skill.Name, skill);
                }
            }
        } catch (Exception) {
            // Non-critical file system error; ignore unreadable directories
        }
    }

    private static SkillDefinition? ParseSkillFile(string filePath, string baseDirectory, string defaultName) {
        try {
            var raw = File.ReadAllText(filePath);
            return ParseSkillContent(raw, baseDirectory, defaultName);
        } catch (Exception) {
            return null;
        }
    }

    private static SkillDefinition? ParseSkillContent(string text, string baseDirectory, string defaultName) {
        if (string.IsNullOrWhiteSpace(defaultName)) {
            return null;
        }

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) {
            return new SkillDefinition(defaultName, string.Empty, text.Trim(), baseDirectory);
        }

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var firstDelimiter = -1;
        var secondDelimiter = -1;

        for (var i = 0; i < lines.Length; i++) {
            if (lines[i].Trim() != "---") continue;

            if (firstDelimiter < 0) {
                firstDelimiter = i;
            } else {
                secondDelimiter = i;
                break;
            }
        }

        if (firstDelimiter < 0 || secondDelimiter <= firstDelimiter) {
            return new SkillDefinition(defaultName, string.Empty, text.Trim(), baseDirectory);
        }

        string? explicitName = null;
        var description = string.Empty;
        bool? explicitAuto = null;
        bool? disableModel = null;

        var lineIndex = firstDelimiter + 1;
        while (lineIndex < secondDelimiter) {
            var line = lines[lineIndex];
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) {
                lineIndex += 1;
                continue;
            }

            var key = line[..colonIndex].Trim().ToLowerInvariant();
            var val = line[(colonIndex + 1)..].Trim();

            switch (key) {
                case "name":
                    explicitName = val.Trim('"', '\'');
                    lineIndex += 1;
                    break;

                case "description":
                    if (val == ">" || val == "|" || val.Length == 0) {
                        // Multi-line block scalar
                        var descBuilder = new StringBuilder();
                        lineIndex += 1;
                        while (lineIndex < secondDelimiter) {
                            var blockLine = lines[lineIndex];
                            if (blockLine.Length > 0 && !char.IsWhiteSpace(blockLine[0])) {
                                break;
                            }

                            if (descBuilder.Length > 0) {
                                descBuilder.Append(val == "|" ? "\n" : " ");
                            }

                            descBuilder.Append(blockLine.Trim());
                            lineIndex += 1;
                        }

                        description = descBuilder.ToString().Trim();
                    } else {
                        description = val.Trim('"', '\'');
                        lineIndex += 1;
                    }

                    break;

                case "auto":
                    if (bool.TryParse(val, out var autoBool)) {
                        explicitAuto = autoBool;
                    }

                    lineIndex += 1;
                    break;

                case "disable-model-invocation":
                    if (bool.TryParse(val, out var disableBool)) {
                        disableModel = disableBool;
                    }

                    lineIndex += 1;
                    break;

                default:
                    lineIndex += 1;
                    break;
            }
        }

        if (explicitName is not null && string.IsNullOrWhiteSpace(explicitName)) {
            return null;
        }

        var resolvedName = !string.IsNullOrWhiteSpace(explicitName) ? explicitName : defaultName;
        if (string.IsNullOrWhiteSpace(resolvedName)) {
            return null;
        }

        var bodyLines = lines[(secondDelimiter + 1)..];
        var content = string.Join("\n", bodyLines).Trim();

        var isAuto = explicitAuto ?? !disableModel ?? false;
        return new SkillDefinition(resolvedName, description, content, baseDirectory, isAuto);
    }
}