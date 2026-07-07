using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public sealed class PhotoService : IPhotoService
{
    private static readonly string[] ImageExt =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public async Task<IReadOnlyList<PhotoFile>> ScanAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return Array.Empty<PhotoFile>();

        var result = new List<PhotoFile>();
        await Task.Run(() =>
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(ImageExt, ext) < 0) continue;

                var info = new FileInfo(path);
                result.Add(new PhotoFile
                {
                    Path = path,
                    Name = info.Name,
                    SizeBytes = info.Length,
                    LastModified = info.LastWriteTimeUtc,
                });
            }
        }, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<PhotoFile> files,
        int aHashThreshold,
        int dHashThreshold,
        CancellationToken ct = default)
    {
        var list = files.ToList();
        var records = new List<(PhotoFile file, ulong a, ulong d)>();

        foreach (var pf in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(pf.Path);
                var a = await AppServices.HashService.ComputeAHashAsync(stream, 8, ct).ConfigureAwait(false);
                stream.Position = 0;
                var d = await AppServices.HashService.ComputeDHashAsync(stream, 8, ct).ConfigureAwait(false);
                records.Add((pf, a, d));
            }
            catch
            {
                // Skip unreadable or unsupported images.
            }
        }

        var groups = new List<DuplicateGroup>();
        var used = new bool[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            if (used[i]) continue;
            var grp = new DuplicateGroup { Id = groups.Count + 1 };
            grp.Members.Add(records[i].file);
            used[i] = true;

            for (int j = i + 1; j < records.Count; j++)
            {
                if (used[j]) continue;
                int da = AppServices.HashService.HammingDistance(records[i].a, records[j].a);
                int dd = AppServices.HashService.HammingDistance(records[i].d, records[j].d);
                if (da <= aHashThreshold || dd <= dHashThreshold)
                {
                    grp.Members.Add(records[j].file);
                    used[j] = true;
                }
            }

            if (grp.Members.Count > 1) groups.Add(grp);
        }

        return groups;
    }

    public async Task<int> RenameAsync(IReadOnlyList<RenameItem> items, CancellationToken ct = default)
    {
        int done = 0;
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(item.SourcePath) && !File.Exists(item.DestinationPath))
                {
                    File.Move(item.SourcePath, item.DestinationPath);
                    done++;
                }
            }
        }, ct).ConfigureAwait(false);

        return done;
    }
}
