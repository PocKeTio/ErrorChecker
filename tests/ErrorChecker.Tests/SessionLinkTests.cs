using System.Security.Cryptography;
using ErrorChecker.Core;
using Xunit;

public class SessionLinkTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Link_survives_round_trip_with_awkward_path()
    {
        var folder = @"\\serveur\partage commun\Équipe & co\sessions\20261009-1200-jdupont-000042";
        var parsed = SessionLink.Parse(new SessionLink(folder, Key).ToString());
        Assert.Equal(folder, parsed.Folder);
        Assert.Equal(Key, parsed.Key);
    }

    [Theory]
    [InlineData("\"{0}\"")]   // passé entre guillemets par Windows
    [InlineData("{0}/")]      // « / » ajouté par le client mail
    public void Link_is_parsed_despite_decorations(string format)
    {
        var link = new SessionLink(@"\\srv\x", Key).ToString();
        Assert.Equal(Key, SessionLink.Parse(string.Format(format, link)).Key);
    }

    [Fact]
    public void Link_with_double_slash_form_is_parsed()
    {
        var link = new SessionLink(@"\\srv\x", Key).ToString().Replace("errorchecker:join", "errorchecker://join/");
        Assert.Equal(@"\\srv\x", SessionLink.Parse(link).Folder);
    }

    [Theory]
    [InlineData("https://example.com/?p=a&k=b")]
    [InlineData("errorchecker:join?p=x")]
    [InlineData("errorchecker:join?p=x&k=AAAA")]
    public void Invalid_links_are_rejected(string link) => Assert.Throws<FormatException>(() => SessionLink.Parse(link));

    [Fact]
    public void New_session_gets_its_own_folder_and_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        try
        {
            var a = SessionLink.CreateNew(root);
            var b = SessionLink.CreateNew(root);
            Assert.True(Directory.Exists(a.Folder));
            Assert.StartsWith(Path.Combine(root, "sessions"), a.Folder);
            Assert.NotEqual(a.Folder, b.Folder);
            Assert.NotEqual(a.Key, b.Key);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Config_accepts_comments_and_unc_paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, AppConfig.FileName), "{\n // commentaire\n \"SharedFolder\": \"\\\\\\\\srv\\\\aide\",\n \"SupportEmail\": \"aide@x.fr\",\n}");
            var config = AppConfig.Load(dir);
            Assert.Equal(@"\\srv\aide", config.SharedFolder);
            Assert.Equal("aide@x.fr", config.SupportEmail);
        }
        finally { Directory.Delete(dir, true); }
    }
}
