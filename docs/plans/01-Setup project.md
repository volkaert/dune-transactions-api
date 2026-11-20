# Plan 01 — Setup du projet `dune-transactions-api`

Source : [docs/specs/01-Setup project.md](../specs/01-Setup%20project.md)

Projet de référence : `dune-users-api` (`/Users/fabrice/dev/projects/dune-users-api`, plans 01 à 04 et notes d'implémentation), et son guide `HOW-TO-USE-THE-API.md`.

## Objectif

Livrer l'API `dune-transactions-api` complète, en reprenant **à l'identique** la stack, l'organisation du code, les conventions et les choix de conception de `dune-users-api`. Seul le domaine change : les users onboardés deviennent des transactions.

À la fin de ce plan :

- l'API C# .NET 10 (FastEndpoints) compile sans warning, démarre et est connectée à PostgreSQL ;
- `docker compose up --build` lance ensemble l'API et la base ;
- la table `transactions` existe, créée par une migration EF Core ;
- `GET /health`, `GET /v1/reconciliations/transactions` et `GET /v1/reconciliations/transactions/missings` sont disponibles ;
- les tests d'intégration (xUnit v3, FastEndpoints.Testing, Shouldly, Testcontainers) couvrent chaque endpoint et le mapping EF Core, contre une vraie instance PostgreSQL.

Contrairement à `dune-users-api`, construit en quatre plans successifs (setup, modèle, endpoint, `/missings`), ce plan part directement de l'état final de la référence. Les tâtonnements de la référence ne sont donc pas rejoués : pas de colonne `created_at` posée par l'application, pas de bornes `(missingsSince, asOf]`, pas de `asOf` calculé par le serveur, et une seule migration initiale.

## Hors périmètre

- Écriture de transactions par l'API : comme dans la référence, l'API est en lecture seule. Les données sont insérées par un autre processus (et par les tests).
- Authentification / autorisation.
- CI/CD, déploiement, observabilité avancée.

## Choix techniques (repris de `dune-users-api`)

| Sujet | Choix |
|---|---|
| SDK | .NET 10 (`10.0.300`, figé via `global.json`, `rollForward: latestFeature`) |
| Solution | `DuneTransactionsApi.slnx` |
| Packages | Central Package Management (`Directory.Packages.props`), versions **uniquement** dans ce fichier |
| Build | `Nullable`, `ImplicitUsings`, `LangVersion=latest`, `TreatWarningsAsErrors` (`Directory.Build.props`) |
| API | FastEndpoints 9.0.0, organisation par feature (REPR) |
| Base | PostgreSQL 18 (`postgres:18-alpine`), même image en Docker Compose et en tests |
| Accès aux données | EF Core 10.0.15 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Microsoft.EntityFrameworkCore.Relational` 10.0.15 référencé explicitement (évite le conflit MSB3277) |
| Nommage SQL | snake_case via `EFCore.NamingConventions` 10.0.1 |
| Migrations | `dotnet-ef` 10.0.15 en outil local (`dotnet-tools.json` à la racine), appliquées au démarrage **en Development uniquement** |
| Horloge | `TimeProvider` injecté (`TimeProvider.System`, `FakeTimeProvider` en tests) |
| Tests | xUnit v3 4.0.3 sous Microsoft Testing Platform (`global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`), FastEndpoints.Testing 9.0.0, Shouldly 4.3.0, Testcontainers.PostgreSql 4.17.0, Microsoft.Extensions.TimeProvider.Testing 10.11.1 |

Les versions sont celles validées dans la référence. Le plan ne les fait pas monter : EF Core 11 n'est pas encore supporté par Npgsql 10.x.

## Correspondance avec `dune-users-api`

| `dune-users-api` | `dune-transactions-api` |
|---|---|
| Namespace / projet `DuneUsersApi` | `DuneTransactionsApi` |
| Base `dune_users` | `dune_transactions` |
| Entité `User` (`Infrastructure/Persistence/Users/`) | `Transaction` (`Infrastructure/Persistence/Transactions/`) |
| Table `users` | `transactions` |
| `onboardedAt` / `onboarded_at` | `createdAt` / `created_at` |
| `insertedAt` / `inserted_at` (horloge de la base) | inchangé |
| `/v1/reconciliations/user-onboardings` | `/v1/reconciliations/transactions` |
| `/v1/reconciliations/user-onboardings/missings` | `/v1/reconciliations/transactions/missings` |
| `fromOnboardedAt`, `toOnboardedAt` | `fromCreatedAt`, `toCreatedAt` |
| `tenantId`, `asOf`, `missingsSince`, `page`, `pageSize` | inchangés (mêmes règles) |
| Réponse `{ "userOnboardings": [ … ] }` | `{ "transactions": [ … ] }` |
| `GetUserOnboardings*`, `GetMissingUserOnboardings*` | `GetTransactions*`, `GetMissingTransactions*` |
| `UserOnboardingQueries` | `TransactionQueries` |
| `TestUsers` | `TestTransactions` |

## Contrat de l'API

### Réponse commune aux deux endpoints

```json
{
  "transactions": [
    {
      "tenantUserId": "alice",
      "offerId": "offer-42",
      "buyOrSell": "Buy",
      "amount": "5000.00",
      "amountCcy": "EUR",
      "accountAlias": "123-abc-567",
      "transactionCreatedAt": "2026-11-19T08:00:00+00:00"
    }
  ]
}
```

- `buyOrSell` : chaîne, exactement `"Buy"` ou `"Sell"`.
- `offerId` : identifiant de la transaction, unique dans toute la base, indépendamment du tenant.
- `amount` : **chaîne**, renvoyée exactement telle qu'elle est stockée en base. Il n'y a de conversion numérique ni à l'écriture, ni en base, ni à la lecture : `"5000.00"` reste `"5000.00"`, et `"1234.5"` reste `"1234.5"`. Le format est garanti par une contrainte de la base (voir « Modèle de données ») : un nombre décimal strictement positif, séparateur `.`, jusqu'à 8 décimales, sans signe, sans exposant ni séparateur de milliers.
- `transactionCreatedAt` : la date `createdAt` de la transaction, toujours renvoyée en UTC (`+00:00`), comme `onboardedAt` dans la référence. La clé JSON suit l'exemple de la spec.
- La virgule finale de l'exemple de la spec n'est pas reprise : ce ne serait pas du JSON valide.

### `GET /v1/reconciliations/transactions`

Transactions du tenant avec `fromCreatedAt <= createdAt < toCreatedAt` et `inserted_at < asOf`.

| Paramètre | Obligatoire | Défaut | Règles |
|---|---|---|---|
| `tenantId` | oui | — | Non vide, 100 caractères max |
| `fromCreatedAt` | oui | — | ISO 8601 avec offset, borne **incluse** |
| `toCreatedAt` | oui | — | ISO 8601 avec offset, borne **exclue**, strictement après `fromCreatedAt` |
| `asOf` | oui | — | ISO 8601 avec offset, borne **exclue** (`inserted_at < asOf`) |
| `page` | non | `1` | Entre `1` et `1000000` |
| `pageSize` | non | `1000` | Entre `1` et `1000` |

### `GET /v1/reconciliations/transactions/missings`

Mêmes paramètres et mêmes règles, plus `missingsSince` (obligatoire, borne **incluse**). La fenêtre d'insertion est `[missingsSince, asOf)`, avec `asOf` strictement après `missingsSince`. Enchaînement : le `missingsSince` d'un appel est le `asOf` de l'appel précédent.

### Règles communes (identiques à la référence)

- Toutes les plages sont semi-ouvertes : borne basse incluse, borne haute exclue.
- Tri par `createdAt`, puis `offerId` (voir ci-dessous). Une page de moins de `pageSize` éléments, éventuellement vide, est la dernière. Une page au-delà de la fin renvoie `200` avec une liste vide.
- Un tenant inconnu ou une plage sans transaction renvoie `200` avec `"transactions": []`.
- Erreurs : `400` au format FastEndpoints, une entrée par paramètre. Les messages citent le paramètre sous son nom d'API en camelCase : `'fromCreatedAt' must be a valid DateTimeOffset. You entered '…'.`, `'toCreatedAt' must be after 'fromCreatedAt'.`, `'asOf' must be after 'missingsSince'.`
- Paramètres inconnus ignorés.

**Départage des ex æquo (écart avec la référence)** : dans la référence, `tenant_user_id` identifie le user dans son tenant et départage les ex æquo. Ici, un même user peut avoir plusieurs transactions créées au même instant, donc `tenant_user_id` ne suffit pas. C'est `offer_id`, l'identifiant de la transaction (unique dans toute la base), qui joue ce rôle : `(created_at, offer_id)` donne un ordre total et une pagination stable. Comme dans la référence, ce départage n'a pas de sens métier.

## Modèle de données

Table `transactions` :

| Colonne | Type | Contraintes | Origine |
|---|---|---|---|
| `id` | `uuid` | PK | Clé technique (comme `users.id`) |
| `tenant_id` | `varchar(100)` | NOT NULL | Filtre `tenantId` |
| `tenant_user_id` | `varchar(100)` | NOT NULL | Réponse |
| `offer_id` | `varchar(100)` | NOT NULL, unique (toute la base) | Identifiant de la transaction, indépendant du tenant ; réponse |
| `buy_or_sell` | `varchar(4)` | NOT NULL, `CHECK (buy_or_sell IN ('Buy', 'Sell'))` | Réponse (enum `BuyOrSell` stocké en texte via `HasConversion<string>()`) |
| `amount` | `varchar(29)` | NOT NULL, `CHECK` de format (ci-dessous) | Réponse, renvoyée telle quelle ; 20 chiffres avant le `.` au plus, 8 après |
| `amount_ccy` | `char(3)` | NOT NULL, `CHECK (amount_ccy ~ '^[A-Z]{3}$')` | Réponse (code ISO 4217, même principe que `address_country_code`) |
| `account_alias` | `varchar(100)` | NOT NULL | Réponse |
| `created_at` | `timestamptz` | NOT NULL | Date métier de la transaction, filtrée par `fromCreatedAt` / `toCreatedAt` |
| `inserted_at` | `timestamptz` | NOT NULL, `DEFAULT now()` | Instant d'insertion, **posé par PostgreSQL**, jamais par l'application ; filtré par `asOf` / `missingsSince` |

Index :

- `ux_transactions_offer_id`, unique, sur `(offer_id)` seul. Contrairement à `ux_users_tenant_id_tenant_user_id` dans la référence, il ne comprend pas `tenant_id`.
- `ix_transactions_tenant_id_created_at_offer_id` sur `(tenant_id, created_at, offer_id)` `INCLUDE (inserted_at)` : il sert le filtre et le tri sans étape de tri, et applique le filtre `asOf` / `missingsSince` sur les entrées de l'index avant de lire la table. Contrairement à la référence, ce n'est pas un `Index Only Scan` : la réponse contient des colonnes hors index, et les y inclure dupliquerait la table.

Contraintes sur `amount`, vérifiées par expressions régulières, sans conversion numérique :

- `ck_transactions_amount_format` : `amount ~ '^(0|[1-9][0-9]{0,19})(\.[0-9]{1,8})?$'`. Cela exclut le signe, l'exposant, la virgule, les espaces, les zéros en tête (`007`), un `.` sans décimale (`5.`) et plus de 8 décimales ;
- `ck_transactions_amount_positive` : `amount ~ '[1-9]'`, ce qui rejette les valeurs nulles (`0`, `0.00`). Avec la contrainte de format, le montant est donc strictement positif.

Une valeur hors format est rejetée à l'insertion, et non arrondie ou normalisée.

Mêmes règles que pour `User` :

- `CreatedAt` est converti en UTC dans son setter, car Npgsql refuse un offset non nul en écriture ;
- `InsertedAt` a `HasDefaultValueSql("now()")` et `PropertySaveBehavior.Ignore` avant et après la sauvegarde : il est omis de l'INSERT et de l'UPDATE, puis relu ;
- `TenantId` et `OfferId` (l'identité de la transaction) en `init` ; les autres propriétés métier en `set`.

## Arborescence cible

```
dune-transactions-api/
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── DuneTransactionsApi.slnx
├── dotnet-tools.json
├── docker-compose.yml
├── .env.example
├── .dockerignore
├── CLAUDE.md
├── README.md
├── HOW-TO-USE-THE-API.md
├── src/DuneTransactionsApi/
│   ├── DuneTransactionsApi.csproj
│   ├── Dockerfile
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── Properties/launchSettings.json
│   ├── Features/
│   │   ├── Health/HealthEndpoint.cs
│   │   └── Reconciliations/
│   │       ├── GetTransactionsEndpoint.cs
│   │       ├── GetTransactionsRequest.cs
│   │       ├── GetTransactionsValidator.cs
│   │       ├── GetTransactionsResponse.cs          # GetTransactionsResponse + TransactionItem
│   │       ├── GetMissingTransactionsEndpoint.cs
│   │       ├── GetMissingTransactionsRequest.cs
│   │       ├── GetMissingTransactionsValidator.cs
│   │       └── TransactionQueries.cs
│   └── Infrastructure/Persistence/
│       ├── AppDbContext.cs
│       ├── Migrations/
│       └── Transactions/
│           ├── Transaction.cs
│           ├── BuyOrSell.cs
│           └── TransactionConfiguration.cs
└── tests/DuneTransactionsApi.Tests/
    ├── DuneTransactionsApi.Tests.csproj
    ├── xunit.runner.json
    ├── Fixtures/
    │   ├── ApiFixture.cs
    │   └── TestTransactions.cs
    ├── Features/
    │   ├── Health/HealthEndpointTests.cs
    │   └── Reconciliations/
    │       ├── GetTransactionsEndpointTests.cs
    │       └── GetMissingTransactionsEndpointTests.cs
    └── Infrastructure/Persistence/TransactionPersistenceTests.cs
```

## Étapes

Chaque étape se termine par une validation. On ne passe à la suivante qu'une fois celle-ci verte.

### Étape 1 — Fondations de la solution

1. `global.json` : SDK `10.0.300`, `rollForward: latestFeature`, runner de tests `Microsoft.Testing.Platform`.
2. `Directory.Build.props` : `net10.0`, `Nullable`, `ImplicitUsings`, `LangVersion=latest`, `TreatWarningsAsErrors`.
3. `Directory.Packages.props` : `ManagePackageVersionsCentrally`, groupes `API` et `Tests`, avec les versions listées plus haut.
4. Solution `DuneTransactionsApi.slnx` (dossiers `/src/` et `/tests/`).
5. `dotnet new tool-manifest` puis `dotnet tool install dotnet-ef --version 10.0.15`.
6. Compléter le `.gitignore` existant si besoin (`.env` doit y figurer).

**Validation** : `dotnet --version` dans le repo → `10.0.300` ; `dotnet tool restore` passe.

### Étape 2 — Projet API, PostgreSQL et `/health`

1. `src/DuneTransactionsApi` (`Microsoft.NET.Sdk.Web`, `RootNamespace` `DuneTransactionsApi`), ajouté à la solution, avec les références de packages sans version.
2. `AppDbContext` avec `ApplyConfigurationsFromAssembly`.
3. `Program.cs`, repris de la référence :
   - chaîne `ConnectionStrings:Postgres` obligatoire (exception explicite si absente) ;
   - `AddSingleton(TimeProvider.System)` ;
   - `AddDbContext` + `UseNpgsql` + `UseSnakeCaseNamingConvention` ;
   - `MigrateAsync()` au démarrage en Development uniquement ;
   - `ValidatorOptions.Global.DisplayNameResolver` (noms camelCase) et `Binding.FailureMessage` (message `'<param>' must be a valid <Type>. You entered '<valeur>'.`) ;
   - `public partial class Program;` en fin de fichier.
4. `HealthEndpoint` identique : `200 { "status": "Healthy", "database": "Up" }`, ou `503 { "status": "Unhealthy", "database": "Down" }` si `CanConnectAsync` échoue.
5. `appsettings.json` (logs EF Core en `Warning`), `appsettings.Development.json` (`Host=localhost;Port=5432;Database=dune_transactions;Username=dune;Password=dune`), `launchSettings.json` (`http://localhost:5080`).

**Validation** : `dotnet build` sans warning ; Postgres lancé, `dotnet run` puis `curl localhost:5080/health` → `200`.

### Étape 3 — Conteneurisation et Docker Compose

1. `Dockerfile` multi-stage (`sdk:10.0` → `aspnet:10.0`) : restore dans une couche séparée, `publish -c Release /p:UseAppHost=false`, port `8080`, `USER $APP_UID`.
2. `.dockerignore` : `bin/`, `obj/`, IDE, `.git/`, `.env`, `docs/`, `tests/`.
3. `docker-compose.yml` :
   - `postgres` : `postgres:18-alpine`, volume nommé sur `/var/lib/postgresql`, healthcheck `pg_isready`, port `${POSTGRES_PORT:-5432}` ;
   - `api` : build depuis le Dockerfile, `depends_on` avec `service_healthy`, `ConnectionStrings__Postgres` construite depuis les variables, `ASPNETCORE_ENVIRONMENT` à `Development` par défaut, port `${API_PORT:-8080}`.
4. `.env.example` : `POSTGRES_DB=dune_transactions`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_PORT`, `API_PORT`, `ASPNETCORE_ENVIRONMENT`.

**Validation** : `docker compose up --build`, puis `curl localhost:8080/health` → `200` avec `"database": "Up"`. Les ports par défaut sont ceux de la référence. Si la stack `dune-users-api` tourne en même temps, il faut l'arrêter, changer `POSTGRES_PORT` / `API_PORT` dans `.env`, ou valider sur une stack isolée (`docker compose -p dune-tx-check` avec d'autres ports), comme dans la référence.

### Étape 4 — Projet de tests et fixture

1. `tests/DuneTransactionsApi.Tests` (`OutputType=Exe`, `IsTestProject`), avec les packages de test, les `Using` globaux (`FastEndpoints`, `FastEndpoints.Testing`, `Shouldly`, `Xunit`) et `xunit.runner.json` (`parallelizeTestCollections: true`).
2. `ApiFixture : AppFixture<Program>`, reprise telle quelle (les pièges sont décrits dans les notes du plan 02 de la référence) :
   - `[assembly: EnableAdvancedTesting]` ;
   - conteneur Postgres **statique**, démarré dans `PreSetupAsync` et arrêté dans `OnCachedWafDisposedAsync` (pas dans `TearDownAsync`, qui s'exécute à la fin de chaque classe de tests) ;
   - environnement `Testing`, chaîne de connexion du conteneur, `FakeTimeProvider` enregistré dans l'injection de dépendances ;
   - `MigrateAsync()` dans `SetupAsync` ;
   - `Time` (horloge de l'injection de dépendances) et `DatabaseNowAsync()` (`SELECT now()`).
3. `HealthEndpointTests` : `200`, `Healthy`, `Up`.

**Validation** : `dotnet test` passe (Docker démarré).

### Étape 5 — Modèle `Transaction` et migration

1. `BuyOrSell` (enum `Buy`, `Sell`), annoté `[JsonConverter(typeof(JsonStringEnumConverter<BuyOrSell>))]` pour être sérialisé en `"Buy"` / `"Sell"` sans réglage global.
2. `Transaction` et `TransactionConfiguration`, conformes à la section « Modèle de données » : longueurs, `Amount` en `string` (`HasMaxLength(29)`, aucune conversion), `char(3)` fixe, contraintes `CHECK` (`ck_transactions_buy_or_sell`, `ck_transactions_amount_format`, `ck_transactions_amount_positive`, `ck_transactions_amount_ccy`), `inserted_at` posé par la base, index unique sur `offer_id`, index de lecture `INCLUDE (inserted_at)`.
3. `DbSet<Transaction> Transactions` dans `AppDbContext`.
4. Une seule migration : `dotnet ef migrations add CreateTransactionsTable --project src/DuneTransactionsApi --output-dir Infrastructure/Persistence/Migrations`. Relire le SQL généré (`dotnet ef migrations script`).
5. `Fixtures/TestTransactions.cs` : `New(tenantId?, offerId?, createdAt?, tenantUserId?, buyOrSell = Buy, amount = "5000.00", amountCcy = "EUR", …)`, avec un tenant et un `offerId` uniques par défaut, et l'extension `ApiFixture.InsertAsync(transactions, ct)`. Les tests partagent la même base et `offerId` est unique dans toute la base : un `offerId` explicite (`"A"`, `"offer-42"`) entrerait en collision d'un test à l'autre. Les tests qui ont besoin d'un `offerId` lisible le préfixent donc avec un identifiant propre au test (par exemple `$"{tenantId}-A"`). Les tests qui vérifient le tri utilisent des préfixes qui gardent l'ordre attendu.
6. `TransactionPersistenceTests`, adaptés de `UserPersistenceTests` :
   - tous les champs sont relus à l'identique ;
   - `created_at` est stocké en UTC, quel que soit l'offset d'origine (`Theory`) ;
   - `inserted_at` vient de l'horloge de la base (encadré par `DatabaseNowAsync`, offset nul) ;
   - un `inserted_at` forcé par l'application est ignoré à l'insertion et à la mise à jour ;
   - le montant est relu **caractère pour caractère** (`Theory`) : `"5000.00"`, `"5000"`, `"1234.5"`, `"0.01"`, `"0.00000001"`, `"99999999999999999999.99999999"` ;
   - un montant hors format est rejeté (`CheckViolation`, `Theory`) : `"0"`, `"0.00"`, `"-1"`, `"+1"`, `"1,5"`, `"1e3"`, `" 1"`, `"007"`, `"5."`, `".5"`, `"1.123456789"`, `""` ; un montant de plus de 29 caractères donne `StringDataRightTruncation` ;
   - `amount_ccy` invalide (`eur`, `E1R` → `CheckViolation` ; `EURO` → `StringDataRightTruncation`) ;
   - `buy_or_sell` hors `Buy` / `Sell` (`buy`, `SELL`, `Hold`) rejeté par la contrainte (insertion SQL brute) ;
   - un `offerId` en double est rejeté (`UniqueViolation`), dans le même tenant **comme dans deux tenants différents** ;
   - plusieurs transactions d'un même `(tenant_id, tenant_user_id)` sont acceptées ;
   - `Model_has_no_pending_changes`.

**Validation** : `dotnet test` vert ; avec `docker compose up --build`, la migration est appliquée et `\d transactions` correspond au modèle.

### Étape 6 — `GET /v1/reconciliations/transactions`

1. `GetTransactionsRequest` : paramètres obligatoires nullables (`string?`, `DateTimeOffset?`), `Page = 1`, `PageSize = 1000`.
2. `GetTransactionsValidator` : règles de la section « Contrat », avec `MaxPage = 1_000_000` et `MaxPageSize = 1000`. Pour la comparaison `toCreatedAt > fromCreatedAt`, écrire `.When(..., ApplyConditionTo.CurrentValidator)`, sans quoi l'absence de `toCreatedAt` n'est pas signalée (bug corrigé dans la référence). Message `'{PropertyName}' must be after '{ComparisonProperty}'.`
3. `GetTransactionsResponse(IReadOnlyList<TransactionItem> Transactions)`, avec `TransactionItem(string TenantUserId, string OfferId, BuyOrSell BuyOrSell, string Amount, string AmountCcy, string AccountAlias, DateTimeOffset TransactionCreatedAt)`. L'ordre des propriétés suit celui de la spec.
4. `TransactionQueries` :
   - `CreatedBetween(tenantId, from, to)` : conversion des bornes en UTC, filtre `[from, to)` ;
   - `ToTransactionsPageAsync(page, pageSize, ct)` : `AsNoTracking`, tri `CreatedAt, OfferId`, `Skip` / `Take`, projection SQL directe vers `TransactionItem` (`amount` est renvoyé tel quel, sans formatage).
5. `GetTransactionsEndpoint` : `asOf` converti en UTC, `InsertedAt < asOf`, `AllowAnonymous`, `Summary` documenté.
6. `GetTransactionsEndpointTests`, adaptés de `GetUserOnboardingsEndpointTests` (URL construite explicitement pour tester le contrat réel) :
   - plage semi-ouverte `[fromCreatedAt, toCreatedAt)` ; bornes avec offset et dates renvoyées en UTC ;
   - tri par `createdAt` ; ex æquo départagés par `offerId` et stables entre pages, y compris **plusieurs transactions du même user au même instant** (cas propre à ce projet) ;
   - pagination `page` / `pageSize`, défaut page 1 de 1000 ;
   - isolation par tenant, tenant inconnu → liste vide ;
   - `asOf` exclu quel que soit son offset, transactions insérées après `asOf` exclues, pages stables pendant des insertions avec le même `asOf` ;
   - `Returns_json_in_spec_format` : clés exactes et dans l'ordre (`transactions` ; `tenantUserId`, `offerId`, `buyOrSell`, `amount`, `amountCcy`, `accountAlias`, `transactionCreatedAt`), `buyOrSell` en chaîne, date en `+00:00` ;
   - `Returns_amount_exactly_as_stored` (`Theory`) : `"5000.00"`, `"5000"`, `"1234.5"`, `"1.10000000"` et `"99999999999999999999.99999999"` sont renvoyés sans aucune modification, en chaîne JSON ;
   - `Error_messages_name_parameters_as_exposed_by_the_api` : messages exacts ;
   - `Rejects_invalid_parameters` (`Theory`) : chaque paramètre manquant, vide, trop long, illisible (dont un `+` non encodé), hors bornes, ou `toCreatedAt <= fromCreatedAt`.

**Validation** : `dotnet test` vert ; appel `curl` sur la stack Docker Compose ; `EXPLAIN` montre l'utilisation de l'index, sans étape de tri.

### Étape 7 — `GET /v1/reconciliations/transactions/missings`

1. `GetMissingTransactionsRequest` et `GetMissingTransactionsValidator` : classes **indépendantes** de celles de l'étape 6 (choix de la référence : les contrats peuvent diverger), avec en plus `MissingsSince` et la règle `asOf > missingsSince` (`ApplyConditionTo.CurrentValidator`).
2. `GetMissingTransactionsEndpoint` : `CreatedBetween(...)` + `InsertedAt >= missingsSince && InsertedAt < asOf` + `ToTransactionsPageAsync(...)`. Même réponse (`GetTransactionsResponse`).
3. `GetMissingTransactionsEndpointTests`, adaptés de la référence :
   - seule la fenêtre `[missingsSince, asOf)` est renvoyée ; `missingsSince` inclus et `asOf` exclu, quel que soit l'offset ;
   - complément exact d'un parcours initial, sans doublon ni trou (initial + missings = parcours complet avec le dernier `asOf`) ;
   - plage de `createdAt` et tenant respectés ; pagination et tri ; défauts ; tenant inconnu ;
   - format JSON ; messages d'erreur (`missingsSince` / `asOf` manquants → deux erreurs, `'asOf' must be after 'missingsSince'.`) ; `Theory` des paramètres invalides.

**Validation** : `dotnet test` vert ; sur Docker Compose, scénario parcours initial → insertion tardive → `/missings` renvoie seulement la transaction tardive, et l'appel suivant renvoie une liste vide.

### Étape 8 — Documentation

1. `README.md` : présentation, stack, prérequis, lancement (tout en Docker, ou API en local avec la base en Docker), tests, migrations, modèle de données, structure, tableau des endpoints avec paramètres et exemples `curl`.
2. `CLAUDE.md` : conventions de la référence transposées (feature folders, un endpoint = une classe, tests d'intégration obligatoires, versions centralisées, `TimeProvider`, plages semi-ouvertes, `inserted_at` posé par la base, fixture partagée et tenant unique par test, messages camelCase, piège `.When(...)`, `offerId` identifiant de la transaction, unique dans toute la base, donc préfixé par test dans les tests, montant stocké et renvoyé en chaîne sans aucune conversion numérique, avec son format garanti par des `CHECK`, tri `createdAt` puis `offerId`).
3. `HOW-TO-USE-THE-API.md` (en anglais) : adaptation du `HOW-TO-USE-THE-API.md` de `dune-users-api`, avec les mêmes sections, le même walkthrough horaire et le même script `reconcile.sh`, transposés aux transactions (`fromCreatedAt` / `toCreatedAt`, clé `transactions`, `jq '.transactions[]'`). Le FAQ « dédupliquer sur `tenantUserId` » devient « dédupliquer sur `offerId` », et le tri départage les ex æquo par `offerId`.
4. Ajouter des « Notes d'implémentation » à ce plan pour consigner les écarts constatés.

## Critères d'acceptation globaux

- [x] `dotnet build` passe sans warning.
- [x] `dotnet test` passe ; les tests d'intégration tournent contre un PostgreSQL lancé par Testcontainers.
- [x] `docker compose up --build` démarre l'API et PostgreSQL ; `/health` → `200` avec `"database": "Up"` ; la migration est appliquée.
- [x] Les deux endpoints renvoient exactement le format de la spec (clés, ordre, `buyOrSell` et `amount` en chaînes, dates UTC).
- [x] L'enchaînement parcours initial → `/missings` renvoie chaque transaction exactement une fois.
- [x] Aucun secret réel versionné (`.env` ignoré, seul `.env.example` commité).
- [x] Le README permet à un nouveau développeur de lancer l'API et les tests sans aide.

## Notes d'implémentation

Écarts et ajustements constatés pendant l'implémentation (2026-11-20) :

- **Versions** : identiques à la référence (voir « Choix techniques »). Migration unique `CreateTransactionsTable`.
- **Bug de la référence corrigé : messages de validation traduits.** FluentValidation traduit ses messages selon la culture du serveur. Sur un poste configuré en français, `'tenantId' must not be empty.` devenait `'tenantId' ne doit pas être vide.`, alors que le contrat documenté est en anglais ; dans le conteneur (culture invariante), les messages restaient en anglais. `Program.cs` fixe désormais `ValidatorOptions.Global.LanguageManager.Culture` à la culture invariante. `dune-users-api` a le même défaut : ses 2 tests `Error_messages_name_parameters_as_exposed_by_the_api` échouaient sur ce poste ; corrigé depuis dans la référence (révision du plan 03 de `dune-users-api`).
- **Bug de la référence corrigé : date vide lue comme `0001-01-01`.** Un paramètre de date présent mais vide (`asOf=`, `fromCreatedAt=`) n'était pas signalé comme absent : FastEndpoints le liait à `default(DateTimeOffset)`. Conséquences : `fromCreatedAt=` renvoyait tout l'historique, `missingsSince=` aussi (d'où des doublons), et `asOf=` une liste vide. `Program.cs` enregistre un parseur `DateTimeOffset?` qui traite une valeur vide ou blanche comme absente, ce qui donne `400` avec `'<param>' must not be empty.`. Les autres valeurs sont lues comme avant (culture invariante, date sans offset lue dans le fuseau du serveur). Constaté aussi sur la stack `dune-users-api` en cours d'exécution (`asOf=` → `200` et liste vide) ; corrigé depuis dans la référence. Tests de non-régression ajoutés dans les deux classes de tests d'endpoints.
- **`offerId` dans les tests** : `TestTransactions.New(tenantId, label, createdAt, …)` construit `offerId = {tenantId}/{label}`, et l'extension `Labels(tenantId)` retire ce préfixe pour comparer les réponses aux labels. Sans `label`, l'`offerId` est un GUID.
- **Contraintes sur `amount`** : PostgreSQL signale la première contrainte violée dans l'ordre des noms. Une valeur vide viole les deux contraintes et est donc signalée par `ck_transactions_amount_format`. Les tests vérifient le nom de la contrainte pour chaque cas.
- **Tests ajoutés par rapport au plan** : `Returns_all_fields_of_transaction`, `Rejects_unencoded_plus_in_dates`, `Stores_buy_or_sell_as_its_name`. Au total, 118 tests, tous verts.
- **Validation Docker Compose** : la stack `dune-users-api` occupait les ports `8080` / `5432`. La validation a donc été faite sur une stack isolée (`docker compose -p dune-tx-check`, `API_PORT=18080`, `POSTGRES_PORT=15432`, volume neuf), supprimée ensuite. Résultats :
  - migration appliquée et table conforme (`\d transactions`) ; `/health` → `200`, puis `503` (`"database": "Down"`) une fois Postgres arrêté ;
  - scénario parcours initial → insertion tardive → `/missings` (seule la transaction tardive) → `/missings` suivant (liste vide) ;
  - `EXPLAIN` : `Index Scan` sur `ix_transactions_tenant_id_created_at_offer_id`, sans étape de tri ; la fenêtre d'insertion est évaluée en `Filter` ;
  - le walkthrough de `HOW-TO-USE-THE-API.md` a été rejoué avec ses données (`inserted_at` forcés en SQL) et le script `reconcile.sh` extrait du guide : sorties identiques à celles documentées.

## Décisions

Prises le 2026-11-20 :

1. **Nom JSON de la date** : `transactionCreatedAt` dans la réponse, comme dans l'exemple de la spec. Dans le modèle, la date s'appelle `createdAt` / `created_at`, et les filtres `fromCreatedAt` / `toCreatedAt`.
2. **`amount`** : stocké en **chaîne** (`varchar(29)`) et renvoyé tel quel, sans conversion numérique nulle part. Jusqu'à 8 décimales, format contrôlé par une contrainte `CHECK` à base d'expression régulière.
3. **Identifiant de la transaction** : `offerId`, unique **dans toute la base**, indépendamment du `tenantId` (index unique sur `offer_id` seul). Il départage les ex æquo du tri, et c'est sur lui que l'appelant déduplique.
4. **Signe du montant** : strictement positif, le sens étant porté par `buyOrSell`. Comme le montant est une chaîne, cette règle est vérifiée par expression régulière (pas de signe, au moins un chiffre non nul), et non par une comparaison numérique.
5. **Valeurs de `buyOrSell`** : exactement `Buy` ou `Sell` (`CHECK`, sensible à la casse).
6. **Ports par défaut** : `8080` / `5432` / `5080`, comme la référence ; on peut les changer via `.env`.
7. **`HOW-TO-USE-THE-DUNE-USERS-API.md`** : supprimé du projet, car il était identique au `HOW-TO-USE-THE-API.md` de `dune-users-api`, qui sert de base à l'étape 8.
