using System.Net;
using DualNet.Core.Engine;
using Xunit;

namespace DualNet.Tests;

public class FileNameGuesserTests
{
    [Fact]
    public void FromContentDisposition_UsesFileName()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[0])
        };
        resp.Content.Headers.TryAddWithoutValidation("Content-Disposition",
            "attachment; filename=\"setup archive.zip\"");

        var name = FileNameGuesser.FromResponse(resp, new Uri("http://server/get?id=1"));
        Assert.Equal("setup archive.zip", name);
    }

    [Fact]
    public void FromUrlPath_DecodesAndUsesLastSegment()
    {
        var name = FileNameGuesser.FromResponse(null, new Uri("http://cdn.example.com/files/my%20video.mp4"));
        Assert.Equal("my video.mp4", name);
    }

    [Fact]
    public void Sanitize_ReplacesInvalidCharacters()
    {
        // '/' and '\0' are invalid on every OS; the rest is platform-specific.
        var sanitized = FileNameGuesser.Sanitize("bad/name\0x.bin");
        Assert.DoesNotContain("/", sanitized);
        Assert.Equal("bad_name_x.bin", sanitized);
    }

    [Fact]
    public void MakeUniquePath_AppendsCounterOnCollision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dualnet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var p1 = FileNameGuesser.MakeUniquePath(dir, "file.zip");
            File.WriteAllText(p1, "x");
            var p2 = FileNameGuesser.MakeUniquePath(dir, "file.zip");
            Assert.Equal("file.zip", Path.GetFileName(p1));
            Assert.Equal("file (1).zip", Path.GetFileName(p2));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
