using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Tasky.Services.Migration;

/// <summary>
/// The (unencrypted) zip inside a migration file:
///   manifest.json
///   data/tasky.db, data/settings.json, data/Attachments/&lt;task id&gt;/…
/// The data/ folder mirrors %AppData%\Tasky exactly, so importing is a folder swap.
/// </summary>
public static class MigrationPackage
{
    private const string ManifestEntry = "manifest.json";
    public  const string DataFolderName = "data";

    public static void Create(string zipFile, MigrationManifest manifest,
                              string databaseSnapshot, string? settingsFile, string attachmentsFolder)
    {
        using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using (var s = manifestEntry.Open()) JsonSerializer.Serialize(s, manifest);

        zip.CreateEntryFromFile(databaseSnapshot, $"{DataFolderName}/{AppPaths.DatabaseFileName}", CompressionLevel.Optimal);
        if (settingsFile is not null && File.Exists(settingsFile))
            zip.CreateEntryFromFile(settingsFile, $"{DataFolderName}/{AppPaths.SettingsFileName}", CompressionLevel.Optimal);

        if (Directory.Exists(attachmentsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(attachmentsFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(attachmentsFolder, file).Replace('\\', '/');
                // Images and documents are usually compressed already.
                zip.CreateEntryFromFile(file, $"{DataFolderName}/{AppPaths.AttachmentsFolderName}/{relative}",
                                        CompressionLevel.Fastest);
            }
        }
    }

    /// <summary>Unpacks into <paramref name="destination"/> and returns the manifest.</summary>
    /// <exception cref="MigrationFileException">The content is not a valid Tasky package.</exception>
    public static MigrationManifest Extract(string zipFile, string destination)
    {
        MigrationManifest? manifest;
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

            var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw Invalid();
            using (var s = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<MigrationManifest>(s);
            if (manifest is null) throw Invalid();
            if (manifest.Format > MigrationManifest.CurrentFormat)
                throw new MigrationFileException("This migration file was made by a newer version of Sysslar. Update Sysslar and try again.");

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == ManifestEntry || entry.FullName.EndsWith('/')) continue;
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                // Refuse entries that would land outside the destination ("zip slip").
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw Invalid();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            throw Invalid();
        }

        if (!File.Exists(Path.Combine(destination, DataFolderName, AppPaths.DatabaseFileName))) throw Invalid();
        return manifest;
    }

    private static MigrationFileException Invalid() => new("The migration file is damaged or not from Sysslar.");
}
