using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed record AppDataMigrationResult(bool Migrated, string? SourceDirectory = null);

public sealed class AppDataMigrationService(AppPaths paths)
{
    public async Task<AppDataMigrationResult> MigrateIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.LegacyRootDirectory) || IsNonEmpty(paths.RootDirectory))
            return new AppDataMigrationResult(false);

        var parent = Directory.GetParent(paths.RootDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定 CloudLight XiaoMi 数据目录的父目录。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".CloudLight-XiaoMi-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            await CopyLegacyTreeAsync(paths.LegacyRootDirectory, staging, cancellationToken);
            var oldDatabase = Path.Combine(paths.LegacyRootDirectory, "presence.db");
            if (File.Exists(oldDatabase))
                await BackupDatabaseAsync(oldDatabase, Path.Combine(staging, "presence.db"), cancellationToken);

            VerifyCopiedFile(paths.LegacyRootDirectory, staging, "settings.json");
            VerifyCopiedFile(paths.LegacyRootDirectory, staging, "auth.dat");

            if (Directory.Exists(paths.RootDirectory))
                Directory.Delete(paths.RootDirectory, recursive: false);
            Directory.Move(staging, paths.RootDirectory);
            return new AppDataMigrationResult(true, paths.LegacyRootDirectory);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static bool IsNonEmpty(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

    private static async Task CopyLegacyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative.Equals("presence.db", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("presence.db-wal", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("presence.db-shm", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task BackupDatabaseAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var targetConnectionString = new SqliteConnectionStringBuilder { DataSource = targetPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var target = new SqliteConnection(targetConnectionString);
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        await using var check = target.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"迁移后的 SQLite 数据库校验失败：{result}");
    }

    private static void VerifyCopiedFile(string source, string target, string fileName)
    {
        var sourceFile = Path.Combine(source, fileName);
        if (!File.Exists(sourceFile)) return;
        var targetFile = Path.Combine(target, fileName);
        if (!File.Exists(targetFile) || new FileInfo(sourceFile).Length != new FileInfo(targetFile).Length)
            throw new IOException($"迁移文件校验失败：{fileName}");
    }
}
