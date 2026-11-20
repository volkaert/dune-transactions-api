using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DuneTransactionsApi.Features.Reconciliations;
using DuneTransactionsApi.Infrastructure.Persistence.Transactions;
using DuneTransactionsApi.Tests.Fixtures;

namespace DuneTransactionsApi.Tests.Features.Reconciliations;

/// <summary>
/// Les appels utilisent une URL construite explicitement pour tester le contrat réel : noms des paramètres,
/// encodage des offsets (<c>+</c> → <c>%2B</c>) et noms des propriétés JSON. Les transactions sont désignées par
/// leur label (voir <see cref="TestTransactions"/>).
/// </summary>
public sealed class GetTransactionsEndpointTests(ApiFixture app) : TestBase<ApiFixture>
{
    private const string Route = "/v1/reconciliations/transactions";

    private static readonly DateTimeOffset Day = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = Day.AddDays(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_transactions_created_in_half_open_range()
    {
        var tenantId = NewTenantId();
        await app.InsertAsync(
        [
            TestTransactions.New(tenantId, "before", Day.AddTicks(-10)),
            TestTransactions.New(tenantId, "at-from", Day),
            TestTransactions.New(tenantId, "middle", Day.AddHours(12)),
            TestTransactions.New(tenantId, "last-second", NextDay.AddSeconds(-1)),
            TestTransactions.New(tenantId, "at-to", NextDay),
        ], Ct);

        var page = await GetPage(tenantId, Day, NextDay);

        page.Labels(tenantId).ShouldBe(["at-from", "middle", "last-second"]);
    }

    [Fact]
    public async Task Accepts_bounds_with_offset_and_returns_utc_dates()
    {
        var tenantId = NewTenantId();
        await app.InsertAsync(
        [
            TestTransactions.New(tenantId, "before", DateTimeOffset.Parse("2026-11-19T23:59:59Z")),
            TestTransactions.New(tenantId, "first", DateTimeOffset.Parse("2026-11-20T00:00:00Z")),
            TestTransactions.New(tenantId, "second", DateTimeOffset.Parse("2026-11-20T02:30:00+02:00")),
            TestTransactions.New(tenantId, "after", DateTimeOffset.Parse("2026-11-20T01:00:00Z")),
        ], Ct);

        // [00:00Z, 01:00Z) exprimé avec un offset de +02:00.
        var page = await GetPage(
            tenantId,
            DateTimeOffset.Parse("2026-11-20T02:00:00+02:00"),
            DateTimeOffset.Parse("2026-11-20T03:00:00+02:00"));

        page.Labels(tenantId).ShouldBe(["first", "second"]);
        page.Select(t => t.TransactionCreatedAt).ShouldBe(
        [
            DateTimeOffset.Parse("2026-11-20T00:00:00Z"),
            DateTimeOffset.Parse("2026-11-20T00:30:00Z"),
        ]);
        page.ShouldAllBe(t => t.TransactionCreatedAt.Offset == TimeSpan.Zero);
    }

    [Fact]
    public async Task Returns_all_fields_of_transaction()
    {
        var tenantId = NewTenantId();
        var transaction = TestTransactions.New(
            tenantId,
            "t-1",
            Day.AddHours(8),
            tenantUserId: "bob",
            buyOrSell: BuyOrSell.Sell,
            amount: "1234.5",
            amountCcy: "USD",
            accountAlias: "987-xyz-321");
        await app.InsertAsync([transaction], Ct);

        var page = await GetPage(tenantId, Day, NextDay);

        page.ShouldBe(
        [
            new TransactionItem("bob", transaction.OfferId, BuyOrSell.Sell, "1234.5", "USD", "987-xyz-321", Day.AddHours(8)),
        ]);
    }

    [Fact]
    public async Task Sorts_by_created_at()
    {
        var tenantId = NewTenantId();
        await app.InsertAsync(
        [
            TestTransactions.New(tenantId, "c", Day.AddHours(1)),
            TestTransactions.New(tenantId, "a", Day.AddHours(3)),
            TestTransactions.New(tenantId, "b", Day.AddHours(2)),
        ], Ct);

        var page = await GetPage(tenantId, Day, NextDay);

        page.Labels(tenantId).ShouldBe(["c", "b", "a"]);
    }

    [Fact]
    public async Task Paginates_transactions_created_at_same_instant_in_stable_order()
    {
        // Plusieurs transactions d'un même user au même instant : seul offerId les départage. C'est une chaîne
        // opaque : on ne vérifie pas un ordre précis entre ex æquo (il dépend de la collation), seulement qu'il est
        // stable et que la pagination renvoie chaque transaction une seule fois.
        var tenantId = NewTenantId();
        string[] labels = ["Z-9", "42", Guid.NewGuid().ToString(), "jdoe@acme", "a"];
        await app.InsertAsync(
            labels.Select(label => TestTransactions.New(tenantId, label, Day.AddHours(8), tenantUserId: "alice")),
            Ct);

        var fullPage = await GetPage(tenantId, Day, NextDay);
        var fullPageAgain = await GetPage(tenantId, Day, NextDay);
        var onePerPage = new List<TransactionItem>();
        for (var pageNumber = 1; pageNumber <= labels.Length; pageNumber++)
        {
            onePerPage.AddRange(await GetPage(tenantId, Day, NextDay, pageNumber, pageSize: 1));
        }

        fullPage.Labels(tenantId).ShouldBe(labels, ignoreOrder: true);
        fullPageAgain.ShouldBe(fullPage);
        onePerPage.ShouldBe(fullPage);
        (await GetPage(tenantId, Day, NextDay, page: labels.Length + 1, pageSize: 1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Paginates_with_page_and_page_size()
    {
        var tenantId = NewTenantId();
        var transactions = Enumerable.Range(0, 5)
            .Select(i => TestTransactions.New(tenantId, $"tx-{i}", Day.AddMinutes(i)))
            .ToList();
        await app.InsertAsync(transactions, Ct);

        var pages = new List<IReadOnlyList<TransactionItem>>();
        for (var pageNumber = 1; pageNumber <= 4; pageNumber++)
        {
            pages.Add(await GetPage(tenantId, Day, NextDay, pageNumber, pageSize: 2));
        }

        pages.Select(p => p.Count).ShouldBe([2, 2, 1, 0]);
        pages.SelectMany(p => p).Select(t => t.OfferId).ShouldBe(transactions.Select(t => t.OfferId));
    }

    [Fact]
    public async Task Uses_first_page_of_1000_by_default()
    {
        var tenantId = NewTenantId();
        var transactions = Enumerable.Range(0, GetTransactionsRequest.DefaultPageSize + 1)
            .Select(i => TestTransactions.New(tenantId, $"tx-{i:D4}", Day.AddSeconds(i)))
            .ToList();
        await app.InsertAsync(transactions, Ct);

        var page = await GetPage(tenantId, Day, NextDay);

        page.Count.ShouldBe(GetTransactionsRequest.DefaultPageSize);
        page.Labels(tenantId).First().ShouldBe("tx-0000");
        page.Labels(tenantId).Last().ShouldBe("tx-0999");
    }

    [Fact]
    public async Task Ignores_transactions_of_other_tenants()
    {
        var tenantId = NewTenantId();
        var otherTenantId = NewTenantId();
        await app.InsertAsync(
        [
            TestTransactions.New(tenantId, "mine", Day.AddHours(1), tenantUserId: "shared-user"),
            TestTransactions.New(otherTenantId, "other-same-user", Day.AddHours(2), tenantUserId: "shared-user"),
            TestTransactions.New(otherTenantId, "other", Day.AddHours(3)),
        ], Ct);

        var page = await GetPage(tenantId, Day, NextDay);

        page.Labels(tenantId).ShouldBe(["mine"]);
    }

    [Fact]
    public async Task Returns_empty_list_for_unknown_tenant()
    {
        var rsp = await Get(Query(NewTenantId(), Day, NextDay, asOf: await app.DatabaseNowAsync(Ct)));

        rsp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await rsp.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("transactions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Returns_json_in_spec_format()
    {
        var tenantId = NewTenantId();
        var transaction = TestTransactions.New(
            tenantId,
            "t-1",
            DateTimeOffset.Parse("2026-11-20T10:00:00+02:00"),
            tenantUserId: "u-001",
            buyOrSell: BuyOrSell.Sell,
            amount: "5000.00",
            amountCcy: "EUR",
            accountAlias: "123-abc-567");
        await app.InsertAsync([transaction], Ct);

        var rsp = await Get(Query(tenantId, Day, NextDay, asOf: await app.DatabaseNowAsync(Ct)));

        rsp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await rsp.Content.ReadAsStringAsync(Ct));
        json.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["transactions"]);
        var item = json.RootElement.GetProperty("transactions").EnumerateArray().ShouldHaveSingleItem();
        item.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["tenantUserId", "offerId", "buyOrSell", "amount", "amountCcy", "accountAlias", "transactionCreatedAt"]);
        item.EnumerateObject().ShouldAllBe(p => p.Value.ValueKind == JsonValueKind.String);
        item.GetProperty("tenantUserId").GetString().ShouldBe("u-001");
        item.GetProperty("offerId").GetString().ShouldBe(transaction.OfferId);
        item.GetProperty("buyOrSell").GetString().ShouldBe("Sell");
        item.GetProperty("amount").GetString().ShouldBe("5000.00");
        item.GetProperty("amountCcy").GetString().ShouldBe("EUR");
        item.GetProperty("accountAlias").GetString().ShouldBe("123-abc-567");
        item.GetProperty("transactionCreatedAt").GetString().ShouldBe("2026-11-20T08:00:00+00:00");
    }

    [Theory]
    [InlineData("5000.00")]
    [InlineData("5000")]
    [InlineData("1234.5")]
    [InlineData("1.10000000")]
    [InlineData("0.00000001")]
    [InlineData("99999999999999999999.99999999")]
    public async Task Returns_amount_exactly_as_stored(string amount)
    {
        var tenantId = NewTenantId();
        await app.InsertAsync([TestTransactions.New(tenantId, amount: amount)], Ct);

        var rsp = await Get(Query(tenantId, Day, NextDay, asOf: await app.DatabaseNowAsync(Ct)));

        using var json = JsonDocument.Parse(await rsp.Content.ReadAsStringAsync(Ct));
        var item = json.RootElement.GetProperty("transactions").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String);
        item.GetProperty("amount").GetString().ShouldBe(amount);
    }

    [Fact]
    public async Task Includes_transactions_inserted_before_as_of_exclusive_whatever_its_offset()
    {
        var tenantId = NewTenantId();
        var transaction = TestTransactions.New(tenantId, "t-1", Day.AddHours(8));
        await app.InsertAsync([transaction], Ct);
        var plusTwo = TimeSpan.FromHours(2);

        // transaction.InsertedAt est relu après l'insertion ; exprimé ici avec un offset de +02:00.
        var atInsertion = await GetPage(tenantId, Day, NextDay, asOf: transaction.InsertedAt.ToOffset(plusTwo));
        var justAfter = await GetPage(
            tenantId, Day, NextDay, asOf: transaction.InsertedAt.AddTicks(10).ToOffset(plusTwo));

        atInsertion.ShouldBeEmpty();
        justAfter.Labels(tenantId).ShouldBe(["t-1"]);
    }

    [Fact]
    public async Task Excludes_transactions_inserted_after_as_of()
    {
        var tenantId = NewTenantId();
        await app.InsertAsync([TestTransactions.New(tenantId, "before-as-of", Day.AddHours(2))], Ct);
        var asOf = await app.DatabaseNowAsync(Ct);
        await app.InsertAsync([TestTransactions.New(tenantId, "after-as-of", Day.AddHours(1))], Ct);
        var laterAsOf = await app.DatabaseNowAsync(Ct);

        var withAsOf = await GetPage(tenantId, Day, NextDay, asOf: asOf);
        var withLaterAsOf = await GetPage(tenantId, Day, NextDay, asOf: laterAsOf);

        withAsOf.Labels(tenantId).ShouldBe(["before-as-of"]);
        withLaterAsOf.Labels(tenantId).ShouldBe(["after-as-of", "before-as-of"]);
    }

    [Fact]
    public async Task Keeps_pages_stable_when_transactions_are_inserted_during_traversal_with_same_as_of()
    {
        var tenantId = NewTenantId();
        await app.InsertAsync(
        [
            TestTransactions.New(tenantId, "A", Day.AddHours(8)),
            TestTransactions.New(tenantId, "B", Day.AddHours(9)),
            TestTransactions.New(tenantId, "C", Day.AddHours(10)),
            TestTransactions.New(tenantId, "D", Day.AddHours(11)),
        ], Ct);
        var asOf = await app.DatabaseNowAsync(Ct);

        var page1 = await GetPage(tenantId, Day, NextDay, page: 1, pageSize: 2, asOf: asOf);
        // Insérée pendant le parcours, avec un createdAt situé dans la page déjà lue.
        await app.InsertAsync([TestTransactions.New(tenantId, "X", Day.AddHours(8.5))], Ct);
        var page2 = await GetPage(tenantId, Day, NextDay, page: 2, pageSize: 2, asOf: asOf);
        var page2WithNewAsOf = await GetPage(
            tenantId, Day, NextDay, page: 2, pageSize: 2, asOf: await app.DatabaseNowAsync(Ct));

        page1.Labels(tenantId).ShouldBe(["A", "B"]);
        page2.Labels(tenantId).ShouldBe(["C", "D"]);
        // Avec un asOf postérieur à l'insertion, les pages se décalent : B est renvoyée deux fois et X n'est jamais vue.
        page2WithNewAsOf.Labels(tenantId).ShouldBe(["B", "C"]);
    }

    [Fact]
    public async Task Error_messages_name_parameters_as_exposed_by_the_api()
    {
        var invalidRules = await GetErrors(Query(
            ("fromCreatedAt", To),
            ("toCreatedAt", From),
            ("page", "0"),
            ("pageSize", "5000")));
        var unreadableDate = await GetErrors(Query(
            ("tenantId", "acme"),
            ("fromCreatedAt", "pas-une-date"),
            ("toCreatedAt", To),
            ("asOf", AsOf)));
        var unreadableAsOf = await GetErrors(Query(
            ("tenantId", "acme"),
            ("fromCreatedAt", From),
            ("toCreatedAt", To),
            ("asOf", "hier")));
        var missingBounds = await GetErrors(Query(("tenantId", "acme"), ("asOf", AsOf)));
        var emptyDates = await GetErrors("tenantId=acme&fromCreatedAt=&toCreatedAt=&asOf=");

        invalidRules.Keys.ShouldBe(["tenantId", "toCreatedAt", "asOf", "page", "pageSize"], ignoreOrder: true);
        invalidRules["tenantId"].ShouldBe(["'tenantId' must not be empty."]);
        invalidRules["toCreatedAt"].ShouldBe(["'toCreatedAt' must be after 'fromCreatedAt'."]);
        invalidRules["asOf"].ShouldBe(["'asOf' must not be empty."]);
        invalidRules["page"].ShouldBe(["'page' must be between 1 and 1000000. You entered 0."]);
        invalidRules["pageSize"].ShouldBe(["'pageSize' must be between 1 and 1000. You entered 5000."]);
        unreadableDate["fromCreatedAt"]
            .ShouldContain("'fromCreatedAt' must be a valid DateTimeOffset. You entered 'pas-une-date'.");
        unreadableAsOf["asOf"].ShouldBe(["'asOf' must be a valid DateTimeOffset. You entered 'hier'."]);
        // La condition de la comparaison ne doit pas masquer le NotNull de toCreatedAt.
        missingBounds.Keys.ShouldBe(["fromCreatedAt", "toCreatedAt"], ignoreOrder: true);
        missingBounds["toCreatedAt"].ShouldBe(["'toCreatedAt' must not be empty."]);
        // Une date vide est traitée comme absente, et non lue comme 0001-01-01.
        emptyDates.Keys.ShouldBe(["fromCreatedAt", "toCreatedAt", "asOf"], ignoreOrder: true);
        emptyDates["fromCreatedAt"].ShouldBe(["'fromCreatedAt' must not be empty."]);
        emptyDates["toCreatedAt"].ShouldBe(["'toCreatedAt' must not be empty."]);
        emptyDates["asOf"].ShouldBe(["'asOf' must not be empty."]);
    }

    [Fact]
    public async Task Rejects_unencoded_plus_in_dates()
    {
        // Dans une query string, "+" est décodé en espace : la date reçue est illisible.
        var errors = await GetErrors(
            $"tenantId=acme&fromCreatedAt=2026-11-20T00:00:00+01:00&toCreatedAt={Uri.EscapeDataString(To)}"
            + $"&asOf={Uri.EscapeDataString(AsOf)}");

        errors["fromCreatedAt"]
            .ShouldContain("'fromCreatedAt' must be a valid DateTimeOffset. You entered '2026-11-20T00:00:00 01:00'.");
    }

    [Theory]
    [InlineData(null, From, To, null, null, "tenantId")]
    [InlineData("", From, To, null, null, "tenantId")]
    [InlineData(TooLongTenantId, From, To, null, null, "tenantId")]
    [InlineData("acme", null, To, null, null, "fromCreatedAt")]
    [InlineData("acme", From, null, null, null, "toCreatedAt")]
    [InlineData("acme", "pas-une-date", To, null, null, "fromCreatedAt")]
    [InlineData("acme", From, "pas-une-date", null, null, "toCreatedAt")]
    [InlineData("acme", "", To, null, null, "fromCreatedAt")]
    [InlineData("acme", From, " ", null, null, "toCreatedAt")]
    [InlineData("acme", From, From, null, null, "toCreatedAt")]
    [InlineData("acme", To, From, null, null, "toCreatedAt")]
    [InlineData("acme", "2026-11-20T02:00:00+02:00", "2026-11-20T00:00:00Z", null, null, "toCreatedAt")]
    [InlineData("acme", From, To, "0", null, "page")]
    [InlineData("acme", From, To, "1000001", null, "page")]
    [InlineData("acme", From, To, "abc", null, "page")]
    [InlineData("acme", From, To, null, "0", "pageSize")]
    [InlineData("acme", From, To, null, "1001", "pageSize")]
    [InlineData("acme", From, To, null, null, "asOf", null)]
    [InlineData("acme", From, To, null, null, "asOf", "hier")]
    [InlineData("acme", From, To, null, null, "asOf", "")]
    public async Task Rejects_invalid_parameters(
        string? tenantId,
        string? from,
        string? to,
        string? page,
        string? pageSize,
        string expectedErrorKey,
        string? asOf = AsOf)
    {
        var rsp = await Get(Query(
            ("tenantId", tenantId),
            ("fromCreatedAt", from),
            ("toCreatedAt", to),
            ("page", page),
            ("pageSize", pageSize),
            ("asOf", asOf)));

        rsp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await rsp.Content.ReadFromJsonAsync<ErrorResponse>(Ct);
        error.ShouldNotBeNull().Errors.Keys.ShouldContain(expectedErrorKey);
    }

    private const string From = "2026-11-20T00:00:00Z";
    private const string To = "2026-11-21T00:00:00Z";
    private const string AsOf = "2026-11-21T06:00:00Z";
    private const string TooLongTenantId =
        "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789X";

    private static string NewTenantId() => TestTransactions.NewTenantId();

    /// <summary>Appelle l'endpoint ; sans <paramref name="asOf"/>, utilise l'heure courante de la base.</summary>
    private async Task<IReadOnlyList<TransactionItem>> GetPage(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? page = null,
        int? pageSize = null,
        DateTimeOffset? asOf = null)
    {
        asOf ??= await app.DatabaseNowAsync(Ct);
        var rsp = await Get(Query(tenantId, from, to, page, pageSize, asOf));

        rsp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await rsp.Content.ReadFromJsonAsync<GetTransactionsResponse>(Ct);
        return body.ShouldNotBeNull().Transactions;
    }

    private async Task<Dictionary<string, List<string>>> GetErrors(string query)
    {
        var rsp = await Get(query);

        rsp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await rsp.Content.ReadFromJsonAsync<ErrorResponse>(Ct);
        return error.ShouldNotBeNull().Errors;
    }

    private Task<HttpResponseMessage> Get(string query) => app.Client.GetAsync($"{Route}?{query}", Ct);

    private static string Query(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? page = null,
        int? pageSize = null,
        DateTimeOffset? asOf = null) => Query(
        ("tenantId", tenantId),
        // Format ISO 8601 avec offset explicite : le "+" est encodé en %2B par Uri.EscapeDataString.
        ("fromCreatedAt", from.ToString("O")),
        ("toCreatedAt", to.ToString("O")),
        ("page", page?.ToString()),
        ("pageSize", pageSize?.ToString()),
        ("asOf", asOf?.ToString("O")));

    private static string Query(params (string Name, string? Value)[] parameters) => string.Join(
        "&",
        parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}"));
}
