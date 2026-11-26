using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Dictionary;

public class DictionaryService(ILogger<DictionaryService> logger, IUnitOfWork unitOfWork, IReadingStore readingStore,
    ITokenizeService tokenizeService, ITokenizeWorkerSignal workerSignal, IEventHub eventHub)
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
    /// Stored as the generator of a row Kavita settled itself because no passage could carry it
    /// </summary>
    public const string UnusableRowGenerator = "kavita:unusable-span";
    public const string UnusableRowPromptVersion = "none";
    /// <summary>
    /// Rows asked of the store per passage wanted on the first try. A read that turns out too short is repeated
    /// four times larger, up to <see cref="MaxSeedRows"/>
    /// </summary>
    private const int SeedRowsPerPassage = 64;
    /// <summary>
    /// The most rows one request reads from the queue, so a queue full of rows that build no passage can't make a
    /// request read the whole table
    /// </summary>
    private const int MaxSeedRows = 16384;

    public async Task<EpubPageTokensDto?> GetEpubPageAsync(int chapterId, int page, CancellationToken ct = default)
    {
        var fileId = await GetEpubFileIdAsync(chapterId, ct);
        if (fileId == null) return null;

        var unit = await readingStore.GetUnitAsync(fileId.Value, page, ct);
        if (!IsServed(unit))
        {
            // A missing or failed unit won't be tokenized however high it's raised, so only a pending one is worth a write
            if (unit is { State: ReadingUnitState.Pending })
            {
                await readingStore.RaiseUnitPriorityAsync(fileId.Value, page, ReadingPriority.OpenChapter, ct);
                workerSignal.Wake();
            }
            return null;
        }

        var wordIds = unit.Blocks
            .SelectMany(b => b.Tokens)
            .SelectMany(t => (t.Sub ?? []).Select(p => p.W).Prepend(t.W))
            .Concat(unit.Blocks.SelectMany(b => b.Phrases).Select(p => (long?) p.W))
            .OfType<long>()
            .ToHashSet();
        var words = await readingStore.GetWordsAsync(wordIds, ct);
        var analysis = await readingStore.GetUnitAnalysisAsync(unit.Id, ct);

        return new EpubPageTokensDto
        {
            Version = unit.TokenizerVersion ?? string.Empty,
            Blocks = unit.Blocks
                .Select(b => new EpubPageBlockDto(b.Index, b.Text.Length, b.Tokens.ToList(),
                    b.Phrases.Count == 0 ? null : b.Phrases.ToList(), b.Ruby.Count == 0 ? null : b.Ruby.ToList()))
                .ToList(),
            Words = words.Values.ToDictionary(w => w.Id, w => new EpubPageWordDto(w.Lemma, w.Reading, w.Jlpt, w.Pos,
                w.IsProper, w.Readings.ToList(), w.Senses.ToList())),
            // In stored order, so the reader's first match for an occurrence is the row LookupAsync picks
            Analysis = analysis
                .Where(a => a.State == AnalysisState.Done)
                .Select(a => new AnalysisEntryDto(a.Block, a.S, a.SenseKey, a.Reading, a.Generator ?? string.Empty))
                .ToList(),
        };
    }

    public async Task<DictionaryLookupResultDto?> LookupAsync(DictionaryLookupRequestDto request, CancellationToken ct = default)
    {
        var fileId = await GetEpubFileIdAsync(request.ChapterId, ct);
        if (fileId == null) return null;

        var unit = await readingStore.GetUnitAsync(fileId.Value, request.Page, ct);
        if (!IsServed(unit)) return null;

        var block = unit.Blocks.FirstOrDefault(b => b.Index == request.Block);
        if (block == null) return null;

        await readingStore.RaiseAnalysisPriorityAsync(fileId.Value, request.Page, request.Block, ReadingPriority.Tapped, ct);

        if (request.Phrase is { } phraseIndex) return await LookupPhraseAsync(block, phraseIndex, request.S, ct);

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

        var phrases = block.Phrases
            .Select((phrase, index) => (Phrase: phrase, Index: index))
            .Where(p => p.Phrase.S <= token.S && token.E <= p.Phrase.E)
            .ToList();
        var wordIds = parts.Select(p => p.W).Append(target.W).OfType<long>()
            .Concat(phrases.Select(p => p.Phrase.W))
            .ToHashSet();
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
            PartOf = phrases
                .Where(p => words.ContainsKey(p.Phrase.W))
                .Select(p =>
                {
                    var phraseWord = words[p.Phrase.W];
                    return new DictionaryPhraseDto(p.Index, p.Phrase.S, p.Phrase.E, phraseWord.Lemma, phraseWord.Reading,
                        FirstGloss(phraseWord));
                })
                .ToList(),
        };
    }

    /// <param name="s">The tapped offset, which has to fall inside the phrase</param>
    private async Task<DictionaryLookupResultDto?> LookupPhraseAsync(StoredBlock block, int phraseIndex, int s,
        CancellationToken ct)
    {
        if (phraseIndex < 0 || phraseIndex >= block.Phrases.Count) return null;
        var phrase = block.Phrases[phraseIndex];
        if (s < phrase.S || s >= phrase.E) return null;

        var words = await readingStore.GetWordsAsync([phrase.W], ct);
        if (!words.TryGetValue(phrase.W, out var word)) return null;

        return new DictionaryLookupResultDto
        {
            Word = new DictionaryWordDto(word.Id, Slice(block.Text, phrase.S, phrase.E), word.Lemma, word.Pos,
                word.IsProper, null, word.Reading, word.Readings.ToList(), word.Jlpt),
            Senses = word.Senses.ToList(),
        };
    }

    /// <summary>
    /// Joins the glosses with "; ", the way the popup shows a sense
    /// </summary>
    private static string FirstGloss(StoredWord word) =>
        word.Senses.Count == 0 ? string.Empty : string.Join("; ", word.Senses[0].Gloss);

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
        var fileIds = await GetSeriesTokenizedFileIdsAsync(seriesId, ct);
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

        var units = new Dictionary<long, PassageUnit>();
        var seedCount = limit * SeedRowsPerPassage;
        PassageBuild build;
        while (true)
        {
            var seeds = await readingStore.GetPendingAnalysisAsync(seedCount, minPriority, ct);
            var truncated = seeds.Count >= seedCount && seedCount < MaxSeedRows;
            build = await BuildPassagesAsync(seeds, limit, minPriority, truncated, units, ct);
            if (build.Passages.Count >= limit || !truncated) break;
            seedCount = Math.Min(seedCount * 4, MaxSeedRows);
        }

        await SettleUnusableRowsAsync(build.Unusable, ct);
        return build.Passages;
    }

    public async Task<AnalysisStoreResultDto> StoreAnalysisAsync(AnalysisStoreRequestDto request, CancellationToken ct = default)
    {
        ValidateLabel(request.Generator, "generator");
        ValidateLabel(request.PromptVersion, "promptVersion");
        var items = request.Items ?? throw new KavitaException("items is required");
        if (items.Any(i => i == null)) throw new KavitaException("items can't hold null");
        if (items.Count > MaxAnswersPerRequest)
            throw new KavitaException($"Send at most {MaxAnswersPerRequest} items per request");

        var answers = items.Select(i => new AnalysisAnswer(i.Id, i.Sense, i.Reading)).ToList();
        var outcome = await readingStore.StoreAnalysisAsync(request.Generator, request.PromptVersion, answers, ct);

        var rejected = PairRejections(answers, outcome.Rejected)
            .Select(p => new AnalysisRejectionDto(p.Rejection.Id, RejectionReason(p.Rejection, p.Answer)))
            .ToList();

        await SendAnalysisReadyAsync(outcome.StoredPages);

        return new AnalysisStoreResultDto(outcome.Stored, rejected);
    }

    /// <summary>
    /// Matches each rejection to the answer it's about. The store checks answers in order, lists rejections in that
    /// order, and calls an answer a duplicate only once an earlier answer for its id was stored. So an answer whose id
    /// was already stored is the next rejection, a duplicate, and any other answer is the next rejection when that
    /// rejection has its id and isn't a duplicate
    /// </summary>
    public static IEnumerable<(AnalysisRejection Rejection, AnalysisAnswer? Answer)> PairRejections(
        IReadOnlyList<AnalysisAnswer> answers, IReadOnlyList<AnalysisRejection> rejections)
    {
        var stored = new HashSet<long>();
        var next = 0;
        foreach (var answer in answers)
        {
            if (next >= rejections.Count) yield break;
            var rejection = rejections[next];
            var isThisOne = rejection.Id == answer.Id &&
                            (stored.Contains(answer.Id) || rejection.Reason != AnalysisRejectReason.Duplicate);
            if (isThisOne)
            {
                yield return (rejection, answer);
                next++;
            }
            else
            {
                stored.Add(answer.Id);
            }
        }

        // Rejections the walk couldn't place still go back to the runner
        for (; next < rejections.Count; next++) yield return (rejections[next], null);
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

    /// <param name="truncated">The queue had more rows than were read, so a block may have rows that weren't read</param>
    private async Task<PassageBuild> BuildPassagesAsync(IReadOnlyList<PendingAnalysisRow> seeds, int limit,
        int minPriority, bool truncated, Dictionary<long, PassageUnit> units, CancellationToken ct)
    {
        var passages = new List<AnalysisPassageDto>();
        var unusable = new List<long>();
        var usedBlocks = new HashSet<(long UnitId, int Block)>();
        var words = await readingStore.GetWordsAsync(seeds.Select(r => r.WordId).ToHashSet(), ct);
        var rowsByUnit = seeds
            .GroupBy(r => r.UnitId)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Block).ToDictionary(b => b.Key, b => b.ToList()));

        foreach (var seed in seeds)
        {
            if (passages.Count >= limit) break;
            if (usedBlocks.Contains((seed.UnitId, seed.Block))) continue;

            if (!units.TryGetValue(seed.UnitId, out var unit))
            {
                unit = await LoadPassageUnitAsync(seed, minPriority, truncated, ct);
                units[seed.UnitId] = unit;
            }

            var seedRows = rowsByUnit[seed.UnitId];
            if (!IsComplete(unit, seedRows, seed.Block, truncated)) continue;

            var draft = BuildPassage(unit, seed.Block, seedRows, usedBlocks, words, truncated);
            unusable.AddRange(draft.Unusable);
            if (draft.Items.Count > 0) passages.Add(new AnalysisPassageDto(draft.Text, draft.Items));
        }

        return new PassageBuild(passages, unusable);
    }

    /// <summary>
    /// The unit's stored block texts give the context around the blocks with rows. The rows' own block texts come
    /// from the queue read, so a re-tokenize in between can only change the context
    /// </summary>
    private async Task<PassageUnit> LoadPassageUnitAsync(PendingAnalysisRow seed, int minPriority, bool truncated,
        CancellationToken ct)
    {
        var unit = await readingStore.GetUnitAsync(seed.MangaFileId, seed.Page, ct);
        if (unit == null || unit.Id != seed.UnitId) return new PassageUnit(seed.UnitId, new Dictionary<int, string>(), null);

        Dictionary<int, int>? pendingCounts = null;
        if (truncated)
        {
            pendingCounts = (await readingStore.GetUnitAnalysisAsync(unit.Id, ct))
                .Where(a => a.State == AnalysisState.Pending && a.Priority >= minPriority)
                .GroupBy(a => a.Block)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        return new PassageUnit(unit.Id, unit.Blocks.ToDictionary(b => b.Index, b => b.Text), pendingCounts);
    }

    /// <returns>False when the queue read stopped before all of the block's rows, which a larger read will reach</returns>
    private static bool IsComplete(PassageUnit unit, Dictionary<int, List<PendingAnalysisRow>> seedRows, int block,
        bool truncated)
    {
        if (!truncated || unit.PendingCounts == null) return true;
        return seedRows.TryGetValue(block, out var rows) && rows.Count >= unit.PendingCounts.GetValueOrDefault(block);
    }

    /// <summary>
    /// Starts at <paramref name="first"/> and adds the blocks after it while the text stays within
    /// <see cref="PassageLength"/>, then puts the block before it in front when that still fits
    /// </summary>
    private static PassageDraft BuildPassage(PassageUnit unit, int first, Dictionary<int, List<PendingAnalysisRow>> seedRows,
        HashSet<(long, int)> usedBlocks, IReadOnlyDictionary<long, StoredWord> words, bool truncated)
    {
        string? TextOf(int block) =>
            seedRows.TryGetValue(block, out var rows) ? rows[0].BlockText : unit.Blocks.GetValueOrDefault(block);

        var content = new List<int> { first };
        var length = TextOf(first)!.Length;
        for (var next = first + 1; TextOf(next) is { } text; next++)
        {
            if (usedBlocks.Contains((unit.Id, next))) break;
            if (!IsComplete(unit, seedRows, next, truncated) && seedRows.ContainsKey(next)) break;
            if (length + BlockSeparator.Length + text.Length > PassageLength) break;
            content.Add(next);
            length += BlockSeparator.Length + text.Length;
        }

        // A trailing block without rows would only lengthen the prompt
        while (content.Count > 1 && !seedRows.ContainsKey(content[^1])) content.RemoveAt(content.Count - 1);
        length = content.Sum(b => TextOf(b)!.Length) + BlockSeparator.Length * (content.Count - 1);

        var blocks = new List<int>(content);
        if (TextOf(first - 1) is { } before && length + BlockSeparator.Length + before.Length <= PassageLength)
        {
            blocks.Insert(0, first - 1);
        }

        var builder = new StringBuilder();
        var items = new List<AnalysisItemDto>();
        var unusable = new List<long>();
        foreach (var block in blocks)
        {
            if (builder.Length > 0) builder.Append(BlockSeparator);
            var offset = builder.Length;
            var text = TextOf(block)!;
            builder.Append(text);

            // The leading block is context only. Its rows wait for a passage that starts at it
            if (!content.Contains(block)) continue;
            usedBlocks.Add((unit.Id, block));

            var end = 0;
            foreach (var row in seedRows.GetValueOrDefault(block, []).OrderBy(r => r.S).ThenBy(r => r.E))
            {
                if (row.S < 0 || row.E > text.Length || row.E <= row.S || !words.TryGetValue(row.WordId, out var word))
                {
                    unusable.Add(row.Id);
                    continue;
                }

                // Top-level tokens don't overlap, so this only skips a row the store got wrong. It stays pending
                if (row.S < end) continue;
                items.Add(new AnalysisItemDto(row.Id, offset + row.S, offset + row.E, text[row.S..row.E], word.Lemma,
                    word.Readings.ToList(), word.Senses.ToList()));
                end = row.E;
            }
        }

        return new PassageDraft(builder.ToString(), items, unusable);
    }

    /// <summary>
    /// Answers rows no passage can carry with a null sense and reading, so they stop heading the queue and hiding
    /// the rows behind them
    /// </summary>
    private async Task SettleUnusableRowsAsync(IReadOnlyList<long> rowIds, CancellationToken ct)
    {
        if (rowIds.Count == 0) return;

        logger.LogWarning("[DictionaryService] {Count} analysis rows don't fit their block text or have no word, " +
                          "so they're settled with no answer", rowIds.Count);
        try
        {
            var answers = rowIds.Distinct().Select(id => new AnalysisAnswer(id, null, null)).ToList();
            await readingStore.StoreAnalysisAsync(UnusableRowGenerator, UnusableRowPromptVersion, answers, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DictionaryService] Couldn't settle unusable analysis rows");
        }
    }

    /// <summary>
    /// Runs after the answers are committed, so a failure here is logged and never fails the request. A failed
    /// request would make the runner send the batch again and get every id back as not pending
    /// </summary>
    private async Task SendAnalysisReadyAsync(IReadOnlyList<AnalysisStoredPage> sources)
    {
        if (sources.Count == 0) return;

        try
        {
            var fileIds = sources.Select(s => s.MangaFileId).Distinct().ToList();
            var chapters = await unitOfWork.DataContext.MangaFile
                .Where(f => fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.ChapterId, f.Chapter.Volume.SeriesId })
                .ToDictionaryAsync(f => f.Id, CancellationToken.None);

            var groups = sources
                .Where(s => chapters.ContainsKey(s.MangaFileId))
                .GroupBy(s => (chapters[s.MangaFileId].SeriesId, chapters[s.MangaFileId].ChapterId));
            foreach (var group in groups)
            {
                var pages = group.Select(s => s.Page).Distinct().Order().ToList();
                await eventHub.SendMessageAsync(MessageFactory.DictionaryAnalysisReady,
                    MessageFactory.DictionaryAnalysisReadyEvent(group.Key.SeriesId, group.Key.ChapterId, pages), false,
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DictionaryService] Couldn't tell readers about {Count} pages with new analysis",
                sources.Count);
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

    /// <returns>The series' EPUB files the tokenizer handles, which are the only ones that can get analysis rows</returns>
    private async Task<List<int>> GetSeriesTokenizedFileIdsAsync(int seriesId, CancellationToken ct)
    {
        var files = await unitOfWork.DataContext.MangaFile
            .Where(f => f.Chapter.Volume.SeriesId == seriesId && f.Format == MangaFormat.Epub &&
                        f.Chapter.Volume.Series.Library.EnableDictionary)
            .Select(f => new { f.Id, ChapterLanguage = f.Chapter.Language, SeriesLanguage = f.Chapter.Volume.Series.Metadata.Language })
            .ToListAsync(ct);

        return files
            .Where(f => TokenizeService.IsJapanese(string.IsNullOrWhiteSpace(f.ChapterLanguage) ? f.SeriesLanguage : f.ChapterLanguage))
            .Select(f => f.Id)
            .ToList();
    }

    /// <summary>
    /// A refresh unit's blocks still match its file, so the reader keeps them until the new ones are saved and
    /// DictionaryPagesReady tells it to fetch the page again
    /// </summary>
    private static bool IsServed([NotNullWhen(true)] ReadingUnit? unit) =>
        unit is { State: ReadingUnitState.Done or ReadingUnitState.Refresh };

    private static string Slice(string text, int s, int e)
    {
        s = Math.Clamp(s, 0, text.Length);
        e = Math.Clamp(e, s, text.Length);
        return text[s..e];
    }

    /// <param name="Blocks">Empty when the unit is gone, so its passages get no context</param>
    /// <param name="PendingCounts">Queued rows per block, read only when the queue read was cut short</param>
    private sealed record PassageUnit(long Id, IReadOnlyDictionary<int, string> Blocks,
        IReadOnlyDictionary<int, int>? PendingCounts);

    private sealed record PassageDraft(string Text, List<AnalysisItemDto> Items, List<long> Unusable);

    private sealed record PassageBuild(List<AnalysisPassageDto> Passages, List<long> Unusable);
}
