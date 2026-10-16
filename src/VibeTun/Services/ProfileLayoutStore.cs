using VibeTun.Models;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>Порядок карточек и пользовательские группы вкладки профилей.</summary>
public sealed class ProfileLayoutStore
{
    public ProfileLayout Load()
    {
        AppPaths.EnsureCreated();
        return Json.Read<ProfileLayout>(AppPaths.ProfileLayoutFile) ?? new ProfileLayout();
    }

    public void Save(ProfileLayout layout)
    {
        AppPaths.EnsureCreated();
        Json.Write(AppPaths.ProfileLayoutFile, layout);
    }
}
