# Bölüm 10 — Resilience ve Observability

**Durum:** ✅ Implemented (resilience pipeline'ları, OpenTelemetry metrik/iz, Prometheus, Tempo, Grafana veri kaynakları, sağlık uçları) · 🟡 Partial (hazır Grafana **dashboard'u yok**; worker'larda sağlık ucu yok; alarm kuralı yok)
**Ana dosyalar:** `src/Rag.Core/Resilience/*`, `src/Rag.Core/Telemetry/RagTelemetry.cs`, `src/Rag.Core/Extensions/OpenTelemetryExtensions.cs`, `src/Rag.Api/Program.cs`, `infra/*`
**Testler:** `tests/Rag.Core.Tests/Resilience/*` · **E2E:** `scripts/e2e.py` (sağlık, Prometheus hedefi, Tempo izleri, Qdrant kesintisi ve toparlanma)

## 10.1 Resilience: üç savunma, tek zincir

Dış bir çağrı (OpenAI, Qdrant, S3, rerank sağlayıcısı) üç mekanizmayla sarılır — **bağımlılık eklenmeden, repoda yazılı** (`Rag.Core/Resilience`; Polly kullanılmıyor):

| Mekanizma | Ne yapar | Ayar (ExternalApi / Qdrant / Storage) |
|---|---|---|
| **Retry** | Geçici hatada tekrar dener; üstel geri-çekilme + **jitter** | 3 deneme, 1 sn tabanlı |
| **Circuit breaker** | Hata oranı eşiği aşınca **hızlı reddeder** (`BrokenCircuitException`); `HalfOpen`'da **tek** sonda çağrısı | %50 hata, örnekleme 60/30/30 sn, min 5/3/3 çağrı, açık kalma 30/20/20 sn |
| **Timeout** | Deneme başına süre sınırı | 60/30/30 sn |

**Neden breaker?** Çöken bir bağımlılığa her istek 30 sn bekleyip düşerse thread/bağlantı tükenir ve çöküş yayılır. Breaker açıkken çağrı anında düşer; bağımlılık toparlanınca tek bir sonda ile kapanır. **Neden jitter?** Birçok worker aynı anda aynı saniyede tekrar denerse toparlanan servisi yeniden ezer.

Yalnızca **geçici** hatalar yeniden denenir (`IsTransientHttp/Grpc/Storage`: ağ, 408/429/5xx, gRPC `Unavailable`…). Kalıcı hata (400, kimlik doğrulama) denenmez. Her rerank sağlayıcısı **kendi** breaker'ına sahiptir (`ForRerankProvider`, en çok 2 deneme — gecikme bütçesi için).

### İsteğe bağlı aşamalarda fallback politikası

| Aşama | Hata/zaman aşımı olursa | Cache'lenir mi? |
|---|---|---|
| Query rewriting | **Orijinal sorgu** ile devam; uyarı `rewrite:rejected/failed/timeout` | **Hayır** (bozulmuş sonuç kilitlenmez) |
| Reranking | `FailurePolicy=Fallback`: retrieval sırası; uyarı `rerank:failed/timeout`. `Fail`: sorgu hata verir | Hayır |
| Hybrid bacağı | Çöken bacak atlanır, diğeriyle devam; uyarı `hybrid:*-leg-failed`. İkisi de çökerse hata | Hayır |

Ortak ilke: **bozulmuş (degraded) sonuç cache'e yazılmaz** ve `QueryResponse.Warnings` ile istemciye bildirilir.

## 10.2 Gözlemlenebilirlik: log, metrik, iz

| Sinyal | Uygulama | Nerede |
|---|---|---|
| **Log** | Serilog, compact JSON, `Service`/`ServiceVersion` alanları | stdout |
| **Metrik** | OpenTelemetry Meter `Rag.Service` → Prometheus (`/metrics`, API'de) | Prometheus :9090, Grafana :3000 |
| **İz** | `ActivitySource` (`rag.query`, `rag.index`) + ASP.NET/HTTP instrumentation → OTLP → Tempo | Tempo :3200 (Grafana'dan) |

Özel metrikler (Prometheus adı `_total` ekli): `rag.documents.indexed`, `rag.index.errors`, `rag.queries.executed` (`cache_hit` etiketi), `rag.query.errors`, `rag.embedding.duration`, `rag.rerank.{requests,input_tokens,fallbacks,duration}`, `rag.rewrite.{requests,duration}` (`outcome`: `rewritten|unchanged|skipped|rejected|failed|timeout|cached`).

İzde `rag.query` span'ı: `rag.collection`, `rag.strategy` (etkin strateji + reranker + rewriter kimliği), `rag.result_count`, `rag.cache_hit`, `rag.rewrite.outcome` ve **sorgu hash'leri** (`rag.rewrite.query_hash`). **Sorgu metni span'a varsayılan olarak yazılmaz** (`RecordQueryText=false`): sorgular hassas olabilir.

**Hassas veri koruması:** hata mesajları API anahtarı değerini yazmaz (doğrulayıcı testi var); loglar sorgu metnini içermez (`request.Query` log çağrılarında yoktur, kod araması yapıldı); rewriter logları yalnızca istisna *türünü* yazar.

### Sağlık uçları

`/health/live` her zaman 200 (süreç yaşıyor). `/health/ready` Qdrant (`/healthz`) ve nesne deposunu (kimlik doğrulamalı tek S3 çağrısı) kontrol eder; bir bağımlılık düşerse hazır değil döner. E2E: Qdrant durdurulunca sorgu temiz hata verir, geri gelince **yeniden başlatmadan** toparlanır.

## 10.3 Doğrulanmış ve doğrulanmamış

| ✅ Gerçek altyapıda doğrulandı (`scripts/e2e.py`) | ❌ Doğrulanmadı / yok |
|---|---|
| Prometheus API hedefini scrape ediyor; Tempo iz alıyor | Worker'larda `/health` ucu ve Docker `HEALTHCHECK` **yok** |
| `rag_queries_executed_total{cache_hit="true"}` artıyor | Grafana'da **hazır dashboard yok** (yalnızca veri kaynakları) |
| Rewrite metrikleri (`rewritten/cached/skipped`) | Alarm kuralları (DLQ derinliği, breaker açık, hata oranı) yok |
| Qdrant kesintisi → temiz hata → toparlanma | Breaker açıkken worker'ın sıcak requeue döngüsü ölçülmedi (Bölüm 3) |
| Anahtarsız yapılandırma → açılışta durma | Sorgu başına toplam **deadline** yok: süre, bileşen zaman aşımlarının toplamı ile sınırlı |
| | Tüm başarısız sorgular HTTP **400** döner (altyapı hatası da); hata türü ayrımı yok |

Son satır gerçek bir sınırdır: `Rag.Api` `response.Success ? 200 : 400` döner. Qdrant kapalıyken E2E'de görülen kod 400'dür. İstemci için doğrusu 503 olurdu; yanıt sözleşmesinde hata türü olmadığı için bu V1'de **bilinen sınır** olarak bırakıldı.

## 10.4 Mülakatta nasıl anlatırım?

> "Her dış bağımlılık için retry + circuit breaker + timeout zinciri var; breaker açıkken hızlı düşer, jitter toparlanan servisi ezmez. İsteğe bağlı aşamalar (rewrite, rerank) başarısız olursa orijinal akışa düşer, uyarı döner ve bozulmuş sonuç cache'lenmez. Metrik, iz ve log var; sorgu metnini iz ve loglara yazmıyorum. Eksiklerimi biliyorum: worker sağlık ucu, dashboard, alarm ve toplam deadline yok."
