using System;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Models.Entities.User;

/// <summary>
/// A user's Yomitan options document. Kept off <see cref="AppUserPreferences"/> so the large blob doesn't load with every preferences request.
/// </summary>
[Index(nameof(AppUserId), IsUnique = true)]
public class AppUserYomitanOptions
{
    public int Id { get; set; }
    /// <summary>
    /// Yomitan's full options document (profiles, profileCurrent, global). Defaults and upgrades happen in the browser.
    /// </summary>
    public required string OptionsJson { get; set; }
    public DateTime LastModifiedUtc { get; set; }

    public int AppUserId { get; set; }
    public virtual AppUser AppUser { get; set; } = null!;
}
