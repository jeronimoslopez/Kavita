using System.Threading;
using System.Threading.Tasks;

namespace Kavita.API.Services;

/// <summary>
/// Stores each user's Yomitan options document. The server does not interpret the JSON beyond checking that it is an object.
/// </summary>
public interface IYomitanOptionsService
{
    /// <summary>
    /// Maximum size of an options document in UTF-8 bytes
    /// </summary>
    const int MaxOptionsBytes = 2 * 1024 * 1024;

    Task<string?> GetOptionsJsonAsync(int userId, CancellationToken ct = default);
    /// <summary>
    /// Validates and upserts the user's options document
    /// </summary>
    /// <exception cref="Kavita.Common.KavitaException">When the document is too large or not a JSON object</exception>
    Task SaveOptionsJsonAsync(int userId, string optionsJson, CancellationToken ct = default);
}
