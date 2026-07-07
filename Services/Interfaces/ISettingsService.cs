using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface ISettingsService
{
    AppSettings Load();

    Task SaveAsync(AppSettings settings);
}
