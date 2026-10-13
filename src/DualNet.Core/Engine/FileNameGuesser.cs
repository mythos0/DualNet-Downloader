using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DualNet.Core.Engine;

/// <summary>Derives a safe file name from server headers or the URL.</summary>
public static class FileNameGuesser
{
    public static string FromResponse(HttpResponseMessage? response, Uri uri)
    {
        string? name = null;

        if (response?.Content is not null)
        {
            try
            {
                if (response.Content.Headers.ContentDisposition is { } cd)
                {
                    name = cd.FileNameStar ?? cd.FileName;
                    if (name is not null)
                        name = name.Trim('"').Trim();
                }
            }
            catch { /* malformed header — fall through */ }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            string path = uri.AbsolutePath;
            var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            name = last is null ? null : Uri.UnescapeDataString(last);
            if (!string.IsNullOrEmpty(name))
                name = name.Split('?', StringSplitOptions.TrimEntries)[0];
        }

        name = Sanitize(name);
        if (string.IsNullOrWhiteSpace(name))
            name = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";

        return name;
    }

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length > 150) result = result[..150];
        return result;
    }

    /// <summary>Returns a non-colliding path in <paramref name="directory"/>, e.g. "file (2).zip".</summary>
    public static string MakeUniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not find a unique file name for '{fileName}'.");
    }

    /// <summary>
    /// Atomically claims a free file path by creating it exclusively (FileMode.CreateNew).
    /// Prevents two concurrent downloads from resolving to the same target file.
    /// Returns the claimed path and an already-open stream the caller must keep or dispose.
    /// </summary>
    public static (string Path, FileStream Stream) ClaimAvailablePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        string candidate = Path.Combine(directory, fileName);
        for (int i = 1; i < 1000; i++)
        {
            try
            {
                var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return (candidate, stream);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            }
        }
        throw new IOException($"Could not claim a unique file name for '{fileName}'.");
    }
}
