namespace Kite.Skills;

public sealed record SkillDefinition(
    string Name,
    string Description,
    string Content,
    string Directory,
    bool Auto = false);