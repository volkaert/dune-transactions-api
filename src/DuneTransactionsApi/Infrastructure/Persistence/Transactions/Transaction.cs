namespace DuneTransactionsApi.Infrastructure.Persistence.Transactions;

public sealed class Transaction
{
    private DateTimeOffset _createdAt;

    public Guid Id { get; private set; }

    public required string TenantId { get; init; }

    /// <summary>Identifiant de la transaction, unique dans toute la base (indépendamment du tenant).</summary>
    public required string OfferId { get; init; }

    /// <summary>Identifiant, dans son tenant, du user à l'origine de la transaction.</summary>
    public required string TenantUserId { get; set; }

    public required BuyOrSell BuyOrSell { get; set; }

    /// <summary>
    /// Montant, stocké et renvoyé tel quel, sans conversion numérique (ex. <c>5000.00</c>). Son format (décimal
    /// strictement positif, séparateur <c>.</c>, 8 décimales au plus) est garanti par des contraintes de la base.
    /// </summary>
    public required string Amount { get; set; }

    /// <summary>Devise du montant : code ISO 4217, en majuscules (ex. <c>EUR</c>).</summary>
    public required string AmountCcy { get; set; }

    public required string AccountAlias { get; set; }

    /// <summary>
    /// Instant d'insertion de la transaction en base, en UTC. Renseigné par PostgreSQL (<c>DEFAULT now()</c>, heure
    /// de début de la transaction SQL) et relu après l'insertion : l'application ne peut ni le fixer ni le modifier.
    /// </summary>
    public DateTimeOffset InsertedAt { get; private set; }

    /// <summary>
    /// Instant de création de la transaction (date métier). Accepte n'importe quel offset, mais la valeur est
    /// convertie en UTC : PostgreSQL (<c>timestamptz</c>) ne conserve pas l'offset d'origine.
    /// </summary>
    public required DateTimeOffset CreatedAt
    {
        get => _createdAt;
        set => _createdAt = value.ToUniversalTime();
    }
}
