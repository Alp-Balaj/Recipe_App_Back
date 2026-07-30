using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Chat;

// IAiUsageService over AiUsageRecord rows (ai-quotas, Stream B). The budget window is the
// current UTC calendar day: one aggregate over (UserId, CreatedAt >= today) — backed by the
// index in ApplicationDbContext — against limits from AiQuotaOptions.
public class AiUsageService : IAiUsageService
{
    private readonly ApplicationDbContext _db;
    private readonly AiQuotaOptions _options;

    public AiUsageService(ApplicationDbContext db, AiQuotaOptions options)
    {
        _db = db;
        _options = options;
    }

    public async Task<AiBudget> GetBudgetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var dayStartUtc = DateTime.UtcNow.Date;

        // Count and sum in one round trip. SingleOrDefaultAsync returns null on a day with no
        // usage yet (GroupBy over an empty set yields no groups), which maps to a zeroed budget.
        var used = await _db.AiUsageRecords
            .Where(r => r.UserId == userId && r.CreatedAt >= dayStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new { Calls = g.Count(), Tokens = g.Sum(r => (long)r.TotalTokens) })
            .SingleOrDefaultAsync(cancellationToken);

        return new AiBudget(
            _options.DailyCallLimit,
            used?.Calls ?? 0,
            _options.DailyTokenLimit,
            used?.Tokens ?? 0,
            dayStartUtc.AddDays(1));
    }

    public void RecordCall(Guid userId, string lane, ChatTokenUsage? usage)
    {
        // Staged only — the caller's SaveChanges commits this atomically with the turn it
        // paid for (see IAiUsageService). Null usage still records the call at zero tokens:
        // the call quota is the primary gate and must count every spend.
        _db.AiUsageRecords.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Lane = lane,
            PromptTokens = usage?.PromptTokens ?? 0,
            CompletionTokens = usage?.CompletionTokens ?? 0,
            TotalTokens = usage?.TotalTokens ?? 0,
            CreatedAt = DateTime.UtcNow,
        });
    }
}
