namespace MTTextClient.Core;

/// <summary>
/// Client-side algorithm lifecycle discriminator. Replaces the vendor
/// <c>AlgorithmData.ActionType</c> nested enum that was removed in MoonTrader
/// 0.7.25267, where each operation became its own wire request type
/// (AlgorithmRunRequestData, AlgorithmStopRequestData, …). Command layers use
/// this to pick the CoreConnection wrapper; the wrapper builds the request.
/// </summary>
public enum AlgoOp
{
    Start,
    Stop,
    StartAll,
    StopAll,
}
