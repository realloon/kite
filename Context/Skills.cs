using Kite.Config;

namespace Kite.Context;

internal static class Skills {
    public static IReadOnlyList<Skill> List(string workspace) {
        var result = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        ScanDirectory(Path.Combine(workspace, ".agents", "skills"), result);
        ScanDirectory(Paths.GlobalSkills, result);

        return [.. result.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void ScanDirectory(string rootDir, Dictionary<string, Skill> destination) {
        if (!Directory.Exists(rootDir)) return;

        try {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir)) {
                var dirName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(dirName)) continue;

                var skillFile = Path.Combine(subDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;

                if (Skill.FromFile(skillFile, subDir, dirName) is { } skill) {
                    destination.TryAdd(skill.Name, skill);
                }
            }
        } catch (Exception) { }
    }
}