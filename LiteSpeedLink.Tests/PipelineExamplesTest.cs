using FluentAssertions;

using Microsoft.Data.Sqlite;

using Npgsql;

using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Tests;

public sealed record Principal(string User, string Role);
public readonly record struct AccessRequest(Principal Principal, string Operation);

/// <summary>Autenticación síncrona: token -> Principal, leído de SQLite (lookup local y barato).</summary>
public sealed class SqliteAuthenticationPipeline(string connectionString) : IPipeline<string, Principal?>
{
    public (ResponseStatus, Principal?) Process(string token)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user, role FROM tokens WHERE token = $t";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (ResponseStatus.Success, new(r.GetString(0), r.GetString(1))) : (ResponseStatus.Failed, null);
    }
}

/// <summary>Autorización asíncrona: (Principal, operación) -> permitido, contra Postgres (I/O remoto).</summary>
public sealed class PostgresAuthorizationPipeline(NpgsqlDataSource db) : IAsyncValuePipeline<AccessRequest, bool>
{
    public async ValueTask<(ResponseStatus, bool)> ProcessAsync(AccessRequest req)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS(SELECT 1 FROM lsl_permissions WHERE role = $1 AND operation = $2)");
        cmd.Parameters.AddWithValue(req.Principal.Role);
        cmd.Parameters.AddWithValue(req.Operation);
        return (bool)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))! ? (ResponseStatus.Success, true) : (ResponseStatus.Failed, false);
    }
}

public class PipelineExamplesTest
{
    const string PgConn = "Host=localhost;Port=5432;Username=mami;Password=mami_ventas;Database=mami";

    [Fact]
    public void SqliteAuthentication()
    {
        // Shared cache en memoria: vive mientras "keeper" siga abierta.
        var cs = $"Data Source=auth{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();
        using (var cmd = keeper.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE tokens(token TEXT PRIMARY KEY, user TEXT, role TEXT); INSERT INTO tokens VALUES('tok-1','pedro','admin');";
            cmd.ExecuteNonQuery();
        }

        IPipeline<string, Principal?> auth = new SqliteAuthenticationPipeline(cs);

        auth.Process("tok-1").Should().Be((ResponseStatus.Success, new Principal("pedro", "admin")));
        auth.Process("forged").Item1.Should().Be(ResponseStatus.Failed);
    }

    [Fact]
    public async Task PostgresAuthorization()
    {
        await using var db = NpgsqlDataSource.Create(PgConn);
        await using (var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS lsl_permissions(role text, operation text, PRIMARY KEY(role, operation));
            INSERT INTO lsl_permissions VALUES('admin','IAuth.Greet') ON CONFLICT DO NOTHING;
            """))
            await cmd.ExecuteNonQueryAsync();

        IAsyncValuePipeline<AccessRequest, bool> authz = new PostgresAuthorizationPipeline(db);

        (await authz.ProcessAsync(new(new("pedro", "admin"), "IAuth.Greet"))).Should().Be((ResponseStatus.Success, true));
        (await authz.ProcessAsync(new(new("eve", "guest"), "IAuth.Greet"))).Item1.Should().Be(ResponseStatus.Failed);
    }
}
