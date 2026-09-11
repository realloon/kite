namespace Kite.Sessions;

internal sealed class TurnSnapshot(string prompt, int messageIndex, int entryIndex, decimal cost) {
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]?> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public string Prompt { get; } = prompt;
    public int MessageIndex { get; } = messageIndex;
    public int EntryIndex { get; } = entryIndex;
    public decimal Cost { get; } = cost;

    public void Capture(string path) {
        lock (_gate) {
            if (_files.ContainsKey(path)) return;

            _files[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }

    public int Restore() {
        lock (_gate) {
            var restored = 0;
            foreach (var (path, bytes) in _files) {
                if (bytes is null) {
                    if (File.Exists(path)) {
                        File.Delete(path);
                        restored += 1;
                    }
                } else {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                    restored += 1;
                }
            }

            return restored;
        }
    }
}