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

        var workspaceSkill = FindInDirectory(Path.Combine(workspace, ".agents", "skills"), name);
        return workspaceSkill ?? FindInDirectory(Path.Combine(Paths.DataDirectory, "skills"), name);
    }

    private static Skill? FindInDirectory(string rootDir, string name) {
        if (!Directory.Exists(rootDir)) {
            return null;
        }

        try {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir)) {
                var dirName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(dirName)) continue;

                var skillFile = Path.Combine(subDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;

                var skill = Skill.FromFile(skillFile, subDir, dirName);
                if (skill is not null && string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)) {
                    return skill;
                }
            }
        } catch (Exception) {
            return null;
        }

        return null;
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