using DuneTransactionsApi.Infrastructure.Persistence;
using FastEndpoints;

namespace DuneTransactionsApi.Features.Reconciliations;

public sealed class GetTransactionsEndpoint(AppDbContext db)
    : Endpoint<GetTransactionsRequest, GetTransactionsResponse>
{
    public override void Configure()
    {
        Get("/v1/reconciliations/transactions");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Liste paginée des transactions d'un tenant créées sur une plage de dates.";
            s.Description =
                "Renvoie les transactions dont createdAt est dans [fromCreatedAt, toCreatedAt), triées par "
                + "createdAt puis offerId. Une page de moins de pageSize éléments est la dernière. "
                + "Seules les transactions insérées en base avant asOf (exclu) sont renvoyées : en passant le même "
                + "asOf pour toutes les pages, le parcours reste stable même si des transactions sont insérées "
                + "pendant ce temps.";
            s.Responses[200] = "Une page de résultats, éventuellement vide.";
            s.Responses[400] = "Paramètre manquant, mal formé ou invalide.";
        });
    }

    public override async Task<GetTransactionsResponse> ExecuteAsync(GetTransactionsRequest req, CancellationToken ct)
    {
        // Npgsql refuse un paramètre timestamptz dont l'offset n'est pas nul.
        var asOf = req.AsOf!.Value.ToUniversalTime();

        var transactions = await db.Transactions
            .CreatedBetween(req.TenantId!, req.FromCreatedAt!.Value, req.ToCreatedAt!.Value)
            // Fige le parcours : une transaction insérée après asOf ne décale pas les pages suivantes.
            .Where(t => t.InsertedAt < asOf)
            .ToTransactionsPageAsync(req.Page, req.PageSize, ct);

        return new GetTransactionsResponse(transactions);
    }
}
