using DuneTransactionsApi.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore;

namespace DuneTransactionsApi.Features.Reconciliations;

/// <summary>
/// Requête commune aux endpoints de réconciliation des transactions : chacun n'y ajoute que son filtre sur
/// <see cref="Transaction.InsertedAt"/>. Servie par l'index <c>ix_transactions_tenant_id_created_at_offer_id</c>.
/// </summary>
internal static class TransactionQueries
{
    /// <summary>Transactions du tenant dont <c>createdAt</c> est dans <c>[from, to)</c>.</summary>
    public static IQueryable<Transaction> CreatedBetween(
        this IQueryable<Transaction> transactions,
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        // Npgsql refuse un paramètre timestamptz dont l'offset n'est pas nul.
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();

        return transactions.Where(t => t.TenantId == tenantId && t.CreatedAt >= fromUtc && t.CreatedAt < toUtc);
    }

    /// <summary>Page de résultats, triée par <c>createdAt</c> puis <c>offerId</c>.</summary>
    public static Task<List<TransactionItem>> ToTransactionsPageAsync(
        this IQueryable<Transaction> transactions,
        int page,
        int pageSize,
        CancellationToken ct) => transactions
        .AsNoTracking()
        // offer_id, unique dans toute la base, ne sert qu'à départager les ex æquo (ordre total et stable pour la
        // pagination, même pour plusieurs transactions d'un même user au même instant) : il n'a pas d'ordre métier.
        .OrderBy(t => t.CreatedAt).ThenBy(t => t.OfferId)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(t => new TransactionItem(
            t.TenantUserId,
            t.OfferId,
            t.BuyOrSell,
            t.Amount,
            t.AmountCcy,
            t.AccountAlias,
            t.CreatedAt))
        .ToListAsync(ct);
}
