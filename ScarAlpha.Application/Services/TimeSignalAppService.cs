using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Application.Services;

public sealed record TimeSignalPreviewRequest(
    string RawText,
    int TimeZoneOffsetMinutes = 180, // Default UTC+3 (Damascus, Riyadh, Cairo)
    string? BaseDate = null,
    int DurationSeconds = 60);

public sealed record ParsedTimeSignalDto(
    string RawLine,
    string Asset,
    string RawAsset,
    string Direction,
    DateTimeOffset ScheduledTimeUtc,
    string ScheduledTimeLocalFormatted,
    int DurationSeconds,
    bool IsValid,
    string? ValidationError);

public sealed record TimeSignalPreviewResponse(
    IReadOnlyList<ParsedTimeSignalDto> Signals,
    int TotalCount,
    int ValidCount,
    int InvalidCount);

public sealed record TimeSignalBulkCreateRequest(
    string RawText,
    int TimeZoneOffsetMinutes = 180,
    string? BaseDate = null,
    int DurationSeconds = 60);

public sealed record TimeSignalBulkCreateResponse(
    int CreatedCount,
    IReadOnlyList<TimeSignalDto> CreatedSignals);

public sealed record TimeSignalDto(
    Guid Id,
    string Asset,
    string RawAsset,
    string Direction,
    DateTimeOffset ScheduledTime,
    int DurationSeconds,
    string Status,
    DateTimeOffset? ExecutedAt,
    int TargetCount,
    int ExecutedCount,
    int FailedCount,
    string? ExecutionLog,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record TimeSignalListResponse(
    IReadOnlyList<TimeSignalDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed class TimeSignalAppService
{
    private readonly ITimeSignalRepository _repository;
    private readonly ILogger<TimeSignalAppService> _logger;

    public TimeSignalAppService(ITimeSignalRepository repository, ILogger<TimeSignalAppService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public TimeSignalPreviewResponse Preview(TimeSignalPreviewRequest request)
    {
        var parsed = ParseRawSignals(
            request.RawText,
            request.TimeZoneOffsetMinutes,
            request.BaseDate,
            request.DurationSeconds);

        var valid = parsed.Count(p => p.IsValid);
        var invalid = parsed.Count - valid;

        return new TimeSignalPreviewResponse(parsed, parsed.Count, valid, invalid);
    }

    public async Task<TimeSignalBulkCreateResponse> CreateBulkAsync(
        TimeSignalBulkCreateRequest request,
        CancellationToken ct = default)
    {
        var parsed = ParseRawSignals(
            request.RawText,
            request.TimeZoneOffsetMinutes,
            request.BaseDate,
            request.DurationSeconds);

        var validSignals = parsed.Where(p => p.IsValid).ToList();
        if (validSignals.Count == 0)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, "No valid signals found in provided text.");
        }

        var entities = new List<TimeSignal>(validSignals.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var p in validSignals)
        {
            var entity = new TimeSignal
            {
                Id = Guid.NewGuid(),
                Asset = p.Asset,
                RawAsset = p.RawAsset,
                Direction = p.Direction,
                ScheduledTime = p.ScheduledTimeUtc,
                DurationSeconds = p.DurationSeconds,
                Status = TimeSignalStatus.Pending,
                CreatedAt = now
            };

            entities.Add(entity);
        }

        await _repository.AddRangeAsync(entities, ct);

        _logger.LogInformation("Scheduled {Count} bulk time signals", entities.Count);

        var dtos = entities.Select(Map).ToList();
        return new TimeSignalBulkCreateResponse(dtos.Count, dtos);
    }

    public async Task<TimeSignalListResponse> ListSignalsAsync(
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        TimeSignalStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TimeSignalStatus>(status.Trim(), true, out var ps))
        {
            parsedStatus = ps;
        }

        var (items, total) = await _repository.ListAsync(parsedStatus, page, pageSize, ct);

        return new TimeSignalListResponse(
            items.Select(Map).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<TimeSignalDto> CancelSignalAsync(Guid id, CancellationToken ct = default)
    {
        var signal = await _repository.GetByIdAsync(id, ct)
            ?? throw new ApiException(ApiErrorCodes.NotFound, $"Time signal '{id}' not found.", 404);

        if (signal.Status == TimeSignalStatus.Executing)
        {
            throw new ApiException(ApiErrorCodes.ValidationError, "Cannot cancel a signal that is currently executing.", 409);
        }

        signal.Status = TimeSignalStatus.Cancelled;
        await _repository.UpdateAsync(signal, ct);

        return Map(signal);
    }

    public async Task<bool> DeleteSignalAsync(Guid id, CancellationToken ct = default)
    {
        return await _repository.DeleteAsync(id, ct);
    }

    public static IReadOnlyList<ParsedTimeSignalDto> ParseRawSignals(
        string rawText,
        int timeZoneOffsetMinutes,
        string? baseDateStr,
        int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return Array.Empty<ParsedTimeSignalDto>();

        var tzOffset = TimeSpan.FromMinutes(timeZoneOffsetMinutes);

        DateTime baseDate;
        if (!string.IsNullOrWhiteSpace(baseDateStr) &&
            DateTime.TryParse(baseDateStr.Trim(), out var parsedDate))
        {
            baseDate = parsedDate.Date;
        }
        else
        {
            // Default to today in the user's timezone
            baseDate = (DateTimeOffset.UtcNow + tzOffset).Date;
        }

        var lines = rawText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var results = new List<ParsedTimeSignalDto>(lines.Count);
        var dayOffset = 0;
        var prevHour = -1;

        foreach (var line in lines)
        {
            var trimmed = line;
            if (trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                continue; // Skip comments

            // Primary matching: Asset - Time - Direction
            string rawAsset, rawTime, rawDir;
            var match = Regex.Match(trimmed, @"^[\s\-\*]*([A-Za-z0-9/_]+(?:-[Oo][Tt][Cc]|_otc)?|[A-Za-z0-9/_-]+)\s*(?:[-–—|:]|\s)\s*(\d{1,2}:\d{2})\s*(?:[-–—|:]|\s)\s*([A-Za-z]+)\s*$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                rawAsset = match.Groups[1].Value.Trim();
                rawTime = match.Groups[2].Value.Trim();
                rawDir = match.Groups[3].Value.Trim();
            }
            else
            {
                // Fallback: split by delimiters that MUST have surrounding whitespace, avoiding hyphens inside symbols
                var parts = Regex.Split(trimmed, @"\s+[-–—|]\s+|\s{2,}|\t+")
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (parts.Count >= 3)
                {
                    rawAsset = parts[0].Trim();
                    rawTime = parts[1].Trim();
                    rawDir = parts[2].Trim();
                }
                else
                {
                    results.Add(new ParsedTimeSignalDto(
                        RawLine: line,
                        Asset: string.Empty,
                        RawAsset: string.Empty,
                        Direction: string.Empty,
                        ScheduledTimeUtc: DateTimeOffset.MinValue,
                        ScheduledTimeLocalFormatted: string.Empty,
                        DurationSeconds: durationSeconds,
                        IsValid: false,
                        ValidationError: "Invalid line format. Expected format: ASSET - TIME - DIRECTION (e.g. USD/IDR-OTC - 22:34 - CALL)"));
                    continue;
                }
            }

            // 1. Asset normalization
            var asset = NormalizeAsset(rawAsset);
            if (string.IsNullOrWhiteSpace(asset))
            {
                results.Add(new ParsedTimeSignalDto(
                    RawLine: line,
                    Asset: string.Empty,
                    RawAsset: rawAsset,
                    Direction: string.Empty,
                    ScheduledTimeUtc: DateTimeOffset.MinValue,
                    ScheduledTimeLocalFormatted: string.Empty,
                    DurationSeconds: durationSeconds,
                    IsValid: false,
                    ValidationError: $"Invalid asset symbol: '{rawAsset}'"));
                continue;
            }

            // 2. Direction normalization
            var direction = NormalizeDirection(rawDir);
            if (direction is null)
            {
                results.Add(new ParsedTimeSignalDto(
                    RawLine: line,
                    Asset: asset,
                    RawAsset: rawAsset,
                    Direction: rawDir,
                    ScheduledTimeUtc: DateTimeOffset.MinValue,
                    ScheduledTimeLocalFormatted: string.Empty,
                    DurationSeconds: durationSeconds,
                    IsValid: false,
                    ValidationError: $"Invalid direction: '{rawDir}'. Must be CALL or PUT"));
                continue;
            }

            // 3. Time parsing
            if (!TimeSpan.TryParse(rawTime, out var timeOfDay))
            {
                // Try format "H:mm"
                if (DateTime.TryParseExact(rawTime, new[] { "H:mm", "HH:mm", "h:mm", "hh:mm" },
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dtParsed))
                {
                    timeOfDay = dtParsed.TimeOfDay;
                }
                else
                {
                    results.Add(new ParsedTimeSignalDto(
                        RawLine: line,
                        Asset: asset,
                        RawAsset: rawAsset,
                        Direction: direction,
                        ScheduledTimeUtc: DateTimeOffset.MinValue,
                        ScheduledTimeLocalFormatted: string.Empty,
                        DurationSeconds: durationSeconds,
                        IsValid: false,
                        ValidationError: $"Invalid time format: '{rawTime}'. Expected HH:mm (e.g. 22:34)"));
                    continue;
                }
            }

            var currHour = timeOfDay.Hours;
            // Detect midnight crossing: e.g. previous was 23:57 and current is 00:00 or 00:15
            if (prevHour >= 20 && currHour < 6)
            {
                dayOffset++;
            }
            prevHour = currHour;

            var localDateTime = baseDate.AddDays(dayOffset).Date.Add(timeOfDay);
            var scheduledUtc = new DateTimeOffset(localDateTime, tzOffset).ToUniversalTime();

            results.Add(new ParsedTimeSignalDto(
                RawLine: line,
                Asset: asset,
                RawAsset: rawAsset,
                Direction: direction,
                ScheduledTimeUtc: scheduledUtc,
                ScheduledTimeLocalFormatted: localDateTime.ToString("yyyy-MM-dd HH:mm"),
                DurationSeconds: durationSeconds > 0 ? durationSeconds : 60,
                IsValid: true,
                ValidationError: null));
        }

        return results;
    }

    public static string NormalizeAsset(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().Replace("/", "").Replace(" ", "");

        // Check if OTC
        if (s.EndsWith("-otc", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4].ToUpperInvariant() + "_otc";
        }
        else if (s.EndsWith("_otc", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4].ToUpperInvariant() + "_otc";
        }
        else if (s.EndsWith("otc", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^3].ToUpperInvariant() + "_otc";
        }
        else
        {
            s = s.ToUpperInvariant();
        }

        return s;
    }

    public static string? NormalizeDirection(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();

        return s switch
        {
            "CALL" or "CAL" or "UP" or "BUY" or "C" => "CALL",
            "PUT" or "DOWN" or "SELL" or "P" => "PUT",
            _ => null
        };
    }

    private static TimeSignalDto Map(TimeSignal s) => new(
        s.Id,
        s.Asset,
        s.RawAsset,
        s.Direction,
        s.ScheduledTime,
        s.DurationSeconds,
        s.Status.ToString(),
        s.ExecutedAt,
        s.TargetCount,
        s.ExecutedCount,
        s.FailedCount,
        s.ExecutionLog,
        s.ErrorMessage,
        s.CreatedAt);
}
