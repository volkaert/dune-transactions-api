using DuneTransactionsApi.Infrastructure.Persistence;
using FastEndpoints;

namespace DuneTransactionsApi.Features.Reconciliations;

public sealed class GetMissingTransactionsEndpoint(AppDbContext db)
    : Endpoint<GetMissingTransactionsRequest, GetTransactionsResponse>
{
    public override void Configure()
    {
        Get("/v1/reconciliations/transactions/missings");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Liste paginée des transactions manquées par un parcours de /v1/reconciliations/transactions.";
            s.Description =
                "Renvoie, comme /v1/reconciliations/transactions, les transactions du tenant dont createdAt est dans "
                + "[fromCreatedAt, toCreatedAt), mais seulement celles insérées en base dans [missingsSince, asOf). "
                + "Avec missingsSince = le asOf du parcours précédent, la réponse est exactement le complément de ce "
                + "parcours, sans doublon. Tri par createdAt puis offerId ; une page de moins de pageSize éléments "
                + "est la dernière.";
            s.Responses[200] = "Une page de résultats, éventuellement vide.";
            s.Responses[400] = "Paramètre manquant, mal formé ou invalide.";
        });
    }

    public override async Task<GetTransactionsResponse> ExecuteAsync(
        GetMissingTransactionsRequest req,
        CancellationToken ct)
    {
        // Npgsql refuse un paramètre timestamptz dont l'offset n'est pas nul.
        var missingsSince = req.MissingsSince!.Value.ToUniversalTime();
        var asOf = req.AsOf!.Value.ToUniversalTime();

        var transactions = await db.Transactions
            .CreatedBetween(req.TenantId!, req.FromCreatedAt!.Value, req.ToCreatedAt!.Value)
            // [missingsSince, asOf) : complément exact d'un parcours qui a renvoyé inserted_at < missingsSince.
            .Where(t => t.InsertedAt >= missingsSince && t.InsertedAt < asOf)
            .ToTransactionsPageAsync(req.Page, req.PageSize, ct);

        return new GetTransactionsResponse(transactions);
    }
}
