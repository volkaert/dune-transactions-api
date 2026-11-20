using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DuneTransactionsApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateTransactionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    offer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    buy_or_sell = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    amount = table.Column<string>(type: "character varying(29)", maxLength: 29, nullable: false),
                    amount_ccy = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    account_alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    inserted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_amount_ccy", "amount_ccy ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_transactions_amount_format", "amount ~ '^(0|[1-9][0-9]{0,19})(\\.[0-9]{1,8})?$'");
                    table.CheckConstraint("ck_transactions_amount_positive", "amount ~ '[1-9]'");
                    table.CheckConstraint("ck_transactions_buy_or_sell", "buy_or_sell IN ('Buy', 'Sell')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_tenant_id_created_at_offer_id",
                table: "transactions",
                columns: new[] { "tenant_id", "created_at", "offer_id" })
                .Annotation("Npgsql:IndexInclude", new[] { "inserted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_transactions_offer_id",
                table: "transactions",
                column: "offer_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transactions");
        }
    }
}
