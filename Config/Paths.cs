namespace Kite.Config;

internal static class Paths {
    public static string DataDirectory {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".kite");
        }
    }

    public static string Presets => Path.Combine(DataDirectory, "presets.json");
}