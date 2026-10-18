using System.Diagnostics;
using System.Text.Json;
using Iron.Audit.Contracts;
using Iron.Audit.Data;
using Iron.Core.Identifiers;
using Iron.Core.Principals;
using Iron.Data.Commands;
using Iron.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Audit.Tests;

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new AuditDbContext(DesignTime.OptionsFor<AuditDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Audit model has changes without a migration. Run: make ef-add MODULE=Audit NAME=<WhatChanged>, then commit the files under src/Iron.Audit/Data/Migrations.");
    }
}

[Collection(AuditCollection.Name)]
public class AuditTableTests(AuditFixture fixture)
{
    [Fact]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheAppRoleWritesAndReadsEventsButCanNeverChangeOrDeleteThem()
    {
        var actor = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), fixture.AppConnectionString);
        await using (var scope = provider.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await writer.WriteAsync(new AuditEvent("tests.role.checked", AuditOutcome.Success, actor, "the app role"), TestContext.Current.CancellationToken);
        }

        await using var dataSource = new NpgsqlDataSourceBuilder(fixture.AppConnectionString).Build();
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Run(connection, "update audit.events set outcome = 'changed'"))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Run(connection, "delete from audit.events"))).SqlState);
        await using var count = connection.CreateCommand();
        count.CommandText = "select count(*) from audit.events where actor_id = @actor";
        count.Parameters.AddWithValue("actor", actor);
        Assert.Equal(1L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrittenEventCarriesActorSourceDetailsAndCorrelation()
    {
        var actor = Ids.New();
        var recordId = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        using var activity = new Activity("tests.job").Start();

        await using (var scope = provider.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await writer.WriteAsync(
                new AuditEvent("tests.widget.approved", AuditOutcome.Success, actor, "widget 42", new Dictionary<string, string> { ["from"] = "draft", ["to"] = "approved" }, "widget", recordId, "looks fine"),
                TestContext.Current.CancellationToken);
        }

        await using var readScope = provider.CreateAsyncScope();
        var context = readScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await context.Events.AsNoTracking().SingleAsync(e => e.ActorId == actor, TestContext.Current.CancellationToken);
        Assert.Equal("tests.widget.approved", row.EventType);
        Assert.Equal(AuditSource.Job, row.Source);
        Assert.Equal(SystemPrincipal.SystemId, row.CreatedBy);
        Assert.Equal(activity.TraceId.ToHexString(), row.CorrelationId);
        Assert.Equal("widget", row.RecordType);
        Assert.Equal(recordId, row.RecordId);
        Assert.Equal("looks fine", row.Reason);
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Details ?? "{}");
        Assert.Equal("approved", details?["to"]);
        Assert.Equal("draft", details?["from"]);
    }

    private static async Task Run(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AReasonLongerThanTheColumnIsTrimmedNotRefused()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using (var scope = provider.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await writer.WriteAsync(new AuditEvent("tests.long_reason", AuditOutcome.Failure, Ids.New(), new string('t', 900), null, null, null, new string('x', 5000)), TestContext.Current.CancellationToken);
        }

        await using var readScope = provider.CreateAsyncScope();
        var context = readScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.EventType == "tests.long_reason", TestContext.Current.CancellationToken);
        Assert.Equal(AuditEventRecord.ReasonMaxLength, stored.Reason!.Length);
        Assert.Equal(AuditEventRecord.TargetMaxLength, stored.Target!.Length);
    }
}
