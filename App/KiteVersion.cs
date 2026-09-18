using System.Reflection;

namespace Kite.App;

internal static class KiteVersion {
    public static string Value { get; } = Resolve();

    private static string Resolve() {
        var assembly = typeof(KiteVersion).Assembly;
        if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() is not { } attribute) {
            throw new InvalidOperationException("Assembly informational version is missing");
        }

        // The SDK appends "+<source revision>" when the build runs inside a git checkout.
        var informational = attribute.InformationalVersion;
        var separator = informational.IndexOf('+');
        return separator < 0 ? informational : informational[..separator];
    }
}