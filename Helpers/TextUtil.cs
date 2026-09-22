namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 与文本截断相关的共享工具。
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
        if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s;

        int len = maxLength;
        // 截断点的最后一个码元是「高位代理」⇒ 它后面的低位代理已被切掉，回退一位保住整个代理对。
        if (char.IsHighSurrogate(s[len - 1])) len--;
        return s.Substring(0, len);
    }
}
