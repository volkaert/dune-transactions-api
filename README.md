# dune-transactions-api

API C# .NET 10 (FastEndpoints + PostgreSQL) exposant les opérations de réconciliation des transactions : récupération des transactions créées sur une plage de dates, puis des transactions enregistrées en retard.

Le projet reprend la stack, l'organisation et les conventions de `dune-users-api`.

## Stack

- .NET 10, [FastEndpoints](https://fast-endpoints.com)
- PostgreSQL 18, EF Core (Npgsql)
- Docker Compose (API + base)
- Tests : xUnit v3, FastEndpoints.Testing, Shouldly, Testcontainers

## Prérequis

- SDK .NET 10 (version figée dans `global.json`)
- Docker (Docker Desktop ou équivalent), **démarré** : nécessaire pour Docker Compose et pour les tests

## Lancer l'API

### Tout en Docker

```bash
cp .env.example .env   # optionnel : valeurs par défaut utilisées sinon
docker compose up --build
curl http://localhost:8080/health
# {"status":"Healthy","database":"Up"}
```

Les ports par défaut (`8080` pour l'API, `5432` pour PostgreSQL) sont ceux de `dune-users-api`. Pour faire tourner les deux stacks en même temps, changer `API_PORT` et `POSTGRES_PORT` dans `.env`.

### API en local, base en Docker

```bash
docker compose up -d postgres
dotnet run --project src/DuneTransactionsApi
curl http://localhost:5080/health
```

En environnement `Development`, les migrations EF Core sont appliquées automatiquement au démarrage.

## Lancer les tests

```bash
dotnet test
```

Les tests d'intégration démarrent l'API en mémoire (`AppFixture` de FastEndpoints) et une vraie instance PostgreSQL via Testcontainers. Docker doit tourner.

## Migrations EF Core

`dotnet-ef` est déclaré comme outil local (`dotnet-tools.json`) :

```bash
dotnet tool restore
dotnet ef migrations add <NomDeLaMigration> \
  --project src/DuneTransactionsApi \
  --output-dir Infrastructure/Persistence/Migrations
```

Les tables, colonnes, index et contraintes sont nommés en snake_case (`EFCore.NamingConventions`).

## Modèle de données

Table `transactions` :

| Colonne | Type | Contraintes |
|---|---|---|
| `id` | `uuid` | Clé primaire technique |
| `tenant_id` | `varchar(100)` | |
| `offer_id` | `varchar(100)` | **Unique dans toute la base** (`ux_transactions_offer_id`) : identifiant de la transaction, indépendant du tenant |
| `tenant_user_id` | `varchar(100)` | Un user peut avoir plusieurs transactions |
| `buy_or_sell` | `varchar(4)` | `Buy` ou `Sell` exactement |
| `amount` | `varchar(29)` | **Chaîne**, jamais convertie : décimal strictement positif, séparateur `.`, 8 décimales au plus, sans signe ni exposant (contraintes `ck_transactions_amount_format` et `ck_transactions_amount_positive`) |
| `amount_ccy` | `char(3)` | Code ISO 4217 en majuscules |
| `account_alias` | `varchar(100)` | |
| `created_at` | `timestamptz` | Date métier de la transaction |
| `inserted_at` | `timestamptz` | `DEFAULT now()`, posée par PostgreSQL et ignorée par EF Core à l'écriture |

Les dates sont stockées en `timestamptz` : l'instant est conservé, mais pas l'offset d'origine (relu en UTC). L'index `ix_transactions_tenant_id_created_at_offer_id` (`INCLUDE (inserted_at)`) sert les deux endpoints de réconciliation, sans étape de tri.

## Structure

```
src/DuneTransactionsApi/
  Features/<Feature>/              # un dossier par feature : endpoints, requêtes, réponses, validateurs
  Infrastructure/Persistence/      # AppDbContext, migrations
    Transactions/                  # entité, enum BuyOrSell et configuration EF Core
tests/DuneTransactionsApi.Tests/
  Fixtures/ApiFixture.cs           # API + PostgreSQL (Testcontainers) + horloge de test
  Fixtures/TestTransactions.cs     # fabrique et insertion de transactions de test
  Features/<Feature>/              # tests d'intégration par feature
  Infrastructure/Persistence/      # tests du mapping EF Core
```

## Endpoints

Guide d'utilisation complet, avec exemples et enchaînement horaire des appels à `/missings` : [HOW-TO-USE-THE-API.md](HOW-TO-USE-THE-API.md) (en anglais).

| Méthode | Route | Description |
|---|---|---|
| GET | `/health` | `200` si l'API et la base sont disponibles, `503` si la base est injoignable |
| GET | `/v1/reconciliations/transactions` | Transactions d'un tenant créées sur une plage de dates (paginé) |
| GET | `/v1/reconciliations/transactions/missings` | Transactions de la même plage insérées en base après un parcours précédent (paginé) |

### Format de réponse

```json
{
  "transactions": [
    {
      "tenantUserId": "alice",
      "offerId": "offer-101",
      "buyOrSell": "Buy",
      "amount": "5000.00",
      "amountCcy": "EUR",
      "accountAlias": "123-abc-567",
      "transactionCreatedAt": "2026-11-19T08:00:00+00:00"
    }
  ]
}
```

- `amount` est renvoyé exactement tel qu'il est stocké (`"5000"` reste `"5000"`).
- `transactionCreatedAt` est la date `createdAt`, en UTC.
- Tri par `createdAt` puis `offerId` (simple départage des ex æquo, sans ordre métier).

### `GET /v1/reconciliations/transactions`

Renvoie les transactions d'un tenant dont `createdAt` est dans `[fromCreatedAt, toCreatedAt)` (début inclus, fin exclue) et insérées en base avant `asOf` (exclu).

| Paramètre | Obligatoire | Défaut | Règles |
|---|---|---|---|
| `tenantId` | oui | — | 100 caractères max |
| `fromCreatedAt` | oui | — | Date ISO 8601 avec offset, borne incluse |
| `toCreatedAt` | oui | — | Date ISO 8601 avec offset, borne exclue, après `fromCreatedAt` |
| `asOf` | oui | — | Date ISO 8601 avec offset, fournie par l'appelant, borne **exclue** : seules les transactions insérées en base avant cet instant sont renvoyées |
| `page` | non | `1` | Entre 1 et 1 000 000 |
| `pageSize` | non | `1000` | Entre 1 et 1000 |

```bash
curl -G "http://localhost:8080/v1/reconciliations/transactions" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "asOf=2026-11-20T06:00:00Z"
```

- Une page de moins de `pageSize` éléments (éventuellement vide) est la dernière.
- Parcours stable : fixer `asOf` au début du parcours et passer la même valeur pour toutes les pages. Choisir `asOf` légèrement dans le passé (ex. maintenant moins une minute) pour absorber un décalage d'horloge avec la base et les insertions en cours.
- Dans une query string, `+` est décodé en espace : encoder les offsets positifs (`%2B`) ou utiliser `Z`. Une date non encodée est rejetée (`400`).
- Une date sans offset est interprétée dans le fuseau du serveur : toujours préciser l'offset.
- Paramètre manquant, vide ou invalide : `400` avec le détail des erreurs par paramètre (messages en anglais).

### `GET /v1/reconciliations/transactions/missings`

Renvoie les transactions qu'un parcours de `/v1/reconciliations/transactions` n'a pas pu voir : celles de la plage `[fromCreatedAt, toCreatedAt)` insérées en base dans `[missingsSince, asOf)`. Même format de réponse, même tri, même pagination.

Mêmes paramètres que `/v1/reconciliations/transactions`, avec les mêmes règles, plus :

| Paramètre | Obligatoire | Défaut | Règles |
|---|---|---|---|
| `missingsSince` | oui | — | Date ISO 8601 avec offset, borne **incluse**. Normalement le `asOf` du parcours précédent |
| `asOf` | oui | — | Borne **exclue**, strictement après `missingsSince` |

```bash
curl -G "http://localhost:8080/v1/reconciliations/transactions/missings" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "missingsSince=2026-11-20T06:00:00Z" \
  --data-urlencode "asOf=2026-11-20T07:00:00Z"
```

Enchaîner les appels en passant à chaque fois le `asOf` précédent comme `missingsSince` : l'ensemble des réponses contient toutes les transactions de la plage, chacune une seule fois.
