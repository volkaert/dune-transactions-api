Le projet `dune-transactions-api` est une API en C# .NET 10 qui propose des opérations permettant par exemple de récupérer la liste des transactions du jour.

Ce projet doit utiliser:
- C# .NET 10
- FastEndpoints comme librairie d'API
- PostgreSQL comme base de données
- DockerCompose pour gérer ensemble l'API et la base de données

Pour les tests des endpoints de l'API:
- Runner : xUnit (ou xUnit.v3 pour les nouveautés .NET récentes)
- Intégration API : FastEndpoints.Testing
- Assertions : Shouldly ou AwesomeAssertions (pour une syntaxe fluide et lisible)
- Base de données / Infrastructure : Testcontainers (pour faire tourner une vraie instance PostgreSQL, Redis ou RabbitMQ en Docker pendant les tests, parfaitement supporté par l'AppFixture de FastEndpoints).

Cette API (et ce projet) ressemble beaucoup à l'API `dune-users-api` qui est décrite ici:
- dans mon repo GitHub ici: https://github.com/volkaert/dune-users-api
- sur mon disque local ici: /Users/fabrice/dev/projects/dune-users-api
- dans le fichier HOW-TO-USE-THE-DUNE-USERS-API.md qui est présent à la racine du projet courant

Utilise donc bien les mêmes outils et les mêmes choix de conception pour l'API `dune-transactions-api` (de ce projet) que ceux utilisés pour l'API `dune-users-api`.

Les différences entre l'API `dune-transactions-api` (de ce projet) et l'API `dune-users-api` sont:
- les users onboardés sont remplacés par des transactions
- les endpoints s'appellent `/v1/reconciliations/transactions` et `/v1/reconciliations/transactions/missings`
- la donnée `onboardedAt` est remplacée par `createdAt`
- les paramètres de filtre `fromOnboardedAt` et `toOnboardedAt` sont remplacés par `fromCreatedAt` et `toCreatedAt`

Les endpoints `/v1/reconciliations/transactions` et `/v1/reconciliations/transactions/missings` doivent retourner des pages de la forme: 
```json
{
    "transactions": [
        {
            "tenantUserId": "<tenantUserId>",
            "offerId": "<offerId>",
            "buyOrSell": "Buy|Sell",
            "amount": "5000.00",
            "amountCcy": "EUR",
            "accountAlias": "123-abc-567",
            "transactionCreatedAt": "<CreatedAt>",
        }
    ]
}  
```