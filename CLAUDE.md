# CLAUDE.md

API C# .NET 10 avec FastEndpoints, PostgreSQL (EF Core / Npgsql) et Docker Compose. Voir `README.md` pour les commandes. Le projet reprend les choix de conception de `dune-users-api` ; seul le domaine change (transactions au lieu de users onboardés).

## Commandes

- Build : `dotnet build` (les warnings sont traités comme des erreurs)
- Tests : `dotnet test` (Microsoft Testing Platform ; Docker doit tourner pour Testcontainers)
- Stack complète : `docker compose up --build`

## Conventions

- Organisation par feature : `src/DuneTransactionsApi/Features/<Feature>/`. Un endpoint = une classe FastEndpoints, avec sa requête, sa réponse et son validateur dans le même dossier.
- Chaque endpoint a des tests d'intégration dans `tests/DuneTransactionsApi.Tests/Features/<Feature>/`, basés sur `ApiFixture` (`TestBase<ApiFixture>`) et des assertions Shouldly.
- Versions des packages NuGet uniquement dans `Directory.Packages.props` (gestion centralisée) ; les `.csproj` ne portent pas de version.
- Entités et configurations EF Core dans `Infrastructure/Persistence/<Entité>/`. Migrations générées avec `dotnet ef` dans `Infrastructure/Persistence/Migrations`. Nommage snake_case automatique (`UseSnakeCaseNamingConvention`).
- Dates : `DateTimeOffset` ↔ `timestamptz`. Npgsql refuse un offset non nul : convertir en UTC (`ToUniversalTime()`) avant écriture. L'offset d'origine n'est pas conservé.
- Heure courante : toujours via `TimeProvider` (injecté), jamais `DateTime.UtcNow` / `DateTimeOffset.UtcNow`.
- Plages de dates de l'API : borne basse incluse, borne haute exclue (`[fromCreatedAt, toCreatedAt)`, `[missingsSince, asOf)`, `inserted_at < asOf`). Deux plages consécutives s'enchaînent ainsi sans chevauchement ni trou.
- `transactions.inserted_at` (insertion en base) est posé par PostgreSQL (`DEFAULT now()`), jamais par l'application : EF Core l'ignore à l'écriture et le relit après l'insertion.
- `offerId` identifie la transaction et est **unique dans toute la base**, indépendamment du tenant. Il départage les ex æquo du tri (`createdAt` puis `offerId`).
- `amount` est une **chaîne** en base comme dans l'API, jamais convertie en nombre. Son format (décimal strictement positif, `.`, 8 décimales au plus) est garanti par des contraintes `CHECK` à expressions régulières.
- `buyOrSell` : enum `BuyOrSell`, stocké et sérialisé sous son nom exact (`Buy` / `Sell`).
- La clé JSON de la date de création est `transactionCreatedAt` (spec), la propriété du modèle `CreatedAt`, les filtres `fromCreatedAt` / `toCreatedAt`.
- Tests : `ApiFixture` est partagée par toutes les classes de tests (application, base et horloge `FakeTimeProvider` communes). Isoler les données par un `TenantId` unique, et positionner l'horloge (`SetUtcNow`) dans les tests qui en dépendent. `offerId` étant unique dans toute la base, ne jamais écrire un `offerId` en dur : passer un label à `TestTransactions.New(tenantId, label, …)` (`offerId` = `{tenantId}/{label}`) et comparer avec `Labels(tenantId)`.
- Specs dans `docs/specs/`, plans d'implémentation dans `docs/plans/`.
- Erreurs de validation : les messages sont toujours en anglais (`LanguageManager.Culture` fixée dans `Program.cs`, sinon ils suivent la culture du serveur) et citent les paramètres sous leur nom d'API en camelCase (`DisplayNameResolver` de FluentValidation et `Binding.FailureMessage` de FastEndpoints). Dans un `WithMessage` personnalisé, utiliser `{PropertyName}` / `{ComparisonProperty}` plutôt que des noms en dur.
- Une date présente mais vide (`asOf=`) est traitée comme absente (parseur `DateTimeOffset?` dans `Program.cs`) : sans lui, FastEndpoints la lit comme `0001-01-01`.
- FluentValidation : un `.When(...)` dans une chaîne s'applique par défaut à tous les validateurs qui le précèdent (dont `NotNull`). Pour ne conditionner qu'une comparaison, passer `ApplyConditionTo.CurrentValidator`.
