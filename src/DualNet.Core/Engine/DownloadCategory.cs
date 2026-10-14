namespace DualNet.Core.Engine;

/// <summary>IDM-style file-type categories derived from the file extension.</summary>
public enum CategoryKind
{
    Other,
    Video,
    Audio,
    Archive,
    Document,
    Program,
    Image
}

public static class DownloadCategory
{
    private static readonly Dictionary<string, CategoryKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // video
        [".mp4"] = CategoryKind.Video, [".mkv"] = CategoryKind.Video, [".avi"] = CategoryKind.Video,
        [".mov"] = CategoryKind.Video, [".wmv"] = CategoryKind.Video, [".flv"] = CategoryKind.Video,
        [".webm"] = CategoryKind.Video, [".m4v"] = CategoryKind.Video, [".ts"] = CategoryKind.Video,
        [".mpg"] = CategoryKind.Video, [".mpeg"] = CategoryKind.Video,
        // audio
        [".mp3"] = CategoryKind.Audio, [".flac"] = CategoryKind.Audio, [".wav"] = CategoryKind.Audio,
        [".aac"] = CategoryKind.Audio, [".ogg"] = CategoryKind.Audio, [".m4a"] = CategoryKind.Audio,
        [".wma"] = CategoryKind.Audio, [".opus"] = CategoryKind.Audio,
        // archives
        [".zip"] = CategoryKind.Archive, [".rar"] = CategoryKind.Archive, [".7z"] = CategoryKind.Archive,
        [".tar"] = CategoryKind.Archive, [".gz"] = CategoryKind.Archive, [".bz2"] = CategoryKind.Archive,
        [".xz"] = CategoryKind.Archive, [".zst"] = CategoryKind.Archive, [".iso"] = CategoryKind.Archive,
        // documents
        [".pdf"] = CategoryKind.Document, [".doc"] = CategoryKind.Document, [".docx"] = CategoryKind.Document,
        [".xls"] = CategoryKind.Document, [".xlsx"] = CategoryKind.Document, [".ppt"] = CategoryKind.Document,
        [".pptx"] = CategoryKind.Document, [".txt"] = CategoryKind.Document, [".md"] = CategoryKind.Document,
        [".epub"] = CategoryKind.Document, [".csv"] = CategoryKind.Document,
        // programs
        [".exe"] = CategoryKind.Program, [".msi"] = CategoryKind.Program, [".apk"] = CategoryKind.Program,
        [".dmg"] = CategoryKind.Program, [".deb"] = CategoryKind.Program, [".rpm"] = CategoryKind.Program,
        [".appx"] = CategoryKind.Program, [".msix"] = CategoryKind.Program, [".bat"] = CategoryKind.Program,
        [".ps1"] = CategoryKind.Program, [".jar"] = CategoryKind.Program,
        // images
        [".jpg"] = CategoryKind.Image, [".jpeg"] = CategoryKind.Image, [".png"] = CategoryKind.Image,
        [".gif"] = CategoryKind.Image, [".webp"] = CategoryKind.Image, [".bmp"] = CategoryKind.Image,
        [".svg"] = CategoryKind.Image, [".ico"] = CategoryKind.Image, [".heic"] = CategoryKind.Image,
    };

    public static CategoryKind Of(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return ext.Length > 0 && Map.TryGetValue(ext, out var kind) ? kind : CategoryKind.Other;
    }

    public static string Glyph(CategoryKind kind) => kind switch
    {
        CategoryKind.Video => "🎬",
        CategoryKind.Audio => "🎵",
        CategoryKind.Archive => "📦",
        CategoryKind.Document => "📄",
        CategoryKind.Program => "💻",
        CategoryKind.Image => "🖼",
        _ => "⬇"
    };

    public static string Label(CategoryKind kind) => kind switch
    {
        CategoryKind.Video => "Video",
        CategoryKind.Audio => "Audio",
        CategoryKind.Archive => "Compressed",
        CategoryKind.Document => "Documents",
        CategoryKind.Program => "Programs",
        CategoryKind.Image => "Pictures",
        _ => "Other"
    };

    /// <summary>Sub-folder name for the category when "organize by category" is on, else null.</summary>
    public static string? Subfolder(CategoryKind kind, bool organizeByCategory) =>
        organizeByCategory ? Label(kind) : null;
}
