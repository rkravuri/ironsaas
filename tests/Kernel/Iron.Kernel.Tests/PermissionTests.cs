namespace Iron.Kernel.Tests;

public class PermissionTests
{
    [Fact]
    [Proves("ADR-0012")]
    public void SplitsItsNameIntoModuleResourceAndAction()
    {
        var permission = new Permission("identity.users.read", "List and view users", Risk.Low);

        Assert.Equal("identity", permission.Module);
        Assert.Equal("users", permission.Resource);
        Assert.Equal("read", permission.Action);
        Assert.Equal("identity.users.read", permission.ToString());
    }

    [Theory]
    [Proves("TF-030 AC4", "ADR-0012")]
    [InlineData("Identity.Users.Read")]
    [InlineData("identity.users")]
    [InlineData("identity.users.read.all")]
    [InlineData("identity..read")]
    [InlineData("1identity.users.read")]
    [InlineData("identity.users.read ")]
    [InlineData("")]
    public void RefusesANameThatIsNotModuleResourceAction(string name)
    {
        Assert.False(Permission.IsValidName(name));
        Assert.Throws<ArgumentException>(() => new Permission(name, "Something", Risk.Low));
    }

    [Fact]
    [Proves("TF-030 AC4", "ADR-0012")]
    public void RefusesAnEmptyDescription()
    {
        Assert.Throws<ArgumentException>(() => new Permission("identity.users.read", " ", Risk.Low));
    }

    [Fact]
    [Proves("ADR-0012")]
    public void TwoPermissionsWithTheSameNameAreEqual()
    {
        var a = new Permission("files.documents.upload", "Upload documents", Risk.Medium);
        var b = new Permission("files.documents.upload", "Different words", Risk.High);
        var c = new Permission("files.documents.read", "Read documents", Risk.Low);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }
}
