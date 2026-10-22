using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Models.Entities.User;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Services.Yomitan;

public class YomitanOptionsService(IDataContext context) : IYomitanOptionsService
{
    public async Task<string?> GetOptionsJsonAsync(int userId, CancellationToken ct = default)
    {
        return await context.AppUserYomitanOptions
            .Where(o => o.AppUserId == userId)
            .Select(o => o.OptionsJson)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveOptionsJsonAsync(int userId, string optionsJson, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(optionsJson) > IYomitanOptionsService.MaxOptionsBytes)
        {
            throw new KavitaException("Yomitan options are larger than 2 MB");
        }

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new KavitaException("Yomitan options must be a JSON object");
            }
        }
        catch (JsonException)
        {
            throw new KavitaException("Yomitan options are not valid JSON");
        }

        var existing = await context.AppUserYomitanOptions.FirstOrDefaultAsync(o => o.AppUserId == userId, ct);
        if (existing == null)
        {
            context.AppUserYomitanOptions.Add(new AppUserYomitanOptions
            {
                AppUserId = userId,
                OptionsJson = optionsJson,
                LastModifiedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.OptionsJson = optionsJson;
            existing.LastModifiedUtc = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);
    }
}
