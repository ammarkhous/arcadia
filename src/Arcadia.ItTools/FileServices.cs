using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Files;
using Microsoft.Extensions.Logging;

namespace Arcadia.ItTools;

public sealed class StorageService(ILogger<StorageService> logger) : IStorageService
{
    public IReadOnlyList<DriveEntry> GetDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady)
                {
                    list.Add(new DriveEntry(d.Name, null, null, d.DriveType.ToString(), 0, 0, false, false));
                    continue;
                }
                list.Add(new DriveEntry(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, d.DriveFormat, d.DriveType.ToString(),
                    d.TotalSize, d.TotalFreeSpace, true, string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Drive {Drive} is not accessible", d.Name);
                list.Add(new DriveEntry(d.Name, null, null, d.DriveType.ToString(), 0, 0, false, false));
            }
        }
        return list;
    }
}

public sealed class LargeFileFinder(ILogger<LargeFileFinder> logger) : ILargeFileFinder
{
    public const int MaxResults = 5000;

    public Task<IReadOnlyList<LargeFile>> FindAsync(string root, long minimumBytes, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new ToolException(ToolFailure.NotFound, $"The folder '{root}' does not exist.");

        return Task.Run<IReadOnlyList<LargeFile>>(() =>
        {
            var sw = Stopwatch.StartNew();
            long scanned = 0;
            var found = new List<LargeFile>();
            var lastReport = Stopwatch.StartNew();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Hidden and system files count (they are often the large ones); links are not followed to avoid loops.
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            var files = new FileSystemEnumerable<LargeFile>(root,
                (ref FileSystemEntry e) => new LargeFile(e.FileName.ToString(), e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                {
                    if (e.IsDirectory) return false;
                    scanned++;
                    if (lastReport.ElapsedMilliseconds > 200)
                    {
                        lastReport.Restart();
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new LargeFileScanProgress(scanned, found.Count, e.Directory.ToString()));
                    }
                    return e.Length >= minimumBytes;
                },
            };

            foreach (var file in files)
            {
                found.Add(file);
                if (found.Count >= MaxResults * 2)
                {
                    // Keep memory bounded on pathological folders: drop the smallest half.
                    found = [.. found.OrderByDescending(f => f.SizeBytes).Take(MaxResults)];
                }
            }
            progress?.Report(new LargeFileScanProgress(scanned, found.Count, null));
            logger.LogInformation("Large file scan of {Root}: {Scanned} files in {Ms:0} ms, {Found} ≥ {Min} bytes", root, scanned, sw.Elapsed.TotalMilliseconds, found.Count, minimumBytes);
            return found.OrderByDescending(f => f.SizeBytes).Take(MaxResults).ToList();
        }, cancellationToken);
    }
}

public sealed class HashService : IHashService
{
    private const int BufferSize = 1 << 20;

    public async Task<FileHashResult> ComputeAsync(string path, HashAlgorithmKind algorithm, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new ToolException(ToolFailure.NotFound, $"'{path}' was not found.", ex); }
        catch (UnauthorizedAccessException ex) { throw new ToolException(ToolFailure.AccessDenied, $"'{path}' cannot be read.", ex); }
        catch (IOException ex) { throw new ToolException(ToolFailure.Unavailable, ex.Message, ex); }

        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(algorithm switch
            {
                HashAlgorithmKind.Sha512 => HashAlgorithmName.SHA512,
                HashAlgorithmKind.Md5 => HashAlgorithmName.MD5,
                _ => HashAlgorithmName.SHA256,
            });
            var buffer = new byte[BufferSize];
            long total = stream.Length, done = 0;
            var tick = Stopwatch.StartNew();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                done += read;
                if (tick.ElapsedMilliseconds > 100 && total > 0)
                {
                    tick.Restart();
                    progress?.Report((double)done / total);
                }
            }
            progress?.Report(1);
            return new FileHashResult(path, total, algorithm, Convert.ToHexStringLower(hash.GetHashAndReset()), sw.Elapsed);
        }
    }
}
