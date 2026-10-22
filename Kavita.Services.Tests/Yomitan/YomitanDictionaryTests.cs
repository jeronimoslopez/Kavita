using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kavita.Models.DTOs.Yomitan;
using Kavita.Services.Yomitan;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Services.Tests.Yomitan;

public class YomitanDictionaryTests : IDisposable
{
    private const string Title = "Test Dictionary";
    private const string SvgPath = "img/a.svg";
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10" viewBox="0 0 20 10"><rect width="20" height="10"/></svg>""";

    private static readonly string[] Dictionaries = [Title];

    private readonly string _directory;
    private readonly YomitanDictionaryDatabase _database;
    private readonly YomitanDictionaryImporter _importer;
    private readonly YomitanDictionaryService _service;

    public YomitanDictionaryTests()
    {
        _directory = Path.Join(Path.GetTempPath(), "kavita-yomitan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new YomitanDictionaryDatabase(Path.Join(_directory, "dictionaries.db"));
        _importer = new YomitanDictionaryImporter(_database, NullLogger<YomitanDictionaryImporter>.Instance);
        _service = new YomitanDictionaryService(_database, new YomitanImportProgress());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort, the OS temp folder gets cleaned eventually
        }
        GC.SuppressFinalize(this);
    }

    #region Fixture

    private string CreateFixtureZip(string revision = "rev1", string fileName = "test.zip")
    {
        var path = Path.Join(_directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        AddEntry(archive, "index.json", """
            {"title":"TITLE","format":3,"revision":"REVISION","sequenced":true,"author":"Kavita","frequencyMode":"rank-based",
             "sourceLanguage":"ja","targetLanguage":"en",
             "tagMeta":{"legacy":{"category":"misc","order":1,"notes":"legacy tag","score":0}}}
            """.Replace("TITLE", Title).Replace("REVISION", revision));
        AddEntry(archive, "term_bank_1.json", """
            [
              ["食べる","たべる","v1 common","v1",10,["to eat",{"type":"text","text":"to consume"}],100,"P"],
              ["食べ物","たべもの","n","",5,[{"type":"structured-content","content":[
                  {"tag":"span","data":{"class":"x"},"content":"food"},
                  {"tag":"div","content":{"tag":"img","path":"img/a.svg","width":2,"height":1,"sizeUnits":"em","alt":"A","collapsible":false,"verticalAlign":"middle"}}
              ]}],101,""],
              ["たべる","","v1","v1",1,["kana form"],100,""],
              ["飲む","のむ",null,"v5",3,[{"type":"image","path":"img/a.svg","width":10,"title":"drink"}],102,""],
              ["𠮟る","しかる","v5","v5",0,["to scold"],103,""]
            ]
            """);
        AddEntry(archive, SvgPath, Svg);
        AddEntry(archive, "term_meta_bank_1.json", """
            [
              ["食べる","freq",{"value":1,"displayValue":"1㋕"}],
              ["食べる","freq",{"reading":"たべる","frequency":5}],
              ["飲む","pitch",{"reading":"のむ","pitches":[{"position":1}]}]
            ]
            """);
        AddEntry(archive, "kanji_bank_1.json", """
            [["食","ショク ジキ","く.う た.べる","jouyou",["eat","food"],{"freq":"328","grade":"2"}]]
            """);
        AddEntry(archive, "kanji_meta_bank_1.json", """[["食","freq",3]]""");
        AddEntry(archive, "tag_bank_1.json", """
            [["v1","partOfSpeech",0,"Ichidan verb",0],["common","popular",-10,"common word",10],["v1","dup",5,"duplicate",0]]
            """);
        AddEntry(archive, "styles.css", ".gloss { color: red; }");
        return path;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private async Task ImportFixture()
    {
        var result = await _importer.ImportIfNeededAsync(CreateFixtureZip(), "test.zip");
        Assert.Equal(YomitanImportOutcome.Imported, result.Outcome);
        Assert.Null(result.Error);
    }

    #endregion

    [Fact]
    public async Task Import_WritesSummaryLikeYomitan()
    {
        await ImportFixture();

        var summaries = await _service.GetDictionaryInfoAsync();
        var summary = Assert.Single(summaries);

        Assert.Equal(Title, summary.GetProperty("title").GetString());
        Assert.Equal("rev1", summary.GetProperty("revision").GetString());
        Assert.True(summary.GetProperty("sequenced").GetBoolean());
        Assert.Equal(3, summary.GetProperty("version").GetInt32());
        Assert.True(summary.GetProperty("importDate").GetInt64() > 0);
        Assert.True(summary.GetProperty("prefixWildcardsSupported").GetBoolean());
        Assert.True(summary.GetProperty("importSuccess").GetBoolean());
        Assert.Equal(".gloss { color: red; }", summary.GetProperty("styles").GetString());
        Assert.Equal("rank-based", summary.GetProperty("frequencyMode").GetString());
        Assert.Equal("Kavita", summary.GetProperty("author").GetString());
        Assert.Equal("ja", summary.GetProperty("sourceLanguage").GetString());
        Assert.False(summary.TryGetProperty("isUpdatable", out _));

        var counts = summary.GetProperty("counts");
        Assert.Equal(5, counts.GetProperty("terms").GetProperty("total").GetInt32());
        Assert.Equal(3, counts.GetProperty("termMeta").GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("termMeta").GetProperty("freq").GetInt32());
        Assert.Equal(1, counts.GetProperty("termMeta").GetProperty("pitch").GetInt32());
        Assert.Equal(1, counts.GetProperty("kanji").GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("kanjiMeta").GetProperty("freq").GetInt32());
        // Three from the bank plus the legacy index.json tag
        Assert.Equal(4, counts.GetProperty("tagMeta").GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("media").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Import_SkipsSameRevision_ReplacesNewRevision()
    {
        await ImportFixture();

        var again = await _importer.ImportIfNeededAsync(CreateFixtureZip(fileName: "again.zip"), "test.zip");
        Assert.Equal(YomitanImportOutcome.Skipped, again.Outcome);

        var updated = await _importer.ImportIfNeededAsync(CreateFixtureZip("rev2", "rev2.zip"), "test.zip");
        Assert.Equal(YomitanImportOutcome.Imported, updated.Outcome);

        var summary = Assert.Single(await _service.GetDictionaryInfoAsync());
        Assert.Equal("rev2", summary.GetProperty("revision").GetString());
        var terms = await _service.FindTermsBulkAsync(["食べる"], Dictionaries, "exact");
        Assert.Single(terms);
    }

    [Fact]
    public async Task FindTermsBulk_Exact_DedupsAcrossItemsAndIndexes()
    {
        await ImportFixture();

        var results = await _service.FindTermsBulkAsync(["食べる", "たべる"], Dictionaries, "exact");

        // 食べる matches by expression for item 0. For item 1, たべる matches row 3 by expression, and rows 1 and 3 by
        // reading, both already visited
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal(0, first.Index);
        Assert.Equal("term", first.MatchSource);
        Assert.Equal("exact", first.MatchType);
        Assert.Equal("食べる", first.Term);
        Assert.Equal("たべる", first.Reading);
        Assert.Equal(["v1", "common"], first.DefinitionTags);
        Assert.Equal(["P"], first.TermTags);
        Assert.Equal(["v1"], first.Rules);
        Assert.Equal(10, first.Score);
        Assert.Equal(100, first.Sequence);
        Assert.Equal(Title, first.Dictionary);
        // The text glossary object becomes a plain string
        Assert.Equal("""["to eat","to consume"]""", first.Definitions.GetRawText());

        var second = results[1];
        Assert.Equal(1, second.Index);
        Assert.Equal("term", second.MatchSource);
        Assert.Equal("たべる", second.Term);
        Assert.Equal("たべる", second.Reading);
    }

    [Fact]
    public async Task FindTermsBulk_MatchesByReading()
    {
        await ImportFixture();

        var results = await _service.FindTermsBulkAsync(["のむ"], Dictionaries, "exact");

        var result = Assert.Single(results);
        Assert.Equal("reading", result.MatchSource);
        Assert.Equal("飲む", result.Term);
        Assert.Empty(result.DefinitionTags);
    }

    [Fact]
    public async Task FindTermsBulk_Prefix_PromotesExactEquality()
    {
        await ImportFixture();

        var results = await _service.FindTermsBulkAsync(["食べ", "食べる"], Dictionaries, "prefix");

        // Item 0 sees 食べる then 食べ物 in key order, item 1 would see 食べる again, which is deduplicated
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0, r.Index));
        Assert.Equal(["食べる", "食べ物"], results.Select(r => r.Term));
        Assert.All(results, r => Assert.Equal("prefix", r.MatchType));

        var exact = await _service.FindTermsBulkAsync(["食べる"], Dictionaries, "prefix");
        Assert.Equal("exact", Assert.Single(exact).MatchType);
    }

    [Fact]
    public async Task FindTermsBulk_Suffix()
    {
        await ImportFixture();

        var results = await _service.FindTermsBulkAsync(["べる"], Dictionaries, "suffix");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("suffix", r.MatchType));
        Assert.Contains(results, r => r.Term == "食べる" && r.MatchSource == "term");
        Assert.Contains(results, r => r.Term == "たべる" && r.MatchSource == "term");

        // Reversal keeps surrogate pairs together
        var supplementary = await _service.FindTermsBulkAsync(["𠮟る"], Dictionaries, "suffix");
        var scold = Assert.Single(supplementary);
        Assert.Equal("exact", scold.MatchType);
        Assert.Equal("𠮟る", scold.Term);

        var byReading = await _service.FindTermsBulkAsync(["かる"], Dictionaries, "suffix");
        Assert.Equal("reading", Assert.Single(byReading).MatchSource);
    }

    [Fact]
    public async Task FindTermsBulk_FiltersDictionaries()
    {
        await ImportFixture();

        Assert.Empty(await _service.FindTermsBulkAsync(["食べる"], ["Other"], "exact"));
        Assert.Empty(await _service.FindTermsBulkAsync(["食べる"], [], "exact"));
    }

    [Fact]
    public async Task FindTermsExactBulk_MatchesTermAndReading()
    {
        await ImportFixture();

        var results = await _service.FindTermsExactBulkAsync(
        [
            new YomitanTermExactRequestDto { Term = "食べる", Reading = "x" },
            new YomitanTermExactRequestDto { Term = "食べる", Reading = "たべる" },
        ], Dictionaries);

        var result = Assert.Single(results);
        Assert.Equal(1, result.Index);
        Assert.Equal("term", result.MatchSource);
        Assert.Equal("exact", result.MatchType);
    }

    [Fact]
    public async Task FindTermsBySequenceBulk()
    {
        await ImportFixture();

        var results = await _service.FindTermsBySequenceBulkAsync(
        [
            new YomitanSequenceQueryDto { Query = 999, Dictionary = Title },
            new YomitanSequenceQueryDto { Query = 100, Dictionary = Title },
            new YomitanSequenceQueryDto { Query = 100, Dictionary = "Other" },
        ]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(1, r.Index);
            Assert.Equal("sequence", r.MatchSource);
            Assert.Equal("exact", r.MatchType);
            Assert.Equal(100, r.Sequence);
        });
        Assert.Equal(["食べる", "たべる"], results.Select(r => r.Term));
    }

    [Fact]
    public async Task StructuredContentImage_IsRewrittenLikeYomitan()
    {
        await ImportFixture();

        var food = Assert.Single(await _service.FindTermsBulkAsync(["食べ物"], Dictionaries, "exact"));
        Assert.Equal(101, food.Sequence);
        var glossary = food.Definitions[0];
        Assert.Equal("structured-content", glossary.GetProperty("type").GetString());

        var content = glossary.GetProperty("content");
        Assert.Equal("food", content[0].GetProperty("content").GetString());
        Assert.Equal("x", content[0].GetProperty("data").GetProperty("class").GetString());

        var image = content[1].GetProperty("content");
        Assert.Equal("img", image.GetProperty("tag").GetString());
        Assert.Equal(SvgPath, image.GetProperty("path").GetString());
        // width and height are the image's own size, the dictionary's values become preferred sizes
        Assert.Equal(20, image.GetProperty("width").GetInt32());
        Assert.Equal(10, image.GetProperty("height").GetInt32());
        Assert.Equal(2, image.GetProperty("preferredWidth").GetInt32());
        Assert.Equal(1, image.GetProperty("preferredHeight").GetInt32());
        Assert.Equal("em", image.GetProperty("sizeUnits").GetString());
        Assert.Equal("A", image.GetProperty("alt").GetString());
        Assert.Equal("middle", image.GetProperty("verticalAlign").GetString());
        Assert.False(image.GetProperty("collapsible").GetBoolean());

        var drink = Assert.Single(await _service.FindTermsBulkAsync(["飲む"], Dictionaries, "exact"));
        var drinkImage = drink.Definitions[0];
        Assert.Equal("image", drinkImage.GetProperty("type").GetString());
        Assert.Equal(20, drinkImage.GetProperty("width").GetInt32());
        Assert.Equal(10, drinkImage.GetProperty("preferredWidth").GetInt32());
        Assert.False(drinkImage.TryGetProperty("preferredHeight", out _));
        Assert.Equal("drink", drinkImage.GetProperty("title").GetString());
    }

    [Fact]
    public async Task FindTermMetaBulk()
    {
        await ImportFixture();

        var results = await _service.FindTermMetaBulkAsync(["飲む", "食べる"], Dictionaries);

        Assert.Equal(3, results.Count);
        Assert.Equal("pitch", results[0].Mode);
        Assert.Equal(0, results[0].Index);
        Assert.Equal("飲む", results[0].Term);
        Assert.Equal(1, results[0].Data.GetProperty("pitches")[0].GetProperty("position").GetInt32());
        Assert.All(results.Skip(1), r => Assert.Equal(1, r.Index));
        Assert.Equal("1㋕", results[1].Data.GetProperty("displayValue").GetString());
        Assert.Equal(5, results[2].Data.GetProperty("frequency").GetInt32());
    }

    [Fact]
    public async Task FindKanjiAndKanjiMeta()
    {
        await ImportFixture();

        var kanji = Assert.Single(await _service.FindKanjiBulkAsync(["x", "食"], Dictionaries));
        Assert.Equal(1, kanji.Index);
        Assert.Equal(["ショク", "ジキ"], kanji.Onyomi);
        Assert.Equal(["く.う", "た.べる"], kanji.Kunyomi);
        Assert.Equal(["jouyou"], kanji.Tags);
        Assert.Equal("""["eat","food"]""", kanji.Definitions.GetRawText());
        Assert.Equal("328", kanji.Stats.GetProperty("freq").GetString());

        var meta = Assert.Single(await _service.FindKanjiMetaBulkAsync(["食"], Dictionaries));
        Assert.Equal("freq", meta.Mode);
        Assert.Equal(3, meta.Data.GetInt32());
    }

    [Fact]
    public async Task FindTagMetaBulk_AlignsWithRequest()
    {
        await ImportFixture();

        var results = await _service.FindTagMetaBulkAsync(
        [
            new YomitanTagQueryDto { Query = "v1", Dictionary = Title },
            new YomitanTagQueryDto { Query = "missing", Dictionary = Title },
            new YomitanTagQueryDto { Query = "legacy", Dictionary = Title },
            new YomitanTagQueryDto { Query = "v1", Dictionary = "Other" },
        ]);

        Assert.Equal(4, results.Count);
        // First match wins
        Assert.Equal("partOfSpeech", results[0]!.Category);
        Assert.Equal("Ichidan verb", results[0]!.Notes);
        Assert.Null(results[1]);
        Assert.Equal("misc", results[2]!.Category);
        Assert.Equal(1, results[2]!.Order);
        Assert.Null(results[3]);
    }

    [Fact]
    public async Task GetMedia_ReturnsBase64AndDimensions()
    {
        await ImportFixture();

        var results = await _service.GetMediaAsync(
        [
            new YomitanMediaRequestDto { Path = "missing.png", Dictionary = Title },
            new YomitanMediaRequestDto { Path = SvgPath, Dictionary = Title },
        ]);

        var media = Assert.Single(results);
        Assert.Equal(1, media.Index);
        Assert.Equal(Title, media.Dictionary);
        Assert.Equal(SvgPath, media.Path);
        Assert.Equal("image/svg+xml", media.MediaType);
        Assert.Equal(20, media.Width);
        Assert.Equal(10, media.Height);
        Assert.Equal(Svg, Encoding.UTF8.GetString(Convert.FromBase64String(media.Content)));
    }

    [Fact]
    public async Task TermQueries_UseIndexes()
    {
        await ImportFixture();

        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT t.id FROM json_each(@items) AS j CROSS JOIN terms AS t
                ON t.expression >= j.value AND t.expression < j.value || char(1114111)
            WHERE +t.dictionary IN (SELECT value FROM json_each(@dictionaries))
            """;
        command.Parameters.AddWithValue("@dictionaries", JsonSerializer.Serialize(Dictionaries));
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(new[] { "食" }));
        var plan = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(3));

        Assert.Contains("ix_terms_expression", plan.ToString());
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"/>""", 100, 50)]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 30"/>""", 40, 30)]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="80px" viewBox="0 0 40 30"/>""", 80, 60)]
    [InlineData("""<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg" width="100%"/>""", 300, 150)]
    public void SvgDimensions(string svg, int width, int height)
    {
        Assert.Equal((width, height), YomitanMediaUtil.GetSvgDimensions(Encoding.UTF8.GetBytes(svg)));
    }

    [Theory]
    [InlineData("abc", "cba")]
    [InlineData("𠮟る", "る𠮟")]
    [InlineData("", "")]
    public void StringReverse_KeepsSurrogatePairs(string input, string expected)
    {
        Assert.Equal(expected, YomitanDictionaryImporter.StringReverse(input));
    }
}
