using System;
using System.Collections.Generic;
using HtmlAgilityPack;
using Kavita.API.Services;

namespace Kavita.Services.Dictionary;

public class EpubTextBlocks : IEpubTextBlocks
{
    public IReadOnlyList<EpubTextBlock> GetBlocks(HtmlNode body) => throw new NotImplementedException();

    public IReadOnlyList<EpubTextBlock> StampBlocks(HtmlNode body) => throw new NotImplementedException();
}
