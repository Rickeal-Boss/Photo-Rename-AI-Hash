namespace PhotoRenameAIHash.Models;

public class PhotoFile
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public System.DateTime LastModified { get; set; }
    public string? NewName { get; set; }
    public string? Status { get; set; }
}
