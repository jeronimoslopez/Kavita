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
    public async Task GetEpubPage_AnswersNoContentWhenThePageIsNotTokenized()
    {
        _service.GetEpubPageAsync(3, 1, Arg.Any<CancellationToken>()).Returns((EpubPageTokensDto?) null);

        var result = await CreateController().GetEpubPage(3, 1);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Lookup_IsNotFoundWithoutChapterAccess_AndNeverReachesTheService()
    {
        _unitOfWork.UserRepository.HasAccessToChapter(UserId, 3, Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateController().Lookup(new DictionaryLookupRequestDto(3, 0, 0, 0, null));

        Assert.IsType<NotFoundResult>(result.Result);
        await _service.DidNotReceiveWithAnyArgs().LookupAsync(default!, default);
    }

    [Fact]
    public async Task Lookup_AnswersNoContentWhenNothingIsAtTheOffset()
    {
        _unitOfWork.UserRepository.HasAccessToChapter(UserId, 3, Arg.Any<CancellationToken>()).Returns(true);
        _service.LookupAsync(Arg.Any<DictionaryLookupRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((DictionaryLookupResultDto?) null);

        var result = await CreateController().Lookup(new DictionaryLookupRequestDto(3, 0, 0, 99, null));

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Lookup_LetsAnAdminThroughWithoutTheAccessQuery()
    {
        var found = new DictionaryLookupResultDto();
        _service.LookupAsync(Arg.Any<DictionaryLookupRequestDto>(), Arg.Any<CancellationToken>()).Returns(found);

        var result = await CreateController(isAdmin: true).Lookup(new DictionaryLookupRequestDto(3, 0, 0, 0, null));

        Assert.Same(found, Assert.IsType<OkObjectResult>(result.Result).Value);
        await _unitOfWork.UserRepository.DidNotReceiveWithAnyArgs().HasAccessToChapter(default, default, default);
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
}
