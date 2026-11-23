using System.Collections.Generic;

namespace Kavita.Models.DTOs.SignalR.Bodies;

/// <param name="Pages">0-based EPUB pages that just got AI analysis answers</param>
public sealed record DictionaryAnalysisReadyEventBodyDto(int SeriesId, int ChapterId, IList<int> Pages);
