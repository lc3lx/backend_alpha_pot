using Microsoft.AspNetCore.Mvc;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Infrastructure.Ai;

namespace ScarAlpha.Api.Endpoints;

public static class AiEndpoints
{
    public static RouteGroupBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai").WithTags("Ai").RequireAuthorization();
        group.MapPost("/advise", async (
            [FromBody] AiAdviseRequest request,
            AiAdvisorAppService advisor,
            CancellationToken ct) =>
            Results.Ok(await advisor.AdviseAsync(request.Asset, request.Lang, ct)));
        group.MapPost("/chat", async (
            [FromBody] AiChatRequest request,
            AiAdvisorAppService advisor,
            CancellationToken ct) =>
            Results.Ok(await advisor.ChatAsync(request, ct)));
        return group;
    }
}
