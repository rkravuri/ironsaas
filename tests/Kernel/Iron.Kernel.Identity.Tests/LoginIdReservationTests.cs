using System.Net;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>A login identifier is never given to a second person (S3 T7, TF-219, ADR-0051).</summary>
[Collection(IdentityCollection.Name)]
public class LoginIdReservationTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("TF-219 AC4", "ADR-0051")]
    public async Task ALoginIdentifierStaysWithItsPersonAfterDeactivationAndAfterRemoval()
    {
        await using var host = await UnitHost.StartAsync(fixture, await fixture.CreateTenantDatabaseAsync($"reuse_{Ids.New():N}"));
        var (_, admin) = await host.AdministratorAsync();
        var root = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Get, "/api/v1/me", admin))).GetProperty("home_unit").GetProperty("id").GetGuid();
        var loginId = $"leaver-{Ids.New():N}@acme.test";
        var leaver = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId, display_name = "Lee Leaver", home_unit_id = root }), HttpStatusCode.Created))
            .GetProperty("user").GetProperty("id").GetGuid();
        using var deactivated = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{leaver}/deactivate", admin);
        using var whileDeactivated = await host.SendAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId, display_name = "Someone New", home_unit_id = root });

        // Retention removes the person: the database's owner deletes their rows, as a retention job would.
        await using (var connection = new NpgsqlConnection(host.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var events = new NpgsqlCommand($"delete from kernel_identity.security_events where principal_id = '{leaver}'", connection);
            await events.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var delete = new NpgsqlCommand($"delete from kernel_identity.principals where id = '{leaver}'", connection);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        using var afterRemoval = await host.SendAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId.ToUpperInvariant(), display_name = "Someone New", home_unit_id = root });
        var direct = await Assert.ThrowsAsync<DbUpdateException>(() => host.WithScopeAsync(async services =>
        {
            var context = services.GetRequiredService<IdentityDbContext>();
            context.Principals.Add(new Principal(loginId, PrincipalType.User, "Straight In", loginId));
            return await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }));
        var reservation = await host.WithScopeAsync(services => services.GetRequiredService<IdentityDbContext>().LoginIdReservations.AsNoTracking().SingleAsync(r => r.LoginId == loginId, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(IdentityErrors.LoginIdTaken, await UnitHost.ProblemAsync(whileDeactivated, HttpStatusCode.Conflict));
        Assert.Equal(IdentityErrors.LoginIdRetired, await UnitHost.ProblemAsync(afterRemoval, HttpStatusCode.Conflict));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(direct.InnerException).SqlState);
        Assert.Equal(leaver, reservation.PrincipalId);
    }

    [Fact]
    [Proves("TF-219 AC4", "ADR-0051")]
    public async Task EveryPrincipalHasItsReservationAndItsIdentifierNeverChanges()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync();

        var reservation = await host.WithScopeAsync(services => services.GetRequiredService<IdentityDbContext>().LoginIdReservations.AsNoTracking().SingleAsync(r => r.PrincipalId == person.Id, TestContext.Current.CancellationToken));
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var rename = new NpgsqlCommand($"update kernel_identity.principals set login_id = 'someone-else@acme.test' where id = '{person.Id}'", connection);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => rename.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(person.LoginId, reservation.LoginId);
        Assert.Equal(person.Id, reservation.Id);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }
}
