namespace CursePlus.Core;

public static class FileScope
{
    public static bool Includes(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized.Split('/')[^1];
        if (new[] { "Thumbs.db", "desktop.ini", ".DS_Store" }.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith('~')) return false;
        if (new[] { ".bak", ".backup", ".old", ".tmp", ".temp", ".log" }.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        return !new[] { "kubejs/.cache/", "kubejs/logs/", "kubejs/exported/" }.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static ProfileState Clean(ProfileState state) => state with { Files = state.Files.Where(f => Includes(f.Path)).ToList() };
}
