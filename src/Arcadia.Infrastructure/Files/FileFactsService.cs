using System.IO.Enumeration;
using System.Text;
using Arcadia.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Files;

/// <summary>
/// Reads file metadata off the UI thread. Every call is bounded by a timeout so a disconnected network share or a
/// sleeping USB drive makes a file "unavailable" instead of freezing Arcadia. Nothing is ever written.
/// </summary>
public sealed class FileFactsService(ILogger<FileFactsService> logger) : IFileFactsService
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    public const long MaxContentBytes = 1_000_000;
    public const int MaxContentChars = 64_000;

    public async Task<FileFacts> GetFactsAsync(string path, bool readContent, CancellationToken cancellationToken = default)
    {
        var normal = PathRules.Normalize(path);
        if (normal == null) return new FileFacts(path, false, false, null, null, null);
        try
        {
            return await Task.Run(() => Read(normal, readContent), cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Reading {Path} timed out (drive or share unavailable?)", normal);
            return new FileFacts(normal, false, false, null, null, null);
        }
    }

    internal static FileFacts Read(string path, bool readContent)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                string? content = null;
                if (readContent && FileKinds.ContentIndexed.Contains(file.Extension) && file.Length <= MaxContentBytes) content = ReadText(path);
                return new FileFacts(path, true, false, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, content);
            }
            var dir = new DirectoryInfo(path);
            if (dir.Exists) return new FileFacts(path, true, true, null, dir.CreationTimeUtc, dir.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // treated as unavailable
        }
        return new FileFacts(path, false, false, null, null, null);
    }

    /// <summary>Text for the search index: UTF-8 (or the BOM's encoding), shared read so open files still work.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxContentChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // locked by another process etc.
        }
    }

    public Task<IReadOnlyList<FileFacts>> ScanFolderAsync(string folder, bool recursive, Func<string, bool> include, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        var root = PathRules.Normalize(folder) ?? throw new ArgumentException("Invalid folder path.", nameof(folder));
        return Task.Run<IReadOnlyList<FileFacts>>(() =>
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };
            var list = new List<FileFacts>();
            var files = new FileSystemEnumerable<FileFacts>(root,
                (ref FileSystemEntry e) => new FileFacts(e.ToFullPath(), true, false, e.Length, e.CreationTimeUtc.UtcDateTime, e.LastWriteTimeUtc.UtcDateTime), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !FileKinds.IsTemporary(e.FileName.ToString()),
            };
            foreach (var f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!include(f.Path)) continue;
                var ext = Path.GetExtension(f.Path);
                list.Add(FileKinds.ContentIndexed.Contains(ext) && f.SizeBytes <= MaxContentBytes ? f with { ContentText = ReadText(f.Path) } : f);
                if (list.Count % 50 == 0) progress?.Report(list.Count);
            }
            progress?.Report(list.Count);
            return list;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FileFacts>> CheckAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var results = new FileFacts[paths.Count];
        // One quick probe per drive/share: when a root is unreachable all its files are unavailable without waiting on each.
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in paths.Select(RootOf).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                roots[root!] = await Task.Run(() => Directory.Exists(root), cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                roots[root!] = false;
                logger.LogInformation("{Root} did not answer; its documents are marked unavailable for now", root);
            }
        }
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (i, ct) =>
        {
            var p = paths[i];
            var root = RootOf(p);
            results[i] = root != null && roots.GetValueOrDefault(root)
                ? await GetFactsAsync(p, readContent: false, ct).ConfigureAwait(false)
                : new FileFacts(p, false, false, null, null, null);
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>"C:\" or "\\server\share\" for a full path.</summary>
    internal static string? RootOf(string path)
    {
        try { return Path.GetPathRoot(path); }
        catch (ArgumentException) { return null; }
    }
}
