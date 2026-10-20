using System.Text.Json;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Iron.Kernel.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Settings.Tests;

/// <summary>
/// Settings when the tree changes (TF-223, ADR-0038): a unit that moves keeps its own values and takes the rest from its
/// new parent; a merge moves the values of the unit that goes, drops those the surviving unit has its own of, and writes both
/// to the history.
/// </summary>
[Collection(SettingsCollection.Name)]
public class UnitChangeTests(SettingsFixture fixture)
{
    [Fact]
    [Proves("TF-223 AC3", "ADR-0038")]
    public async Task AUnitKeepsItsOwnSettingsWhenItMovesAndTakesTheRestFromItsNewParent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var region = await SettingsFixture.UnitAsync(provider, "Region");
        var other = await SettingsFixture.UnitAsync(provider, "Other region");
        var branch = await SettingsFixture.UnitAsync(provider, "Branch", region);
        var desk = await SettingsFixture.UnitAsync(provider, "Desk", branch);

        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettings>();
        await SetAsync(writer, ProbeSettings.Tone.Key, branch, "\"brief\"");
        await SetAsync(writer, ProbeSettings.Batch.Key, region, "100");
        await SetAsync(writer, ProbeSettings.Batch.Key, other, "200");
        Assert.Equal(100, await settings.GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(desk), ct));

        var moved = await scope.ServiceProvider.GetRequiredService<OrganisationUnitService>().MoveAsync(branch, other, ct);

        Assert.True(moved.IsSuccess);
        Assert.Equal("brief", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(desk), ct));
        Assert.Equal("brief", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(branch), ct));
        Assert.Equal(200, await settings.GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(desk), ct));
    }

    [Fact]
    [Proves("ADR-0038")]
    public async Task AMergeMovesTheUnitsValuesAndDropsThoseTheSurvivingUnitHasItsOwnOf()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var head = await SettingsFixture.UnitAsync(provider, "Head office");
        var branch = await SettingsFixture.UnitAsync(provider, "Branch");

        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        await SetAsync(writer, ProbeSettings.Tone.Key, branch, "\"brief\"");
        await SetAsync(writer, ProbeSettings.Batch.Key, branch, "120");
        await SetAsync(writer, ProbeSettings.Batch.Key, head, "300");
        var merges = scope.ServiceProvider.GetRequiredService<UnitMergeService>();

        var preview = await merges.PreviewAsync(branch, head, ct);
        var merged = await merges.MergeAsync(branch, head, ct);

        var part = Assert.Single(preview.Value.Parts, p => p.Code == UnitMergeParts.Settings);
        Assert.Equal([ProbeSettings.Tone.Key], part.Ids);
        Assert.Equal([ProbeSettings.Batch.Key], part.Conflicts);
        Assert.True(merged.IsSuccess, merged.IsFailure ? merged.Error.Message : null);
        await using var read = provider.CreateAsyncScope();
        var settings = read.ServiceProvider.GetRequiredService<ISettings>();
        Assert.Equal("brief", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(head), ct));
        Assert.Equal(300, await settings.GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(head), ct));
        var context = read.ServiceProvider.GetRequiredService<SettingsDbContext>();
        Assert.Empty(await context.Values.AsNoTracking().Where(v => v.TargetId == branch).ToListAsync(ct));
        var history = await context.History.AsNoTracking().Where(h => h.TargetId == branch || h.TargetId == head).ToListAsync(ct);
        Assert.Equal(2, history.Count(h => h.TargetId == branch && h.Kind == SettingChange.ResetKind));
        Assert.Contains(history, h => h.TargetId == head && h.Key == ProbeSettings.Tone.Key && h.Kind == SettingChange.SetKind && h.After == "\"brief\"");
    }

    private static async Task SetAsync(SettingsWriter writer, string key, Guid unit, string json)
    {
        using var value = JsonDocument.Parse(json);
        var result = await writer.SetAsync(key, SettingPlace.Unit(unit), value.RootElement.Clone(), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }
}
