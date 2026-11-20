using DuneTransactionsApi.Infrastructure.Persistence;
using DuneTransactionsApi.Infrastructure.Persistence.Transactions;
using DuneTransactionsApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DuneTransactionsApi.Tests.Infrastructure.Persistence;

public sealed class TransactionPersistenceTests(ApiFixture app) : TestBase<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Persists_and_reads_back_all_fields()
    {
        var transaction = TestTransactions.New(
            tenantUserId: "bob",
            buyOrSell: BuyOrSell.Sell,
            amount: "1234.5",
            amountCcy: "USD",
            accountAlias: "987-xyz-321");

        await Insert(transaction);
        var stored = await Find(transaction.Id);

        stored.TenantId.ShouldBe(transaction.TenantId);
        stored.OfferId.ShouldBe(transaction.OfferId);
        stored.TenantUserId.ShouldBe("bob");
        stored.BuyOrSell.ShouldBe(BuyOrSell.Sell);
        stored.Amount.ShouldBe("1234.5");
        stored.AmountCcy.ShouldBe("USD");
        stored.AccountAlias.ShouldBe("987-xyz-321");
        stored.CreatedAt.ShouldBe(transaction.CreatedAt);
        stored.InsertedAt.ShouldBe(transaction.InsertedAt);
    }

    [Theory]
    [InlineData("2026-11-20T10:00:00+02:00", "2026-11-20T08:00:00Z")]
    [InlineData("2026-11-20T10:00:00-05:00", "2026-11-20T15:00:00Z")]
    [InlineData("2026-11-20T10:00:00Z", "2026-11-20T10:00:00Z")]
    public async Task Stores_created_at_as_utc_instant(string createdAt, string expectedUtc)
    {
        var transaction = TestTransactions.New(createdAt: DateTimeOffset.Parse(createdAt));

        await Insert(transaction);
        var stored = await Find(transaction.Id);

        stored.CreatedAt.ShouldBe(DateTimeOffset.Parse(expectedUtc));
        stored.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Sets_inserted_at_from_database_clock()
    {
        // Encadré par l'heure de PostgreSQL : la valeur vient de la base, pas de l'horloge de l'application
        // (FakeTimeProvider, figée sur une autre date).
        var transaction = TestTransactions.New();

        var before = await DatabaseNow();
        await Insert(transaction);
        var after = await DatabaseNow();
        var stored = await Find(transaction.Id);

        stored.InsertedAt.ShouldBeInRange(before, after);
        stored.InsertedAt.Offset.ShouldBe(TimeSpan.Zero);
        transaction.InsertedAt.ShouldBe(stored.InsertedAt, "valeur relue après l'insertion");
    }

    [Fact]
    public async Task Ignores_inserted_at_set_by_application()
    {
        var forced = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var transaction = TestTransactions.New();

        var before = await DatabaseNow();
        await WithDb(async db =>
        {
            db.Transactions.Add(transaction);
            db.Entry(transaction).Property(t => t.InsertedAt).CurrentValue = forced;
            await db.SaveChangesAsync(Ct);
        });
        var stored = await Find(transaction.Id);

        stored.InsertedAt.ShouldNotBe(forced);
        stored.InsertedAt.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task Keeps_inserted_at_on_update()
    {
        var transaction = TestTransactions.New();
        await Insert(transaction);
        var inserted = await Find(transaction.Id);

        await WithDb(async db =>
        {
            var tracked = await db.Transactions.SingleAsync(t => t.Id == transaction.Id, Ct);
            tracked.AccountAlias = "updated";
            db.Entry(tracked).Property(t => t.InsertedAt).CurrentValue = DateTimeOffset.UnixEpoch;
            await db.SaveChangesAsync(Ct);
        });
        var stored = await Find(transaction.Id);

        stored.AccountAlias.ShouldBe("updated");
        stored.InsertedAt.ShouldBe(inserted.InsertedAt);
    }

    [Fact]
    public async Task Filters_on_created_at_by_utc_range()
    {
        var transaction = TestTransactions.New(createdAt: DateTimeOffset.Parse("2026-11-20T23:30:00-02:00"));
        await Insert(transaction);

        var dayStart = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var ids = await WithDb(db => db.Transactions
            .Where(t => t.TenantId == transaction.TenantId && t.CreatedAt >= dayStart && t.CreatedAt < dayEnd)
            .Select(t => t.Id)
            .ToListAsync(Ct));

        ids.ShouldBe([transaction.Id]);
    }

    [Theory]
    [InlineData(BuyOrSell.Buy, "Buy")]
    [InlineData(BuyOrSell.Sell, "Sell")]
    public async Task Stores_buy_or_sell_as_its_name(BuyOrSell buyOrSell, string expectedColumnValue)
    {
        var transaction = TestTransactions.New(buyOrSell: buyOrSell);
        await Insert(transaction);

        var column = await WithDb(db => db.Database
            .SqlQuery<string>($"SELECT buy_or_sell AS \"Value\" FROM transactions WHERE id = {transaction.Id}")
            .SingleAsync(Ct));

        column.ShouldBe(expectedColumnValue);
    }

    [Theory]
    [InlineData("buy")]
    [InlineData("SELL")]
    [InlineData("Hold")]
    public async Task Rejects_buy_or_sell_other_than_buy_or_sell(string buyOrSell)
    {
        // Insertion SQL brute : l'enum de l'application ne permet pas d'écrire une autre valeur.
        var ex = await Should.ThrowAsync<PostgresException>(() => WithDb(db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO transactions (id, tenant_id, offer_id, tenant_user_id, buy_or_sell, amount, amount_ccy, account_alias, created_at)
            VALUES ({Guid.NewGuid()}, {TestTransactions.NewTenantId()}, {Guid.NewGuid().ToString()}, 'alice', {buyOrSell}, '1.00', 'EUR', 'alias', now())
            """,
            Ct)));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        ex.ConstraintName.ShouldBe("ck_transactions_buy_or_sell");
    }

    [Theory]
    [InlineData("5000.00")]
    [InlineData("5000")]
    [InlineData("1234.5")]
    [InlineData("1.10000000")]
    [InlineData("0.01")]
    [InlineData("0.00000001")]
    [InlineData("99999999999999999999.99999999")]
    public async Task Reads_back_amount_exactly_as_stored(string amount)
    {
        var transaction = TestTransactions.New(amount: amount);

        await Insert(transaction);
        var stored = await Find(transaction.Id);

        stored.Amount.ShouldBe(amount);
    }

    [Theory]
    [InlineData("0", "ck_transactions_amount_positive")]
    [InlineData("0.00", "ck_transactions_amount_positive")]
    [InlineData("0.00000000", "ck_transactions_amount_positive")]
    [InlineData("-1", "ck_transactions_amount_format")]
    [InlineData("+1", "ck_transactions_amount_format")]
    [InlineData("1,5", "ck_transactions_amount_format")]
    [InlineData("1e3", "ck_transactions_amount_format")]
    [InlineData(" 1", "ck_transactions_amount_format")]
    [InlineData("1 ", "ck_transactions_amount_format")]
    [InlineData("007", "ck_transactions_amount_format")]
    [InlineData("5.", "ck_transactions_amount_format")]
    [InlineData(".5", "ck_transactions_amount_format")]
    [InlineData("1.123456789", "ck_transactions_amount_format")]
    [InlineData("1 000.00", "ck_transactions_amount_format")]
    [InlineData("100000000000000000000", "ck_transactions_amount_format")]
    [InlineData("", "ck_transactions_amount_format")]
    public async Task Rejects_amount_with_invalid_format(string amount, string expectedConstraint)
    {
        var transaction = TestTransactions.New(amount: amount);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => Insert(transaction));

        var postgres = ex.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        postgres.ConstraintName.ShouldBe(expectedConstraint);
    }

    [Fact]
    public async Task Rejects_amount_longer_than_29_characters()
    {
        var transaction = TestTransactions.New(amount: "99999999999999999999.999999990");

        var ex = await Should.ThrowAsync<DbUpdateException>(() => Insert(transaction));

        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState
            .ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Theory]
    [InlineData("eur", PostgresErrorCodes.CheckViolation)]
    [InlineData("E1R", PostgresErrorCodes.CheckViolation)]
    [InlineData("EURO", PostgresErrorCodes.StringDataRightTruncation)]
    public async Task Rejects_invalid_amount_currency(string amountCcy, string expectedSqlState)
    {
        var transaction = TestTransactions.New(amountCcy: amountCcy);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => Insert(transaction));

        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(expectedSqlState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_duplicate_offer_id_whatever_the_tenant(bool sameTenant)
    {
        var first = TestTransactions.New();
        var duplicate = TestTransactions.New(
            tenantId: sameTenant ? first.TenantId : TestTransactions.NewTenantId(),
            offerId: first.OfferId);
        await Insert(first);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => Insert(duplicate));

        var postgres = ex.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgres.ConstraintName.ShouldBe("ux_transactions_offer_id");
    }

    [Fact]
    public async Task Accepts_several_transactions_of_same_user_at_same_instant()
    {
        var first = TestTransactions.New(tenantUserId: "alice");
        var second = TestTransactions.New(first.TenantId, tenantUserId: "alice", createdAt: first.CreatedAt);

        await Insert(first);
        await Insert(second);

        var count = await WithDb(db => db.Transactions.CountAsync(t => t.TenantId == first.TenantId, Ct));
        count.ShouldBe(2);
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        var pending = await WithDb(db => Task.FromResult(db.Database.HasPendingModelChanges()));

        pending.ShouldBeFalse();
    }

    private Task Insert(Transaction transaction) => app.InsertAsync([transaction], Ct);

    private Task<DateTimeOffset> DatabaseNow() => app.DatabaseNowAsync(Ct);

    private Task<Transaction> Find(Guid id) =>
        WithDb(db => db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct));

    private async Task WithDb(Func<AppDbContext, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
