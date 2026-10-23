using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Compliance.Services;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// Signatures bound to the version they sign (S3 T7, TF-063, TF-219, ADR-0051), on the sample module's document: approved
/// by signature, changed afterwards, and shown with its signature against the earlier version.
/// </summary>
[Collection(ComplianceCollection.Name)]
public class SignatureTests(ComplianceFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-063 AC2", "TF-063 AC3", "TF-062 AC1", "TF-062 AC3", "ADR-0051")]
    public async Task ADocumentApprovedBySignatureAndChangedAfterShowsItsSignatureAgainstTheEarlierVersion()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var signer = new StandInSigner("Ada Lovelace", "ada@echo.test");
        await using var provider = fixture.BuildServices(time, signer);
        var document = await CreateAsync(provider, "Supply agreement", "Ten valves a month.", 1200.50m);

        var signed = await SignAsync(provider, document, SignatureMeanings.Approved, "Agreed with finance.");
        time.Advance(TimeSpan.FromMinutes(5));
        await ReviseAsync(provider, document.Id, "Twelve valves a month.", 1440.60m);

        await using var scope = provider.CreateAsyncScope();
        var signatures = scope.ServiceProvider.GetRequiredService<ISignatures>();
        var view = Assert.Single(await signatures.ForRecordAsync(SampleDocument.RecordType, document.Id, Cancel));
        var stored = await scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Signatures.AsNoTracking().SingleAsync(s => s.Id == view.Id, Cancel);
        var history = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == document.Id).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(Cancel);

        // Who, how, when, what it means and which version (TF-063 AC2).
        Assert.Equal(signed.Id, view.Id);
        Assert.Equal(("Ada Lovelace", "ada@echo.test", SignatureMethods.Password, SignatureMeanings.Approved), (view.SignerName, view.SignerLoginId, view.Method, view.Meaning));
        Assert.Equal(signed.SignedAt.UtcTicks / 10, view.SignedAt.UtcTicks / 10); // the database keeps microseconds
        Assert.Equal("Agreed with finance.", view.Comment);
        Assert.Equal(1, view.RecordVersion);
        Assert.True(view.HashMatches);

        // The record changed since (TF-063 AC3): the signature applies to version 1, the record is at version 2.
        Assert.Equal(2, view.CurrentVersion);
        Assert.True(view.RecordChanged);
        Assert.Contains("Ten valves a month.", stored.Snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Twelve", stored.Snapshot, StringComparison.Ordinal);
        Assert.Equal(SnapshotCanonicalForm.Hash(stored.Snapshot), stored.SnapshotHash);

        // The document's history: created, signed, changed in two fields, the bank account masked (TF-062).
        Assert.Equal([AuditEvents.RecordCreated, ComplianceAuditEvents.SignatureSigned, AuditEvents.RecordChanged], history.Select(e => e.EventType));
        var created = AuditEventFields(history[0]);
        Assert.Equal(("****3000", FieldTreatments.Masked), (created.Single(f => f.Field == "bank_account").New, created.Single(f => f.Field == "bank_account").Treatment));
        var changed = AuditEventFields(history[2]);
        Assert.Equal(["amount", "body"], changed.Select(f => f.Field).Order(StringComparer.Ordinal));
        Assert.Equal(("1200.50", "1440.60"), (changed.Single(f => f.Field == "amount").Old, changed.Single(f => f.Field == "amount").New));
    }

    [Fact]
    [Proves("TF-219 AC2", "ADR-0051")]
    public async Task ASignatureAttachedToAnotherRecordOrVersionIsRefusedBecauseTheHashDoesNotMatch()
    {
        var signer = new StandInSigner();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), signer);
        var signedDocument = await CreateAsync(provider, "Signed", "The same words.", 10m);
        var twin = await CreateAsync(provider, "Signed", "The same words.", 10m);
        var signature = await SignAsync(provider, signedDocument, SignatureMeanings.Approved, null);

        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SignatureService>();
            var same = await service.AttachAsync(signature.Id, SampleDocument.RecordType, signedDocument.Id, Cancel);
            var other = await service.AttachAsync(signature.Id, SampleDocument.RecordType, twin.Id, Cancel);
            Assert.True(same.IsSuccess);
            Assert.Equal(ComplianceErrors.HashMismatch, other.Error.Code);
        }

        await ReviseAsync(provider, signedDocument.Id, "Other words.", 11m);
        await using (var scope = provider.CreateAsyncScope())
        {
            var laterVersion = await scope.ServiceProvider.GetRequiredService<SignatureService>().AttachAsync(signature.Id, SampleDocument.RecordType, signedDocument.Id, Cancel);
            var refusals = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .Where(e => e.EventType == ComplianceAuditEvents.SignatureAttachRefused && (e.RecordId == twin.Id || e.RecordId == signedDocument.Id)).ToListAsync(Cancel);

            Assert.Equal(ComplianceErrors.HashMismatch, laterVersion.Error.Code);
            Assert.Equal(2, refusals.Count);
            Assert.All(refusals, e => Assert.Equal(AuditOutcome.Denied, e.Outcome));
        }
    }

    [Fact]
    [Proves("TF-063 AC1", "ADR-0051")]
    public async Task APolicyDecidesTheMeaningsAndAProofIsTakenOnceInTheRequestThatMadeIt()
    {
        var signer = new StandInSigner();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), signer);
        var document = await CreateAsync(provider, "Narrowed", "Words.", 1m);
        await using var scope = provider.CreateAsyncScope();
        var signatures = scope.ServiceProvider.GetRequiredService<ISignatures>();
        var policy = await signatures.PolicyAsync(SampleDocument.RecordType, Cancel);
        var proof = (await signatures.AuthenticateAsync(SampleDocument.RecordType, SigningCredentials.None, Cancel)).Value;

        var disallowed = await signatures.RecordAsync(proof, SampleDocuments.Snapshot(document), SignatureMeanings.Reviewed, null, Cancel);
        var forged = await signatures.RecordAsync(proof with { Token = Ids.New() }, SampleDocuments.Snapshot(document), SignatureMeanings.Approved, null, Cancel);
        var made = await signatures.RecordAsync(proof, SampleDocuments.Snapshot(document), SignatureMeanings.Approved, null, Cancel);
        var reused = await signatures.RecordAsync(proof, SampleDocuments.Snapshot(document), SignatureMeanings.Approved, null, Cancel);

        Assert.True(policy.Required);
        Assert.True(policy.Reauthenticate);
        Assert.Equal(ComplianceErrors.MeaningNotAllowed, disallowed.Error.Code);
        Assert.Equal(ComplianceErrors.ProofInvalid, forged.Error.Code);
        Assert.True(made.IsSuccess);
        Assert.Equal(ComplianceErrors.ProofInvalid, reused.Error.Code);
        Assert.Equal(1, signer.Proofs);
    }

    [Fact]
    [Proves("TF-063 AC6", "ADR-0051")]
    public async Task ASignatureRefusedBeforeAnyCredentialIsRecordedAsAnAttempt()
    {
        var signer = new StandInSigner { Refusal = Error.Forbidden(ComplianceErrors.Impersonation, "You are signed in as someone else.") };
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), signer);
        await using var scope = provider.CreateAsyncScope();

        var refused = await scope.ServiceProvider.GetRequiredService<ISignatures>().AuthenticateAsync(SampleDocument.RecordType, SigningCredentials.None, Cancel);
        var recorded = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == ComplianceAuditEvents.SignatureRefused && e.ActorId == signer.SignerId).ToListAsync(Cancel);

        Assert.Equal(ComplianceErrors.Impersonation, refused.Error.Code);
        var attempt = Assert.Single(recorded);
        Assert.Equal((AuditOutcome.Denied, AuditSeverity.High, ComplianceErrors.Impersonation), (attempt.Outcome, attempt.Severity, attempt.Reason));
    }

    private static async Task<SampleDocument> CreateAsync(IServiceProvider provider, string title, string body, decimal amount)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDocumentsDbContext>();
        var root = (await scope.ServiceProvider.GetRequiredService<IOrganisationUnits>().RootAsync(Cancel))!.Id;
        var document = new SampleDocument(root, title, body, amount, "GB33BUKB20201555553000");
        context.Documents.Add(document);
        await context.SaveChangesAsync(Cancel);
        return document;
    }

    private static async Task ReviseAsync(IServiceProvider provider, Guid id, string body, decimal amount)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleDocumentsDbContext>();
        var document = await context.Documents.SingleAsync(d => d.Id == id, Cancel);
        document.Revise(body, amount);
        await context.SaveChangesAsync(Cancel);
    }

    /// <summary>Signs the document as its module would: the signer proved first, then the signature in a transaction.</summary>
    private static async Task<SignatureView> SignAsync(IServiceProvider provider, SampleDocument document, string meaning, string? comment)
    {
        await using var scope = provider.CreateAsyncScope();
        var signatures = scope.ServiceProvider.GetRequiredService<ISignatures>();
        var proof = await signatures.AuthenticateAsync(SampleDocument.RecordType, new SigningCredentials("ada@echo.test", "a password", null, null, null), Cancel);
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(Cancel);
        var current = await scope.ServiceProvider.GetRequiredService<SampleDocumentsDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id, Cancel);
        var signed = await signatures.RecordAsync(proof.Value, SampleDocuments.Snapshot(current), meaning, comment, Cancel);
        await transaction.CommitAsync(Cancel);
        return signed.Value;
    }

    private static IReadOnlyList<FieldChange> AuditEventFields(AuditEventRecord record) =>
        System.Text.Json.JsonSerializer.Deserialize<List<FieldChange>>(record.Changes!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
}
