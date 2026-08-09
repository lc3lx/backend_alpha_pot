using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScarAlpha.Domain.Enums;
using ScarAlpha.Infrastructure.Persistence;

namespace ScarAlpha.Api.Tests;

internal static class TestAccessHelpers
{
    public static async Task ApproveFromTokenAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string token)
    {
        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var meRes = await client.SendAsync(meReq);
        meRes.EnsureSuccessStatusCode();
        var userId = Guid.Parse((await meRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString()!);
        await SetApprovalAsync(factory, userId, AdminApprovalStatus.Approved);
    }

    public static async Task SetApprovalAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        AdminApprovalStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = db.BinollaLinks.FirstOrDefault(x => x.UserId == userId);
        if (link is null) return;

        link.ApprovalStatus = status;
        link.AdminApproved = status == AdminApprovalStatus.Approved;
        link.ApprovedAt = DateTimeOffset.UtcNow;
        link.ApprovedBy = "test-admin";
        link.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}
