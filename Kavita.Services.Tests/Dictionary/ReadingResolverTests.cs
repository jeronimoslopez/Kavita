using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class ReadingResolverTests
{
    #region Words

    private static SenseDto Sense(string key, params string[] r) => new(key, ["n"], [$"gloss {key}"], r.Length == 0 ? null : r);

    /// <summary>
    /// 瞬く as the sidecar gives it: しばたたく, then the common またたく, then めたたく, one sense each
    /// </summary>
    private static readonly WordEntryDto[] BlinkEntries =
    [
        new(2831002, false, ["しばたたく", "しばたく"]),
        new(1341200, true, ["またたく", "まばたく"]),
        new(2831001, false, ["めたたく", "めばたく"]),
    ];

    private static readonly SenseDto[] BlinkSenses = [Sense("2831002:1"), Sense("1341200:1"), Sense("1341200:2"), Sense("2831001:1")];

    private static StoredWord Blink => new(1, "瞬く", "動詞", false, "しばたたく", null,
        ["しばたたく", "しばたく", "またたく", "まばたく", "めたたく", "めばたく"], BlinkSenses, "lex") { Entries = BlinkEntries };

    /// <summary>
    /// 今日, one entry whose second sense JMdict restricts to こんにち
    /// </summary>
    private static readonly SenseDto[] TodaySenses = [Sense("1579110:1"), Sense("1579110:2", "こんにち")];

    private static readonly WordEntryDto[] TodayEntries = [new(1579110, true, ["きょう", "こんにち", "こんち", "こんじつ"])];

    private static StoredWord Say => new(2, "言う", "動詞", false, "いう", 5, ["いう", "ゆう"],
        [Sense("1587040:1"), Sense("1587040:2")], "lex") { Entries = [new WordEntryDto(1587040, true, ["いう", "ゆう"])] };

    private static IReadOnlyList<ReadingGroupDto> BlinkGroups => ReadingGroups.Of(BlinkEntries, BlinkSenses);

    /// <summary>
    /// 鴇 read とき or つき, two entries neither of which is common
    /// </summary>
    private static IReadOnlyList<ReadingGroupDto> RareGroups =>
        ReadingGroups.Of([new WordEntryDto(1, false, ["とき"]), new WordEntryDto(2, false, ["つき"])], [Sense("1:1"), Sense("2:1")]);

    private static ReadingInputs Inputs(IReadOnlyList<ReadingGroupDto>? groups, MarkedReadingResult? marked = null,
        string? choice = null, ReadingHintDto? hint = null, string? tokenReading = null, bool blind = false) =>
        new(groups, marked, choice, hint, tokenReading, blind);

    private static IEnumerable<string> FirstReadings(ReadingsDto result) => result.Groups.Select(g => g.Readings[0]);

    #endregion

    #region Groups

    [Fact]
    public void Groups_AreOnePerEntryInEntryOrderWithTheEntrysSensesAndFlag()
    {
        var groups = BlinkGroups;

        Assert.Equal([2831002L, 1341200L, 2831001L], groups.Select(g => g.Seq));
        Assert.Equal(["しばたたく", "しばたく"], groups[0].Readings);
        Assert.Equal([false, true, false], groups.Select(g => g.Common));
        Assert.Equal(["1341200:1", "1341200:2"], groups[1].Senses.Select(s => s.Key));
    }

    [Fact]
    public void Groups_SplitAnEntryWhoseSensesAreRestrictedByReading()
    {
        var groups = ReadingGroups.Of(TodayEntries, TodaySenses);

        // きょう, こんち and こんじつ take only the first sense, and こんにち takes both
        Assert.Equal(2, groups.Count);
        Assert.Equal(["きょう", "こんち", "こんじつ"], groups[0].Readings);
        Assert.Equal(["1579110:1"], groups[0].Senses.Select(s => s.Key));
        Assert.Equal(["こんにち"], groups[1].Readings);
        Assert.Equal(["1579110:1", "1579110:2"], groups[1].Senses.Select(s => s.Key));
        Assert.All(groups, g => Assert.Equal(1579110L, g.Seq));
    }

    [Fact]
    public void Groups_SplitIntoThreeWhenTwoReadingsEachHaveTheirOwnSenses()
    {
        // 上手: じょうず is one entry, and うわて and かみて share sense 1 and each have two more of their own
        WordEntryDto[] entries = [new(1353320, true, ["じょうず"]), new(1580400, false, ["うわて", "かみて"])];
        SenseDto[] senses =
        [
            Sense("1353320:1"), Sense("1580400:1"), Sense("1580400:2", "かみて"), Sense("1580400:3", "かみて"),
            Sense("1580400:4", "うわて"), Sense("1580400:5", "うわて"),
        ];

        var groups = ReadingGroups.Of(entries, senses);

        Assert.Equal(["じょうず", "うわて", "かみて"], groups.Select(g => g.Readings.Single()));
        Assert.Equal(["1580400:1", "1580400:4", "1580400:5"], groups[1].Senses.Select(s => s.Key));
        Assert.Equal(["1580400:1", "1580400:2", "1580400:3"], groups[2].Senses.Select(s => s.Key));
    }

    [Fact]
    public void Groups_LeaveOutAReadingNoSenseAppliesToAndAnEntryWithoutSenses()
    {
        WordEntryDto[] entries = [new(1, true, ["あ", "い"]), new(2, false, ["う"])];
        SenseDto[] senses = [Sense("1:1", "あ")];

        var groups = ReadingGroups.Of(entries, senses);

        Assert.Equal(["あ"], Assert.Single(groups).Readings);
    }

    [Fact]
    public void Groups_OfAWordWithoutEntriesAreEmpty()
    {
        Assert.Empty(ReadingGroups.Of([], TodaySenses));
        Assert.Empty(ReadingGroups.Of(Blink with { Entries = null }));
    }

    [Fact]
    public void Groups_MergeEntriesThatAllHaveTheSameReading()
    {
        // そう as three JMdict entries, each read そう
        WordEntryDto[] entries = [new(2137720, false, ["そう"]), new(1006830, true, ["そう"]), new(2833030, false, ["そう"])];
        SenseDto[] senses = [Sense("2137720:1"), Sense("1006830:1"), Sense("1006830:2"), Sense("2833030:1")];

        var group = Assert.Single(ReadingGroups.Of(entries, senses));

        Assert.Equal(["そう"], group.Readings);
        Assert.Equal(2137720L, group.Seq);
        Assert.True(group.Common);
        Assert.Equal(["2137720:1", "1006830:1", "1006830:2", "2833030:1"], group.Senses.Select(s => s.Key));
    }

    [Fact]
    public void Groups_MergeEqualReadingsInPlaceAndKeepDifferentListsApart()
    {
        // 羅's [うすもの, ら] and [ら] share ら but stay apart. The second [ら] folds into the first, where it sat
        WordEntryDto[] entries =
        [
            new(1, false, ["ら"]), new(2, false, ["うすもの", "ら"]), new(3, true, ["ら"]), new(4, false, ["あみ"]),
        ];
        SenseDto[] senses = [Sense("1:1"), Sense("2:1"), Sense("3:1"), Sense("4:1")];

        var groups = ReadingGroups.Of(entries, senses);

        Assert.Equal([["ら"], ["うすもの", "ら"], ["あみ"]], groups.Select(g => g.Readings.ToArray()));
        Assert.Equal([1L, 2L, 4L], groups.Select(g => g.Seq));
        Assert.Equal(["1:1", "3:1"], groups[0].Senses.Select(s => s.Key));
        Assert.Equal([true, false, false], groups.Select(g => g.Common));
    }

    [Fact]
    public void Groups_MergeSplitPartsWithEqualReadingsAcrossEntries()
    {
        // Both entries restrict a sense to こんにち, so each splits into [きょう] and [こんにち], and the parts merge
        WordEntryDto[] entries = [new(10, true, ["きょう", "こんにち"]), new(20, false, ["きょう", "こんにち"])];
        SenseDto[] senses = [Sense("10:1"), Sense("10:2", "こんにち"), Sense("20:1"), Sense("20:2", "こんにち")];

        var groups = ReadingGroups.Of(entries, senses);

        Assert.Equal(["きょう", "こんにち"], groups.Select(g => g.Readings.Single()));
        Assert.Equal(["10:1", "20:1"], groups[0].Senses.Select(s => s.Key));
        Assert.Equal(["10:1", "10:2", "20:1", "20:2"], groups[1].Senses.Select(s => s.Key));
        Assert.All(groups, g => Assert.Equal(10L, g.Seq));
    }

    [Fact]
    public void Groups_OfAKanaLemmaAreOneGroupReadAsTheLemma()
    {
        // ちょ printed in kana, whose first entry is 一寸 read いっすん
        WordEntryDto[] entries = [new(1160790, false, ["いっすん"]), new(1008950, true, ["ちょっと", "ちょと", "ちょいと"])];
        SenseDto[] senses = [Sense("1160790:1"), Sense("1008950:1"), Sense("1008950:2", "ちょっと")];
        var word = new StoredWord(3, "チョ", "副詞", false, "ちょ", null, ["いっすん", "ちょっと"], senses, "lex")
        {
            Entries = entries,
        };

        var group = Assert.Single(ReadingGroups.Of(word));

        Assert.Equal(["ちょ"], group.Readings);
        Assert.Equal(1160790L, group.Seq);
        Assert.True(group.Common);
        Assert.Equal(["1160790:1", "1008950:1", "1008950:2"], group.Senses.Select(s => s.Key));

        var result = ReadingResolver.Resolve(ReadingResolver.ForPhrase(word));
        Assert.Equal((ReadingLevel.Certain, ReadingSource.Dictionary, "ちょ"), (result.Level, result.Source, result.Pick));
        Assert.Empty(ReadingGroups.Of(word with { Entries = [] }));
    }

    #endregion

    #region Resolver

    private static MarkedReadingResult Marked(string printed, string reading, string? dictionaryPrinted = null,
        string? dictionaryReading = null) => new(printed, reading, dictionaryPrinted, dictionaryReading);

    private static readonly ReadingHintDto SeriesBlink = new("またたく", "またたく", ReadingSource.Series);

    public static TheoryData<string, ReadingInputs, ReadingsDto, string[]> Cases => new()
    {
        {
            "a blind token on 瞬く in a rare group gives way to the common group, as a guess from the dictionary",
            Inputs(BlinkGroups, tokenReading: "しばたたく", blind: true),
            new ReadingsDto(ReadingLevel.Guess, ReadingSource.Dictionary, null, "またたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a token the sentence chose in a rare group still wins",
            Inputs(BlinkGroups, tokenReading: "しばたたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Tokenizer, null, "しばたたく", false, []),
            ["しばたたく", "またたく", "めたたく"]
        },
        {
            "a blind token in a common group is a guess, with its group first",
            Inputs(BlinkGroups, tokenReading: "まばたく", blind: true),
            new ReadingsDto(ReadingLevel.Guess, ReadingSource.Tokenizer, null, "まばたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a blind token is a guess with its group first when no group is common",
            Inputs(RareGroups, tokenReading: "つき", blind: true),
            new ReadingsDto(ReadingLevel.Guess, ReadingSource.Tokenizer, null, "つき", false, []),
            ["つき", "とき"]
        },
        {
            "a saved choice beats a blind token in a rare group",
            Inputs(BlinkGroups, choice: "めたたく", tokenReading: "しばたたく", blind: true),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Choice, null, "めたたく", false, []),
            ["めたたく", "しばたたく", "またたく"]
        },
        {
            "a token the sentence chose is likely",
            Inputs(BlinkGroups, tokenReading: "またたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Tokenizer, null, "またたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a series hint beats the blind token",
            Inputs(BlinkGroups, hint: SeriesBlink, tokenReading: "しばたたく", blind: true),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Series, null, "またたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a saved choice beats a hint",
            Inputs(BlinkGroups, choice: "めばたく", hint: SeriesBlink, tokenReading: "しばたたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Choice, null, "めばたく", false, []),
            ["めたたく", "しばたたく", "またたく"]
        },
        {
            "a choice no group has is skipped",
            Inputs(BlinkGroups, choice: "まじまじ", tokenReading: "またたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Tokenizer, null, "またたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a hint whose reading no group has is ignored, like 化粧 marked メイク",
            Inputs(BlinkGroups, hint: new ReadingHintDto("めいく", "メイク", ReadingSource.Book), tokenReading: "またたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Tokenizer, null, "またたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a book hint names its source",
            Inputs(BlinkGroups, hint: new ReadingHintDto("まばたく", "まばたく", ReadingSource.Book), tokenReading: "しばたたく"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Book, null, "まばたく", false, []),
            ["またたく", "しばたたく", "めたたく"]
        },
        {
            "a token reading no group has falls back to the first group's first reading as a guess",
            Inputs(BlinkGroups, tokenReading: "なにか"),
            new ReadingsDto(ReadingLevel.Guess, ReadingSource.Dictionary, null, "しばたたく", false, []),
            ["しばたたく", "またたく", "めたたく"]
        },
        {
            "furigana that matches a group is certain and shows only that group",
            Inputs(BlinkGroups, Marked("またた", "またた", "またたく", "またたく"), choice: "めたたく", tokenReading: "しばたたく", blind: true),
            new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "またたく", "またたく", false, []),
            ["またたく"]
        },
        {
            "furigana on 小姐 read ねえちゃん matches no group, so every sense shows ungrouped",
            Inputs(ReadingGroups.Of([new WordEntryDto(9100, false, ["しょうじょ"])], [Sense("9100:1")]),
                Marked("ねえちゃん", "ねえちゃん", "ねえちゃん", "ねえちゃん")),
            new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "ねえちゃん", null, true, []),
            []
        },
        {
            "furigana without a dictionary form uses the surface pair",
            Inputs(BlinkGroups, Marked("マタタク", "またたく")),
            new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "マタタク", "またたく", false, []),
            ["またたく"]
        },
        {
            "言う has one group, so it's certain, with the token's reading as the pick",
            Inputs(ReadingGroups.Of(Say), tokenReading: "いう", blind: true),
            new ReadingsDto(ReadingLevel.Certain, ReadingSource.Dictionary, null, "いう", false, []),
            ["いう"]
        },
        {
            "one group without the token's reading has no pick",
            Inputs(ReadingGroups.Of(Say), tokenReading: "なにか", choice: "ゆう"),
            new ReadingsDto(ReadingLevel.Certain, ReadingSource.Dictionary, null, null, false, []),
            ["いう"]
        },
        {
            "a name without entries takes its hint and shows it as printed",
            Inputs([], hint: new ReadingHintDto("がおしゅん", "ガオシュン", ReadingSource.Book), tokenReading: "たかじゅん"),
            new ReadingsDto(ReadingLevel.Likely, ReadingSource.Book, "ガオシュン", null, false, []),
            []
        },
        {
            "a word with empty entries and nothing else is certain from the dictionary",
            Inputs([], tokenReading: "なぞ", blind: true),
            ReadingsDto.None,
            []
        },
        {
            "a word stored without entries gets the fixed answer whatever its furigana or hint",
            Inputs(null, Marked("ガオシュン", "がおしゅん"), hint: new ReadingHintDto("がおしゅん", "ガオシュン", ReadingSource.Book)),
            ReadingsDto.None,
            []
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Resolve(string why, ReadingInputs inputs, ReadingsDto expected, string[] firstReadings)
    {
        var result = ReadingResolver.Resolve(inputs);

        Assert.True(expected == result with { Groups = expected.Groups }, $"{why}: got {result}");
        Assert.Equal(firstReadings, FirstReadings(result));
    }

    [Fact]
    public void Resolve_TodaySplitBySensesOpensOnTheTokensGroupWithItsOwnSenses()
    {
        var result = ReadingResolver.Resolve(Inputs(ReadingGroups.Of(TodayEntries, TodaySenses), tokenReading: "こんにち"));

        Assert.Equal((ReadingLevel.Likely, ReadingSource.Tokenizer, "こんにち"), (result.Level, result.Source, result.Pick));
        Assert.Equal(["こんにち"], result.Groups[0].Readings);
        Assert.Equal(2, result.Groups[0].Senses.Count);
        Assert.Equal(["きょう", "こんち", "こんじつ"], result.Groups[1].Readings);
    }

    [Fact]
    public void Resolve_AChoiceOfAnyReadingOfAGroupPutsThatGroupFirst()
    {
        var result = ReadingResolver.Resolve(Inputs(BlinkGroups, choice: "しばたく", tokenReading: "またたく"));

        Assert.Equal((ReadingSource.Choice, "しばたく"), (result.Source, result.Pick));
        Assert.Equal(["しばたたく", "しばたく"], result.Groups[0].Readings);
    }

    #endregion

    #region Inputs from a page

    private static StoredBlock BlinkBlock(IReadOnlyList<RubyDto>? ruby = null, IReadOnlyList<int>? blind = null) =>
        new(0, "星が瞬いた", [new TokenDto(0, 1, 9, "ほし", null), new TokenDto(1, 2, null, null, null),
            new TokenDto(2, 4, 1, "まばたい", null), new TokenDto(4, 5, null, null, null)])
        {
            Ruby = ruby ?? [], Blind = blind ?? [],
        };

    [Fact]
    public void ForToken_SwapsTheSurfacesTrailingKanaForTheLemmasAndReadsBlindFromTheBlock()
    {
        var block = BlinkBlock(blind: [2]);

        var inputs = ReadingResolver.ForToken(block, block.Tokens[2], Blink, "めたたく", SeriesBlink);

        // 瞬い read まばたい with the lemma 瞬く gives まばたく
        Assert.Equal("まばたく", inputs.TokenReading);
        Assert.True(inputs.Blind);
        Assert.Null(inputs.Marked);
        Assert.Equal("めたたく", inputs.Choice);
        Assert.Same(SeriesBlink, inputs.Hint);
        Assert.Equal(3, inputs.Groups!.Count);
    }

    [Fact]
    public void ForToken_ReadsTheTokensFuriganaInDictionaryForm()
    {
        var block = BlinkBlock(ruby: [new RubyDto(2, 3, "まばた")]);

        var result = ReadingResolver.Resolve(ReadingResolver.ForToken(block, block.Tokens[2], Blink, null, null));

        Assert.Equal(new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "まばたく", "まばたく", false, result.Groups), result);
        Assert.Equal(["またたく", "まばたく"], Assert.Single(result.Groups).Readings);
    }

    [Fact]
    public void ForToken_OfAWordStoredWithoutEntriesHasNoGroups()
    {
        var block = BlinkBlock(ruby: [new RubyDto(2, 3, "まばた")]);

        var inputs = ReadingResolver.ForToken(block, block.Tokens[2], Blink with { Entries = null }, "めたたく", SeriesBlink);

        Assert.Null(inputs.Groups);
        var result = ReadingResolver.Resolve(inputs);
        Assert.Equal(ReadingsDto.None with { Groups = result.Groups }, result);
        Assert.Empty(result.Groups);
    }

    [Theory]
    [InlineData(null, "しばたたく")] // no occurrence reading, so the word's
    [InlineData("まばたい", "まばたく")]
    public void TokenReading_FallsBackToTheWordsReading(string? reading, string expected)
    {
        Assert.Equal(expected, ReadingResolver.TokenReading("星が瞬いた", 2, 4, reading, Blink));
    }

    [Fact]
    public void TokenReading_FallsBackWhenTheStemDoesntMatchOrTheReadingIsTooShort()
    {
        Assert.Equal("しばたたく", ReadingResolver.TokenReading("瞬いた", 0, 2, "まばたい", Blink with { Lemma = "目叩く" }));
        Assert.Equal("しばたたく", ReadingResolver.TokenReading("瞬いた", 0, 3, "た", Blink));
        Assert.Equal("いう", ReadingResolver.TokenReading("言った", 0, 2, "いっ", Say));
    }

    [Fact]
    public void ForPartAndForPhrase_UseOnlyTheGroupsAndAReading()
    {
        var block = new StoredBlock(0, "瞬き星", [new TokenDto(0, 3, 7, "まばたきぼし", [new SubTokenDto(0, 2, 1, "またたき")])]);

        var part = ReadingResolver.ForPart(block, block.Tokens[0].Sub![0], Blink);
        var phrase = ReadingResolver.ForPhrase(Blink);

        Assert.Equal(new ReadingInputs(part.Groups, null, null, null, "またたく", false), part);
        Assert.Equal(new ReadingInputs(phrase.Groups, null, null, null, "しばたたく", false), phrase);
        Assert.Equal(ReadingSource.Tokenizer, ReadingResolver.Resolve(part).Source);
        Assert.Equal(ReadingLevel.Likely, ReadingResolver.Resolve(phrase).Level);
    }

    #endregion

    #region Hints

    private static WordMarkTotals Totals(int uses, params (string Reading, string Printed, int Count)[] readings) =>
        new(1, uses, readings.Select(r => new ReadingMarkTotal(r.Reading, r.Printed, r.Count)).ToList());

    public static TheoryData<string, bool, WordMarkTotals?, ReadingMarkTotal?> HintCases => new()
    {
        { "one marked reading on a fifth of the uses", false, Totals(10, ("またたく", "またたく", 2)), new ReadingMarkTotal("またたく", "またたく", 2) },
        { "just under a fifth", false, Totals(11, ("またたく", "またたく", 2)), null },
        { "two marked readings disagree", false, Totals(4, ("またたく", "またたく", 3), ("しばたたく", "しばたたく", 1)), null },
        { "a marked reading in no group", false, Totals(2, ("めいく", "メイク", 2)), null },
        { "nothing marked", false, Totals(10), null },
        { "no rows at all", false, null, null },
        { "a name takes its most counted reading on any share", true, Totals(100, ("がおしゅん", "ガオシュン", 2), ("こうじゅん", "コウジュン", 1)), new ReadingMarkTotal("がおしゅん", "ガオシュン", 2) },
        { "a name's reading needs no group", true, Totals(5, ("めいく", "メイク", 1)), new ReadingMarkTotal("めいく", "メイク", 1) },
    };

    [Theory]
    [MemberData(nameof(HintCases))]
    public void Hint_Qualifying(string why, bool isProper, WordMarkTotals? totals, ReadingMarkTotal? expected)
    {
        Assert.True(expected == ReadingHints.Qualifying(isProper, BlinkGroups, totals), why);
    }

    [Fact]
    public void Hint_TakesTheBookFirstAndThenTheSeries()
    {
        var book = Totals(10, ("まばたく", "まばたく", 5));
        var series = Totals(40, ("またたく", "瞬く", 10));

        Assert.Equal(new ReadingHintDto("まばたく", "まばたく", ReadingSource.Book), ReadingHints.Of(false, BlinkGroups, book, series));
        Assert.Equal(new ReadingHintDto("またたく", "瞬く", ReadingSource.Series), ReadingHints.Of(false, BlinkGroups, Totals(10), series));
        Assert.Null(ReadingHints.Of(false, BlinkGroups, null, Totals(40, ("またたく", "瞬く", 1))));
    }

    [Fact]
    public void Hint_ThresholdsAreTheContractsNumbers()
    {
        Assert.Equal(0.2, ReadingHints.MinMarkedShare);
        Assert.Equal(1, ReadingHints.MaxDistinctReadings);
    }

    #endregion
}
