using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Infrastructure;

using Npgsql;

using Testcontainers.PostgreSql;

namespace Moonlight.Tests;

// Uses TEST_DATABASE_URL when set (CI service container, local Postgres); otherwise starts a Testcontainers Postgres.
// Every test class gets its own freshly migrated database, dropped afterwards, so classes never collide.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = "";
    private string _databaseName = "";

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (!string.IsNullOrEmpty(external))
        {
            _adminConnectionString = external;
        }
        else
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
        }

        _databaseName = "moonlight_test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        // A bounded pool per class keeps parallel test classes under Postgres' max_connections.
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            MaxPoolSize = 10,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<MoonlightDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options;
        await using var db = new MoonlightDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
