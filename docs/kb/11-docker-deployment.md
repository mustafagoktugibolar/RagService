# Bölüm 11 — Docker ve Dağıtım

**Durum:** ✅ Implemented (Compose, çok aşamalı Dockerfile'lar, E2E bindirmesi) · 🔵 Kapsam dışı (Kubernetes manifest'leri, CI'dan imaj yayını, gizli yönetimi servisi)
**Ana dosyalar:** `compose.yaml`, `compose.infra.yaml`, `compose.e2e.yaml`, `src/*/Dockerfile`, `.env.template`, `.dockerignore`

## 11.1 Dosyalar

| Dosya | Amaç |
|---|---|
| `compose.yaml` | Tam yığın: RabbitMQ, Qdrant, PostgreSQL(PgVector), SeaweedFS, Redis, Tempo, Prometheus, Grafana + `rag.api`, `rag.queryworker`, `rag.indexworker` (+ `seed` profili: Wikipedia seeder) |
| `compose.infra.yaml` | Yalnızca altyapı — projeleri IDE'den çalıştırmak için |
| `compose.e2e.yaml` | `scripts/e2e.py` için bindirme: uygulama konteynerlerinde `.env`'i **değiştirir** (Hashing embedding, anahtar yok) |
| `.env.template` → `.env` | Yapılandırma (git'te yok). `__` ayırıcı: `RAG__VECTORSTOREPROVIDER`, `RETRIEVAL__STRATEGY` |

## 11.2 Çalıştırma

```bash
cp .env.template .env            # OPENAI__APIKEY'i doldurun (veya RAG__EMBEDDINGPROVIDER=Hashing ile yalnızca yerel deneyin)
bash scripts/download-reranker-model.sh   # yalnızca Provider=Onnx kullanacaksanız (SHA-256 doğrulamalı)
docker compose up -d
# API: http://localhost:8080  (Scalar: /scalar/v1)   Grafana: :3000   RabbitMQ UI: :15672
```

Ücretsiz, tam doğrulama: `python3 scripts/e2e.py` (49 kontrol; rapor `docs/release/e2e-report.md`). Bu betik `.env` dosyasını **okumaz** ve uygulama konteynerlerine ulaştırmaz.

## 11.3 Dockerfile'lar

Çok aşamalı: `base` (runtime) → `build` (SDK, `restore` katmanı proje dosyalarıyla önbelleklenir) → `publish` → `final`. `Rag.Api`, `Rag.IndexWorker` ve `Rag.QueryWorker` imajlarında `USER $APP_UID` vardır (root değil); tek seferlik `Rag.WikipediaSeeder` imajında yoktur. `Rag.Api` ve `Rag.QueryWorker`, `Rag.Reranking.Onnx` projesine referans verir (ONNX Runtime yerel kütüphaneleri imaja girer); **model dosyası imajda değildir**, `./models` klasörü `/app/models` olarak **salt-okunur** bağlanır. Reranking kapalıyken bu proje hiçbir şey yüklemez.

## 11.4 Sağlık ve başlatma sırası

SeaweedFS, Redis, RabbitMQ `healthcheck` tanımlıdır; worker'lar `depends_on: condition: service_healthy` ile bunları bekler. **Düzeltilen hata:** SeaweedFS sağlık kontrolü `localhost` (IPv6) yerine `127.0.0.1` kullanmalıydı (Bölüm 9). Qdrant için `service_started` yeterli sayılır (worker'lar bağlantıyı yeniden dener). Worker'ların kendi sağlık ucu yoktur.

## 11.5 Gizli bilgi (secret) yönetimi

- `.env` `.gitignore`'dadır; **şablonda anahtar yoktur** (`OPENAI__APIKEY=`). CI'da `secrets-scan` işi izlenen dosyalarda anahtar benzeri dizgi arar.
- Anahtar yalnızca ortam değişkeni olarak konteynere girer; hiçbir log/hata mesajı değerini yazmaz.
- Compose'daki `guest/guest`, `rag/rag`, `ragaccess/ragsecret` **yerel geliştirme değerleridir**. Üretimde: sır yöneticisi (Docker/K8s secrets, Vault), ağ politikası, kimlik doğrulamalı Qdrant (`QDRANT__APIKEY`), TLS.

## 11.6 Bilinen sınırlar

- Qdrant ve Tempo/Prometheus/Grafana `latest` etiketli (yeniden üretilebilirlik için sabitleyin).
- API'de kimlik doğrulama yok (Bölüm 13) — ağa açmayın.
- Tek düğüm; yatay ölçekleme için worker replikası artırılabilir (kuyruk rekabetçi tüketici), ancak bu **yük altında test edilmedi**.
- İmaj yayını, vulnerability taraması, SBOM yok.
