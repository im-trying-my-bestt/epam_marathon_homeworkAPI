# Seed data

Початкові дані для таблиць DynamoDB `Locations` та `Dishes` (формат `batch-write-item`).

```bash
aws dynamodb batch-write-item --request-items file://seed-data/locations.json --region us-west-2
aws dynamodb batch-write-item --request-items file://seed-data/dishes.json --region us-west-2
```

PowerShell (шлях до файлу з префіксом `file://`):

```powershell
aws dynamodb batch-write-item --request-items file://seed-data/locations.json --region us-west-2
aws dynamodb batch-write-item --request-items file://seed-data/dishes.json --region us-west-2
```

Примітка: `imageUrl` у `locations.json` вказує на S3-бакет `restaurant-backend-dev-images-747482682250`.
Для власного акаунта замініть ID акаунта в URL на свій.
