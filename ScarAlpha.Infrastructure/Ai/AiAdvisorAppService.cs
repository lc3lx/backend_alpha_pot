using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Application.Services;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Ai;

/// <summary>
/// Asks OpenRouter to explain a direction that several independent readings
/// already agree on: RSI level, RSI slope, that side's backtest, the trend,
/// and the last candles. One reading alone does not open a suggestion.
/// Approval on the page is what places the trade.
/// </summary>
public sealed class AiAdvisorAppService
{
    public const string HttpClientName = "openrouter";

    private static readonly ConcurrentDictionary<Guid, long> LastAdvise = new();
    private static readonly ConcurrentDictionary<Guid, long> LastChat = new();

    private readonly RsiSignalAppService _signals;
    private readonly ITradeRepository _trades;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AiAdvisorAppService> _logger;

    public AiAdvisorAppService(
        RsiSignalAppService signals,
        ITradeRepository trades,
        IHttpClientFactory http,
        IConfiguration config,
        ICurrentUser currentUser,
        ILogger<AiAdvisorAppService> logger)
    {
        _signals = signals;
        _trades = trades;
        _http = http;
        _config = config;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<AiAdviceResponse> AdviseAsync(string? asset, string? lang, CancellationToken ct)
    {
        var ar = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        Throttle(ar);
        var symbol = (asset ?? string.Empty).Trim();
        if (symbol.Length == 0)
            throw new ApiException(ApiErrorCodes.ValidationError, ar ? "اختَر زوجاً." : "Pick a pair.");

        var loaded = await _signals.LoadAiPairAsync(symbol, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            var missing = ar
                ? "ما في بيانات لهذا الزوج بعد."
                : "No market data for this pair yet.";
            return new AiAdviceResponse(symbol, "Wait", missing, "rules", null, null, null, null, null);
        }

        var (row, analysis) = loaded.Value;
        var score = Score(row, analysis);
        if (score.LowPayout)
        {
            var skipped = ar
                ? "ما دخلت لأن نسبة الزوج قليلة."
                : "I did not enter because the pair payout is too low.";
            return Build(row, "Wait", skipped, "rules");
        }
        var localDirection = Decide(score);
        var local = LocalSummary(ar, localDirection, row, score);
        var key = ReadKey();
        if (key is null)
        {
            var prefix = ar
                ? "مفتاح OpenRouter غير مضبوط. "
                : "OpenRouter key is not configured. ";
            return Build(row, localDirection, prefix + local, "rules");
        }

        try
        {
            var model = await AskAsync(key, ar, localDirection, row, analysis, score, ct).ConfigureAwait(false);
            var direction = Resolve(localDirection, model.Direction, score);
            var summary = direction == model.Direction && !string.IsNullOrWhiteSpace(model.Summary)
                ? model.Summary!
                : local;
            var source = direction == model.Direction && !string.IsNullOrWhiteSpace(model.Summary)
                ? "openrouter"
                : "rules";
            if (string.IsNullOrWhiteSpace(model.Summary) && direction == localDirection)
                source = "rules";
            return Build(row, direction, summary, source);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("OpenRouter advise failed ({Type})", ex.GetType().Name);
            var prefix = ar
                ? "تعذر الاتصال بـ OpenRouter. "
                : "OpenRouter could not be reached. ";
            return Build(row, localDirection, prefix + local, "rules");
        }
    }

    public async Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken ct)
    {
        var ar = !string.Equals(request.Lang, "en", StringComparison.OrdinalIgnoreCase);
        ThrottleChat(ar);
        var message = (request.Message ?? string.Empty).Trim();
        if (message.Length == 0)
            throw new ApiException(ApiErrorCodes.ValidationError, ar ? "اكتب رسالة." : "Write a message.");
        if (message.Length > 500)
            message = message[..500];

        var today = await TodayLineAsync(ar, request.TzOffsetMinutes, ct).ConfigureAwait(false);
        var local = LocalChat(ar, message, today);
        var key = ReadKey();
        if (key is null)
            return new AiChatResponse(local, "rules");

        try
        {
            var reply = await AskChatAsync(key, ar, message, today, request.History, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(reply)
                ? new AiChatResponse(local, "rules")
                : new AiChatResponse(reply, "openrouter");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("OpenRouter chat failed ({Type})", ex.GetType().Name);
            return new AiChatResponse(local, "rules");
        }
    }

    private static string Resolve(string localDirection, string? modelDirection, FactorScore score)
    {
        if (score.LowPayout)
            return "Wait";
        if (modelDirection is "Call" or "Put" && Supported(modelDirection, score))
            return modelDirection;
        return localDirection;
    }

    private static bool Supported(string direction, FactorScore score)
    {
        if (direction == "Call")
            return score.CallVotes >= 2 && score.CallVotes > score.PutVotes;
        if (direction == "Put")
            return score.PutVotes >= 2 && score.PutVotes > score.CallVotes;
        return false;
    }

    private static string Decide(FactorScore score)
    {
        if (score.LowPayout)
            return "Wait";
        if (score.CallVotes >= 2 && score.CallVotes > score.PutVotes)
            return "Call";
        if (score.PutVotes >= 2 && score.PutVotes > score.CallVotes)
            return "Put";
        return "Wait";
    }

    private void Throttle(bool ar)
    {
        var userId = _currentUser.UserId;
        if (userId == Guid.Empty)
            return;
        var now = Environment.TickCount64;
        if (LastAdvise.TryGetValue(userId, out var prev) && now - prev < 4000)
        {
            throw new ApiException(
                ApiErrorCodes.RateLimited,
                ar ? "انتظر لحظات قبل تحليل جديد." : "Wait a moment before another analysis.",
                429);
        }
        LastAdvise[userId] = now;
    }

    private void ThrottleChat(bool ar)
    {
        var userId = _currentUser.UserId;
        if (userId == Guid.Empty)
            return;
        var now = Environment.TickCount64;
        if (LastChat.TryGetValue(userId, out var prev) && now - prev < 2000)
        {
            throw new ApiException(
                ApiErrorCodes.RateLimited,
                ar ? "انتظر لحظات قبل رسالة جديدة." : "Wait a moment before another message.",
                429);
        }
        LastChat[userId] = now;
    }

    private async Task<string> TodayLineAsync(bool ar, int? tzOffsetMinutes, CancellationToken ct)
    {
        var offsetMinutes = Math.Clamp(-(tzOffsetMinutes ?? -60), -14 * 60, 14 * 60);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var localNow = DateTimeOffset.UtcNow.ToOffset(offset);
        var start = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, offset);
        var trades = await _trades.ListByUserAsync(_currentUser.UserId, 200, ct: ct).ConfigureAwait(false);
        decimal profit = 0m;
        decimal loss = 0m;
        foreach (var trade in trades)
        {
            if (trade.CreatedAt < start || trade.Pnl is null)
                continue;
            if (trade.Status is not (TradeStatus.Profit or TradeStatus.Loss or TradeStatus.Tie))
                continue;
            if (trade.Pnl > 0)
                profit += trade.Pnl.Value;
            else
                loss += trade.Pnl.Value;
        }
        var net = profit + loss;
        return ar
            ? $"ربح اليوم {profit:0.##}$ وخسارته {Math.Abs(loss):0.##}$ والصافي {net:0.##}$."
            : $"Today profit is ${profit:0.##}, loss is ${Math.Abs(loss):0.##}, and net is ${net:0.##}.";
    }

    private async Task<string?> AskChatAsync(
        string key,
        bool ar,
        string message,
        string today,
        IReadOnlyList<AiChatTurn>? history,
        CancellationToken ct)
    {
        var model = _config["OPENROUTER_MODEL"];
        if (string.IsNullOrWhiteSpace(model))
            model = _config["OpenRouter:Model"];
        if (string.IsNullOrWhiteSpace(model))
            model = "openai/gpt-4o-mini";

        var system = ar
            ? "أنت مساعد سكار ألفا على صفحة البوت. احكِ باختصار، جملتين أو ثلاث، بلغة دافئة. إذا سلّم عليك رد التحية واسأل عن صحته وكيف ماشي ربحه. استخدم رقم ربح اليوم المعطى فقط ولا تخترع رقماً. احكِ شوي عن المنصة: بتحلل المؤشر والباك تست والترند والشموع، وبتفوت الصفقة لحاله لما يتفق أكثر من عامل، وما بتدخل إذا نسبة الزوج قليلة، وكل حساب لحاله. إذا انتقد المنصة دافع عنها بهدوء ومن دون ما توعد بربح مضمون. لا تقل إنك فتحت صفقة من الشات. إذا طلب دخول صفقة، قلّه يختار الزوج ويضغط حلّل. أجب بـ JSON فقط: {\"reply\":\"...\"}."
            : "You are Scar Alpha's assistant on the bot page. Reply in two or three warm sentences. If they greet you, greet them back and ask about their health and how their profit is going. Use only the given today figure and never invent a number. Say a little about the platform: it reads RSI, backtest, trend, and candles, enters by itself when more than one factor agrees, stays out when the payout is low, and keeps each account separate. If they criticize the platform, defend it calmly and do not promise guaranteed profit. Do not claim you opened a trade from chat. If they ask to enter, tell them to pick a pair and press analyze. Reply with JSON only: {\"reply\":\"...\"}.";

        var historyLines = new List<string>();
        if (history is not null)
        {
            foreach (var turn in history.TakeLast(6))
            {
                var text = (turn.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                    continue;
                if (text.Length > 300)
                    text = text[..300];
                var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                historyLines.Add(role + ": " + text);
            }
        }

        var user = string.Join('\n', new[]
        {
            today,
            historyLines.Count == 0 ? "history=none" : "history:\n" + string.Join('\n', historyLines),
            "message: " + message
        });

        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0.6,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://www.scaralphaai.com");
        request.Headers.TryAddWithoutValidation("X-Title", "Scar Alpha");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.CreateClient(HttpClientName)
            .SendAsync(request, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter chat status {Status}", (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;
        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        return ReadReply(content);
    }

    private static string? ReadReply(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                text = text[(start + 1)..end].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("reply", out var reply))
            {
                var value = reply.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return Trim(value);
            }
        }
        catch (JsonException)
        {
            // Prose is still a usable reply.
        }

        return Trim(text);
    }

    private static string LocalChat(bool ar, string message, string today)
    {
        var text = message.ToLowerInvariant();
        var greeting = text.Contains("مرح") || text.Contains("هلا") || text.Contains("سلام")
            || text.Contains("صباح") || text.Contains("مساء") || text.Contains("hi") || text.Contains("hello");
        var criticism = text.Contains("نصب") || text.Contains("احتي") || text.Contains("سيئ")
            || text.Contains("خسر") || text.Contains("scam") || text.Contains("fake");
        if (ar && greeting)
            return $"أهلاً فيك، نورت. كيف صحتك اليوم؟ {today} سكار ألفا بيفوت الصفقة لحاله لما يتفق أكثر من عامل، وبيبعد عن الزوج إذا نسبته قليلة.";
        if (ar && criticism)
            return $"فاهمك. المنصة ما بتوعد بربح مضمون، بس بتحلل المؤشر والباك تست والترند والشموع قبل الدخول، وما بتخلط حسابك مع غيرو. {today}";
        if (ar)
            return $"تمام، أنا معك. كيف صحتك وكيف الربح اليوم؟ {today} إذا بدك صفقة اختَر الزوج واضغط حلّل.";
        if (greeting)
            return $"Welcome. How are you feeling today? {today} Scar Alpha enters by itself when more than one factor agrees, and it stays out when the payout is low.";
        if (criticism)
            return $"I hear you. The platform does not promise a sure profit. It reads RSI, backtest, trend, and candles before entering, and it keeps your account separate. {today}";
        return $"I'm here with you. How are you, and how is the profit today? {today} Pick a pair and press analyze if you want a trade.";
    }

    private string? ReadKey()
    {
        var key = _config["OPENROUTER_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            key = _config["OpenRouter:ApiKey"];
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private async Task<ModelAdvice> AskAsync(
        string key,
        bool ar,
        string lean,
        StrategySignal row,
        MarketAnalysis? analysis,
        FactorScore score,
        CancellationToken ct)
    {
        var model = _config["OPENROUTER_MODEL"];
        if (string.IsNullOrWhiteSpace(model))
            model = _config["OpenRouter:Model"];
        if (string.IsNullOrWhiteSpace(model))
            model = "openai/gpt-4o-mini";

        var system = ar
            ? "أنت محلل خيارات. القرار لازم يطلع من اجتماع أكثر من عامل، مش من رقم واحد. اختَر Call فقط إذا أصوات الشراء 2 أو أكثر وأكبر من أصوات البيع. اختَر Put فقط إذا أصوات البيع 2 أو أكثر وأكبر من أصوات الشراء. غير هيك، أو إذا نسبة الربح قليلة، اختَر Wait. أجب بـ JSON فقط: {\"direction\":\"Call|Put|Wait\",\"summary\":\"جملة أو جملتان بالعربية تذكر العوامل التي اتفقوا\"}. لا تخترع أرقاماً ولا عوامل."
            : "You are an options analyst. The decision must come from several factors together, not one number. Choose Call only when call votes are at least 2 and greater than put votes. Choose Put only when put votes are at least 2 and greater than call votes. Otherwise, or when payout is too low, choose Wait. Reply with JSON only: {\"direction\":\"Call|Put|Wait\",\"summary\":\"one or two English sentences naming the factors that agreed\"}. Do not invent numbers or factors.";

        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = BuildFacts(lean, row, analysis, score) }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://www.scaralphaai.com");
        request.Headers.TryAddWithoutValidation("X-Title", "Scar Alpha");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.CreateClient(HttpClientName)
            .SendAsync(request, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter advise status {Status}", (int)response.StatusCode);
            return default;
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return default;
        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        return ReadAdvice(content);
    }

    private static string BuildFacts(string lean, StrategySignal row, MarketAnalysis? analysis, FactorScore score)
    {
        static string Side(RsiBacktestStats? stats) => stats is null
            ? "none"
            : $"rate={stats.SuccessRate} visits={stats.TotalSignals} passed={stats.Passed}";

        var regime = analysis?.Regime;
        return string.Join('\n', new[]
        {
            $"pair={row.Asset}",
            $"closedRsi={row.Rsi}",
            $"liveRsi={row.LiveRsi}",
            $"callBacktest={Side(row.CallBacktest)}",
            $"putBacktest={Side(row.PutBacktest)}",
            $"regime={regime?.Regime.ToString() ?? "unknown"}",
            $"adx={regime?.Adx}",
            $"plusDi={regime?.PlusDi}",
            $"minusDi={regime?.MinusDi}",
            $"recentUpCandles={score.UpCandles}",
            $"recentDownCandles={score.DownCandles}",
            $"callVotes={score.CallVotes}",
            $"putVotes={score.PutVotes}",
            $"callFactors={string.Join(", ", score.CallReasons)}",
            $"putFactors={string.Join(", ", score.PutReasons)}",
            $"payoutLow={score.LowPayout}",
            $"block={row.EntryBlockReason ?? "none"}",
            $"lean={lean}",
            "Votes are independent: RSI zone, RSI slope, that side's backtest, trend, and the last five candles. A side needs at least two votes and more votes than the other side."
        });
    }

    private static ModelAdvice ReadAdvice(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return default;
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                text = text[(start + 1)..end].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var direction = doc.RootElement.TryGetProperty("direction", out var dir)
                ? Normalize(dir.GetString())
                : null;
            var summary = doc.RootElement.TryGetProperty("summary", out var sum)
                ? sum.GetString()
                : null;
            return new ModelAdvice(direction, string.IsNullOrWhiteSpace(summary) ? null : Trim(summary));
        }
        catch (JsonException)
        {
            return new ModelAdvice(null, Trim(text));
        }
    }

    private static string? Normalize(string? direction)
    {
        return (direction ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "call" or "up" or "buy" => "Call",
            "put" or "down" or "sell" => "Put",
            "wait" or "none" => "Wait",
            _ => null
        };
    }

    private static string Trim(string text) => text.Length > 600 ? text[..600] : text;

    private static FactorScore Score(StrategySignal row, MarketAnalysis? analysis)
    {
        var call = new List<string>(5);
        var put = new List<string>(5);
        var rsi = row.Rsi;
        var live = row.LiveRsi ?? rsi;

        if (rsi <= 35m)
            call.Add("rsi_buy");
        else if (rsi >= 65m)
            put.Add("rsi_sell");

        var slope = live - rsi;
        if (slope >= 1m && rsi < 55m)
            call.Add("rsi_up");
        else if (slope <= -1m && rsi > 45m)
            put.Add("rsi_down");

        if (row.CallBacktest is { Passed: true })
            call.Add("bt_call");
        if (row.PutBacktest is { Passed: true })
            put.Add("bt_put");

        var regime = analysis?.Regime?.Regime ?? MarketRegime.Unclear;
        if (regime == MarketRegime.Uptrend)
            call.Add("trend_up");
        else if (regime == MarketRegime.Downtrend)
            put.Add("trend_down");

        var (up, down) = RecentBodies(analysis);
        if (up >= 3 && up > down)
            call.Add("candles_up");
        else if (down >= 3 && down > up)
            put.Add("candles_down");

        var lowPayout = row.EntryBlockReason?.Contains("LOW_PAYOUT", StringComparison.Ordinal) == true;
        return new FactorScore(call, put, up, down, lowPayout);
    }

    private static (int Up, int Down) RecentBodies(MarketAnalysis? analysis)
    {
        if (analysis is null || analysis.ClosedCandles.Count == 0)
            return (0, 0);
        var candles = analysis.ClosedCandles;
        var take = Math.Min(5, candles.Count);
        var start = candles.Count - take;
        var up = 0;
        var down = 0;
        for (var i = start; i < candles.Count; i++)
        {
            var candle = candles[i];
            var open = candle.Open ?? (i > 0 ? candles[i - 1].Close : candle.Close);
            if (candle.Close > open)
                up++;
            else if (candle.Close < open)
                down++;
        }
        return (up, down);
    }

    private static string LocalSummary(bool ar, string direction, StrategySignal row, FactorScore score)
    {
        var pair = Pretty(row.Asset);
        if (score.LowPayout)
        {
            var payout = row.EntryBlockReason?.Split(':').LastOrDefault();
            return ar
                ? $"{pair}: نسبة الزوج {payout}% أقل من 75، فما في دخول."
                : $"{pair}: pair payout {payout}% is below 75, so there is no entry.";
        }
        if (direction == "Call")
        {
            var why = Reasons(ar, score.CallReasons);
            return ar
                ? $"{pair}: صعود لأن {why}."
                : $"{pair}: up because {why}.";
        }
        if (direction == "Put")
        {
            var why = Reasons(ar, score.PutReasons);
            return ar
                ? $"{pair}: هبوط لأن {why}."
                : $"{pair}: down because {why}.";
        }
        return ar
            ? $"{pair}: العوامل ما اتفقوا على جهة واحدة. الاقتراح انتظار."
            : $"{pair}: the factors do not agree on one side. Suggestion: wait.";
    }

    private static string Reasons(bool ar, IReadOnlyList<string> codes)
    {
        var labels = new List<string>(codes.Count);
        foreach (var code in codes)
            labels.Add(Label(ar, code));
        return string.Join(ar ? "، و" : ", and ", labels);
    }

    private static string Label(bool ar, string code) => code switch
    {
        "rsi_buy" => ar ? "المؤشر بمنطقة الشراء" : "RSI is in the buy zone",
        "rsi_sell" => ar ? "المؤشر بمنطقة البيع" : "RSI is in the sell zone",
        "rsi_up" => ar ? "المؤشر طالع" : "RSI is rising",
        "rsi_down" => ar ? "المؤشر نازل" : "RSI is falling",
        "bt_call" => ar ? "باك تست الشراء ناجح" : "the buy backtest passed",
        "bt_put" => ar ? "باك تست البيع ناجح" : "the sell backtest passed",
        "trend_up" => ar ? "الترند صاعد" : "the trend is up",
        "trend_down" => ar ? "الترند هابط" : "the trend is down",
        "candles_up" => ar ? "آخر الشموع صاعدة" : "the latest candles are up",
        "candles_down" => ar ? "آخر الشموع هابطة" : "the latest candles are down",
        _ => code
    };

    private static string Pretty(string asset)
    {
        var name = asset.EndsWith("_otc", StringComparison.OrdinalIgnoreCase) ? asset[..^4] : asset;
        if (name.Length == 6 && name.All(char.IsLetter))
            return $"{name[..3]}/{name[3..]}";
        return name;
    }

    private static AiAdviceResponse Build(StrategySignal row, string direction, string summary, string source)
    {
        var side = direction == "Call" ? row.CallBacktest : direction == "Put" ? row.PutBacktest : null;
        return new AiAdviceResponse(
            row.Asset,
            direction,
            summary,
            source,
            row.Rsi,
            row.LiveRsi,
            side?.SuccessRate,
            side?.TotalSignals,
            row.EntryBlockReason);
    }

    private readonly record struct ModelAdvice(string? Direction, string? Summary);

    private sealed class FactorScore
    {
        public FactorScore(
            IReadOnlyList<string> callReasons,
            IReadOnlyList<string> putReasons,
            int upCandles,
            int downCandles,
            bool lowPayout)
        {
            CallReasons = callReasons;
            PutReasons = putReasons;
            UpCandles = upCandles;
            DownCandles = downCandles;
            LowPayout = lowPayout;
        }

        public IReadOnlyList<string> CallReasons { get; }
        public IReadOnlyList<string> PutReasons { get; }
        public int CallVotes => CallReasons.Count;
        public int PutVotes => PutReasons.Count;
        public int UpCandles { get; }
        public int DownCandles { get; }
        public bool LowPayout { get; }
    }
}
