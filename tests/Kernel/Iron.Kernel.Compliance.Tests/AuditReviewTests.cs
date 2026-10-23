using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Compliance.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>Periodic reviews of the audit trail (S3 T7, TF-219 AC3, ADR-0051): recorded, always signed, listed by month.</summary>
[Collection(ComplianceCollection.Name)]
public class AuditReviewTests(ComplianceFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-219 AC3", "TF-063 AC2", "ADR-0051")]
    public async Task AReviewRecordsThePeriodTheScopeTheOutcomeAndTheReviewerAndIsSignedReviewed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2031, 4, 20, 9, 0, 0, TimeSpan.Zero));
        var signer = new StandInSigner("Rita Reviewer", "rita@echo.test");
        await using var provider = fixture.BuildServices(time, signer);
        var unit = Ids.New();
        await using var scope = provider.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<AuditTrailReviews>();

        var recorded = await reviews.RecordAsync(
            new AuditReviewInput(new DateOnly(2031, 3, 1), new DateOnly(2031, 3, 31), [unit], ["identity.login_failed", "authz.role_assigned"], AuditReviewOutcomes.Findings, "Two failed sign-ins from one address; asked the person.", [new ReviewSearch("Failed sign-ins in March", "/audit?event_type=identity.login_failed&from=2031-03-01")]),
            SigningCredentials.None,
            Cancel);
        var found = await reviews.FindAsync(recorded.Value.Review.Id, Cancel);
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == ComplianceAuditEvents.AuditReviewRecorded && e.RecordId == recorded.Value.Review.Id, Cancel);

        var review = found!.Review;
        Assert.Equal((new DateOnly(2031, 3, 1), new DateOnly(2031, 3, 31)), (review.PeriodFrom, review.PeriodTo));
        Assert.Equal((signer.SignerId, "Rita Reviewer"), (review.ReviewerId, review.ReviewerName));
        Assert.Equal([unit], review.UnitIds);
        Assert.Equal(["authz.role_assigned", "identity.login_failed"], review.EventTypes);
        Assert.Equal(AuditReviewOutcomes.Findings, review.Outcome);
        Assert.Equal("/audit?event_type=identity.login_failed&from=2031-03-01", Assert.Single(found.Searches).Link);
        Assert.Equal((SignatureMeanings.Reviewed, 1, true, false), (found.Signature!.Meaning, found.Signature.RecordVersion, found.Signature.HashMatches, found.Signature.RecordChanged));
        Assert.Equal(signer.SignerId, audited.ActorId);
    }

    [Fact]
    [Proves("TF-219 AC3", "ADR-0051")]
    public async Task TheConsoleListsReviewsByMonthSoAMonthNobodyReviewedShows()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2032, 6, 10, 9, 0, 0, TimeSpan.Zero));
        await using var provider = fixture.BuildServices(time, new StandInSigner());
        await using var scope = provider.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<AuditTrailReviews>();
        await reviews.RecordAsync(new AuditReviewInput(new DateOnly(2032, 3, 1), new DateOnly(2032, 3, 31), [], [], AuditReviewOutcomes.NoFindings, null, []), SigningCredentials.None, Cancel);
        await reviews.RecordAsync(new AuditReviewInput(new DateOnly(2032, 5, 1), new DateOnly(2032, 5, 31), [], [], AuditReviewOutcomes.NoFindings, null, []), SigningCredentials.None, Cancel);

        var months = await reviews.CoverageAsync(4, Cancel);

        Assert.Equal([new DateOnly(2032, 6, 1), new DateOnly(2032, 5, 1), new DateOnly(2032, 4, 1), new DateOnly(2032, 3, 1)], months.Select(m => m.From));
        Assert.Equal([0, 1, 0, 1], months.Select(m => m.Reviews.Count));
        Assert.Equal(new DateOnly(2032, 4, 30), months[2].To);
    }

    [Fact]
    [Proves("TF-219 AC3", "TF-063 AC1", "ADR-0051")]
    public async Task AnIncompleteReviewIsRefusedAndAReviewIsNeverRecordedUnsigned()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2033, 2, 10, 9, 0, 0, TimeSpan.Zero));
        var signer = new StandInSigner();
        await using var provider = fixture.BuildServices(time, signer);
        await using var scope = provider.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<AuditTrailReviews>();
        var january = (From: new DateOnly(2033, 1, 1), To: new DateOnly(2033, 1, 31));

        var noNotes = await reviews.RecordAsync(new AuditReviewInput(january.From, january.To, [], [], AuditReviewOutcomes.Findings, "  ", []), SigningCredentials.None, Cancel);
        var backwards = await reviews.RecordAsync(new AuditReviewInput(january.To, january.From, [], [], AuditReviewOutcomes.NoFindings, null, []), SigningCredentials.None, Cancel);
        var future = await reviews.RecordAsync(new AuditReviewInput(january.From, new DateOnly(2033, 3, 1), [], [], AuditReviewOutcomes.NoFindings, null, []), SigningCredentials.None, Cancel);
        var elsewhere = await reviews.RecordAsync(new AuditReviewInput(january.From, january.To, [], [], AuditReviewOutcomes.NoFindings, null, [new ReviewSearch("Look", "https://example.test/audit")]), SigningCredentials.None, Cancel);
        signer.Refusal = Error.Forbidden(ComplianceErrors.Invalid, "That did not confirm it is you.");
        await ComplianceFixture.PolicyAsync(provider, AuditTrailReview.RecordType, new { required = false, reauthenticate = false });
        var unsigned = await reviews.RecordAsync(new AuditReviewInput(january.From, january.To, [], [], AuditReviewOutcomes.NoFindings, null, []), SigningCredentials.None, Cancel);
        var policy = await scope.ServiceProvider.GetRequiredService<ISignatures>().PolicyAsync(AuditTrailReview.RecordType, Cancel);
        var stored = await scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().AuditTrailReviews.AsNoTracking().CountAsync(r => r.PeriodFrom == january.From, Cancel);

        Assert.Equal(["notes"], noNotes.Error.Fields.Select(f => f.Field));
        Assert.Equal(["period_to"], backwards.Error.Fields.Select(f => f.Field));
        Assert.Equal(["period_to"], future.Error.Fields.Select(f => f.Field));
        Assert.Equal(["searches"], elsewhere.Error.Fields.Select(f => f.Field));
        Assert.Equal(ComplianceErrors.Invalid, unsigned.Error.Code);
        Assert.True(policy.Required);
        Assert.True(policy.Reauthenticate);
        Assert.Equal(0, stored);
    }
}
