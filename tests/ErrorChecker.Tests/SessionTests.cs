using ErrorChecker.Core;
using Xunit;

public class SessionTests
{
    private const string Share = @"\\srv\aide";

    [Fact]
    public void Code_is_short_readable_and_unambiguous()
    {
        var root = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        try
        {
            var session = Session.CreateNew(root);
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", session.Display);
            Assert.Equal($"errorchecker:{session.Code}", session.Link);
            Assert.True(Directory.Exists(session.Folder));
            Assert.DoesNotContain(session.Code, session.Folder);   // le dossier ne révèle pas le code
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("K7QM-2XPA-9TRD")]
    [InlineData("k7qm 2xpa 9trd")]
    [InlineData("K7QM2XPA9TRD")]
    [InlineData("errorchecker:K7QM2XPA9TRD")]
    [InlineData("\"errorchecker://K7QM2XPA9TRD/\"")]
    [InlineData("K7QM-2XPA-9TRD ")]
    public void Typed_code_and_link_open_the_same_session(string input)
    {
        var expected = Session.Open(Share, "K7QM2XPA9TRD");
        var opened = Session.Open(Share, input);
        Assert.Equal(expected.Folder, opened.Folder);
        Assert.Equal(expected.Key, opened.Key);
    }

    [Fact]
    public void Look_alike_letters_are_read_as_digits() =>
        Assert.Equal(Session.Open(Share, "10Q0-0000-0000").Key, Session.Open(Share, "lOQo-oooo-oooo").Key);

    [Fact]
    public void Different_codes_give_different_folders_and_keys()
    {
        var a = Session.Open(Share, "K7QM2XPA9TRD");
        var b = Session.Open(Share, "K7QM2XPA9TRE");
        Assert.NotEqual(a.Folder, b.Folder);
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(32, a.Key.Length);
    }

    [Theory]
    [InlineData("K7QM-2XPA")]          // trop court
    [InlineData("K7QM-2XPA-9TRD-0")]   // trop long
    [InlineData("K7QM-2XPA-9TRU")]     // U absent de l'alphabet
    [InlineData("https://exemple.fr")]
    public void Invalid_codes_are_rejected(string input) => Assert.Throws<FormatException>(() => Session.Open(Share, input));

    [Fact]
    public void Config_lists_helpers_and_accepts_comments()
    {
        var dir = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, AppConfig.FileName),
                "{\n // commentaire\n \"SharedFolder\": \"\\\\\\\\srv\\\\aide\",\n" +
                " \"Helpers\": [ { \"Name\": \"Gianni\", \"Email\": \"g@x.fr\" }, { \"Email\": \"b@x.fr\" } ],\n}");
            var config = AppConfig.Load(dir);
            Assert.Equal(@"\\srv\aide", config.SharedFolder);
            Assert.Equal(new[] { "Gianni", "b@x.fr" }, config.Helpers.Select(h => h.ToString()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Config_without_helpers_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, AppConfig.FileName), "{ \"SharedFolder\": \"x\", \"Helpers\": [] }");
            Assert.Throws<InvalidDataException>(() => AppConfig.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
