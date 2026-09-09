using Microsoft.Data.Sqlite;

namespace AiBus.Api;

public sealed class DatabaseBackupService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<DatabaseBackupService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("Backups:Enabled", false);
        if (!enabled)
        {
            logger.LogInformation("Automatic SQLite backups are disabled for {Environment}.", environment.EnvironmentName);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CreateBackupIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic SQLite backup failed.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    public async Task<string?> CreateBackupIfDueAsync(CancellationToken cancellationToken = default)
    {
        var sourceConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required for backups.");
        var sourceBuilder = new SqliteConnectionStringBuilder(sourceConnectionString);
        var sourcePath = Path.GetFullPath(sourceBuilder.DataSource);
        sourceBuilder.Pooling = false;
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("SQLite database was not found for backup.", sourcePath);

        var configuredDirectory = configuration["Backups:Directory"];
        var backupDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Path.GetDirectoryName(sourcePath)!, "backups")
            : configuredDirectory);
        Directory.CreateDirectory(backupDirectory);

        var todayPrefix = $"aibus-auto-{DateTime.UtcNow:yyyyMMdd}-";
        if (Directory.EnumerateFiles(backupDirectory, $"{todayPrefix}*.db", SearchOption.TopDirectoryOnly).Any())
            return null;

        var backupPath = Path.Combine(backupDirectory, $"{todayPrefix}{DateTime.UtcNow:HHmmss}.db");
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        await using (var source = new SqliteConnection(sourceBuilder.ConnectionString))
        await using (var destination = new SqliteConnection(destinationBuilder.ConnectionString))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);

            await using var check = destination.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite backup integrity check failed: {result}");
        }

        var retentionDays = Math.Clamp(configuration.GetValue("Backups:RetentionDays", 30), 7, 365);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var oldBackup in Directory.EnumerateFiles(backupDirectory, "aibus-auto-*.db", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(oldBackup) < cutoff)
                File.Delete(oldBackup);
        }

        logger.LogInformation("SQLite backup created and verified at {BackupPath}.", backupPath);
        return backupPath;
    }
}
