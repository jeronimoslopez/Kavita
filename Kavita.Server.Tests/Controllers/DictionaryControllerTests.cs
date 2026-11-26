using System.Reflection;
using System.Security.Claims;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Store;
using Kavita.Common;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Server.Attributes;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class DictionaryControllerTests
{
    private const int UserId = 7;
    private readonly IDictionaryService _service = Substitute.For<IDictionaryService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private DictionaryController CreateController(bool isAdmin = false)
    {
        var userContext = Substitute.For<IUserContext>();
        userContext.GetUserIdOrThrow().Returns(UserId);
        var services = new ServiceCollection().AddSingleton(userContext).BuildServiceProvider();

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, PolicyConstants.AdminRole));

        return new DictionaryController(_service, _unitOfWork)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }

    [Theory]
    [InlineData(nameof(DictionaryController.AnalyzeSeries))]
    [InlineData(nameof(DictionaryController.RemoveSeries))]
    [InlineData(nameof(DictionaryController.GetPendingAnalysis))]
    [InlineData(nameof(DictionaryController.StoreAnalysis))]
    public void RunnerAndSeriesEndpoints_AreAdminOnly(string action)
    {
        var method = typeof(DictionaryController).GetMethod(action)!;
        Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PolicyGroups.AdminPolicy);
    }

    [Theory]
    [InlineData(nameof(DictionaryController.GetEpubPage))]
    [InlineData(nameof(DictionaryController.PrioritizeChapter))]
    public void ReaderEndpoints_CheckChapterAccess(string action)
    {
        var method = typeof(DictionaryController).GetMethod(action)!;
        Assert.NotNull(method.GetCustomAttribute<ChapterAccessAttribute>());
    }

    [Fact]
    public void ClearReadingChoice_ChecksSeriesAccess()
    {
        var method = typeof(DictionaryController).GetMethod(nameof(DictionaryController.ClearReadingChoice))!;
        Assert.NotNull(method.GetCustomAttribute<SeriesAccessAttribute>());
        Assert.NotNull(method.GetCustomAttribute<HttpDeleteAttribute>());
    }

    [Fact]
    public async Task GetEpubPage_AsksForTheRequestingUsersPage()
    {
        var page = new EpubPageTokensDto();
        _service.GetEpubPageAsync(UserId, 3, 1, Arg.Any<CancellationToken>()).Returns(page);

        var result = await CreateController().GetEpubPage(3, 1);

        Assert.Same(page, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SaveReadingChoice_IsNotFoundWithoutSeriesAccess_AndNeverReachesTheService()
    {
        _unitOfWork.UserRepository.HasAccessToSeries(UserId, 5, Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateController().SaveReadingChoice(new ReadingChoiceDto(5, 90, "またたく"));

        Assert.IsType<NotFoundResult>(result);
        await _service.DidNotReceiveWithAnyArgs().SaveReadingChoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task SaveReadingChoice_SavesForTheUserAndIsBadRequestForAReadingNoGroupHas()
    {
        _unitOfWork.UserRepository.HasAccessToSeries(UserId, 5, Arg.Any<CancellationToken>()).Returns(true);
        var good = new ReadingChoiceDto(5, 90, "またたく");
        var bad = new ReadingChoiceDto(5, 90, "まじまじ");
        _service.SaveReadingChoiceAsync(UserId, bad, Arg.Any<CancellationToken>())
            .Returns(_ => throw new KavitaException("まじまじ isn't a reading of one of 瞬く's groups"));

        Assert.IsType<OkResult>(await CreateController().SaveReadingChoice(good));
        Assert.IsType<BadRequestObjectResult>(await CreateController().SaveReadingChoice(bad));
        await _service.Received(1).SaveReadingChoiceAsync(UserId, good, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadingChoice_AdminSkipsTheAccessQuery_AndClearingGoesToTheService()
    {
        Assert.IsType<OkResult>(await CreateController(isAdmin: true).SaveReadingChoice(new ReadingChoiceDto(5, 90, "またたく")));
        Assert.IsType<OkResult>(await CreateController().ClearReadingChoice(5, 90));

        await _unitOfWork.UserRepository.DidNotReceiveWithAnyArgs().HasAccessToSeries(default, default(int), default);
        await _service.Received(1).ClearReadingChoiceAsync(UserId, 5, 90, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadingChoiceBody_ReadsInCamelCase()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ReadingChoiceDto>("""{"seriesId": 5, "wordId": 90, "reading": "またたく"}""", Web);

        Assert.Equal(new ReadingChoiceDto(5, 90, "またたく"), dto);
    }

    [Fact]
    public void LookupResult_WritesEveryReadingsFieldWithNulls()
    {
        var result = new DictionaryLookupResultDto { Word = new DictionaryWordDto(1, "気", "気", "名詞", false, "き", "き", ["き"], 4) };

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result, Web));

        Assert.Equal("""{"level":"certain","source":"dictionary","shown":null,"pick":null,"bookOnly":false,"groups":[]}""",
            json.RootElement.GetProperty("readings").GetRawText());
    }

    [Fact]
    public async Task GetEpubPage_AnswersNoContentWhenThePageIsNotTokenized()
    {
        _service.GetEpubPageAsync(UserId, 3, 1, Arg.Any<CancellationToken>()).Returns((EpubPageTokensDto?) null);

        var result = await CreateController().GetEpubPage(3, 1);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Lookup_IsNotFoundWithoutChapterAccess_AndNeverReachesTheService()
    {
        _unitOfWork.UserRepository.HasAccessToChapter(UserId, 3, Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateController().Lookup(new DictionaryLookupRequestDto(3, 0, 0, 0, null));

        Assert.IsType<NotFoundResult>(result.Result);
        await _service.DidNotReceiveWithAnyArgs().LookupAsync(default, default!, default);
    }

    [Fact]
    public async Task Lookup_AnswersNoContentWhenNothingIsAtTheOffset()
    {
        _unitOfWork.UserRepository.HasAccessToChapter(UserId, 3, Arg.Any<CancellationToken>()).Returns(true);
        _service.LookupAsync(Arg.Any<int>(), Arg.Any<DictionaryLookupRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((DictionaryLookupResultDto?) null);

        var result = await CreateController().Lookup(new DictionaryLookupRequestDto(3, 0, 0, 99, null));

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Lookup_LetsAnAdminThroughWithoutTheAccessQuery()
    {
        var found = new DictionaryLookupResultDto();
        _service.LookupAsync(Arg.Any<int>(), Arg.Any<DictionaryLookupRequestDto>(), Arg.Any<CancellationToken>()).Returns(found);

        var result = await CreateController(isAdmin: true).Lookup(new DictionaryLookupRequestDto(3, 0, 0, 0, null));

        Assert.Same(found, Assert.IsType<OkObjectResult>(result.Result).Value);
        await _unitOfWork.UserRepository.DidNotReceiveWithAnyArgs().HasAccessToChapter(default, default, default);
    }

    /// <summary>
    /// ASP.NET reads request bodies and writes answers with the web defaults, camelCase and case-insensitive
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("""{"chapterId": 3, "page": 1, "block": 2, "s": 4, "sub": null}""", null)]
    [InlineData("""{"chapterId": 3, "page": 1, "block": 2, "s": 4}""", null)]
    [InlineData("""{"chapterId": 3, "page": 1, "block": 2, "s": 4, "sub": null, "phrase": null}""", null)]
    [InlineData("""{"chapterId": 3, "page": 1, "block": 2, "s": 4, "sub": null, "phrase": 1}""", 1)]
    public void LookupRequest_ReadsWithOrWithoutAPhrase(string json, int? phrase)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<DictionaryLookupRequestDto>(json, Web)!;

        Assert.Equal(new DictionaryLookupRequestDto(3, 1, 2, 4, null, phrase), request);
    }

    [Fact]
    public void LookupResult_WritesPartOfInCamelCase()
    {
        var result = new DictionaryLookupResultDto
        {
            Word = new DictionaryWordDto(1, "気", "気", "名詞", false, "き", "き", ["き"], 4),
            PartOf = [new DictionaryPhraseDto(0, 0, 3, "ki ga suru", "kigasuru", "to have a feeling that")],
        };

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result, Web));

        Assert.Equal("""[{"index":0,"s":0,"e":3,"lemma":"ki ga suru","reading":"kigasuru","gloss":"to have a feeling that"}]""",
            json.RootElement.GetProperty("partOf").GetRawText());
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(0, 1)]
    [InlineData(50, 20)]
    public async Task GetPendingAnalysis_DefaultsAndCapsTheLimit(int? limit, int expected)
    {
        _service.GetPendingAnalysisAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalysisPassageDto>());

        var result = await CreateController(isAdmin: true).GetPendingAnalysis(limit, 10);

        Assert.IsType<NoContentResult>(result.Result);
        await _service.Received(1).GetPendingAnalysisAsync(expected, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreAnalysis_IsBadRequestWhenTheServiceRefusesTheBody()
    {
        _service.StoreAnalysisAsync(Arg.Any<AnalysisStoreRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<AnalysisStoreResultDto>(_ => throw new KavitaException("generator is required"));

        var result = await CreateController(isAdmin: true).StoreAnalysis(new AnalysisStoreRequestDto());

        Assert.Equal("generator is required", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task WhileTheStoreIsUpgraded_ReaderCallsSucceedQuietlyAndRunnerWritesAreUnavailable()
    {
        _service.PrioritizeChapterAsync(3, Arg.Any<CancellationToken>()).Returns(_ => throw new ReadingStoreNotReadyException());
        _service.GetPendingAnalysisAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IList<AnalysisPassageDto>>(_ => throw new ReadingStoreNotReadyException());
        _service.StoreAnalysisAsync(Arg.Any<AnalysisStoreRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<AnalysisStoreResultDto>(_ => throw new ReadingStoreNotReadyException());

        Assert.IsType<OkResult>(await CreateController().PrioritizeChapter(3));
        Assert.IsType<NoContentResult>((await CreateController(isAdmin: true).GetPendingAnalysis(null)).Result);
        var stored = await CreateController(isAdmin: true).StoreAnalysis(new AnalysisStoreRequestDto());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(stored.Result).StatusCode);
    }
}
