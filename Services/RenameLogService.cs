using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;          // Volatile / Interlocked：跨线程累加并读取写盘失败计数
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>
/// 负责「可追溯」数据的持久化：重命名日志 rename_log.csv —— 写入输出文件夹，
/// 记录 原文件名↔新文件名 对应关系（MD5、操作、状态），用于断点续传与审计。
/// </summary>
public sealed class RenameLogService
{
    /// <summary>
    /// 日志写入失败次数（原子累加）。写盘失败按设计不阻断主流程，但必须可感知——
    /// 磁盘写满时若静默吞掉，用户会误以为「已全部记录」，实际审计与续传索引已中断。
    /// </summary>
    private int _failedWrites;

    public int FailedWrites => Volatile.Read(ref _failedWrites);

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
                // Fingerprint 列：本次运行的参数指纹（见 <see cref="RenameLogEntry.Fingerprint"/>）。
                // 必须落盘，否则下次运行时无从判断「这条成功记录是不是按当前参数生成的」。
                sb.AppendLine("Timestamp,OriginalName,NewName,Operation,Status,Md5,OriginalPath,NewPath,Message,Fingerprint");
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
                // 必须保持「最后一列」（列位 <see cref="FingerprintColumnIndex"/>）：
                // LoadRenameLogAsync 在旧表头（无该列）场景下按这个固定列位兜底读取，
                // 将来若新增列请追加在它之后，不要插到它前面。
                Csv(entry.Fingerprint), // Csv() 统一加引号，指纹含 '|' / ',' 也能正确往返
            }));

            // P2-4：UTF-8 带 BOM——新文件落盘时写入 BOM（Excel 直接打开中文不乱码），
            // 追加已有文件时不会重复写 BOM。
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 日志写入失败不阻断主流程（既定取舍），但必须留下痕迹供批次结束时统一告警
            Interlocked.Increment(ref _failedWrites);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取输出文件夹（含递归子文件夹，用于「按日期归档」分散写入各日期子目录的场景）下的 rename_log.csv，
    /// 汇总「已完成」文件：按 新文件名(NewName) 与 源路径(OriginalPath) 建立索引，供启动时断点续传跳过。
    /// 仅计入状态既不含「错误」也不含「跳过(已存在)」的记录——这两种状态下文件都未被真正处理，
    /// 计入索引会让用户改模板/换冲突策略/清掉冲突文件后重跑仍被永久跳过；
    /// 若日志缺失/损坏则返回空集合（非阻断主流程）。
    /// </summary>
    /// <param name="outputFolder">输出 / 归档目标文件夹。</param>
    /// <param name="fingerprint">
    /// 本次运行的参数指纹（模式 / 冲突策略 / 命名模板 / 取 EXIF 日期 / 识别引擎 / 输出目录…）。
    /// <b>只有 Fingerprint 列存在且与本次指纹完全相等的记录才计入索引</b>：
    /// 参数变了，旧记录描述的产物就不再是本次想要的结果，必须重新处理。
    /// </param>
    /// <remarks>
    /// <b>兼容性（有意为之，勿「修」）：</b>旧版本写出的 rename_log.csv 没有 Fingerprint 列，
    /// 升级后的首次运行会把它们全部判为「不可计入索引」→ 整批重新处理。
    /// 这是刻意选择：宁可重做（重做时内容相同的文件会被判为「未改动(内容相同)」，不产生副本），
    /// 也绝不能延续「改了参数却整批静默跳过」的 P0 缺陷。届时 UI 会给出
    /// <see cref="CompletedLog.IgnoredByMissingFingerprintColumn"/> 提示（与「参数确实变了」分开陈述），
    /// 用户可据此判断。注意：该理由对 Copy / Move 成立（输入名不进输出，重做幂等），
    /// <b>对重命名模式不成立</b>——重命名的输入名本身就是输出的一部分，重做会让文件名叠加，
    /// 故编排层对重命名模式另有「叠加保护」（见 <see cref="CompletedLog.DoneByNewPathAnyFingerprint"/>）。
    /// <b>仅限「首次」：</b>表头只在文件创建时写一次，若不按 <see cref="FingerprintColumnIndex"/>
    /// 兜底定位，升级后新写入的记录也会一直读不到指纹 → 变成「每次都整批重做」的永久回归。
    /// </remarks>
    /// <param name="acceptedFingerprints">
    /// 本次可接受的「运行指纹」集合，任一命中即视为同一命名配置。
    /// 之所以是多个而非单个：指纹算法会随版本演进（见 <c>OrganizeService.ComputeFingerprint</c> 的版本前缀），
    /// 若只认最新一种写法，算法一升级老用户的所有历史记录就全部失配 → 每次升级都要全量重做一遍。
    /// 传入「新版算法值 + 上一代算法值」即可平滑迁移：参数没变的老记录仍能续传，参数真变了才重做。
    /// </param>
    public Task<CompletedLog> LoadRenameLogAsync(string outputFolder, params string[] acceptedFingerprints)
    {
        var log = new CompletedLog();
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
            return Task.FromResult(log);

        try
        {
            var csvFiles = CollectRenameLogFiles(outputFolder, log);

            foreach (var csv in csvFiles)
            {
                try
                {
                    if (!File.Exists(csv)) continue;
                    var lines = File.ReadAllLines(csv);
                    if (lines.Length < 2) continue;

                    var header = SplitCsvLine(lines[0]);
                    int iNewName = Array.IndexOf(header, "NewName");
                    int iStatus = Array.IndexOf(header, "Status");
                    int iOriginalPath = Array.IndexOf(header, "OriginalPath");
                    int iNewPath = Array.IndexOf(header, "NewPath");
                    int iFingerprint = Array.IndexOf(header, "Fingerprint");

                    // 兼容「旧表头 + 新记录」混合的日志：表头只在文件首次创建时写一次，
                    // 老用户升级后其 rename_log.csv 的表头仍是 9 列（无 Fingerprint），
                    // 而此后追加的记录一律是 10 列（指纹固定写在最后一列）。
                    // 若只按列名找，这里会得到 -1 → 连升级后<b>新写入</b>的记录也被判为「参数不一致」
                    // → 断点续传对老用户不是「升级后首次重跑」，而是「以后每次都整批重做」，
                    // 续传功能等于永久失效（且每次都会报 N 条「参数不一致」，用户无从判断）。
                    // 故表头缺该列时按固定列位兜底；旧数据行只有 9 列、取不到该列 → 仍判为忽略（安全方向）。
                    if (iFingerprint < 0) iFingerprint = FingerprintColumnIndex;

                    for (int r = 1; r < lines.Length; r++)
                    {
                        var cols = SplitCsvLine(lines[r]);
                        if (iStatus < 0 || iStatus >= cols.Length) continue;
                        string status = cols[iStatus];
                        // 失败不计入「已完成」，便于下次重试；
                        // 「跳过」同理：该状态下文件并未被真正处理（冲突策略为 Skip 时直接放弃、
                        // 或重命名模式下为避免文件名叠加而保护性跳过），
                        // 若计入索引会导致用户改模板/换策略/清掉冲突文件后重跑仍被永久跳过。
                        // 用 Contains("跳过") 而非列举具体文案：日志里可能出现的跳过态只有
                        // 「跳过(已存在)」与「跳过(疑似已命名)」两类，且将来新增跳过态时
                        // 必须落在同一侧（宁可重做也不静默跳过），列举写法会漏掉新态。
                        if (status.Contains("错误") || status.Contains("跳过")) continue;

                        // 运行指纹比对：列缺失（旧版日志）或值不等 → 该行不计入索引。
                        // 宁可让文件重做（重做的代价是「内容相同则跳过 / 加序号」，不丢数据），
                        // 也绝不能让「改了参数却整批静默跳过」再次发生。
                        // 可接受多个指纹（新版 + 上一代），任一命中即视为同一命名配置。
                        // 两种原因必须<b>分开计数</b>：列缺失是「旧版日志格式」（升级场景，与用户参数无关），
                        // 值不等才是「用户改了命名配置」。此前合并成一个计数，导致升级后首跑
                        // 谎报成「检测到 N 条历史记录来自不同的命名配置……请保持这些参数不变」——
                        // 用户什么都没改却被要求「保持参数不变」，提示与事实矛盾。
                        bool hasFingerprintColumn = iFingerprint >= 0 && iFingerprint < cols.Length;
                        if (acceptedFingerprints == null || acceptedFingerprints.Length == 0)
                        {
                            // 调用方没给可接受指纹：按「参数不一致」计（宁可不计入索引）。
                            log.IgnoredByFingerprint++;
                            RecordIgnoredRow(log, cols, iNewPath);
                            continue;
                        }

                        if (!hasFingerprintColumn)
                        {
                            // 旧版日志（9 列，无指纹列）写出的数据行：与用户是否改参数无关。
                            log.IgnoredByMissingFingerprintColumn++;
                            RecordIgnoredRow(log, cols, iNewPath);
                            continue;
                        }

                        if (Array.IndexOf(acceptedFingerprints, cols[iFingerprint]) < 0)
                        {
                            log.IgnoredByFingerprint++;
                            RecordIgnoredRow(log, cols, iNewPath);
                            continue;
                        }

                        if (iNewName >= 0 && iNewName < cols.Length && !string.IsNullOrEmpty(cols[iNewName]))
                            log.DoneByName.Add(cols[iNewName]);
                        if (iOriginalPath >= 0 && iOriginalPath < cols.Length && !string.IsNullOrEmpty(cols[iOriginalPath]))
                            log.DoneBySource.Add(cols[iOriginalPath]);
                        // P1-2：目标路径索引（绝对路径）。续传的「重命名模式目标匹配」改用它，
                        // 因为 DoneByName 只有文件名、不含目录，递归扫描下会把「另一个子目录里
                        // 恰好同名的新文件」误判成已处理并永久跳过。
                        // 空值不入索引：老日志 / 失败 / 「跳过(已存在)」记录可能没有 NewPath，
                        // 把空串塞进集合会让路径为空的文件被无条件跳过。
                        if (iNewPath >= 0 && iNewPath < cols.Length && !string.IsNullOrEmpty(cols[iNewPath]))
                            log.DoneByNewPath.Add(cols[iNewPath]);
                    }
                }
                catch
                {
                    // 单个日志文件读取失败：跳过该文件，不影响其它日志（此前是整批 catch，一处坏日志 → 索引全空）
                    log.EnumerationIncomplete = true;
                }
            }
        }
        catch
        {
            // 损坏则忽略，当作空
        }

        return Task.FromResult(log);
    }

    private const string LogFileName = "rename_log.csv";

    /// <summary>
    /// Fingerprint 在 CSV 行中的固定列位（0 基；表头顺序见 <see cref="AppendRenameLogAsync"/>）。
    /// 仅当表头<b>缺少</b>该列名时（老版本写出的 9 列表头）用它兜底定位，
    /// 使升级后新写入的 10 列记录仍能被索引到（详见 LoadRenameLogAsync 内注释）。
    /// 新增列请追加在指纹之后，不要插到它前面，否则该兜底会失准。
    /// </summary>
    private const int FingerprintColumnIndex = 9;

    /// <summary>
    /// 目录枚举选项：与 <c>PhotoService.DirOptions</c> 同口径——
    /// <c>IgnoreInaccessible = true</c> 让无权限目录在枚举期被跳过而不是抛异常；
    /// <c>AttributesToSkip = ReparsePoint</c> 跳过 junction / 符号链接目录，
    /// 避免指向祖先的 junction 造成无限递归（Windows 用户目录里遍地皆是）。
    /// </summary>
    private static readonly EnumerationOptions DirOptions = new()
    {
        RecurseSubdirectories = false, // 由本方法自己逐层推进，便于「哪一目录失败」可隔离
        MatchType = MatchType.Win32,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// 逐目录容错地收集 <c>rename_log.csv</c>。
    /// <b>为什么不能用 <c>SearchOption.AllDirectories</c>：</b>它不带 <c>IgnoreInaccessible</c>，
    /// 输出目录下只要有一个无权限子目录（系统卷信息、其它用户的目录、已断开的网络位置等）
    /// 整个枚举就抛异常 → 外层裸 catch 吞掉 → <b>续传索引静默变空 → 整批重复处理</b>。
    /// 改为自己按层推进、每个目录单独 try/catch：坏目录只让索引少一部分（并置
    /// <see cref="CompletedLog.EnumerationIncomplete"/> 提示用户），不会让整批前功尽弃。
    /// </summary>
    private static List<string> CollectRenameLogFiles(string root, CompletedLog log)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, LogFileName, SearchOption.TopDirectoryOnly))
                    found.Add(f);
            }
            catch
            {
                log.EnumerationIncomplete = true; // 该目录读不到：索引可能漏记录
            }

            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir, "*", DirOptions))
                    pending.Push(d);
            }
            catch
            {
                log.EnumerationIncomplete = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 把「因指纹原因未计入索引」的行的目标路径（NewPath）收进
    /// <see cref="CompletedLog.DoneByNewPathAnyFingerprint"/>。
    /// <para>用途：重命名模式的「文件名叠加」保护。重命名的输入名本身就是输出的一部分
    /// （模板含 <c>{name}</c> 时），一旦该文件的续传索引失效（指纹变化 / 升级后旧日志无指纹列），
    /// 它会被再次加工 → 文件名逐轮叠加变长，且不可逆。这里把「曾被本工具改名到该路径」这一事实
    /// <b>与指纹无关地</b>记下来，供编排层判断「这个文件是不是已经被改过名了」。</para>
    /// <para>只记非空 NewPath：失败 / 「跳过」行通常没有目标路径，空串入集合会让空路径被无条件命中。</para>
    /// </summary>
    private static void RecordIgnoredRow(CompletedLog log, string[] cols, int iNewPath)
    {
        if (iNewPath >= 0 && iNewPath < cols.Length && !string.IsNullOrEmpty(cols[iNewPath]))
            log.DoneByNewPathAnyFingerprint.Add(cols[iNewPath]);
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

    /// <summary>
    /// CSV 字段转义：引号翻倍，并把 CR / LF 折成空格。
    /// 后者不是形式主义——Windows 文件名不可能含换行，但 <c>ex.Message</c> 可以；
    /// 一个含换行的 Message 会把一行记录劈成两行：第一段 Status 仍在列 3、主索引不受影响，
    /// 但若续行恰好凑够 ≥7 列且列 3 不含「错误」，会被解析成一条假的「已完成」记录，
    /// 导致该文件被永久跳过。概率极低但非零，且与「跳过(已存在)污染续传索引」同源，1 行堵掉。
    /// </summary>
    private static string Csv(string s)
        => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}

/// <summary>已完成文件索引：用于启动时断点续传跳过。键忽略大小写。</summary>
public sealed class CompletedLog
{
    /// <summary>
    /// 已成功生成的目标文件名（NewName）集合——覆盖「已正确命名」被重复处理的场景。
    /// <b>注意：不再用于续传匹配</b>（P1-2）：它只有文件名、不含目录，递归扫描下会把
    /// 「另一个子目录里恰好同名的新文件」误判成已处理并永久跳过。续传匹配改用
    /// <see cref="DoneByNewPath"/>。此处仍照旧填充，供排查与将来可能的其它用途使用。
    /// </summary>
    public HashSet<string> DoneByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已成功处理的源文件路径（OriginalPath）集合——覆盖「复制/移动模式源文件仍在，重跑应跳过」的场景。</summary>
    public HashSet<string> DoneBySource { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已成功生成的目标文件<b>绝对路径</b>（NewPath）集合——续传「重命名模式目标匹配」的判据。
    /// 用绝对路径而非纯文件名：「当前路径 == 某条历史记录的目标路径」才是精确的
    /// 「本文件已被本规则处理过」，既保住「重命名后重跑不重复处理」，
    /// 又不会误伤同名的其它文件（详见调用处 OrganizeService.RunAsync 的注释）。
    /// 空值不入集合（见 LoadRenameLogAsync）。
    /// </summary>
    public HashSet<string> DoneByNewPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 「曾被本工具改名为该绝对路径」的历史目标路径集合，<b>不区分运行指纹</b>（含旧版日志格式的行）。
    /// 与 <see cref="DoneByNewPath"/> 的区别：后者只收指纹匹配的记录，本集合把指纹不匹配 /
    /// 缺列的记录的目标路径也收进来。用途只有一个——重命名模式下判断「当前文件是不是已经被改过名」，
    /// 从而在续传索引失效时阻止文件名被逐轮叠加（不可逆）。<b>不得用作续传跳过判据</b>
    /// （那会让「改了参数」的记录也静默跳过，正是要消灭的 P0 缺陷）。
    /// </summary>
    public HashSet<string> DoneByNewPathAnyFingerprint { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 因「运行指纹」与本次参数不一致而未被计入索引的记录条数。
    /// &gt;0 说明本次参数（模式 / 冲突策略 / 命名模板 / 引擎…）与历史批次不同，
    /// 对应文件本次会重新处理——这是有意为之，UI 应提示用户「若想沿用旧记录请保持参数不变」。
    /// </summary>
    public int IgnoredByFingerprint { get; set; }

    /// <summary>
    /// 因「日志行没有指纹列」（旧版本写出的 9 列日志）而未被计入索引的记录条数。
    /// <b>与 <see cref="IgnoredByFingerprint"/> 分开计数</b>：本条的原因是「旧版日志格式」，
    /// 与用户是否修改过命名参数<b>无关</b>；升级后首跑时若把它并进 IgnoredByFingerprint，
    /// 会向「什么都没改」的用户谎报「你改了命名配置，请保持这些参数不变」。
    /// </summary>
    public int IgnoredByMissingFingerprintColumn { get; set; }

    /// <summary>
    /// 日志目录枚举是否不完整（部分子目录无权限 / 不可访问，或个别日志文件读取失败）。
    /// true 表示续传索引可能漏记录 → 部分已完成文件会被重新处理（重做安全：内容相同则跳过，不丢数据）。
    /// </summary>
    public bool EnumerationIncomplete { get; set; }
}
