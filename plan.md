# Definition Seeder Plan

## Amaç

Layout (ekran tanımları) ve SQL sorgularını saatte bir Azure OpenAI ile yorumlayıp doğal dil
açıklamasına çevir, RAG sistemine index'le. Kullanıcılar "parent-child grid", "parametreli
sql sorgusu" gibi doğal dil sorguları ile örnek bulabilsin.

---

## Koleksiyonlar

| Koleksiyon | İçerik | Örnek Sorgu |
|---|---|---|
| `layouts` | Ekran JSON'larından üretilen açıklamalar | "grid with cell that opens form in new window" |
| `sqls` | SQL sorgularından üretilen açıklamalar | "AircraftTypeGroupCode parametresi alan sorgu" |

---

## Yeni Proje: `Rag.DefinitionSeeder`

`Microsoft.NET.Sdk.Worker` — sürekli çalışan background service, saatte bir tetiklenir.
Compose'a normal servis olarak eklenir (`profiles` yok).

### Çalışma döngüsü

```
Başlangıç
    ↓
Redis'ten lastRunAt oku (yoksa epoch)
    ↓
Her saat:
    API'den updatedAfter=lastRunAt ile değişen kayıtları çek
    Her kayıt için:
        LLM → açıklama üret
        MinIO'ya yaz (üzerine yaz, upsert)
        RabbitMQ'ya publish (IndexDocumentRequest)
    Redis'e lastRunAt = şimdi yaz
    ↓
PeriodicTimer (1 saat) bekle → tekrar
```

---

## Artımlı Güncelleme Stratejisi

- **Cursor:** `lastRunAt` timestamp'ı Redis'te saklanır (`definitions:lastrun:layouts`, `definitions:lastrun:sqls`)
- **API filtresi:** `updatedAfter={lastRunAt}` query parametresi ile sadece değişenler çekilir
- **MinIO:** Aynı key'e overwrite (upsert) — IndexWorker Qdrant'ta da upsert yapar, duplikat olmaz
- **İlk çalışma:** `lastRunAt` yoksa tüm kayıtlar çekilir (full sync)

---

## Azure OpenAI Kullanımı

Ekstra paket gerekmez — `Rag.Core` proje referansı ile `Azure.AI.OpenAI` ve `OpenAI` zaten gelir.

```csharp
var client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
var chat = client.GetChatClient(deployment);
var response = await chat.CompleteChatAsync(prompt);
var description = response.Value.Content[0].Text;
```

### Layout için prompt

```
Analyze the following UI screen definition JSON and describe its features in both
Turkish and English. Cover:
- Overall purpose and layout structure
- Each grid/component's columns and features (filtering, search, sorting, column chooser, etc.)
- Inter-component relationships (parent-child, dependent loading)
- User interaction patterns
- Data source info (which SQL, which endpoint)

Screen Name: {name}
JSON:
{json}
```

### SQL için prompt

```
Analyze the following SQL query and describe it in both Turkish and English. Cover:
- What it does (which tables, what it returns)
- Parameters and their types
- JOIN relationships
- Filter conditions
- Sorting / grouping

SQL Name: {sqlName}
SQL:
{sql}
```

---

## Proje Yapısı

```
src/Rag.DefinitionSeeder/
  Program.cs                    ← Host kurulumu, her iki worker register
  LayoutSyncWorker.cs           ← BackgroundService, PeriodicTimer(1h)
  SqlSyncWorker.cs              ← BackgroundService, PeriodicTimer(1h)
  AzureOpenAiDescriber.cs       ← Chat completion (Azure.AI.OpenAI SDK)
  SourceApiClient.cs            ← Layout/SQL API client (sayfalı çekme)
  appsettings.json              ← Explicit CopyToOutputDirectory
  Dockerfile
```

---

## Config (`appsettings.json`)

```json
{
  "AzureOpenAI": {
    "Endpoint": "",
    "ApiKey": "",
    "ChatDeployment": "gpt-4o-mini"
  },
  "Minio": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "Bucket": "rag-documents"
  },
  "RabbitMQ": {
    "HostName": "kubernetes.docker.internal",
    "Port": 5672,
    "UserName": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "QueueName": "rag.index",
    "ConnectionRetryDelaySeconds": 5
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  "SourceApi": {
    "BaseUrl": "https://...",
    "ApiKey": "",
    "LayoutsEndpoint": "/api/v1/layouts",
    "SqlsEndpoint": "/api/v1/sqls",
    "PageSize": 50
  },
  "Seeder": {
    "LayoutCollection": "layouts",
    "SqlCollection": "sqls",
    "SyncIntervalHours": 1
  }
}
```

---

## MinIO Dosya Yapısı

```
rag-documents/
  layouts/{screenName}.txt     ← LLM'in ürettiği açıklama
  sqls/{sqlName}.txt           ← LLM'in ürettiği açıklama
```

## IndexDocumentRequest Metadata

**Layouts:**
```json
{
  "metadata": {
    "type": "layout",
    "screenName": "Aircraft Type Management",
    "clientType": "web",
    "version": "5"
  }
}
```

**SQLs:**
```json
{
  "metadata": {
    "type": "sql",
    "sqlName": "SelectAircraftTypeGroup",
    "dataSourceName": "default"
  }
}
```

---

## Compose

```yaml
rag.definition-seeder:
  image: rag.definition-seeder
  build:
    context: .
    dockerfile: src/Rag.DefinitionSeeder/Dockerfile
  env_file:
    - .env
  depends_on:
    rabbitmq:
      condition: service_healthy
    minio:
      condition: service_healthy
    redis:
      condition: service_healthy
```

---

## Yapılacaklar

- [ ] `src/Rag.DefinitionSeeder/` projesi oluştur (`Microsoft.NET.Sdk.Worker`)
- [ ] `SourceApiClient.cs` — sayfalı çekme, `updatedAfter` filtresi
- [ ] `AzureOpenAiDescriber.cs` — HttpClient ile chat completion
- [ ] `LayoutSyncWorker.cs` — PeriodicTimer, Redis cursor, layouts sync
- [ ] `SqlSyncWorker.cs` — PeriodicTimer, Redis cursor, sqls sync
- [ ] `appsettings.json` — config (appsettings.json auto-copied, Worker SDK)
- [ ] `Dockerfile` — WikipediaSeeder'dan kopyala
- [ ] `compose.yaml`'a servis ekle
- [ ] `.env.template`'e `SOURCEAPI__BASEURL`, `SOURCEAPI__APIKEY`, `AZUREOPENAI__CHATDEPLOYMENT` ekle
