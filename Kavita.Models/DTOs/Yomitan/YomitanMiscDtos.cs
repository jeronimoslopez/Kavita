#nullable enable
using System;
using System.Collections.Generic;

namespace Kavita.Models.DTOs.Yomitan;

public enum YomitanImportState
{
    /// <summary>
    /// The startup import has not run yet
    /// </summary>
    Idle = 0,
    Importing = 1,
    Completed = 2,
    /// <summary>
    /// The import finished but at least one dictionary failed
    /// </summary>
    Failed = 3,
}

public sealed record YomitanImportStatusDto
{
    public YomitanImportState State { get; init; }
    /// <summary>
    /// Zip file name of the dictionary being imported, null when idle
    /// </summary>
    public string? CurrentDictionary { get; init; }
    /// <summary>
    /// Title from the dictionary's index.json, null until it is read
    /// </summary>
    public string? CurrentTitle { get; init; }
    public int BanksDone { get; init; }
    public int BanksTotal { get; init; }
    public int DictionariesDone { get; init; }
    public int DictionariesTotal { get; init; }
    public IList<string> Errors { get; init; } = [];
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
}

public sealed record YomitanOptionsDto
{
    /// <summary>
    /// Yomitan's full options document (profiles, profileCurrent, global) as JSON text, null when never saved
    /// </summary>
    public string? OptionsJson { get; init; }
}

public sealed record YomitanFetchRequestDto
{
    public string Url { get; init; } = string.Empty;
    /// <summary>
    /// GET or POST, defaults to GET
    /// </summary>
    public string? Method { get; init; }
    /// <summary>
    /// Only Content-Type, Accept and Accept-Language are forwarded upstream
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>
    /// Request body, sent as-is
    /// </summary>
    public string? Body { get; init; }
}

public sealed record YomitanFetchResponseDto
{
    /// <summary>
    /// Upstream status, non-2xx statuses are passed through
    /// </summary>
    public int Status { get; init; }
    /// <summary>
    /// Final URL after redirects
    /// </summary>
    public required string Url { get; init; }
    public string? ContentType { get; init; }
    /// <summary>
    /// Base64 of the raw response bytes
    /// </summary>
    public required string Body { get; init; }
}
