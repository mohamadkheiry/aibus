using AiBus.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class InfrastructureTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Api_responses_include_security_headers()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Automatic_backup_is_consistent_and_runs_once_per_utc_day()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aibus-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "aibus.db");
        var backupDirectory = Path.Combine(directory, "backups");

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO Sample (Value) VALUES ('verified');";
                await command.ExecuteNonQueryAsync();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={databasePath}",
                ["Backups:Enabled"] = "true",
                ["Backups:Directory"] = backupDirectory,
                ["Backups:RetentionDays"] = "30"
            }).Build();
            var service = new DatabaseBackupService(configuration, new TestEnvironment(), NullLogger<DatabaseBackupService>.Instance);

            var backupPath = await service.CreateBackupIfDueAsync();
            Assert.NotNull(backupPath);
            Assert.True(File.Exists(backupPath));
            Assert.Null(await service.CreateBackupIfDueAsync());

            await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                await backup.OpenAsync();
                await using var verify = backup.CreateCommand();
                verify.CommandText = "SELECT Value FROM Sample LIMIT 1";
                Assert.Equal("verified", await verify.ExecuteScalarAsync());
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AiBus.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
