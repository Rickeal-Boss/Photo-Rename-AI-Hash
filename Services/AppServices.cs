namespace PhotoRenameAIHash.Services;

/// <summary>
/// Lightweight service locator holding singleton implementations shared across view models.
/// (Constructor injection is honoured by passing these instances into each view model.)
/// </summary>
public static class AppServices
{
    public static IHashService HashService { get; } = new HashService();

    public static IPhotoService PhotoService { get; } = new PhotoService();

    public static ISettingsService SettingsService { get; } = new SettingsService();

    public static RenameLogService RenameLogService { get; } = new RenameLogService();

    public static IOrganizeService OrganizeService { get; } = new OrganizeService();
}
