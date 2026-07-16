using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>
/// 负责「可追溯」数据的持久化：重命名日志 rename_log.csv —— 写入输出文件夹，
/// 记录 原文件名↔新文件名 对应关系（MD5、操作、状态），用于断点续传与审计。
/// </summary>
public sealed class RenameLogService
{
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

    /// <summary>
    /// 读取输出文件夹（含递归子文件夹，用于「按日期归档」分散写入各日期子目录的场景）下的 rename_log.csv，
    /// 汇总「已完成」文件：按 新文件名(NewName) 与 源路径(OriginalPath) 建立索引，供启动时断点续传跳过。
    /// 仅计入状态不含「错误」的记录；若日志缺失/损坏则返回空集合（非阻断主流程）。
    /// </summary>
    public Task<CompletedLog> LoadRenameLogAsync(string outputFolder)
    {
        var log = new CompletedLog();
        try
        {
            var csvFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder))
            {
                csvFiles.Add(Path.Combine(outputFolder, "rename_log.csv"));
                csvFiles.AddRange(Directory.EnumerateFiles(outputFolder, "rename_log.csv", SearchOption.AllDirectories));
            }

            foreach (var csv in csvFiles)
            {
                if (!File.Exists(csv)) continue;
                var lines = File.ReadAllLines(csv);
                if (lines.Length < 2) continue;

                var header = SplitCsvLine(lines[0]);
                int iNewName = Array.IndexOf(header, "NewName");
                int iStatus = Array.IndexOf(header, "Status");
                int iOriginalPath = Array.IndexOf(header, "OriginalPath");

                for (int r = 1; r < lines.Length; r++)
                {
                    var cols = SplitCsvLine(lines[r]);
                    if (iStatus < 0 || iStatus >= cols.Length) continue;
                    string status = cols[iStatus];
                    if (status.Contains("错误")) continue; // 失败不计入「已完成」，便于下次重试

                    if (iNewName >= 0 && iNewName < cols.Length && !string.IsNullOrEmpty(cols[iNewName]))
                        log.DoneByName.Add(cols[iNewName]);
                    if (iOriginalPath >= 0 && iOriginalPath < cols.Length && !string.IsNullOrEmpty(cols[iOriginalPath]))
                        log.DoneBySource.Add(cols[iOriginalPath]);
                }
            }
        }
        catch
        {
            // 损坏则忽略，当作空
        }

        return Task.FromResult(log);
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
}

/// <summary>已完成文件索引：用于启动时断点续传跳过。键忽略大小写。</summary>
public sealed class CompletedLog
{
    /// <summary>已成功生成的目标文件名（NewName）集合——覆盖「已正确命名」被重复处理的场景。</summary>
    public HashSet<string> DoneByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已成功处理的源文件路径（OriginalPath）集合——覆盖「复制/移动模式源文件仍在，重跑应跳过」的场景。</summary>
    public HashSet<string> DoneBySource { get; } = new(StringComparer.OrdinalIgnoreCase);
}
