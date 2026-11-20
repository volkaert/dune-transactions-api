using DuneTransactionsApi.Infrastructure.Persistence.Transactions;

namespace DuneTransactionsApi.Features.Reconciliations;

public sealed record GetTransactionsResponse(IReadOnlyList<TransactionItem> Transactions);

/// <param name="TenantUserId">Identifiant, dans son tenant, du user à l'origine de la transaction.</param>
/// <param name="OfferId">Identifiant de la transaction, unique dans toute la base.</param>
/// <param name="BuyOrSell">Sens de la transaction : <c>Buy</c> ou <c>Sell</c>.</param>
/// <param name="Amount">Montant, renvoyé exactement tel qu'il est stocké (chaîne, sans conversion numérique).</param>
/// <param name="AmountCcy">Devise du montant (code ISO 4217).</param>
/// <param name="AccountAlias">Alias du compte.</param>
/// <param name="TransactionCreatedAt">Instant de création de la transaction, en UTC.</param>
public sealed record TransactionItem(
    string TenantUserId,
    string OfferId,
    BuyOrSell BuyOrSell,
    string Amount,
    string AmountCcy,
    string AccountAlias,
    DateTimeOffset TransactionCreatedAt);
