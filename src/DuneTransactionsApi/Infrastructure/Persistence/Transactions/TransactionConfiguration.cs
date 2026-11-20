using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DuneTransactionsApi.Infrastructure.Persistence.Transactions;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    /// <summary>
    /// Décimal positif ou nul, séparateur <c>.</c>, 20 chiffres au plus avant le séparateur et 8 au plus après, sans
    /// signe, exposant, espace ni zéro en tête. Vérifié sur la chaîne elle-même : le montant n'est jamais converti.
    /// </summary>
    public const string AmountFormatPattern = @"^(0|[1-9][0-9]{0,19})(\.[0-9]{1,8})?$";

    public const int AmountMaxLength = 29;

    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions", t =>
        {
            t.HasCheckConstraint("ck_transactions_buy_or_sell", "buy_or_sell IN ('Buy', 'Sell')");
            t.HasCheckConstraint("ck_transactions_amount_format", $"amount ~ '{AmountFormatPattern}'");
            // Avec le format ci-dessus, au moins un chiffre non nul équivaut à un montant strictement positif.
            t.HasCheckConstraint("ck_transactions_amount_positive", "amount ~ '[1-9]'");
            t.HasCheckConstraint("ck_transactions_amount_ccy", "amount_ccy ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(t => t.Id);

        // Toujours fixé par la base : la colonne est omise de l'INSERT (valeur par défaut) et de l'UPDATE, puis relue.
        var insertedAt = builder.Property(t => t.InsertedAt).HasDefaultValueSql("now()");
        insertedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        insertedAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.Property(t => t.TenantId).HasMaxLength(100);
        builder.Property(t => t.OfferId).HasMaxLength(100);
        builder.Property(t => t.TenantUserId).HasMaxLength(100);
        builder.Property(t => t.BuyOrSell).HasConversion<string>().HasMaxLength(4);
        builder.Property(t => t.Amount).HasMaxLength(AmountMaxLength);
        builder.Property(t => t.AmountCcy).HasMaxLength(3).IsFixedLength();
        builder.Property(t => t.AccountAlias).HasMaxLength(100);

        builder.HasIndex(t => t.OfferId)
            .IsUnique()
            .HasDatabaseName("ux_transactions_offer_id");

        // Sert la réconciliation des transactions : filtre par tenant et plage de dates, tri par date puis offer_id
        // (départage des ex æquo), sans étape de tri. inserted_at (filtres asOf / missingsSince) est inclus dans
        // l'index sans en être une clé : il est évalué sur les entrées de l'index, avant la lecture de la table.
        builder.HasIndex(t => new { t.TenantId, t.CreatedAt, t.OfferId })
            .IncludeProperties(t => t.InsertedAt)
            .HasDatabaseName("ix_transactions_tenant_id_created_at_offer_id");
    }
}
