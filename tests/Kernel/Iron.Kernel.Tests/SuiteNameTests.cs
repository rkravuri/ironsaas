using System.Reflection;
using System.Reflection.Emit;

namespace Iron.Kernel.Tests;

/// <summary>ADR-0043: a suite's declared name, its short name, and the suite an assembly belongs to.</summary>
public class SuiteNameTests
{
    [Theory]
    [InlineData("Procurement", true)]
    [InlineData("School", true)]
    [InlineData("HumanResources", true)]
    [InlineData("procurement", false)]
    [InlineData("Kernel", false)]
    [InlineData("Business", false)]
    [InlineData("Foundation", false)]
    [InlineData("FOUNDATION", false)]
    [InlineData("e-Procurement", false)]
    [InlineData("Procurement2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ADeclaredSuiteNameIsOneWordInPascalCaseAndNotALayer(string? name, bool valid)
    {
        Assert.Equal(valid, SuiteName.IsValid(name));
    }

    [Theory]
    [InlineData("procurement", true)]
    [InlineData("school", true)]
    [InlineData("Procurement", false)]
    [InlineData("foundation", false)]
    [InlineData("kernel", false)]
    [InlineData("procurement_orders", false)]
    public void AShortNameIsTheDeclaredNameInLowercase(string name, bool valid)
    {
        Assert.Equal(valid, SuiteName.IsShortName(name));
    }

    [Fact]
    public void TheShortNameOfProcurementIsProcurementInLowercase()
    {
        Assert.Equal("procurement", SuiteName.ShortName("Procurement"));
        Assert.Throws<ArgumentException>(() => SuiteName.ShortName("Business"));
    }

    [Fact]
    public void AnAssemblyBelongsToTheSuiteItsBuildWroteAndOtherwiseToTheFoundation()
    {
        // The build writes [assembly: AssemblyMetadata("IronSuite", "Procurement")] for a suite project (Directory.Build.props).
        var metadata = new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
            [SuiteName.MetadataKey, "Procurement"]);
        var suite = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Iron.Suites.Procurement.Sample"), AssemblyBuilderAccess.Run, [metadata]);

        Assert.Equal("procurement", SuiteName.Of(suite));
        Assert.Equal(SuiteName.Foundation, SuiteName.Of(typeof(SuiteName).Assembly));
    }
}
