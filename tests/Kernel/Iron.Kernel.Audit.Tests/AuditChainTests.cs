using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.History;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Audit.Tests;

/// <summary>
/// The audit chain (TF-061, ADR-0050): events written by many transactions at once take consecutive positions when the
/// sealer runs; an independent implementation recomputes every hash from the documented canonical form; the chain
/// columns are set once, by the sealing function alone; the verifier's role reads the chain and nothing else; and the
/// fleet's verification passes a whole chain, names a changed or deleted event, and names the restore point after a restore.
/// </summary>
[Collection(AuditCollection.Name)]
public class AuditChainTests(AuditFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0050")]
    public async Task ConcurrentWritesAreSealedIntoAConsecutiveChainWhileTwoSealersRun()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(SystemPrincipal.SystemId), fixture.AppConnectionString);
        var writing = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("tests.chain.written", AuditOutcome.Success, Ids.New(), $"writer {writer} event {i}"), Cancel);
            }
        }, Cancel)).ToList();
        var sealing = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!writing.All(w => w.IsCompleted))
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AuditSealer>().SealAsync(Cancel);
            }
        }, Cancel)).ToList();
        await Task.WhenAll(writing.Concat(sealing));
        await SealEverythingAsync(provider);

        var chain = await ReadChainAsync(fixture.ConnectionString);
        Assert.NotEmpty(chain);
        Assert.Equal(Enumerable.Range(1, chain.Count).Select(p => (long)p), chain.Select(e => e.Position));
        Assert.Equal(AuditCanonicalForm.Genesis, chain[0].PreviousHash);
        for (var i = 1; i < chain.Count; i++)
        {
            Assert.Equal(chain[i - 1].Hash, chain[i].PreviousHash);
        }

        await using var scopeAfter = provider.CreateAsyncScope();
        var context = scopeAfter.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.Equal(0, await context.Events.CountAsync(e => e.ChainPosition == null, Cancel));
        var head = await AuditSealer.HeadAsync(context, Cancel);
        Assert.Equal(chain[^1].Position, head!.Position);
        Assert.Equal(chain[^1].Hash, head.Hash);
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task AnIndependentImplementationOfTheDocumentedFormRecomputesEveryHash()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(SystemPrincipal.SystemId));
        await using (var scope = provider.CreateAsyncScope())
        {
            // Text the form must normalise and escape: a quote, a backslash, a line break, a control character, an
            // accent written as two code points, and details whose keys arrive out of order.
            var details = new Dictionary<string, string> { ["zeta"] = "last", ["alpha"] = "Café \"quoted\" back\\slash\nnext\u0001" };
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("tests.chain.form", AuditOutcome.Success, Ids.New(), "Zoë\t", details, "widget", Ids.New(), "a reason"), Cancel);

            // A record's save, with a masked and a secret field (version 2 hashes the changes, ADR-0051).
            FieldChange[] changes = [new("bank_account", "****5555", "****3000", FieldTreatments.Masked), new("name", "Valve", "Valve \"B\""), new("portal_key_hash", null, null, FieldTreatments.Secret)];
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent(AuditEvents.RecordChanged, AuditOutcome.Success, Ids.New(), null, null, "widget", Ids.New(), Changes: changes), Cancel);
        }

        await SealEverythingAsync(provider);
        var chain = await ReadChainAsync(fixture.ConnectionString);
        Assert.Contains(chain, e => e.Content.EventType == "tests.chain.form");
        Assert.Contains(chain, e => e.Content.EventType == AuditEvents.RecordChanged && e.Content.Changes is not null && e.HashVersion == 2);
        foreach (var sealedEvent in chain)
        {
            Assert.Equal(sealedEvent.Hash, IndependentHash(sealedEvent.Content, sealedEvent.PreviousHash, sealedEvent.HashVersion));
        }
    }

    [Fact]
    [Proves("TF-062 AC1", "TF-062 AC4", "ADR-0051")]
    public async Task VersionOneEventsStayVerifiableBesideVersionTwoAndAChangedFieldListIsFound()
    {
        var database = await fixture.NewChainDatabaseAsync("versions");
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(SystemPrincipal.SystemId), database);
        await WriteAsync(provider, 1);

        // The first event sealed as T6 sealed it, with version 1, before version 2 came.
        Guid first;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var record = await context.Events.AsNoTracking().SingleAsync(Cancel);
            first = record.Id;
            var hash = AuditCanonicalForm.Hash(record.Chained(1), AuditCanonicalForm.Genesis, 1);
            await context.Database.ExecuteSqlInterpolatedAsync($"select kernel_audit.seal_event({record.Id}, {1L}, {AuditCanonicalForm.Genesis}, {hash}, {(short)1}, {DateTimeOffset.UtcNow})", Cancel);
            context.Seals.Add(new AuditSeal(1, 1, hash));
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            FieldChange[] changes = [new("title", "Draft", "Final")];
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent(AuditEvents.RecordChanged, AuditOutcome.Success, Ids.New(), null, null, "proposal", Ids.New(), Changes: changes), Cancel);
        }

        await SealEverythingAsync(provider);
        var chain = await ReadChainAsync(database);
        Assert.Equal([(short)1, (short)2], chain.Select(e => e.HashVersion));
        Assert.Empty(AuditChainCheck.Check(1, AuditCanonicalForm.Genesis, chain, []).Problems);
        Assert.All(chain, e => Assert.Equal(e.Hash, IndependentHash(e.Content, e.PreviousHash, e.HashVersion)));

        // The field list is part of the hash: a changed value in it is a changed event.
        await TamperAsync(database, "update kernel_audit.events set changes = '[{\"field\": \"title\", \"old\": \"Draft\", \"new\": \"Signed off\", \"treatment\": \"plain\"}]' where chain_position = 2");
        var problem = Assert.Single(AuditChainCheck.Check(1, AuditCanonicalForm.Genesis, await ReadChainAsync(database), []).Problems);
        Assert.Equal((ChainProblemKinds.Modified, 2L), (problem.Kind, problem.Position));
        Assert.NotEqual(first, problem.EventId);
    }

    [Fact]
    [Proves("TF-060 AC2", "ADR-0050")]
    public async Task TheChainColumnsAreSetOnceBySealingAloneAndNothingElseEverChanges()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(SystemPrincipal.SystemId), fixture.AppConnectionString);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("tests.chain.guarded", AuditOutcome.Success, Ids.New()), Cancel);
        }

        await SealEverythingAsync(provider);
        var sealedEvent = (await ReadChainAsync(fixture.ConnectionString)).Last(e => e.Content.EventType == "tests.chain.guarded");

        // The app role: no UPDATE at all, and the sealing function refuses an event sealed already.
        await using (var app = await OpenAsync(fixture.AppConnectionString))
        {
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(app, $"update kernel_audit.events set hash = previous_hash where id = '{sealedEvent.Content.Id}'"))).SqlState);
            var again = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(app, $"select kernel_audit.seal_event('{sealedEvent.Content.Id}', 999999, '{sealedEvent.Hash}', '{sealedEvent.Hash}', 1::smallint, now())"));
            Assert.Equal("23514", again.SqlState);
        }

        // The owner too: the trigger refuses a change to a sealed event, and to an unsealed one's content.
        await using var owner = await OpenAsync(fixture.ConnectionString);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(owner, $"update kernel_audit.events set outcome = 'changed' where id = '{sealedEvent.Content.Id}'"))).SqlState);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("tests.chain.unsealed", AuditOutcome.Success, Ids.New()), Cancel);
        }

        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(owner, "update kernel_audit.events set reason = 'changed' where event_type = 'tests.chain.unsealed'"))).SqlState);
        await SealEverythingAsync(provider);
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task TheVerifiersRoleReadsTheChainAndNothingElse()
    {
        await using var reader = await OpenAsync(AuditFixture.As(fixture.ConnectionString, AuditFixture.ReaderRole, AuditFixture.ReaderPassword));
        await RunAsync(reader, "select count(*) from kernel_audit.events");
        await RunAsync(reader, "select count(*) from kernel_audit.seals");
        await RunAsync(reader, "select count(*) from kernel_audit.archive_batches");
        await RunAsync(reader, "select count(*) from kernel_audit.prunes");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(reader, "select count(*) from kernel_audit.legal_holds"))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(reader, "select count(*) from kernel_authz.roles"))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(reader, "insert into kernel_audit.seals (id, first_position, last_position, last_hash, created_at, created_by) values (gen_random_uuid(), 1, 1, repeat('0', 64), now(), gen_random_uuid())"))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => RunAsync(reader, "select kernel_audit.remove_archived(1)"))).SqlState);
    }

    [Fact]
    [Proves("TF-061 AC1", "ADR-0050")]
    public async Task TheDailyVerificationOfAWholeChainPassesAndPicksUpWhereItEnded()
    {
        var database = await fixture.NewChainDatabaseAsync("intact");
        await using var provider = await WriteAndSealAsync(database, 6);
        var chains = new FakeChains(await AnchorsOfAsync(provider));

        var first = await Verifier(chains, database).VerifyAsync(Due(database, full: false), Cancel);
        Assert.Equal(AuditVerificationResults.Passed, first.Result);
        Assert.True(first.Full); // nothing verified before: the whole chain
        Assert.Equal((1L, 6L, 6), (first.FromPosition, first.ToPosition, first.EventsChecked));
        Assert.Empty(first.Problems);

        await WriteAsync(provider, 3);
        await SealEverythingAsync(provider);
        chains.Anchors = await AnchorsOfAsync(provider);
        chains.Latest = first;
        var next = await Verifier(chains, database).VerifyAsync(Due(database, full: false), Cancel);
        Assert.Equal(AuditVerificationResults.Passed, next.Result);
        Assert.False(next.Full);
        Assert.Equal((7L, 9L, 3), (next.FromPosition, next.ToPosition, next.EventsChecked));
    }

    [Fact]
    [Proves("TF-061 AC2", "ADR-0050")]
    public async Task AChangedEventIsReportedByItsIdentifierAndPosition()
    {
        var database = await fixture.NewChainDatabaseAsync("changed");
        await using var provider = await WriteAndSealAsync(database, 5);
        var chain = await ReadChainAsync(database);
        var victim = chain[2];
        await TamperAsync(database, $"update kernel_audit.events set reason = 'rewritten after the fact' where id = '{victim.Content.Id}'");

        var verification = await Verifier(new FakeChains(await AnchorsOfAsync(provider)), database).VerifyAsync(Due(database, full: true), Cancel);

        Assert.Equal(AuditVerificationResults.Failed, verification.Result);
        var problem = Assert.Single(verification.Problems);
        Assert.Equal((ChainProblemKinds.Modified, 3L, (Guid?)victim.Content.Id), (problem.Kind, problem.Position, problem.EventId));
        Assert.Contains(victim.Content.Id.ToString("D"), problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-061 AC2", "ADR-0050")]
    public async Task ADeletedEventIsReportedAsAMissingPositionBetweenItsNeighbours()
    {
        var database = await fixture.NewChainDatabaseAsync("deleted");
        await using var provider = await WriteAndSealAsync(database, 5);
        var chain = await ReadChainAsync(database);
        await TamperAsync(database, $"delete from kernel_audit.events where id = '{chain[1].Content.Id}'");

        var verification = await Verifier(new FakeChains(await AnchorsOfAsync(provider)), database).VerifyAsync(Due(database, full: true), Cancel);

        Assert.Equal(AuditVerificationResults.Failed, verification.Result);
        var problem = Assert.Single(verification.Problems);
        Assert.Equal((ChainProblemKinds.Missing, 2L), (problem.Kind, problem.Position));
        Assert.Contains(chain[0].Content.Id.ToString("D"), problem.Message, StringComparison.Ordinal);
        Assert.Contains(chain[2].Content.Id.ToString("D"), problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-061 AC3", "ADR-0050")]
    public async Task AfterARestoreTheVerificationNamesTheRestorePointAndItsTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var database = await fixture.NewChainDatabaseAsync("restored");
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), database);
        await WriteAsync(provider, 4);
        await SealEverythingAsync(provider);
        var backupTaken = await AnchorsOfAsync(provider);

        time.Advance(TimeSpan.FromHours(3));
        await WriteAsync(provider, 3);
        await SealEverythingAsync(provider);
        var anchors = await AnchorsOfAsync(provider);

        // The backup from before the last three events comes back: the rows and their seal go, as a restore does.
        await TamperAsync(database, "delete from kernel_audit.events where chain_position > 4; delete from kernel_audit.seals where last_position > 4");
        var restored = await Verifier(new FakeChains(anchors), database).VerifyAsync(Due(database, full: false), Cancel);
        Assert.Equal(AuditVerificationResults.Restored, restored.Result);
        Assert.Equal(4L, restored.RestorePosition);
        Assert.Equal(backupTaken.Single(a => a.Position == 4).At, restored.RestoreAt);

        // New events sealed after the restore take the positions again with other hashes: still the same restore point.
        await WriteAsync(provider, 3);
        await SealEverythingAsync(provider);
        var resealed = await Verifier(new FakeChains(anchors), database).VerifyAsync(Due(database, full: false), Cancel);
        Assert.Equal(AuditVerificationResults.Restored, resealed.Result);
        Assert.Equal(4L, resealed.RestorePosition);
        Assert.All(resealed.Problems, p => Assert.Equal(ChainProblemKinds.AnchorMismatch, p.Kind));
    }

    [Fact]
    [Proves("TF-061 AC3", "ADR-0050")]
    public async Task ARestoreIsReportedAcceptedAndThenVerifiedAsPassedWithTheRestoreStillShown()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var database = await fixture.NewChainDatabaseAsync("accepted");
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), database);
        await WriteAsync(provider, 4);
        await SealEverythingAsync(provider);
        time.Advance(TimeSpan.FromHours(3));
        await WriteAsync(provider, 3);
        await SealEverythingAsync(provider);
        var chains = new FakeChains(await AnchorsOfAsync(provider));

        // The backup from before the last three events comes back, and the unit seals new events at those positions.
        await TamperAsync(database, "delete from kernel_audit.events where chain_position > 4; delete from kernel_audit.seals where last_position > 4");
        await WriteAsync(provider, 2);
        await SealEverythingAsync(provider);
        var reported = await Verifier(chains, database).VerifyAsync(Due(database, full: true), Cancel);
        Assert.Equal((AuditVerificationResults.Restored, 4L), (reported.Result, reported.RestorePosition));

        // The operator accepts the restore point, as the control plane does; the unit sends its anchors again from it, now
        // in segment 2, and the lost positions' anchors stay in segment 1.
        var accepted = AuditChainSegments.Accept(4, [.. chains.Anchors.Select(a => new AuditAnchor(a.Kind, a.Position, a.Hash, a.At, a.Segment))], [])!;
        Assert.Equal((2, 5L, 5L, 7L), (accepted.Segment.Number, accepted.Segment.StartPosition, accepted.LostFrom, accepted.LostTo));
        var restore = new AuditRestoreInfo(Ids.New(), AuditFixture.ChainTenant, 2, 5, accepted.Segment.PreviousHash, 4, accepted.RestoreAt, 5, 7, "INC-77", "Restored from last night's backup", "Olga Operator", time.GetUtcNow());
        chains.Restores.Add(restore);
        chains.Anchors.AddRange((await AnchorsOfAsync(provider)).Where(a => a.Position > 4).Select(a => a with { Segment = 2 }));

        var full = await Verifier(chains, database).VerifyAsync(Due(database, full: true), Cancel);
        Assert.Equal(AuditVerificationResults.Passed, full.Result);
        Assert.Empty(full.Problems);
        chains.Latest = full;
        await WriteAsync(provider, 2);
        await SealEverythingAsync(provider);
        Assert.Equal(AuditVerificationResults.Passed, (await Verifier(chains, database).VerifyAsync(Due(database, full: false), Cancel)).Result);

        // The unit records the restore once, with the range lost, and its own trail names it.
        var notice = new AcceptedRestoreNotice(2, 5, restore.PreviousHash, 4, restore.RestoreAt, 5, 7, restore.Ticket, restore.Reason, restore.AcceptedBy, restore.AcceptedAt);
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuditRestoreRecorder>().RecordAsync([notice], Cancel));
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AuditRestoreRecorder>().RecordAsync([notice], Cancel));
            var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var shown = await context.ChainRestores.AsNoTracking().SingleAsync(Cancel);
            Assert.Equal((4L, 5L, 7L, "Olga Operator"), (shown.RestorePosition, shown.LostFrom, shown.LostTo, shown.AcceptedBy));
            var recorded = await context.Events.AsNoTracking().SingleAsync(e => e.EventType == AuditEvents.RestoreAccepted, Cancel);
            Assert.Equal((AuditSeverity.High, restore.Reason, shown.Id), (recorded.Severity, recorded.Reason, recorded.RecordId));
        }

        // The link between the segments counts: a segment that says another restore point is a broken chain.
        chains.Restores[0] = restore with { PreviousHash = new string('f', 64) };
        var relinked = await Verifier(chains, database).VerifyAsync(Due(database, full: true), Cancel);
        Assert.Equal(AuditVerificationResults.Failed, relinked.Result);
        Assert.Contains(relinked.Problems, p => p.Kind == ChainProblemKinds.Relinked && p.Position == 5);
    }

    // The canonical form as the README documents it, written apart from Iron.Kernel.Audit's: a JSON array built with the
    // framework's writer for structure, and the form's own escaping applied by hand to every text.
    private static string IndependentHash(ChainedEvent e, string previousHash, short version)
    {
        static string Escape(string? text)
        {
            if (text is null)
            {
                return "null";
            }

            var builder = new StringBuilder("\"");
            foreach (var c in text.Normalize(NormalizationForm.FormC))
            {
                builder.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ when c < 0x20 => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            }

            return builder.Append('"').ToString();
        }

        static string Id(Guid? id) => id is { } value ? Escape(value.ToString().ToLowerInvariant()) : "null";

        static string Json(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => Escape(p.Name) + ":" + Json(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Json)) + "]",
            JsonValueKind.String => Escape(element.GetString()),
            JsonValueKind.Number => element.GetDecimal().ToString("0.############################", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "null",
        };

        var details = string.IsNullOrWhiteSpace(e.Details) ? "null" : Json(JsonDocument.Parse(e.Details).RootElement);
        var parts = new List<string>
        {
            Escape($"iron-audit-v{version}"), Id(e.Id), e.Position.ToString(CultureInfo.InvariantCulture), Escape(e.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)),
            Id(e.CreatedBy), Escape(e.EventType), Escape(e.Outcome), Id(e.ActorId), Id(e.OnBehalfOfId), Id(e.ImpersonatorId), Escape(e.Source), Escape(e.Severity),
            Escape(e.Target), Escape(e.RecordType), Id(e.RecordId), Escape(e.Reason), Escape(e.CorrelationId), details,
        };
        if (version >= 2)
        {
            // Version 2 hashes a save's field changes after the details (ADR-0051).
            parts.Add(string.IsNullOrWhiteSpace(e.Changes) ? "null" : Json(JsonDocument.Parse(e.Changes).RootElement));
        }

        parts.Add(Escape(previousHash));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("[" + string.Join(",", parts) + "]"))).ToLowerInvariant();
    }

    private async Task<ServiceProvider> WriteAndSealAsync(string database, int count)
    {
        var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(SystemPrincipal.SystemId), database);
        await WriteAsync(provider, count);
        await SealEverythingAsync(provider);
        return provider;
    }

    internal static async Task WriteAsync(ServiceProvider provider, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent("tests.chain.event", AuditOutcome.Success, Ids.New(), $"event {i}", new Dictionary<string, string> { ["n"] = i.ToString(CultureInfo.InvariantCulture) }), Cancel);
        }
    }

    internal static async Task SealEverythingAsync(ServiceProvider provider)
    {
        while (true)
        {
            await using var scope = provider.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<AuditSealer>().SealAsync(Cancel) is null)
            {
                return;
            }
        }
    }

    /// <summary>The anchors a unit would have sent: one per seal, its last position and hash, at its time.</summary>
    internal static async Task<List<AuditAnchorInfo>> AnchorsOfAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await context.Seals.AsNoTracking().OrderBy(s => s.LastPosition)
            .Select(s => new AuditAnchorInfo(AuditAnchorKinds.Seal, s.LastPosition, s.LastHash, s.CreatedAt, s.CreatedAt))
            .ToListAsync(Cancel);
    }

    internal static async Task<IReadOnlyList<SealedEvent>> ReadChainAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await AuditChainVerifier.ReadOnlineAsync(dataSource, 1, Cancel);
    }

    private static async Task TamperAsync(string ownerConnection, string sql)
    {
        // What someone who got past the database's permissions would do: as the server's superuser, with the guard off.
        await using var connection = await OpenAsync(ownerConnection);
        await RunAsync(connection, $"alter table kernel_audit.events disable trigger events_guard; {sql}; alter table kernel_audit.events enable trigger events_guard");
    }

    internal AuditChainVerifier Verifier(FakeChains chains, string database)
    {
        var options = new FleetOptions();
        options.Database.AdminConnectionString = database;
        options.Storage.AccountConnectionString = fixture.StorageConnectionString;
        var secrets = new MemorySecrets();
        secrets.Values[$"tenants/{AuditFixture.ChainTenant}/database"] = new Dictionary<string, string> { ["audit_password"] = AuditFixture.ReaderPassword };
        return new AuditChainVerifier(chains, secrets, options, new ArchivedChainReader(options), TimeProvider.System);
    }

    internal static AuditVerificationDue Due(string database, bool full, string container = AuditFixture.Container) =>
        new(AuditFixture.ChainTenant, new NpgsqlConnectionStringBuilder(database).Database!, container, full, null);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        return connection;
    }

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancel);
    }
}

/// <summary>The control plane's side of a chain as a test sets it: the anchors, and the latest verification.</summary>
public sealed class FakeChains(List<AuditAnchorInfo> anchors) : ITenantAuditChains
{
    public List<AuditAnchorInfo> Anchors { get; set; } = anchors;

    public AuditVerificationInfo? Latest { get; set; }

    public List<AuditVerificationInfo> Recorded { get; } = [];

    public List<AuditRestoreInfo> Restores { get; } = [];

    public Task BindUnitClientAsync(string slug, Guid principalId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditRestoreInfo>> RestoresAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditRestoreInfo>>(Restores);

    public Task<IReadOnlyList<AuditAnchorInfo>> AnchorsAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditAnchorInfo>>(Anchors);

    public Task<AuditVerificationInfo?> LatestVerificationAsync(string slug, bool fullOnly, CancellationToken cancellationToken) => Task.FromResult(Latest);

    public Task<IReadOnlyList<AuditVerificationDue>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditVerificationDue>>([]);

    public Task RecordVerificationAsync(AuditVerificationInfo verification, Guid? requestId, CancellationToken cancellationToken)
    {
        Recorded.Add(verification);
        return Task.CompletedTask;
    }
}

public sealed class MemorySecrets : ISecretStore
{
    public Dictionary<string, IReadOnlyDictionary<string, string>> Values { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Values.GetValueOrDefault(path));

    public Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        Values[path] = values;
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
