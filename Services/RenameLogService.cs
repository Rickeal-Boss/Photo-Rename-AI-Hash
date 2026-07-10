using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>
/// 负责两类「可追溯」数据的持久化：
/// 1) 重命名日志 rename_log.csv —— 写入输出文件夹，记录 原文件名↔新文件名 对应关系（MD5、操作、状态）；
/// 2) 感知哈希去重库 dedup_library.json —— 写入 LocalAppData，跨会话记录已见图片的 MD5 / aHash / dHash，
///    用于「感知哈希去重」与「清空去重库」。
/// </summary>
public sealed class RenameLogService
{
    private static readonly string DedupLibPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoRenameAIHash", "dedup_library.json");

    private readonly List<DedupRecord> _library = new();

    public Task LoadDedupLibraryAsync()
    {
        try
        {
            if (File.Exists(DedupLibPath))
            {
                var json = File.ReadAllText(DedupLibPath);
                var loaded = JsonSerializer.Deserialize<List<DedupRecord>>(json);
                if (loaded != null)
                {
                    _library.Clear();
                    _library.AddRange(loaded);
                }
                return Task.CompletedTask;
            }
        }
        catch
        {
            // 损坏则忽略，当作空库
        }

        _library.Clear();
        return Task.CompletedTask;
    }

    public Task SaveDedupLibraryAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(DedupLibPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_library, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(DedupLibPath, json);
        }
        catch
        {
            // 写入失败不阻断主流程
        }

        return Task.CompletedTask;
    }

    public Task ClearDedupLibraryAsync()
    {
        _library.Clear();
        try
        {
            if (File.Exists(DedupLibPath)) File.Delete(DedupLibPath);
        }
        catch
        {
            // 忽略
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 若文件与已记录图片重复（MD5 完全一致，或 aHash/dHash 汉明距离在阈值内）则返回 true；
    /// 否则将其登记入库并返回 false。
    /// </summary>
    public bool IsDuplicateAndRecord(string md5, ulong aHash, ulong dHash, string path,
        int aHashThreshold, int dHashThreshold, IHashService hash)
    {
        if (!string.IsNullOrEmpty(md5) && _library.Any(r => r.Md5 == md5))
            return true;

        if (aHash != 0 || dHash != 0)
        {
            foreach (var r in _library)
            {
                if ((r.AHash != 0 && hash.HammingDistance(r.AHash, aHash) <= aHashThreshold) ||
                    (r.DHash != 0 && hash.HammingDistance(r.DHash, dHash) <= dHashThreshold))
                {
                    return true;
                }
            }
        }

        _library.Add(new DedupRecord
        {
            Md5 = md5,
            AHash = aHash,
            DHash = dHash,
            Path = path,
        });
        return false;
    }

    /// <summary>向输出文件夹的 rename_log.csv 追加一行记录（首次自动写表头）。</summary>
    public Task AppendRenameLogAsync(string outputFolder, RenameLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
            return Task.CompletedTask;

        try
        {
            var path = Path.Combine(outputFolder, "rename_log.csv");
            var isNew = !File.Exists(path);
            var sb = new StringBuilder();
            if (isNew)
            {
                sb.AppendLine("Timestamp,OriginalName,NewName,Operation,Status,Md5,OriginalPath,NewPath,Message");
            }

            sb.AppendLine(string.Join(",", new[]
            {
                Csv(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(entry.OriginalName),
                Csv(entry.NewName),
                Csv(entry.Operation),
                Csv(entry.Status),
                Csv(entry.Md5),
                Csv(entry.OriginalPath),
                Csv(entry.NewPath),
                Csv(entry.Message),
            }));

            File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // 日志写入失败不影响主流程
        }

        return Task.CompletedTask;
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
}

public class DedupRecord
{
    public string Md5 { get; set; } = "";

    public ulong AHash { get; set; }

    public ulong DHash { get; set; }

    public string Path { get; set; } = "";
}
