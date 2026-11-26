using System.Diagnostics;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

public class BookWordJoinTests(ITestOutputHelper output)
{
    private const int Opened = 1;
    private const int Other = 2;
    private const long Suirei = 500;
    private const long Basen = 501;
    private const long Shunba = 502;

    private static BookWordTotal Total(int file, string text, long wordId, string reading, string printed, int count) =>
        new(file, text, wordId, reading, printed, count);

    private static BookWordSet Set(params BookWordTotal[] totals) => BookWordSet.From(totals, Opened);

    private static TokenDto Token(int s, int e, long? w, string? r = null, IList<SubTokenDto>? sub = null) => new(s, e, w, r, sub);

    [Fact]
    public void From_PrefersTheOpenedFilesRowsAndElseTheSeries()
    {
        var set = Set(
            Total(Opened, "翠苓", Suirei, "すいれい", "スイレイ", 2),
            Total(Other, "翠苓", Suirei, "みどりれい", "ミドリレイ", 9),
            Total(Other, "馬閃", Basen, "ばせん", "バセン", 3),
            Total(3, "馬閃", Basen, "ばせん", "ばせん", 1));

        Assert.Equal(new ReadingHintDto("すいれい", "スイレイ", ReadingSource.Book), set.Hints[Suirei]);
        Assert.Equal(new ReadingHintDto("ばせん", "バセン", ReadingSource.Series), set.Hints[Basen]);
    }

    [Fact]
    public void From_TakesTheMostCountedReadingWithTiesToTheFirstInOrdinalOrder()
    {
        var most = Set(Total(Other, "馬閃", Basen, "ばせん", "バセン", 1), Total(3, "馬閃", Basen, "うません", "ウマセン", 2),
            Total(4, "馬閃", Basen, "ばせん", "ばせん", 2));
        var tie = Set(Total(Other, "馬閃", Basen, "ばせん", "バセン", 2), Total(3, "馬閃", Basen, "うません", "ウマセン", 2));

        // ばせん is counted 3 times against うません's 2, and printed ばせん on 2 of the 3. うません sorts first at a tie
        Assert.Equal(new ReadingHintDto("ばせん", "ばせん", ReadingSource.Series), most.Hints[Basen]);
        Assert.Equal("うません", tie.Hints[Basen].Reading);
    }

    [Fact]
    public void From_OrdersWordsLongestFirst()
    {
        var set = Set(Total(Other, "翠苓", Suirei, "すいれい", "スイレイ", 1), Total(Other, "翠苓妃", Basen, "すいれいひ", "スイレイヒ", 1));

        Assert.Equal(["翠苓妃", "翠苓"], set.Words.Select(w => w.Text));
        Assert.Same(BookWordSet.Empty, BookWordSet.From([], Opened));
    }

    [Fact]
    public void Apply_JoinsMarkedAndUnmarkedOccurrencesIntoOneTokenWithTheirPiecesAsParts()
    {
        // 翠苓は翠苓と, the first marked
        var block = new StoredBlock(0, "翠苓は翠苓と",
        [
            Token(0, 1, 10, "みどり"), Token(1, 2, 11), Token(2, 3, 12, "は"),
            Token(3, 4, 10, "みどり"), Token(4, 5, 11), Token(5, 6, 13, "と"),
        ]) { Ruby = [new RubyDto(0, 2, "スイレイ")] };

        var joined = BookWordJoin.Apply(block, Set(Total(Opened, "翠苓", Suirei, "すいれい", "スイレイ", 1)));

        Assert.Equal(
        [
            Token(0, 2, Suirei, null, [new SubTokenDto(0, 1, 10, "みどり"), new SubTokenDto(1, 2, 11, null)]),
            Token(2, 3, 12, "は"),
            Token(3, 5, Suirei, null, [new SubTokenDto(3, 4, 10, "みどり"), new SubTokenDto(4, 5, 11, null)]),
            Token(5, 6, 13, "と"),
        ], joined.Tokens, new TokenComparer());
        Assert.Equal(block.Ruby, joined.Ruby);
        Assert.Equal(6, joined.Text.Length);
    }

    [Fact]
    public void Apply_DropsTheCoveredTokensOwnParts()
    {
        var block = new StoredBlock(0, "果実水", [Token(0, 2, 20, "かじつ", [new SubTokenDto(0, 1, 21, "か"), new SubTokenDto(1, 2, 22, "じつ")]),
            Token(2, 3, 23, "みず")]);

        var joined = BookWordJoin.Apply(block, Set(Total(Other, "果実水", 600, "じゅーす", "ジュース", 4)));

        var token = Assert.Single(joined.Tokens);
        Assert.Equal([new SubTokenDto(0, 2, 20, "かじつ"), new SubTokenDto(2, 3, 23, "みず")], token.Sub);
    }

    /// <summary>
    /// The tokenizer reads 春馬 as one token on nearly every use and splits it on a few. Only the split uses join
    /// </summary>
    [Fact]
    public void Apply_LeavesTextThatIsAlreadyOneTokenAlone()
    {
        var single = new StoredBlock(0, "春馬が", [Token(0, 2, Shunba, "しゅんば"), Token(2, 3, 30, "が")]);
        var split = new StoredBlock(1, "春馬が", [Token(0, 1, 31, "はる"), Token(1, 2, 32, "うま"), Token(2, 3, 30, "が")]);
        var set = Set(Total(Other, "春馬", Shunba, "しゅんば", "シュンバ", 2));

        Assert.Same(single, BookWordJoin.Apply(single, set));
        Assert.Equal([(0, 2, (long?) Shunba), (2, 3, 30)], BookWordJoin.Apply(split, set).Tokens.Select(t => (t.S, t.E, t.W)));
    }

    [Theory]
    // Ends inside 苓妃
    [InlineData("翠苓妃", 0, 1, 1, 3)]
    // Starts inside 白翠
    [InlineData("白翠苓", 0, 2, 2, 3)]
    public void Apply_IgnoresAMatchThatCutsAToken(string text, int s0, int e0, int s1, int e1)
    {
        var block = new StoredBlock(0, text, [Token(s0, e0, 40), Token(s1, e1, 41)]);

        Assert.Same(block, BookWordJoin.Apply(block, Set(Total(Other, "翠苓", Suirei, "すいれい", "スイレイ", 1))));
    }

    [Fact]
    public void Apply_JoinsTheLongestWordFirstAndNeverOverlaps()
    {
        // 翠苓妃 wins over 翠苓 inside it, and 苓妃 can't take a token 翠苓妃 already took
        var block = new StoredBlock(0, "翠苓妃と翠苓", [Token(0, 1, 40), Token(1, 2, 41), Token(2, 3, 42), Token(3, 4, 43),
            Token(4, 5, 40), Token(5, 6, 41)]);
        var set = Set(Total(Other, "翠苓", Suirei, "すいれい", "スイレイ", 1), Total(Other, "翠苓妃", 700, "すいれいひ", "スイレイヒ", 1),
            Total(Other, "苓妃", 701, "れいひ", "レイヒ", 1));

        var joined = BookWordJoin.Apply(block, set);

        Assert.Equal([(0, 3, (long?) 700), (3, 4, 43), (4, 6, Suirei)], joined.Tokens.Select(t => (t.S, t.E, t.W)));
    }

    [Fact]
    public void Apply_DropsPhrasesThatNoLongerCoverWholeTokensAndBlindOffsetsInsideAJoin()
    {
        // 翠苓が来た, with a phrase over 苓が, one over が来た and one over 翠苓 alone
        var block = new StoredBlock(0, "翠苓が来た", [Token(0, 1, 40), Token(1, 2, 41), Token(2, 3, 42, "が"), Token(3, 4, 43, "き"),
            Token(4, 5, 44, "た")])
        {
            Phrases = [new PhraseDto(1, 3, 90), new PhraseDto(2, 5, 91), new PhraseDto(0, 2, 92), new PhraseDto(0, 3, 93)],
            Blind = [0, 1, 3],
        };

        var joined = BookWordJoin.Apply(block, Set(Total(Other, "翠苓", Suirei, "すいれい", "スイレイ", 1)));

        Assert.Equal([new PhraseDto(2, 5, 91), new PhraseDto(0, 3, 93)], joined.Phrases);
        Assert.Equal([3], joined.Blind);
    }

    [Fact]
    public void Apply_ToAUnit_KeepsUnchangedBlocksAndTheUnitWhenNothingJoins()
    {
        var plain = new StoredBlock(0, "とても", [Token(0, 3, 1)]);
        var named = new StoredBlock(1, "翠苓", [Token(0, 1, 40), Token(1, 2, 41)]);
        var unit = new ReadingUnit(1, ReadingUnitKind.Epub, Opened, 0, ReadingUnitState.Done, "s", "v", [plain, named]);
        var set = Set(Total(Other, "翠苓", Suirei, "すいれい", "スイレイ", 1));

        var joined = BookWordJoin.Apply(unit, set);

        Assert.Same(plain, joined.Blocks[0]);
        Assert.Single(joined.Blocks[1].Tokens);
        var untouched = unit with { Blocks = [plain] };
        Assert.Same(untouched, BookWordJoin.Apply(untouched, set));
        Assert.Same(unit, BookWordJoin.Apply(unit, BookWordSet.Empty));
    }

    /// <summary>
    /// A page of 160 blocks and about 9,000 characters, with 200 book words in the series and some of them on the page
    /// </summary>
    [Fact]
    public void Apply_ToALongPageWith200BookWords_IsFast()
    {
        var random = new Random(7);
        const string kanji = "翠苓馬閃羅半趙迂陸孫玉袁白鈴梅壬氏高順李白水蓮桜花";
        var totals = Enumerable.Range(0, 200)
            .Select(i => Total(Other, $"{kanji[i % kanji.Length]}{kanji[(i * 7 + 3) % kanji.Length]}{(char) ('一' + i)}", 1000 + i,
                "よみ", "ヨミ", 1))
            .ToList();
        var set = BookWordSet.From(totals, Opened);

        var blocks = new List<StoredBlock>();
        for (var b = 0; b < 160; b++)
        {
            var text = new System.Text.StringBuilder();
            var tokens = new List<TokenDto>();
            while (text.Length < 56)
            {
                var piece = random.Next(10) == 0 ? totals[random.Next(totals.Count)].Text : "ひとつの言葉";
                foreach (var c in piece)
                {
                    tokens.Add(Token(text.Length, text.Length + 1, 1));
                    text.Append(c);
                }
            }
            blocks.Add(new StoredBlock(b, text.ToString(), tokens));
        }
        var unit = new ReadingUnit(1, ReadingUnitKind.Epub, Opened, 0, ReadingUnitState.Done, "s", "v", blocks);
        Assert.InRange(blocks.Sum(b => b.Text.Length), 8900, 9500);

        var times = new List<double>();
        var joins = 0;
        for (var run = 0; run < 11; run++)
        {
            var watch = Stopwatch.StartNew();
            var joined = BookWordJoin.Apply(unit, set);
            watch.Stop();
            joins = joined.Blocks.Sum(b => b.Tokens.Count(t => t.Sub != null));
            if (run > 0) times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        output.WriteLine($"Join of {blocks.Sum(b => b.Text.Length)} characters in 160 blocks with 200 book words, " +
                         $"{joins} joins: median {times[times.Count / 2]:0.00} ms, max {times[^1]:0.00} ms");
        Assert.True(joins > 0);
        Assert.True(times[times.Count / 2] < 10, $"The join took {times[times.Count / 2]:0.0} ms");
    }

    /// <summary>
    /// Records compare lists by reference, so this compares tokens with their parts by value
    /// </summary>
    private sealed class TokenComparer : IEqualityComparer<TokenDto>
    {
        public bool Equals(TokenDto? x, TokenDto? y) =>
            x is not null && y is not null && x.S == y.S && x.E == y.E && x.W == y.W && x.R == y.R &&
            (x.Sub ?? []).SequenceEqual(y.Sub ?? []);

        public int GetHashCode(TokenDto obj) => HashCode.Combine(obj.S, obj.E, obj.W);
    }
}
