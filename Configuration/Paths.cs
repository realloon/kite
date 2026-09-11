namespace Kite.Configuration;

public static class Paths {
    public static string DataDirectory {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".kite");
        }
    }

    public static string Config => Path.Combine(DataDirectory, "config.json");
}