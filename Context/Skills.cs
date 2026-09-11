using Kite.Configuration;

namespace Kite.Context;

public static class Skills {
    public static IReadOnlyList<Skill> List(string workspace) {
        var result = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        var workspaceDir = Path.Combine(workspace, ".agents", "skills");
        ScanDirectory(workspaceDir, result);

        var globalDir = Path.Combine(Paths.DataDirectory, "skills");
        ScanDirectory(globalDir, result);

        return result.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Skill? Find(string workspace, string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var list = List(workspace);
        return list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ScanDirectory(string rootDir, Dictionary<string, Skill> destination) {
        if (!Directory.Exists(rootDir)) return;

        try {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir)) {
                var dirName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(dirName)) continue;

                var skillFile = Path.Combine(subDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;

                var skill = Skill.FromFile(skillFile, subDir, dirName);
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Name)) {
                    destination.TryAdd(skill.Name, skill);
                }
            }
        } catch (Exception) {
            // Non-critical file system error; ignore unreadable directories
        }
    }
}