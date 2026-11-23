using System.Collections.Generic;

namespace Kavita.Models.DTOs.SignalR.Bodies;

/// <param name="Pages">0-based EPUB pages that were just tokenized</param>
public sealed record DictionaryPagesReadyEventBodyDto(int SeriesId, int ChapterId, IList<int> Pages);
