namespace Arcadia.Core.Documents;

public enum FileKind { Folder, Pdf, Word, Spreadsheet, Presentation, Text, Markdown, Image, Archive, Audio, Video, Code, Other }

public enum PreviewKind { None, Text, Markdown, Image }

/// <summary>Classifies files by extension for filtering, icons and previews.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileKind.Pdf,
        [".doc"] = FileKind.Word, [".docx"] = FileKind.Word, [".odt"] = FileKind.Word, [".rtf"] = FileKind.Word,
        [".xls"] = FileKind.Spreadsheet, [".xlsx"] = FileKind.Spreadsheet, [".xlsm"] = FileKind.Spreadsheet, [".ods"] = FileKind.Spreadsheet, [".csv"] = FileKind.Spreadsheet,
        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation, [".odp"] = FileKind.Presentation,
        [".txt"] = FileKind.Text, [".log"] = FileKind.Text, [".ini"] = FileKind.Text, [".cfg"] = FileKind.Text,
        [".md"] = FileKind.Markdown, [".markdown"] = FileKind.Markdown,
        [".png"] = FileKind.Image, [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".gif"] = FileKind.Image, [".bmp"] = FileKind.Image,
        [".webp"] = FileKind.Image, [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image, [".ico"] = FileKind.Image, [".heic"] = FileKind.Image,
        [".zip"] = FileKind.Archive, [".7z"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".tar"] = FileKind.Archive, [".gz"] = FileKind.Archive,
        [".mp3"] = FileKind.Audio, [".wav"] = FileKind.Audio, [".flac"] = FileKind.Audio, [".m4a"] = FileKind.Audio,
        [".mp4"] = FileKind.Video, [".mkv"] = FileKind.Video, [".mov"] = FileKind.Video, [".avi"] = FileKind.Video,
        [".cs"] = FileKind.Code, [".json"] = FileKind.Code, [".xml"] = FileKind.Code, [".xaml"] = FileKind.Code, [".ps1"] = FileKind.Code,
        [".py"] = FileKind.Code, [".js"] = FileKind.Code, [".ts"] = FileKind.Code, [".html"] = FileKind.Code, [".css"] = FileKind.Code,
        [".sql"] = FileKind.Code, [".yml"] = FileKind.Code, [".yaml"] = FileKind.Code,
    };

    /// <summary>Extensions indexed when a folder's "supported documents" are imported.</summary>
    public static readonly IReadOnlySet<string> IndexedByDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".odt", ".rtf", ".xls", ".xlsx", ".xlsm", ".ods", ".csv", ".ppt", ".pptx", ".odp",
        ".txt", ".md", ".markdown", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".zip", ".7z", ".rar",
    };

    /// <summary>Content of these is read (up to a limit) for full-text search.</summary>
    public static readonly IReadOnlySet<string> ContentIndexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".log", ".csv" };

    /// <summary>The extensions that belong to a kind (for filtering in SQL).</summary>
    public static IReadOnlyList<string> ExtensionsOf(FileKind kind) => ByExtension.Where(p => p.Value == kind).Select(p => p.Key).ToList();

    public static FileKind Classify(string? extension, bool isFolder = false) =>
        isFolder ? FileKind.Folder : extension != null && ByExtension.TryGetValue(extension, out var kind) ? kind : FileKind.Other;

    public static PreviewKind Preview(string? extension) => string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ? PreviewKind.Text : Classify(extension) switch
    {
        FileKind.Text or FileKind.Code => PreviewKind.Text,
        FileKind.Markdown => PreviewKind.Markdown,
        FileKind.Image when extension is not (".heic" or ".ico") => PreviewKind.Image,
        _ => PreviewKind.None,
    };

    /// <summary>Files that are clearly temporary (Office lock files, partial downloads) and never belong in the library.</summary>
    public static bool IsTemporary(string fileName)
    {
        if (fileName.StartsWith("~$", StringComparison.Ordinal) || fileName.StartsWith(".~", StringComparison.Ordinal)) return true;
        var ext = System.IO.Path.GetExtension(fileName);
        return ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".part", StringComparison.OrdinalIgnoreCase) || ext.Equals(".partial", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".download", StringComparison.OrdinalIgnoreCase) || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Path validation and normalisation using the framework's path APIs (no manual concatenation).</summary>
public static class PathRules
{
    /// <summary>
    /// Full, normalised path (resolves ".." and separators) or null when the text is not a usable absolute path.
    /// Supports UNC paths, long paths, spaces and any Unicode (Arabic, German…) file name.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = path.Trim().Trim('"');
        if (text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return null;
        if (!System.IO.Path.IsPathFullyQualified(text)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(text);
            // Keep "C:\" but drop trailing separators elsewhere so equal paths compare equal.
            var root = System.IO.Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? System.IO.Path.TrimEndingDirectorySeparator(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

    public static string DisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public static string Extension(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();

    public static bool IsUnder(string path, string folder)
    {
        var f = System.IO.Path.TrimEndingDirectorySeparator(folder) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
