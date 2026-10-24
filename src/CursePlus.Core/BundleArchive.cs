using System.IO.Compression;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CursePlus.Core;

public sealed class BundleArchive
{
    private readonly Dictionary<string, (long Size, DateTime Written, ChangeBundle Bundle)> manifests = new(StringComparer.OrdinalIgnoreCase);
    public const long MaxPayloadSize = 8L * 1024 * 1024 * 1024;
    public const int MaxFiles = 50000;

    public async Task WriteAsync(string path, ChangeBundle bundle, string sourceDirectory, CancellationToken ct = default)
    {
        Validate(bundle);
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (bundle.Delta is null || bundle.Desired.Files.Count != 0)
                    throw new InvalidDataException("JSON simples exige alterações sem arquivos anexados.");
                await using (var json = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(json, bundle, JsonFormat.Compact, ct);
                File.Move(temporary, path, true);
                return;
            }
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                await using (var json = zip.CreateEntry("changes.json", CompressionLevel.Optimal).Open())
                    await JsonSerializer.SerializeAsync(json, bundle, bundle.Delta is null ? JsonFormat.Options : JsonFormat.Compact, ct);
                var summary = $"CursePlus · {bundle.Name}\n{bundle.CreatedAt:O}\nPerfil: {bundle.Desired.Name}\nMinecraft: {bundle.Desired.MinecraftVersion}\nLoader: {bundle.Desired.Loader.Family} {bundle.Desired.Loader.Version}\nPack: {bundle.Desired.Pack?.Name ?? "Perfil próprio"} {bundle.Desired.Pack?.Version}\n\n";
                if (bundle.Delta is not null) summary += "Somente diferenças em relação ao original. O conteúdo original é obtido pelo CurseForge.\nMods adicionados/alterados:\n";
                summary += string.Join('\n', bundle.Desired.Mods.Select(m => $"{(m.Enabled ? "+" : "-")} {m.Name} | projeto {m.ProjectId} | arquivo {m.FileId}{(m.Modified ? " | modificado" : "")}"));
                if (bundle.Delta is { } delta)
                    summary += "\nMods removidos (projeto): " + string.Join(", ", delta.RemovedMods) + "\nArquivos removidos:\n" + string.Join('\n', delta.RemovedFiles);
                summary += "\n\nArquivos personalizados:\n" + string.Join('\n', bundle.Desired.Files.Select(f => f.Path));
                if (bundle.Desired.MissingMods.Count > 0)
                    summary += "\n\nMods ainda registrados pelo CurseForge, mas ausentes da instância (não incluídos no estado exportado):\n" + string.Join('\n', bundle.Desired.MissingMods);
                if (bundle.Baseline is not null)
                {
                    var comparison = new ChangePlanner().Build(bundle.Baseline, bundle.Desired);
                    summary += "\n\nAlterações em relação ao original:\n" + string.Join('\n', comparison.Operations.Select(o => o.Description));
                }
                else if (bundle.Delta is null) summary += "\n\nSem referência original incluída. O pacote contém o estado completo do conteúdo sincronizado.";
                await using (var writer = new StreamWriter(zip.CreateEntry("summary.txt").Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(summary.AsMemory(), ct);
                var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in bundle.Desired.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!copied.Add(EntryName(bundle, file))) continue;
                    var sourcePath = SafePaths.Resolve(sourceDirectory, file.Path);
                    await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    if (source.Length != file.Size) throw new IOException($"Arquivo mudou desde a captura: {file.Path}");
                    await using var destination = zip.CreateEntry(EntryName(bundle, file), CompressionLevel.Optimal).Open();
                    await CopyVerifiedAsync(source, destination, file, ct);
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ChangeBundle> ReadAsync(string path, CancellationToken ct = default, bool verifyPayload = true)
    {
        var info = new FileInfo(path);
        if (!verifyPayload && manifests.TryGetValue(info.FullName, out var cached) && cached.Size == info.Length && cached.Written == info.LastWriteTimeUtc)
            return cached.Bundle;
        ChangeBundle Remember(ChangeBundle value)
        {
            value = value with { Desired = FileScope.Clean(value.Desired), Baseline = value.Baseline is null ? null : FileScope.Clean(value.Baseline) };
            if (manifests.Count >= 4) manifests.Clear();
            manifests[info.FullName] = (info.Length, info.LastWriteTimeUtc, value);
            return value;
        }
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifesto grande demais.");
            await using var input = File.OpenRead(path);
            var plain = await JsonSerializer.DeserializeAsync<ChangeBundle>(input, JsonFormat.Options, ct) ?? throw new InvalidDataException("Manifesto vazio.");
            Validate(plain);
            if (plain.Delta is null || plain.Desired.Files.Count != 0) throw new InvalidDataException("Arquivos personalizados exigem ZIP.");
            return Remember(plain with { ContentSources = [Path.GetFullPath(path)] });
        }
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaxFiles + 2) throw new InvalidDataException("Pacote com entradas demais.");
        if (zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("Entradas duplicadas no pacote.");
        long total = 0;
        foreach (var entry in zip.Entries)
        { if (entry.Length > MaxPayloadSize - total) throw new InvalidDataException("Pacote grande demais."); total += entry.Length; }
        var json = zip.GetEntry("changes.json") ?? throw new InvalidDataException("changes.json não encontrado.");
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifesto grande demais.");
        await using var stream = json.Open();
        var bundle = await JsonSerializer.DeserializeAsync<ChangeBundle>(stream, JsonFormat.Options, ct) ?? throw new InvalidDataException("Manifesto vazio.");
        Validate(bundle);
        var expected = bundle.Desired.Files.Select(f => EntryName(bundle, f)).ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Any(e => e.FullName != "changes.json" && e.FullName != "summary.txt" && !expected.Contains(e.FullName)))
            throw new InvalidDataException("Entrada não referenciada no pacote.");
        foreach (var file in bundle.Desired.Files.DistinctBy(f => EntryName(bundle, f)))
        {
            var entry = zip.GetEntry(EntryName(bundle, file)) ?? throw new InvalidDataException($"Conteúdo ausente: {file.Path}");
            if (entry.Length != file.Size) throw new InvalidDataException($"Tamanho inválido: {file.Path}");
            if (!verifyPayload) continue;
            await using var bytes = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(bytes, ct));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Integridade inválida: {file.Path}");
        }
        return Remember(bundle with { ContentSources = [Path.GetFullPath(path)] });
    }

    public async Task WritePayloadAsync(string archive, FileState file, string destination, CancellationToken ct)
        => await WritePayloadAsync([archive], file, destination, ct);

    public async Task WritePayloadAsync(IEnumerable<string> archives, FileState file, string destination, CancellationToken ct)
    {
        var temporary = destination + ".curseplus-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyPayloadAsync(archives, file, output, ct);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task CopyPayloadAsync(IEnumerable<string> archives, FileState file, Stream destination, CancellationToken ct)
    {
        SafePaths.Classify(file.Path);
        foreach (var path in archives.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var provider = await ReadAsync(path, ct, false);
            var sourceFile = provider.Desired.Files.FirstOrDefault(f => f.Sha256 == file.Sha256 && f.Size == file.Size && f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
                ?? provider.Desired.Files.FirstOrDefault(f => f.Sha256 == file.Sha256 && f.Size == file.Size);
            if (sourceFile is null) continue;
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(EntryName(provider, sourceFile));
            if (entry is null) continue;
            if (entry.Length != file.Size) continue;
            await using var input = entry.Open();
            await CopyVerifiedAsync(input, destination, file, ct);
            return;
        }
        throw new InvalidDataException($"Conteúdo necessário ausente ou inválido: {file.Path}");
    }

    private static string EntryName(ChangeBundle bundle, FileState file) =>
        bundle.Delta is null ? "payload/" + file.Sha256 : "files/" + SafePaths.Normalize(file.Path);

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, FileState file, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            { length += count; if (length > file.Size) throw new IOException($"Arquivo mudou desde a captura: {file.Path}"); hash.AppendData(buffer, 0, count); await destination.WriteAsync(buffer.AsMemory(0, count), ct); }
            if (length != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Arquivo mudou desde a captura: {file.Path}");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task ComposeAsync(string path, ChangeBundle bundle, IReadOnlyList<string> sources, CancellationToken ct = default)
    {
        Validate(bundle);
        var providers = new List<(string Path, ChangeBundle Bundle)>();
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase)) providers.Add((source, await ReadAsync(source, ct, false)));
        var temp = path + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await using (var json = zip.CreateEntry("changes.json").Open()) await JsonSerializer.SerializeAsync(json, bundle, bundle.Delta is null ? JsonFormat.Options : JsonFormat.Compact, ct);
                await using (var summary = new StreamWriter(zip.CreateEntry("summary.txt").Open())) await summary.WriteAsync($"CursePlus · {bundle.Name}\nEstado revisado para retomada da operação.".AsMemory(), ct);
                foreach (var file in bundle.Desired.Files.DistinctBy(f => EntryName(bundle, f)))
                {
                    var provider = providers.FirstOrDefault(p => p.Bundle.Desired.Files.Any(f => f.Sha256 == file.Sha256 && f.Size == file.Size));
                    if (provider.Path is null) throw new InvalidDataException($"Conteúdo necessário para retomar não encontrado: {file.Path}");
                    var providerFile = provider.Bundle.Desired.Files.First(f => f.Sha256 == file.Sha256 && f.Size == file.Size);
                    await using var destination = zip.CreateEntry(EntryName(bundle, file), CompressionLevel.Optimal).Open();
                    await CopyPayloadAsync([provider.Path], providerFile, destination, ct);
                }
            }
            await ReadAsync(temp, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Validate(ChangeBundle bundle)
    {
        if (bundle.SchemaVersion is not (1 or 2) || (bundle.SchemaVersion == 2) != (bundle.Delta is not null)) throw new InvalidDataException("Versão do pacote não suportada.");
        if (bundle.Desired is null || bundle.Desired.Mods is null || bundle.Desired.Files is null || bundle.Desired.Loader is null || string.IsNullOrWhiteSpace(bundle.Name) || string.IsNullOrWhiteSpace(bundle.Desired.MinecraftVersion)) throw new InvalidDataException("Pacote incompleto.");
        if (bundle.Desired.Pack is { } pack && (pack.ProjectId <= 0 || pack.FileId <= 0)) throw new InvalidDataException("Referência do modpack inválida.");
        if (bundle.Desired.Mods.Count > MaxFiles || bundle.Desired.Files.Count > MaxFiles) throw new InvalidDataException("Pacote com itens demais.");
        if (bundle.Desired.Mods.Any(m => m.ProjectId <= 0 || m.FileId <= 0 || string.IsNullOrWhiteSpace(m.FileName) || Path.GetFileName(m.FileName) != m.FileName)) throw new InvalidDataException("Referência de mod inválida.");
        if (bundle.Desired.Mods.Select(m => m.ProjectId).Distinct().Count() != bundle.Desired.Mods.Count) throw new InvalidDataException("Mods duplicados.");
        if (bundle.Desired.Mods.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bundle.Desired.Mods.Count) throw new InvalidDataException("Nomes de arquivo de mods duplicados.");
        if (bundle.Desired.Files.Select(f => f.Path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bundle.Desired.Files.Count) throw new InvalidDataException("Caminhos duplicados.");
        if (bundle.Desired.Files.GroupBy(f => f.Sha256).Any(g => g.Select(f => f.Size).Distinct().Count() > 1)) throw new InvalidDataException("Tamanhos inconsistentes para o mesmo conteúdo.");
        long total = 0;
        foreach (var file in bundle.Desired.Files)
        {
            if (SafePaths.Classify(file.Path) != file.Kind || !IsHash(file.Sha256) || file.Size < 0 || file.Size > MaxPayloadSize - total)
                throw new InvalidDataException("Referência de arquivo inválida.");
            total += file.Size;
            if (file.Kind == FileKind.LocalMod)
            {
                var mod = bundle.Desired.Mods.FirstOrDefault(m => m.FileName.Equals(file.Path.Split('/')[^1].TrimDisabled(), StringComparison.OrdinalIgnoreCase));
                if (mod is not null && !mod.Modified) throw new InvalidDataException("Mod gerenciado normal não pode ser escrito externamente.");
            }
        }
        foreach (var mod in bundle.Desired.Mods.Where(m => m.Modified))
            if (!IsHash(mod.Sha256) || (bundle.Delta is null && !bundle.Desired.Files.Any(f => f.Kind == FileKind.LocalMod && f.Sha256 == mod.Sha256 && f.Path.Split('/')[^1].TrimDisabled().Equals(mod.FileName, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException($"Conteúdo do mod modificado ausente: {mod.Name}");
        if (bundle.Delta is { } delta)
        {
            if (bundle.Baseline is not null || !IsHash(delta.OriginalHash) || delta.RemovedFiles is null || delta.RemovedMods is null ||
                delta.RemovedMods.Count > MaxFiles || delta.RemovedFiles.Count > MaxFiles ||
                delta.RemovedMods.Any(id => id <= 0 || bundle.Desired.Mods.Any(m => m.ProjectId == id)) ||
                delta.RemovedMods.Distinct().Count() != delta.RemovedMods.Count ||
                delta.RemovedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != delta.RemovedFiles.Count)
                throw new InvalidDataException("Lista de alterações inválida.");
            foreach (var path in delta.RemovedFiles)
            {
                SafePaths.Classify(path);
                if (bundle.Desired.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Arquivo listado para escrita e remoção.");
            }
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
