namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 与文本处理相关的共享工具（安全截断 / CSV 转义）。
/// </summary>
public static class TextUtil
{
    /// <summary>
    /// 按 UTF-16 <b>码元</b>截断到 <paramref name="maxLength"/>，但<b>不在代理对中间切断</b>。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：C# 的 <c>string.Substring</c> 按 UTF-16 码元切，而 emoji / 部分生僻字
    /// 由「高位代理 + 低位代理」两个码元组成。若截断点恰好落在高位代理之后，会产出含
    /// <b>孤立代理项</b>的字符串（非法 Unicode），随后进入 <c>Path.Combine</c> / <c>File.Move</c>
    /// 或写进 CSV / 日志（落盘时被替换为 U+FFFD，用户看到乱码方块）。
    /// <para>
    /// 本方法从 <c>OrganizeService.TruncateSafe</c> 提升而来（第十三轮）：<c>ImageAnalysisHelper.Snippet</c>
    /// 的截断同样需要它，<b>不要</b>再复制第三份实现——两份实现必然漂移（本项目 P64 家族）。
    /// </para>
    /// </remarks>
    /// <param name="s">待截断文本。</param>
    /// <param name="maxLength">最大码元数；&lt;= 0 时返回空串。</param>
    public static string Safe(string s, int maxLength)
    {
        if (maxLength <= 0) return "";
        // 第十四轮 R5-6：签名是非空 string，但实现既判 null 又在 s==null 时原样返回 null
        //（提升自 OrganizeService.TruncateSafe 时无意改了 null 行为——旧实现对 null 抛 NRE）。
        // 统一收口为「null 输入返回空串」：现有调用方（Snippet 先判空 / Sanitize / BuildName）
        // 对空串的处理路径都已存在。
        if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s ?? "";

        int len = maxLength;
        // 截断点的最后一个码元是「高位代理」⇒ 它后面的低位代理已被切掉，回退一位保住整个代理对。
        if (char.IsHighSurrogate(s[len - 1])) len--;
        return s.Substring(0, len);
    }

    /// <summary>
    /// CSV 字段转义：引号翻倍，并把 CR / LF 折成空格。
    /// 后者不是形式主义——Windows 文件名不可能含换行，但 <c>ex.Message</c> 可以；
    /// 一个含换行的 Message 会把一行记录劈成两行：第一段 Status 仍在列 3、主索引不受影响，
    /// 但若续行恰好凑够 ≥7 列且列 3 不含「错误」，会被解析成一条假的「已完成」记录，
    /// 导致该文件被永久跳过。概率极低但非零，且与「跳过(已存在)污染续传索引」同源，1 行堵掉。
    /// </summary>
    /// <remarks>
    /// 第十四轮 R6-2（P79 收口）：此前 <c>RenameLogService</c> 与 <c>OrganizeViewModel</c> 各持一份
    /// 逐字相同的私有实现——安全规则双份实现下「修一半」的代价是某条导出路径整个失去防护，
    /// 且 diff 里看不出来。全仓只有两处 CSV 写出点，统一到这里，<b>不要再复制第三份</b>。
    /// </remarks>
    public static string Csv(string? s)
        => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    /// <summary>
    /// CSV 公式注入防护（第十三轮 SEC-03）：Excel / LibreOffice 打开 CSV 时，单元格以
    /// <c>= + - @</c> 开头会被当作【公式】求值，<b>引号包裹不能阻止该行为</b>。
    /// 做法是在这些字符前补一个单引号 <c>'</c>（Excel 会把它当文本显示）。
    /// </summary>
    /// <remarks>
    /// <b>只能用于不被续传判据消费的列</b>：<c>OriginalPath</c> / <c>NewPath</c> 会被续传索引
    /// （<c>CompletedLog.DoneBySource</c> / <c>DoneByNewPath</c> / <c>DoneByNewPathAnyFingerprint</c>）
    /// 做<b>精确字符串比较</b>，加前缀会让键与磁盘实况不符 → 「阻止文件名被逐轮叠加（不可逆）」
    /// 的保护失效，比本缺陷严重得多。故本方法只用于 OriginalName / NewName / Message。
    /// <para>
    /// 判定看「剥掉前导 TAB / 空格后的首个字符」（第十四轮 R2-3 / R6-3）：OWASP CSV 注入口径下
    /// 部分表格软件会先剥前导空白再判公式。Excel 本身按首字符判定（带引号的 <c>"\t=1+1"</c> 是文本），
    /// 当前不可触发，故这只是低成本加固；前缀仍补在整串最前面，Excel 的文本标记不受影响。
    /// </para>
    /// </remarks>
    public static string CsvText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Csv(s);
        var trimmed = s.TrimStart('\t', ' ');
        var dangerous = trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@';
        return Csv(dangerous ? "'" + s : s);
    }
}
