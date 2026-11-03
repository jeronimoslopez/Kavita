using System;

namespace Kavita.Models.DTOs.SignalR.Bodies;

public sealed record OcrProgressEventBodyDto(int LibraryId, float Progress, DateTime EventTime);
