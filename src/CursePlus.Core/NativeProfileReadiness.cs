namespace CursePlus.Core;

public static class NativeProfileReadiness
{
    public static async Task<bool> CheckAsync(ProfileState profile, bool requireManifest, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(Path.Combine(profile.Directory, "minecraftinstance.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (root.TryGetProperty("isValid", out var valid) && valid.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            foreach (var mod in profile.Mods)
                if (!File.Exists(SafePaths.Resolve(profile.Directory, "mods/" + mod.FileName + (mod.Enabled ? "" : ".disabled")))) return false;
            if (!requireManifest) return true;
            if (!root.TryGetProperty("installedAddons", out var addons) || addons.ValueKind != System.Text.Json.JsonValueKind.Array ||
                !root.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !manifest.TryGetProperty("files", out var files) || files.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("required", out var required) && required.ValueKind == System.Text.Json.JsonValueKind.False) continue;
                if (!file.TryGetProperty("projectID", out var project) || !file.TryGetProperty("fileID", out var fileId)) return false;
                if (!addons.EnumerateArray().Any(a => a.TryGetProperty("addonID", out var id) && id.GetInt64() == project.GetInt64() &&
                    a.TryGetProperty("installedFile", out var installed) && installed.TryGetProperty("id", out var installedId) && installedId.GetInt64() == fileId.GetInt64())) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
