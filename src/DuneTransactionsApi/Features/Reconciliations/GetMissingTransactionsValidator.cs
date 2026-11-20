using FastEndpoints;
using FluentValidation;

namespace DuneTransactionsApi.Features.Reconciliations;

/// <summary>
/// Indépendant de <see cref="GetTransactionsValidator"/> : les règles communes y sont redéclarées pour que les deux
/// contrats puissent évoluer séparément.
/// </summary>
public sealed class GetMissingTransactionsValidator : Validator<GetMissingTransactionsRequest>
{
    public const int MaxPageSize = 1000;

    /// <summary>
    /// Borne le numéro de page pour que le décalage <c>(page - 1) * pageSize</c> tienne dans un <c>int</c>.
    /// </summary>
    public const int MaxPage = 1_000_000;

    public GetMissingTransactionsValidator()
    {
        RuleFor(r => r.TenantId)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(r => r.FromCreatedAt)
            .NotNull();

        RuleFor(r => r.ToCreatedAt)
            .NotNull()
            .GreaterThan(r => r.FromCreatedAt)
            .When(r => r.FromCreatedAt is not null, ApplyConditionTo.CurrentValidator)
            .WithMessage("'{PropertyName}' must be after '{ComparisonProperty}'.");

        RuleFor(r => r.MissingsSince)
            .NotNull();

        RuleFor(r => r.AsOf)
            .NotNull()
            .GreaterThan(r => r.MissingsSince)
            .When(r => r.MissingsSince is not null, ApplyConditionTo.CurrentValidator)
            .WithMessage("'{PropertyName}' must be after '{ComparisonProperty}'.");

        RuleFor(r => r.Page)
            .InclusiveBetween(1, MaxPage);

        RuleFor(r => r.PageSize)
            .InclusiveBetween(1, MaxPageSize);
    }
}
