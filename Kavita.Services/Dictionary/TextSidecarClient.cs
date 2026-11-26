using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Flurl.Http.Content;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

public class TextSidecarClient : ITextSidecarClient
{
    /// <summary>
    /// The sidecar answers in well under a second, so a minute only ever runs out when it's stuck
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The sidecar's limits, from text-sidecar-contract/tokenize-request.schema.json. Text lengths count code points
    public const int MaxBlocksPerRequest = 5_000;
    public const int MaxCodePointsPerBlock = 50_000;
    public const int MaxCodePointsPerRequest = 500_000;
    public const int MaxBytesPerRequest = 8 * 1024 * 1024;

    private static readonly byte[] BodyStart = "{\"blocks\":["u8.ToArray();
    private static readonly byte[] BodyEnd = "]}"u8.ToArray();

    /// <remarks>
    /// A page over the sidecar's limits goes in several requests, and their answers are merged into one. A block with
    /// no text, or with more than <see cref="MaxCodePointsPerBlock"/> code points, isn't sent and comes back with no
    /// tokens
    /// </remarks>
    public async Task<TokenizeResponseDto> TokenizeAsync(string sidecarUrl, IReadOnlyList<TokenizeBlockDto> blocks,
        CancellationToken ct = default)
    {
        var tokens = new IList<TokenDto>?[blocks.Count];
        var phrases = new IList<PhraseDto>?[blocks.Count];
        var words = new List<SidecarWordDto>();
        string? version = null;

        foreach (var batch in Batch(blocks))
        {
            var response = await PostAsync(sidecarUrl, batch.Select(i => blocks[i]).ToList(), ct);
            version ??= response.Version;

            var byId = new Dictionary<string, TokenizedBlockDto>();
            foreach (var block in response.Blocks) byId.TryAdd(block.Id, block);

            // Word indices count from the start of each answer's own list
            var offset = words.Count;
            words.AddRange(response.Words);
            foreach (var index in batch)
            {
                if (!byId.TryGetValue(blocks[index].Id, out var answer))
                    throw new TextSidecarRejectedException(200, $"The text sidecar at {sidecarUrl} left out block {blocks[index].Id}");
                tokens[index] = offset == 0 ? answer.Tokens : answer.Tokens.Select(t => Shift(t, offset)).ToList();
                // A version 1 sidecar leaves phrases out, and a null in the JSON reads as none too
                var blockPhrases = answer.Phrases ?? [];
                phrases[index] = offset == 0 ? blockPhrases : blockPhrases.Select(p => p with { W = p.W + offset }).ToList();
            }
        }

        return new TokenizeResponseDto
        {
            Version = version ?? string.Empty,
            Words = words,
            Blocks = blocks
                .Select((b, i) => new TokenizedBlockDto { Id = b.Id, Tokens = tokens[i] ?? [], Phrases = phrases[i] ?? [] })
                .ToList(),
        };
    }

    /// <returns>The indices of the blocks to send, grouped into requests that each fit the sidecar's limits</returns>
    private static IEnumerable<List<int>> Batch(IReadOnlyList<TokenizeBlockDto> blocks)
    {
        var batch = new List<int>();
        var codePoints = 0;
        var bytes = BodyStart.Length + BodyEnd.Length;

        for (var i = 0; i < blocks.Count; i++)
        {
            var length = CountCodePoints(blocks[i].Text);
            if (length == 0 || length > MaxCodePointsPerBlock) continue;

            // One more for the comma between blocks
            var size = JsonSerializer.SerializeToUtf8Bytes(blocks[i]).Length + 1;
            if (batch.Count > 0 && (batch.Count == MaxBlocksPerRequest || codePoints + length > MaxCodePointsPerRequest ||
                                    bytes + size > MaxBytesPerRequest))
            {
                yield return batch;
                batch = [];
                codePoints = 0;
                bytes = BodyStart.Length + BodyEnd.Length;
            }

            batch.Add(i);
            codePoints += length;
            bytes += size;
        }

        if (batch.Count > 0) yield return batch;
    }

    private static async Task<TokenizeResponseDto> PostAsync(string sidecarUrl, IReadOnlyList<TokenizeBlockDto> blocks,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new TokenizeRequestDto { Blocks = blocks.ToList() });
        try
        {
            return await $"{sidecarUrl.TrimEnd('/')}/tokenize"
                .WithTimeout(Timeout)
                .PostAsync(new CapturedJsonContent(body), cancellationToken: ct)
                .ReceiveJson<TokenizeResponseDto>();
        }
        catch (FlurlHttpException) when (ct.IsCancellationRequested)
        {
            // Kavita is shutting down. The unit is untouched and the sidecar is fine
            throw new OperationCanceledException(ct);
        }
        catch (FlurlHttpTimeoutException ex)
        {
            throw new TextSidecarUnavailableException($"The text sidecar at {sidecarUrl} timed out", ex);
        }
        catch (FlurlHttpException ex) when (ex.StatusCode == null)
        {
            throw new TextSidecarUnavailableException($"The text sidecar at {sidecarUrl} can't be reached", ex);
        }
        catch (FlurlHttpException ex) when (ex.StatusCode is not (413 or 422 or 200))
        {
            // Only 413 and 422 are about the text, and a 200 here is an answer that didn't parse. A 404 from a mistyped
            // URL or a 502 from a proxy is about the sidecar
            throw new TextSidecarUnavailableException($"The text sidecar at {sidecarUrl} answered {ex.StatusCode}", ex);
        }
        catch (FlurlHttpException ex)
        {
            var responseBody = await ex.GetResponseStringAsync();
            throw new TextSidecarRejectedException(ex.StatusCode!.Value,
                $"The text sidecar answered {ex.StatusCode}: {responseBody}", ex);
        }
    }

    private static TokenDto Shift(TokenDto token, int offset)
    {
        return token with
        {
            W = token.W + offset,
            Sub = token.Sub?.Select(part => part with { W = part.W + offset }).ToList(),
        };
    }

    public static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }
}
