using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Dictionary;

public class DictionaryService(ILogger<DictionaryService> logger, IUnitOfWork unitOfWork, IReadingStore readingStore,
    ITokenizeService tokenizeService, ITokenizeWorkerSignal workerSignal, IEventHub eventHub, IMemoryCache cache)
    : IDictionaryService
{
    /// <summary>
    /// The passage length the runner's prompt is written for, in UTF-16 units. A single block longer than this still
    /// becomes one passage, because a block's rows are never split
    /// </summary>
    public const int PassageLength = 2000;
    public const string BlockSeparator = "\n";
    public const int MaxGeneratorLength = 64;
    public const int MaxAnswersPerRequest = 2000;
    /// <summary>
    /// Rows asked of the store per passage wanted on the first try. Most of a passage's rows come from the unit's own
    /// analysis, so these rows only pick which blocks start passages
    /// </summary>
    private const int SeedRowsPerPassage = 64;
    /// <summary>
    /// The most rows one request reads from the queue, so a queue full of rows that build no passage can't make a
    /// request read the whole table
    /// </summary>
    private const int MaxSeedRows = 16384;
    private static readonly TimeSpan RowSourceLifetime = TimeSpan.FromHours(12);

    public async Task<EpubPageTokensDto?> GetEpubPageAsync(int chapterId, int page, CancellationToken ct = default)
    {
        var fileId = await GetEpubFileIdAsync(chapterId, ct);
        if (fileId == null) return null;

        var unit = await readingStore.GetUnitAsync(fileId.Value, page, ct);
        if (unit is not { State: ReadingUnitState.Done })
        {
            await readingStore.RaiseUnitPriorityAsync(fileId.Value, page, ReadingPriority.OpenChapter, ct);
            workerSignal.Wake();
            return null;
        }

        var wordIds = unit.Blocks
            .SelectMany(b => b.Tokens)
            .SelectMany(t => (t.Sub ?? []).Select(p => p.W).Prepend(t.W))
            .OfType<long>()
            .ToHashSet();
        var words = await readingStore.GetWordsAsync(wordIds, ct);
        var analysis = await readingStore.GetUnitAnalysisAsync(unit.Id, ct);

        return new EpubPageTokensDto
        {
            Version = unit.TokenizerVersion ?? string.Empty,
            Blocks = unit.Blocks.Select(b => new EpubPageBlockDto(b.Index, b.Text.Length, b.Tokens.ToList())).ToList(),
            Words = words.Values.ToDictionary(w => w.Id, w => new WordBriefDto(w.Lemma, w.Reading, w.Jlpt)),
            Analysis = analysis
                .Where(a => a.State == AnalysisState.Done)
                .Select(a => new AnalysisEntryDto(a.Block, a.S, a.SenseKey, a.Reading))
                .ToList(),
        };
    }

    public async Task<DictionaryLookupResultDto?> LookupAsync(DictionaryLookupRequestDto request, CancellationToken ct = default)
    {
        var fileId = await GetEpubFileIdAsync(request.ChapterId, ct);
        if (fileId == null) return null;

        var unit = await readingStore.GetUnitAsync(fileId.Value, request.Page, ct);
        if (unit is not { State: ReadingUnitState.Done }) return null;

        var block = unit.Blocks.FirstOrDefault(b => b.Index == request.Block);
        if (block == null) return null;

        await readingStore.RaiseAnalysisPriorityAsync(fileId.Value, request.Page, request.Block, ReadingPriority.Tapped, ct);

        var token = block.Tokens.FirstOrDefault(t => t.S == request.S);
        if (token == null) return null;

        var parts = token.Sub ?? [];
        SubTokenDto target;
        if (request.Sub == null)
        {
            target = new SubTokenDto(token.S, token.E, token.W, token.R);
        }
        else
        {
            if (request.Sub < 0 || request.Sub >= parts.Count) return null;
            target = parts[request.Sub.Value];
        }

        if (target.W == null) return null;

        var wordIds = parts.Select(p => p.W).Append(target.W).OfType<long>().ToHashSet();
        var words = await readingStore.GetWordsAsync(wordIds, ct);
        if (!words.TryGetValue(target.W.Value, out var word)) return null;

        DictionaryAnalysisDto? answer = null;
        if (request.Sub == null)
        {
            var row = (await readingStore.GetUnitAnalysisAsync(unit.Id, ct))
                .FirstOrDefault(a => a.Block == block.Index && a.S == token.S && a.State == AnalysisState.Done);
            if (row != null) answer = new DictionaryAnalysisDto(row.SenseKey, row.Reading, row.Generator ?? string.Empty);
        }

        return new DictionaryLookupResultDto
        {
            Word = new DictionaryWordDto(word.Id, Slice(block.Text, target.S, target.E), word.Lemma, word.Pos,
                word.IsProper, target.R, word.Reading, word.Readings.ToList(), word.Jlpt),
            Senses = word.Senses.ToList(),
            Analysis = answer,
            Parts = parts
                .Select((part, index) => (part, index))
                .Where(p => p.part.W != null && words.ContainsKey(p.part.W.Value))
                .Select(p =>
                {
                    var partWord = words[p.part.W!.Value];
                    return new DictionaryPartDto(p.index, partWord.Id, Slice(block.Text, p.part.S, p.part.E),
                        partWord.Lemma, partWord.Reading, partWord.Senses.ToList());
                })
                .ToList(),
        };
    }

    public async Task PrioritizeChapterAsync(int chapterId, CancellationToken ct = default)
    {
        var files = await tokenizeService.PrioritizeChapterAsync(chapterId, ct);

        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.DictionaryAnalysisOnOpen, ct);
        var analyzeOnOpen = bool.TryParse(setting?.Value, out var on) && on;

        foreach (var (fileIds, priority) in new[]
                 {
                     (files.OpenChapterFileIds, ReadingPriority.OpenChapter),
                     (files.NextChapterFileIds, ReadingPriority.NextChapter),
                 })
        {
            if (fileIds.Count == 0) continue;

            if (analyzeOnOpen)
            {
                await readingStore.RequestAnalysisAsync(fileIds, priority, ct);
                continue;
            }

            foreach (var fileId in fileIds)
            {
                await readingStore.RaiseAnalysisPriorityAsync(fileId, null, null, priority, ct);
            }
        }
    }

    public async Task<int> AnalyzeSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var fileIds = await GetSeriesEpubFileIdsAsync(seriesId, ct);
        if (fileIds.Count > 0) await readingStore.RequestAnalysisAsync(fileIds, ReadingPriority.WholeBook, ct);
        return fileIds.Count;
    }

    public async Task RemoveSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var fileIds = await GetSeriesEpubFileIdsAsync(seriesId, ct);
        if (fileIds.Count > 0) await readingStore.ClearAnalysisAsync(fileIds, ct);
    }

    public async Task<IList<AnalysisPassageDto>> GetPendingAnalysisAsync(int limit, int minPriority, CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        var units = new Dictionary<long, PassageUnit?>();
        var seedCount = limit * SeedRowsPerPassage;
        List<PassageDraft> drafts;
        while (true)
        {
            var seeds = await readingStore.GetPendingAnalysisAsync(seedCount, minPriority, ct);
            drafts = await BuildPassagesAsync(seeds, limit, minPriority, units, ct);
            if (drafts.Count >= limit || seeds.Count < seedCount || seedCount >= MaxSeedRows) break;
            seedCount = Math.Min(seedCount * 4, MaxSeedRows);
        }

        var wordIds = drafts.SelectMany(d => d.Rows).Select(r => r.Row.WordId).ToHashSet();
        var words = await readingStore.GetWordsAsync(wordIds, ct);

        var passages = new List<AnalysisPassageDto>();
        foreach (var draft in drafts)
        {
            var items = draft.Rows
                .Where(r => words.ContainsKey(r.Row.WordId))
                .Select(r =>
                {
                    var word = words[r.Row.WordId];
                    RememberRowSource(r.Row.Id, draft.Unit);
                    return new AnalysisItemDto(r.Row.Id, r.Offset + r.Row.S, r.Offset + r.Row.E,
                        Slice(r.BlockText, r.Row.S, r.Row.E), word.Lemma, word.Readings.ToList(), word.Senses.ToList());
                })
                .ToList();
            if (items.Count > 0) passages.Add(new AnalysisPassageDto(draft.Text, items));
        }

        return passages;
    }

    public async Task<AnalysisStoreResultDto> StoreAnalysisAsync(AnalysisStoreRequestDto request, CancellationToken ct = default)
    {
        ValidateLabel(request.Generator, "generator");
        ValidateLabel(request.PromptVersion, "promptVersion");
        var items = request.Items ?? [];
        if (items.Count > MaxAnswersPerRequest)
            throw new KavitaException($"Send at most {MaxAnswersPerRequest} items per request");

        var answers = items.Select(i => new AnalysisAnswer(i.Id, i.Sense, i.Reading)).ToList();
        var outcome = await readingStore.StoreAnalysisAsync(request.Generator, request.PromptVersion, answers, ct);

        // The store checks the first answer for an id and calls the rest duplicates, so the first is the one a
        // sense or reading rejection is about
        var firstAnswers = answers.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var rejected = outcome.Rejected
            .Select(r => new AnalysisRejectionDto(r.Id, RejectionReason(r, firstAnswers.GetValueOrDefault(r.Id))))
            .ToList();

        // Counting per id works however the store treats a duplicate of a rejected answer
        var rejectionsPerId = outcome.Rejected.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Count());
        var storedIds = answers.GroupBy(a => a.Id)
            .Where(g => g.Count() > rejectionsPerId.GetValueOrDefault(g.Key))
            .Select(g => g.Key)
            .ToList();
        await SendAnalysisReadyAsync(storedIds, ct);

        return new AnalysisStoreResultDto(outcome.Stored, rejected);
    }

    public static string RejectionReason(AnalysisRejection rejection, AnalysisAnswer? answer) => rejection.Reason switch
    {
        AnalysisRejectReason.NotPending => "the row doesn't exist or isn't pending",
        AnalysisRejectReason.SenseNotOffered => $"sense {answer?.SenseKey} was not offered",
        AnalysisRejectReason.ReadingNotOffered => $"reading {answer?.Reading} was not offered",
        AnalysisRejectReason.Duplicate => "the id came earlier in this request, and the earlier answer was kept",
        _ => rejection.Reason.ToString(),
    };

    private static void ValidateLabel(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new KavitaException($"{name} is required");
        if (value.Length > MaxGeneratorLength)
            throw new KavitaException($"{name} must be at most {MaxGeneratorLength} characters");
    }

    private async Task<List<PassageDraft>> BuildPassagesAsync(IReadOnlyList<PendingAnalysisRow> seeds, int limit,
        int minPriority, Dictionary<long, PassageUnit?> units, CancellationToken ct)
    {
        var drafts = new List<PassageDraft>();
        var usedBlocks = new HashSet<(long UnitId, int Block)>();

        foreach (var seed in seeds)
        {
            if (drafts.Count >= limit) break;
            if (usedBlocks.Contains((seed.UnitId, seed.Block))) continue;

            if (!units.TryGetValue(seed.UnitId, out var unit))
            {
                unit = await LoadPassageUnitAsync(seed, minPriority, ct);
                units[seed.UnitId] = unit;
            }

            if (unit == null || !unit.Blocks.ContainsKey(seed.Block) || !unit.Pending.ContainsKey(seed.Block)) continue;

            drafts.Add(BuildPassage(unit, seed.Block, usedBlocks));
        }

        return drafts;
    }

    private async Task<PassageUnit?> LoadPassageUnitAsync(PendingAnalysisRow seed, int minPriority, CancellationToken ct)
    {
        var unit = await readingStore.GetUnitAsync(seed.MangaFileId, seed.Page, ct);
        if (unit == null || unit.Id != seed.UnitId)
        {
            logger.LogDebug("[DictionaryService] Analysis row {RowId} points at a unit that is gone", seed.Id);
            return null;
        }

        var pending = (await readingStore.GetUnitAnalysisAsync(unit.Id, ct))
            .Where(a => a.State == AnalysisState.Pending && a.Priority >= minPriority)
            .GroupBy(a => a.Block)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StoredAnalysis>) g.OrderBy(a => a.S).ThenBy(a => a.E).ToList());

        return new PassageUnit(unit.Id, seed.MangaFileId, seed.Page,
            unit.Blocks.ToDictionary(b => b.Index, b => b.Text), pending);
    }

    /// <summary>
    /// Starts at <paramref name="first"/> and adds the blocks after it while the text stays within
    /// <see cref="PassageLength"/>, then puts the block before it in front when that still fits
    /// </summary>
    private static PassageDraft BuildPassage(PassageUnit unit, int first, HashSet<(long, int)> usedBlocks)
    {
        var content = new List<int> { first };
        var length = unit.Blocks[first].Length;
        for (var next = first + 1; unit.Blocks.TryGetValue(next, out var text); next++)
        {
            if (usedBlocks.Contains((unit.Id, next))) break;
            if (length + BlockSeparator.Length + text.Length > PassageLength) break;
            content.Add(next);
            length += BlockSeparator.Length + text.Length;
        }

        // A trailing block without rows would only lengthen the prompt
        while (content.Count > 1 && !unit.Pending.ContainsKey(content[^1])) content.RemoveAt(content.Count - 1);
        length = content.Sum(b => unit.Blocks[b].Length) + BlockSeparator.Length * (content.Count - 1);

        var blocks = new List<int>(content);
        if (unit.Blocks.TryGetValue(first - 1, out var before) &&
            length + BlockSeparator.Length + before.Length <= PassageLength)
        {
            blocks.Insert(0, first - 1);
        }

        var builder = new StringBuilder();
        var rows = new List<PassageRow>();
        foreach (var block in blocks)
        {
            if (builder.Length > 0) builder.Append(BlockSeparator);
            var offset = builder.Length;
            var text = unit.Blocks[block];
            builder.Append(text);

            // The leading block is context only. Its rows wait for a passage that starts at it
            if (!content.Contains(block)) continue;
            usedBlocks.Add((unit.Id, block));

            var end = 0;
            foreach (var row in unit.Pending.GetValueOrDefault(block, []))
            {
                if (row.S < end || row.S < 0 || row.E > text.Length || row.E <= row.S) continue;
                rows.Add(new PassageRow(row, offset, text));
                end = row.E;
            }
        }

        return new PassageDraft(builder.ToString(), rows, unit);
    }

    /// <summary>
    /// The store can't find a row by its id, so the page of each served row is kept here until its answer comes back
    /// and open readers can be told. A row answered after the entry expires or the server restarts sends no event
    /// </summary>
    private void RememberRowSource(long rowId, PassageUnit unit)
    {
        cache.Set(RowSourceKey(rowId), new RowSource(unit.MangaFileId, unit.Page),
            new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = RowSourceLifetime });
    }

    private static string RowSourceKey(long rowId) => $"dictionary-analysis-row-{rowId}";

    private async Task SendAnalysisReadyAsync(IReadOnlyList<long> storedIds, CancellationToken ct)
    {
        var sources = new List<RowSource>();
        foreach (var id in storedIds)
        {
            if (!cache.TryGetValue<RowSource>(RowSourceKey(id), out var source) || source == null) continue;
            sources.Add(source);
            cache.Remove(RowSourceKey(id));
        }

        if (sources.Count == 0) return;

        var fileIds = sources.Select(s => s.MangaFileId).Distinct().ToList();
        var chapters = await unitOfWork.DataContext.MangaFile
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ChapterId, f.Chapter.Volume.SeriesId })
            .ToDictionaryAsync(f => f.Id, ct);

        var groups = sources
            .Where(s => chapters.ContainsKey(s.MangaFileId))
            .GroupBy(s => (chapters[s.MangaFileId].SeriesId, chapters[s.MangaFileId].ChapterId));
        foreach (var group in groups)
        {
            var pages = group.Select(s => s.Page).Distinct().Order().ToList();
            await eventHub.SendMessageAsync(MessageFactory.DictionaryAnalysisReady,
                MessageFactory.DictionaryAnalysisReadyEvent(group.Key.SeriesId, group.Key.ChapterId, pages), false, ct);
        }
    }

    /// <returns>The chapter's first EPUB file, or null when it has none</returns>
    private async Task<int?> GetEpubFileIdAsync(int chapterId, CancellationToken ct)
    {
        return await unitOfWork.DataContext.MangaFile
            .Where(f => f.ChapterId == chapterId && f.Format == MangaFormat.Epub)
            .OrderBy(f => f.Id)
            .Select(f => (int?) f.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<List<int>> GetSeriesEpubFileIdsAsync(int seriesId, CancellationToken ct)
    {
        return await unitOfWork.DataContext.MangaFile
            .Where(f => f.Chapter.Volume.SeriesId == seriesId && f.Format == MangaFormat.Epub)
            .Select(f => f.Id)
            .ToListAsync(ct);
    }

    private static string Slice(string text, int s, int e)
    {
        s = Math.Clamp(s, 0, text.Length);
        e = Math.Clamp(e, s, text.Length);
        return text[s..e];
    }

    private sealed record PassageUnit(long Id, int MangaFileId, int Page, IReadOnlyDictionary<int, string> Blocks,
        IReadOnlyDictionary<int, IReadOnlyList<StoredAnalysis>> Pending);

    /// <param name="Offset">Where the row's block starts in the passage text</param>
    private sealed record PassageRow(StoredAnalysis Row, int Offset, string BlockText);

    private sealed record PassageDraft(string Text, IReadOnlyList<PassageRow> Rows, PassageUnit Unit);

    private sealed record RowSource(int MangaFileId, int Page);
}
