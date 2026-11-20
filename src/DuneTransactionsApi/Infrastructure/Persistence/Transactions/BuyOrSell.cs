using System.Text.Json.Serialization;

namespace DuneTransactionsApi.Infrastructure.Persistence.Transactions;

/// <summary>
/// Sens d'une transaction. Sérialisé en JSON et stocké en base sous son nom exact (<c>Buy</c> ou <c>Sell</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BuyOrSell>))]
public enum BuyOrSell
{
    Buy,
    Sell,
}
