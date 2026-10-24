# Bölüm 9 — Depolama Sistemleri

**Durum:** ✅ Implemented (S3-uyumlu depo, Local depo, Qdrant, PgVector, Redis)
**Ana dosyalar:** `Providers/Storage/{S3BlobStorage,LocalBlobStorage}.cs`, `Providers/VectorStores/*`, `compose.yaml`, `Options/{S3,Qdrant,PgVector,LocalStorage,Redis}Options.cs`

## 9.1 Hangi veri nerede? (kalıcı vs türetilmiş)

| Veri | Nerede | Kalıcı mı? | Kaybolursa |
|---|---|---|---|
| **Ham doküman** | Nesne deposu (SeaweedFS/MinIO/S3) veya yerel dizin | **Evet — doğruluk kaynağı** | Geri getirilemez |
| **Chunk + vektör + metadata** | Qdrant veya PgVector | **Türetilmiş** | Ham dokümandan yeniden indexlenir |
| **Koleksiyon metadata'sı** (`provider/model/size/IndexVersion`) | Qdrant `rag_collection_metadata` koleksiyonu veya PG tablosu | Türetilmiş | Yeniden indexleme |
| **Embedding cache, sorgu cache, rewrite cache** | Redis | **Hayır** (TTL'li) | Yalnızca maliyet/gecikme |
| **Mesajlar** | RabbitMQ (durable kuyruk) | Geçici | İstemci yeniden gönderir |

**Kural:** yalnızca ham doküman değerlidir; geri kalan her şey ondan yeniden üretilebilir. Bu yüzden vektör deposu değişimi/göçü "yeniden indexleme"dir, veri göçü değil.

## 9.2 Nesne deposu

Tek `S3BlobStorage` **her S3-uyumlu depoya** hizmet eder (`Rag:StorageProvider = S3`; `Minio`/`SeaweedFS` takma ad). Varsayılan SeaweedFS 4.48 (`server -s3`). Değiştirmek yalnızca `S3__SERVICEURL/ACCESSKEY/SECRETKEY/BUCKET` değerleridir. `ForcePathStyle=true` SeaweedFS/MinIO için gereklidir. `Local` sağlayıcı geliştirme içindir. Çağrılar `Storage` resilience pipeline'ından geçer (Bölüm 10); eksik bucket `CreateBucketIfMissing` ile oluşur.

**Gerçek bir hata bu bölümde bulundu (V1 doğrulaması):** SeaweedFS `healthcheck`'i `http://localhost:8333/healthz` kullanıyordu; konteyner içinde `localhost` IPv6 `::1`'e çözülür, S3 ise yalnızca IPv4'te dinler → konteyner sonsuza dek `unhealthy` kalıyor ve `depends_on: service_healthy` olan worker'lar **hiç başlamıyordu**. `127.0.0.1` ile düzeltildi (`compose.yaml`).

## 9.3 Vektör depoları

Ayrıntı Bölüm 4. Özet: **Qdrant** (ANN/HNSW, sparse vektör → hybrid) ve **PgVector** (tam tarama, SQL ile birleştirilebilir, hybrid yok). Seçim `Rag:VectorStoreProvider`. İkisi de aynı `IVectorStore` sözleşmesini uygular ve `Rag.IntegrationTests` ile gerçek kapsayıcılarda sınanır.

## 9.4 Redis

Üç ayrı kullanım, **hepsi atılabilir**: embedding cache (`rag:emb:*`), sorgu cache (`rag:query:v2:*`), rewrite cache (`rag:rewrite:*`). Compose'da `appendonly yes` açık (yeniden başlatmada cache korunur) — ama **doğruluk bunlara bağlı değildir**. Redis erişilemezse rewrite cache sessizce atlanır (test: `ACacheOutage_DoesNotBreakRewriting`).

## 9.5 Trade-off'lar

| Karar | Artısı | Eksisi |
|---|---|---|
| Qdrant ayrı servis | ANN, sparse, ölçek | Ek operasyon yükü |
| PgVector | Tek veritabanı, SQL | ANN indeksi yok (bu repoda) |
| Nesne deposu soyutlaması | Sağlayıcı bağımsız | Yalnızca S3 API'sinin ortak alt kümesi |
| Türetilmiş veri | Basit kurtarma | Büyük korpusta yeniden indexleme maliyeti (embedding ücreti) |

## 9.6 Ne ters gidebilir?

- Qdrant `latest` imajı sabitlenmemiş: sürüm değişince davranış değişebilir (üretimde sabitleyin).
- SeaweedFS tek düğüm, replikasyon yok (yerel/geliştirme topolojisi).
- Compose'daki varsayılan parolalar (`rag/rag`, `ragaccess/ragsecret`, RabbitMQ `guest/guest`) **yalnızca yerel geliştirme** içindir (Bölüm 11, 13).
