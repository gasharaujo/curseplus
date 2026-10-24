using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers;

namespace CursePlus.Core;

public sealed class ProfileReader
{
    public async Task<List<ProfileState>> DiscoverAsync(string instancesDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(instancesDirectory)) return [];
        var result = new List<ProfileState>();
        foreach (var directory in Directory.EnumerateDirectories(instancesDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var metadata = Path.Combine(directory, "minecraftinstance.json");
            if (!File.Exists(metadata)) continue;
            try { result.Add(await ReadAsync(directory, false, ct)); }
            catch (JsonException) { /* CurseForge may be replacing its metadata; skip until refresh. */ }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<ProfileState> ReadAsync(string directory, bool captureFiles = true, CancellationToken ct = default)
    {
        var metadata = Path.Combine(directory, "minecraftinstance.json");
        await using var stream = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        var loader = Get(root, "baseModLoader");
        var manifest = Get(root, "manifest");
        var pack = Get(root, "installedModpack");
        var packFile = Get(pack, "installedFile");
        var projectId = Number(root, "projectID");
        if (projectId <= 0) projectId = Number(pack, "addonID");
        var fileId = Number(root, "fileID");
        if (fileId <= 0) fileId = Number(packFile, "id");
        var loaderName = String(loader, "name");
        var split = loaderName.IndexOf('-');
        var family = split > 0 ? loaderName[..split] : loaderName;
        var loaderVersion = split > 0 ? loaderName[(split + 1)..] : String(loader, "forgeVersion");
        var mods = new List<ModState>();
        var files = new List<FileState>();
        var missingMods = new List<string>();
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addons = Get(root, "installedAddons");
        if (addons.ValueKind == JsonValueKind.Array)
        {
            foreach (var addon in addons.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var installed = Get(addon, "installedFile");
                var filename = String(addon, "fileNameOnDisk");
                if (filename.Length == 0) filename = String(installed, "fileNameOnDisk");
                if (filename.Length == 0) filename = String(installed, "fileName");
                if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(filename) != filename) throw new InvalidDataException("Nome de mod inválido nos metadados.");
                var addonId = Number(addon, "addonID");
                var addonFileId = Number(installed, "id");
                if (addonId <= 0 || addonFileId <= 0) continue;
                var enabled = Boolean(addon, "isEnabled", true);
                var relative = "mods/" + filename;
                var actual = SafePaths.Resolve(directory, relative);
                if (!File.Exists(actual) && File.Exists(actual + ".disabled"))
                { relative += ".disabled"; actual += ".disabled"; enabled = false; }
                if (!File.Exists(actual) && actual.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && File.Exists(actual[..^9]))
                { relative = relative[..^9]; actual = actual[..^9]; enabled = true; }
                if (!File.Exists(actual))
                {
                    // Metadata can lag behind an intentional removal. Capture the files that actually exist.
                    missingMods.Add(String(addon, "name") is { Length: > 0 } missingName ? missingName : filename);
                    if (captureFiles) continue;
                }
                managedPaths.Add(relative);
                var modified = Boolean(addon, "isModified") || Boolean(addon, "isWorkingCopy");
                string? hash = null;
                if (captureFiles)
                {
                    var hashes = await HashFileAsync(actual, ct);
                    hash = hashes.Sha256;
                    var expected = ExpectedSha1(installed);
                    if (expected is not null && !expected.Equals(hashes.Sha1, StringComparison.OrdinalIgnoreCase)) modified = true;
                    if (modified) files.Add(new(relative, hash, new FileInfo(actual).Length, FileKind.LocalMod));
                }
                mods.Add(new(addonId, addonFileId, String(addon, "name"), filename.TrimDisabled(), enabled,
                    String(addon, "webSiteURL"), modified, hash));
            }
        }
        if (captureFiles)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var folder in new[] { "config", "defaultconfigs", "scripts", "kubejs", "mods" })
            {
                var path = Path.Combine(directory, folder);
                if (!Directory.Exists(path)) continue;
                SafePaths.Resolve(directory, folder + "/.probe");
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                    if (folder == "mods" && (managedPaths.Contains(relative) ||
                        (!relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))) continue;
                    if (!FileScope.Includes(relative)) continue;
                    SafePaths.Resolve(directory, relative);
                    var info = new FileInfo(file);
                    var beforeLength = info.Length;
                    var beforeWrite = info.LastWriteTimeUtc;
                    await using var content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
                    info.Refresh();
                    if (info.Length != beforeLength || info.LastWriteTimeUtc != beforeWrite)
                        throw new IOException($"O arquivo mudou durante a leitura: {relative}. Feche o jogo e tente novamente.");
                    files.Add(new(relative, sha256, info.Length, SafePaths.Classify(relative)));
                }
            }
        }
        if (mods.Select(m => m.ProjectId).Distinct().Count() != mods.Count)
            throw new InvalidDataException("O CurseForge registra mais de uma versão do mesmo mod. Resolva a duplicidade antes de sincronizar.");
        var packVersion = String(manifest, "version");
        if (packVersion.Length == 0) packVersion = Path.GetFileNameWithoutExtension(String(packFile, "fileName"));
        return new()
        {
            Id = String(root, "guid") is { Length: > 0 } guid ? guid : "local:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory)))).ToLowerInvariant(),
            Directory = Path.GetFullPath(directory), Name = String(root, "name") is { Length: > 0 } name ? name : Path.GetFileName(directory),
            MinecraftVersion = String(root, "gameVersion"), Loader = new(family.ToLowerInvariant(), loaderVersion),
            Pack = projectId > 0 && fileId > 0 ? new(projectId, fileId, String(pack, "name"), packVersion, String(packFile, "fileName")) : null,
            Mods = mods.OrderBy(m => m.Name).ToList(), Files = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), MissingMods = missingMods
        };
    }

    public static async Task<(string Sha256, string Sha1)> HashFileAsync(string path, CancellationToken ct = default)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            { sha256.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count); }
            return (Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string? ExpectedSha1(JsonElement installed)
    {
        var hashes = Get(installed, "hashes");
        if (hashes.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in hashes.EnumerateArray())
            if (Number(item, "type") == 1 || Number(item, "algo") == 1) return String(item, "value");
        return null;
    }
    private static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    private static string String(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static long Number(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : 0;
    private static bool Boolean(JsonElement e, string key, bool fallback = false) => Get(e, key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
}

internal static class ModFileNames
{
    public static string TrimDisabled(this string filename) => filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? filename[..^9] : filename;
}
