namespace PhotoRenameAIHash.Models;

public enum AppTheme
{
    Light,
    Dark,
    System
}

public class AppSettings
{
    public const string AppVersion = "1.0.0";

    public AppTheme Theme { get; set; } = AppTheme.System;

    public string DefaultFolder { get; set; } = "";

    /// <summary>Hamming-distance threshold for aHash grouping (0 = exact match).</summary>
    public int AHashThreshold { get; set; } = 8;

    /// <summary>Hamming-distance threshold for dHash grouping (0 = exact match).</summary>
    public int DHashThreshold { get; set; } = 10;

    public string Language { get; set; } = "zh-CN";
}
