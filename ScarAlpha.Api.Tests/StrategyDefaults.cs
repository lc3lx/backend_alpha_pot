using ScarAlpha.Application.Abstractions;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// Strategy configuration lives in process-global statics, set once at startup from
/// appsettings. Any test that boots the API host therefore leaves production values
/// applied for everything that runs afterwards — a broker calibration or a widened entry
/// window then shows up as an indicator failure in an unrelated test.
///
/// Strategy tests call <see cref="Reset"/> from their constructor so they always start
/// from the plain, uncalibrated defaults regardless of what ran before them.
/// </summary>
internal static class StrategyDefaults
{
    public static void Reset()
    {
        Indicators.RsiCalibrationOffset = 0m;
        Indicators.RsiCalibrationOffsetLow = 0m;
        IndicatorWarmup.MinRsiCandles = IndicatorWarmup.DefaultMinRsiCandles;
        RsiEntryLevels.CallMax = 25m;
        RsiEntryLevels.PutMin = 75m;
        RsiEntryLevels.MinZoneVisits = 2;
        RsiEntryLevels.SetupTtlSeconds = 30;
        StrategyGate.RegimeEnabled = false;
    }
}
