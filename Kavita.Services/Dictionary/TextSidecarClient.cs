using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

public class TextSidecarClient : ITextSidecarClient
{
    public Task<TokenizeResponseDto> TokenizeAsync(string sidecarUrl, IReadOnlyList<TokenizeBlockDto> blocks,
        CancellationToken ct = default) => throw new NotImplementedException();
}
