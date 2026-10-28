using Iron.Kernel.Files.Data;
using Iron.Kernel.Files.Services;

namespace Iron.Kernel.Files.Tests;

/// <summary>TF-071 AC4 (ADR-0053): a file's name is display text, its last path part, without what could mislead or harm.</summary>
public class FileNameTests
{
    [Theory]
    [Proves("TF-071 AC4", "ADR-0053")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("C:\\Users\\ada\\invoice.pdf", "invoice.pdf")]
    [InlineData("/var/tmp/report final.xlsx", "report final.xlsx")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("line\r\nbreak\t.txt", "linebreak.txt")]
    [InlineData("nul\0byte.txt", "nulbyte.txt")]
    [InlineData("quote\".txt", "quote_.txt")]
    [InlineData("a<b>c|d?e*f:g.txt", "a_b_c_d_e_f_g.txt")]
    [InlineData("file.txt:hidden-stream", "file.txt_hidden-stream")]
    [InlineData("..", "file")]
    [InlineData("...hidden", "hidden")]
    [InlineData("trailing. . .", "trailing")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("lpt1", "_lpt1")]
    [InlineData("   ", "file")]
    [InlineData(null, "file")]
    [InlineData("many    spaces   here.doc", "many spaces here.doc")]
    [InlineData("e\u0301te\u0301.txt", "\u00e9t\u00e9.txt")]
    [InlineData("Lieferschein Nr. 7.pdf", "Lieferschein Nr. 7.pdf")]
    public void AMaliciousOrAwkwardNameIsNormalisedToSafeDisplayText(string? given, string expected)
    {
        Assert.Equal(expected, FileNames.Normalise(given));
    }

    [Fact]
    [Proves("TF-071 AC4", "ADR-0053")]
    public void ALongNameIsShortenedToTheLimitAndKeepsItsExtension()
    {
        var name = FileNames.Normalise(new string('a', 400) + ".pdf");
        var emoji = FileNames.Normalise(new string('b', 250) + "\U0001F600\U0001F600\U0001F600" + ".txt");

        Assert.Equal(StoredFile.NameMaxLength, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
        Assert.Equal(new string('b', 250) + ".txt", emoji); // the emoji that would be cut in two goes whole
    }
}
