using DuneTransactionsApi.Features.Reconciliations;
using DuneTransactionsApi.Infrastructure.Persistence;
using DuneTransactionsApi.Infrastructure.Persistence.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace DuneTransactionsApi.Tests.Fixtures;

/// <summary>Fabrique et insertion de transactions de test.</summary>
/// <remarks>
/// Les tests partagent la même base, et <c>offer_id</c> est unique dans toute la base : un <c>offerId</c> lisible
/// écrit en dur (« A », « offer-42 ») entrerait en collision d'un test à l'autre. Un test désigne donc ses
/// transactions par un <em>label</em>, et l'<c>offerId</c> réel est <c>{tenantId}/{label}</c>. Tous les
/// <c>offerId</c> d'un tenant ont ainsi le même préfixe : leur ordre est celui des labels.
/// </remarks>
public static class TestTransactions
{
    public static readonly DateTimeOffset DefaultCreatedAt = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Crée une transaction valide. Sans <paramref name="tenantId"/>, un tenant unique est généré. L'<c>offerId</c>
    /// est <paramref name="offerId"/> s'il est fourni, sinon <c>{tenantId}/{label}</c>, sinon un identifiant unique.
    /// </summary>
    public static Transaction New(
        string? tenantId = null,
        string? label = null,
        DateTimeOffset? createdAt = null,
        string? offerId = null,
        string tenantUserId = "alice",
        BuyOrSell buyOrSell = BuyOrSell.Buy,
        string amount = "5000.00",
        string amountCcy = "EUR",
        string accountAlias = "123-abc-567")
    {
        tenantId ??= NewTenantId();
        return new Transaction
        {
            TenantId = tenantId,
            OfferId = offerId ?? (label is null ? Guid.NewGuid().ToString() : OfferIdFor(tenantId, label)),
            TenantUserId = tenantUserId,
            BuyOrSell = buyOrSell,
            Amount = amount,
            AmountCcy = amountCcy,
            AccountAlias = accountAlias,
            CreatedAt = createdAt ?? DefaultCreatedAt,
        };
    }

    public static string NewTenantId() => Guid.NewGuid().ToString();

    public static string OfferIdFor(string tenantId, string label) => $"{tenantId}/{label}";

    /// <summary>Labels des transactions renvoyées, dans l'ordre de la réponse (voir <see cref="TestTransactions"/>).</summary>
    public static IEnumerable<string> Labels(this IEnumerable<TransactionItem> items, string tenantId)
    {
        var prefix = OfferIdFor(tenantId, "");
        return items.Select(i => i.OfferId.StartsWith(prefix, StringComparison.Ordinal) ? i.OfferId[prefix.Length..] : i.OfferId);
    }

    /// <summary>Insère les transactions en une seule sauvegarde, avec un <see cref="AppDbContext"/> dédié.</summary>
    public static async Task InsertAsync(this ApiFixture app, IEnumerable<Transaction> transactions, CancellationToken ct)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync(ct);
    }
}
