using System.Text;

namespace Kite.Context;

internal sealed record Skill(string Name, string Description, string Content, string Directory, bool Auto = false) {
    public static Skill? FromFile(string filePath, string baseDirectory, string defaultName) {
        try {
            return ParseContent(File.ReadAllText(filePath), baseDirectory, defaultName);
        } catch (Exception) {
            return null;
        }
    }

    private static Skill? ParseContent(string text, string baseDirectory, string defaultName) {
        if (string.IsNullOrWhiteSpace(defaultName)) {
            return null;
        }

        if (!text.TrimStart().StartsWith("---", StringComparison.Ordinal)) {
            return new Skill(defaultName, string.Empty, text.Trim(), baseDirectory);
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
            return new Skill(defaultName, string.Empty, text.Trim(), baseDirectory);
        }

        string? explicitName = null;
        var description = string.Empty;
        bool? explicitAuto = null;
        bool? disableModel = null;

        var lineIndex = firstDelimiter + 1;
        while (lineIndex < secondDelimiter) {
            var line = lines[lineIndex];
            lineIndex += 1;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            var key = line[..colonIndex].Trim().ToLowerInvariant();
            var val = line[(colonIndex + 1)..].Trim();

            switch (key) {
                case "name":
                    explicitName = val.Trim('"', '\'');
                    break;

                case "description" when val is ">" or "|" or "": {
                    var descBuilder = new StringBuilder();
                    while (lineIndex < secondDelimiter) {
                        var blockLine = lines[lineIndex];
                        if (blockLine.Length > 0 && !char.IsWhiteSpace(blockLine[0])) break;

                        if (descBuilder.Length > 0) {
                            descBuilder.Append(val == "|" ? '\n' : ' ');
                        }

                        descBuilder.Append(blockLine.Trim());
                        lineIndex += 1;
                    }

                    description = descBuilder.ToString().Trim();
                    break;
                }

                case "description":
                    description = val.Trim('"', '\'');
                    break;

                case "auto":
                    if (bool.TryParse(val, out var autoBool)) {
                        explicitAuto = autoBool;
                    }

                    break;

                case "disable-model-invocation":
                    if (bool.TryParse(val, out var disableBool)) {
                        disableModel = disableBool;
                    }

                    break;
            }
        }

        if (explicitName is not null && string.IsNullOrWhiteSpace(explicitName)) {
            return null;
        }

        var content = string.Join('\n', lines[(secondDelimiter + 1)..]).Trim();
        var isAuto = explicitAuto ?? !disableModel ?? false;
        return new Skill(explicitName ?? defaultName, description, content, baseDirectory, isAuto);
    }
}