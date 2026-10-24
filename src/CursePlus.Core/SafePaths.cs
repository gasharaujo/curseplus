namespace CursePlus.Core;

public static class SafePaths
{
    public static string Normalize(string path)
    {
        var value = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains(':') ||
            value.Split('/').Any(p => p is ".." or "." or "" || p.EndsWith(' ') || p.EndsWith('.')) ||
            value.Any(c => c < 32 || "<>\"|?*".Contains(c)))
            throw new InvalidDataException($"Caminho relativo inválido: {path}");
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (value.Split('/').Any(p => reserved.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Nome de dispositivo não permitido.");
        return value;
    }

    public static FileKind Classify(string path)
    {
        var normalized = Normalize(path);
        var root = normalized.Split('/')[0].ToLowerInvariant();
        return root switch
        {
            "config" or "defaultconfigs" => FileKind.Configuration,
            "scripts" or "kubejs" => FileKind.Script,
            "mods" when normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                        normalized.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) => FileKind.LocalMod,
            _ => throw new InvalidDataException($"Arquivo fora do conteúdo sincronizado: {path}")
        };
    }

    public static string Resolve(string root, string relative)
    {
        var safe = Normalize(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, safe.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho sai da pasta da instância.");
        var current = Path.GetDirectoryName(result);
        while (current is not null && current.Length >= fullRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
        {
            if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links de diretório não são permitidos na sincronização.");
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(result) && (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Links de arquivo não são permitidos.");
        return result;
    }
}
