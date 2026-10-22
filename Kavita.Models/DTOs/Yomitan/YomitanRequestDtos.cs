using System.Collections.Generic;

namespace Kavita.Models.DTOs.Yomitan;

public sealed record YomitanTermsBulkRequestDto
{
    public IList<string> TermList { get; init; } = [];
    public IList<string> Dictionaries { get; init; } = [];
    /// <summary>
    /// exact, prefix or suffix
    /// </summary>
    public string MatchType { get; init; } = "exact";
}

public sealed record YomitanTermExactRequestDto
{
    public string Term { get; init; } = string.Empty;
    public string Reading { get; init; } = string.Empty;
}

public sealed record YomitanTermsExactBulkRequestDto
{
    public IList<YomitanTermExactRequestDto> TermList { get; init; } = [];
    public IList<string> Dictionaries { get; init; } = [];
}

public sealed record YomitanSequenceQueryDto
{
    public long Query { get; init; }
    public string Dictionary { get; init; } = string.Empty;
}

public sealed record YomitanTermsBySequenceBulkRequestDto
{
    public IList<YomitanSequenceQueryDto> Items { get; init; } = [];
}

public sealed record YomitanTermMetaBulkRequestDto
{
    public IList<string> TermList { get; init; } = [];
    public IList<string> Dictionaries { get; init; } = [];
}

public sealed record YomitanKanjiBulkRequestDto
{
    public IList<string> KanjiList { get; init; } = [];
    public IList<string> Dictionaries { get; init; } = [];
}

public sealed record YomitanTagQueryDto
{
    public string Query { get; init; } = string.Empty;
    public string Dictionary { get; init; } = string.Empty;
}

public sealed record YomitanTagMetaBulkRequestDto
{
    public IList<YomitanTagQueryDto> Items { get; init; } = [];
}

public sealed record YomitanMediaRequestDto
{
    public string Path { get; init; } = string.Empty;
    public string Dictionary { get; init; } = string.Empty;
}

public sealed record YomitanMediaBulkRequestDto
{
    public IList<YomitanMediaRequestDto> Items { get; init; } = [];
}
