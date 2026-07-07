using System.Collections.Generic;

namespace PhotoRenameAIHash.Models;

public class DuplicateGroup
{
    public int Id { get; set; }

    public List<PhotoFile> Members { get; set; } = new();

    /// <summary>Index (into Members) of the file to keep when deleting duplicates.</summary>
    public int KeepIndex { get; set; } = 0;
}
