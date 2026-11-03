namespace Kavita.Models.Entities.Enums;

public enum OcrPageState
{
    Pending = 0,
    Done = 1,
    /// <summary>
    /// The sidecar rejected the page too many times
    /// </summary>
    Failed = 2,
}
