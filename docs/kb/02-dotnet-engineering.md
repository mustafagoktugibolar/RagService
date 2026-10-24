# Bölüm 2 — .NET Mühendisliği

**Durum:** ✅ Implemented (aşağıdaki her kod örneği repodan alınmıştır)
**Ana dosyalar:** `src/Rag.Core/Extensions/ServiceCollectionExtensions.cs`, `RerankingServiceCollectionExtensions.cs`, `QueryRewritingServiceCollectionExtensions.cs`, `src/Rag.QueryWorker/{Program,Worker}.cs`, `src/Rag.Core/Options/*`, `src/Rag.Core/Contracts/*`
**Testler:** `tests/Rag.Core.Tests/Rewriting/RetrievalStartupValidationTests.cs`, `tests/Rag.Core.Tests/Providers/Embeddings/HashingEmbeddingProviderTests.cs`

## 2.1 Nedir?

Bu bölüm, RagService'in **neden bu şekilde yazıldığını** .NET mühendisliği açısından anlatır: bağımlılık enjeksiyonu (DI), `BackgroundService` ile uzun ömürlü işçiler, `async`/`await` ve `CancellationToken`, strongly-typed `Options`, başlangıçta doğrulama ve `record` ile değişmez sözleşmeler.

## 2.2 Hangi problemi çözer?

| Problem | Bu repodaki cevap |
|---|---|
| Sağlayıcıyı (OpenAI/Azure, Qdrant/PgVector, S3/Local) değiştirmek kod değişikliği olmasın | Arayüz (`IEmbeddingProvider`, `IVectorStore`, `IBlobStorage`) + yapılandırmayla seçim |
| Yanlış yapılandırma ilk sorguda değil, **açılışta** patlasın | `ValidateOnStart` (`RetrievalOptionsValidator`) |
| Bir istek iptal edilince bütün zincir durmalı | `CancellationToken` her `async` metoda parametre olarak akar |
| Worker kapanırken yarım iş bırakılmasın | `BackgroundService` + `stoppingToken` |
| Üretim yolu deney koduna bağımlı olmasın | `Rag.Core` hiçbir şeye bağımlı değil; `Rag.Evaluation` `Rag.Core`'a bağımlı, tersi yok |

## 2.3 İçeride nasıl çalışır?

### Bağımlılık yönü

```
Rag.Api / Rag.IndexWorker / Rag.QueryWorker / Rag.WikipediaSeeder   (ince host'lar)
        │  AddRagCore(configuration)
        ▼
Rag.Core  (soyutlamalar, servisler, sağlayıcılar, resilience, telemetri)
        ▲
Rag.Reranking.Onnx  (isteğe bağlı: yerel cross-encoder)      Rag.Evaluation (+Cli)  (ölçüm; üretim yolunda YOK)
```

Host'lar neredeyse mantık içermez; gerçek davranış `IndexDocumentService` ve `VectorSearchService`'tedir. **Değerlendirme harness'i aynı `VectorSearchService`'i kullanır** (`DenseBaselineHarness.RetrieverFor`): üretimde çalışan kod ile ölçülen kod aynıdır. Fark yalnızca bağlanan sağlayıcılardır (bellek içi depo, hashing embedder).

### DI ve ömürler

`AddRagCore` şunları kaydeder:

- **Singleton:** metin çıkarıcılar, `IChunker`, `IBlobStorage` (S3 istemcisi), reranker'lar (circuit breaker ve eşzamanlılık sınırı **süreç boyunca paylaşılmalı**).
- **Transient:** `IndexDocumentService`, `VectorSearchService`, `IEmbeddingProvider`, `IVectorStore` — her istek (worker'da her mesaj için bir `scope`) kendi örneğini alır.

İsteğe bağlı aşamalar **koşullu kaydedilir**: `Retrieval:Reranking:Enabled=false` iken hiçbir reranker, kimlik bilgisi veya HTTP istemcisi kaydedilmez; `Retrieval:QueryRewriting:Enabled=false` iken de aynı. `VectorSearchService` bu aşamaları **null olabilen** bağımlılık olarak alır (`IReranker? reranker = null`, `QueryRewritingStage? rewriting = null`) — kapalı özellik sıfır maliyetlidir ve eski testler değişmeden çalışır.

```csharp
// VectorSearchService ctor (özet)
IOptions<RetrievalOptions>? retrievalOptions = null,
IReranker? reranker = null,
QueryRewritingStage? rewriting = null
```

### Options deseni ve başlangıç doğrulaması

Yapılandırma `appsettings.json` + ortam değişkenleri (`__` ayırıcı: `RETRIEVAL__STRATEGY=Hybrid`) ile gelir ve `IOptions<T>`'ye bağlanır. Kritik kısım: `RetrievalOptionsValidator` `ValidateOnStart` ile **host açılırken** çalışır. Reranking/rewriting açıkken aralık dışı değer, eksik kimlik bilgisi, HTTPS olmayan uç nokta veya kayıtsız sağlayıcı varsa **süreç başlamaz**. Hata mesajı kimlik bilgisinin **değerini yazmaz** (testle sabit: `OpenAiRewriting_RequiresAKeyAndAModel_AndNeverEchoesTheKey`). E2E'de doğrulandı: anahtarsız `OpenAiDecisions` ile `rag.api` ayağa kalkmaz.

### async / CancellationToken

Kural: **iptal bir hata değil, bir sinyaldir.** Servisler `OperationCanceledException`'ı yakalamaz, yeniden fırlatır (`catch (OperationCanceledException) { throw; }`); aksi halde iptal "başarısız sorgu" gibi raporlanırdı. Zaman aşımı ile çağıran iptalini ayırmak için:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // çağıran iptal etti
catch (OperationCanceledException) { /* kendi zaman aşımımız: fallback */ }
```

Bu desen `RerankingRetriever` ve `QueryRewritingStage`'te aynıdır. Tek kasıtlı istisna: worker, mesajı ack/nack ederken `CancellationToken.None` kullanır — kapanış sırasında bile mesajın durumunu brokera bildirmek gerekir.

### BackgroundService

`Worker : BackgroundService` bir RabbitMQ tüketicisi kurar, `stoppingToken` ile durur. Eşzamanlılık `SemaphoreSlim(Concurrency)` ile, ön-getirme (`prefetch`) `BasicQos` ile sınırlıdır (ayrıntı: Bölüm 3). Bağlantı kopukken sonsuz değil, `ConnectionRetryDelaySeconds` aralıklı yeniden dener.

### Sözleşmeler: `record` + `init`

`QueryRequest`, `QueryResponse`, `IndexDocumentRequest`… `sealed record` ve `init`-only özelliklerdir: değişmez, değer eşitliği olan, JSON'a (Web defaults) doğrudan çevrilebilen veri taşıyıcıları. `request with { RequestId = ... }` ile kopya üretilir (bkz. `Program.cs`).

## 2.4 Kodda nerede?

| Konu | Dosya |
|---|---|
| Kayıt ve sağlayıcı seçimi | `Extensions/ServiceCollectionExtensions.cs` (`AddEmbeddingProvider`: `openai`/`azureopenai`/`hashing`) |
| Koşullu aşamalar | `RerankingServiceCollectionExtensions`, `QueryRewritingServiceCollectionExtensions` |
| Başlangıç doğrulaması | `RetrievalOptionsValidator` |
| Host | `src/Rag.Api/Program.cs`, `src/Rag.QueryWorker/Program.cs` |
| Test için `Program` | `public partial class Program { }` (`WebApplicationFactory`) |

## 2.5 Alternatifler ve trade-off'lar

| Karar | Alternatif | Neden bu |
|---|---|---|
| Bilinçli, el yazması resilience (Bölüm 10) | Polly / `Microsoft.Extensions.Resilience` | Bağımlılık az, davranış testle sabit; **bedeli:** bakım yükü bizde |
| İsteğe bağlı aşama = `null` bağımlılık | Her zaman kayıtlı `NoOp` | Kapalıyken kimlik bilgisi/HTTP istemcisi bile gerekmez. Bedeli: `null` kontrolü |
| Transient servisler | Scoped | Worker'da `CreateScope` ile aynı sonuç; HTTP'de istek başına zaten yeni |
| `Hashing` embedder `AddRagCore`'da | Yalnızca test projesinde | Docker E2E ve entegrasyon testleri ücretli çağrı yapmadan aynı kodu çalıştırır |

## 2.6 Ne ters gidebilir?

- **Singleton'a transient sızıntısı:** reranker singleton'dır; scoped bir bağımlılık alırsa "captive dependency" olur. Şu an yalnızca `IOptions`, `IHttpClientFactory`, logger alırlar.
- **`ValidateOnStart` yalnızca etkin özellikleri doğrular:** kapalı bir aşamanın bozuk değeri fark edilmez; açıldığı gün patlar (istenen davranış, ama bilinmeli).
- **Büyük `Program.cs`:** API'de metrik/sağlık/uç noktalar tek dosyada; küçük bir host için kabul edilebilir.

## 2.7 Nasıl test/gözlem edilir?

`Rag.Core.Tests` mock yerine **bellek içi sahte** nesneler (`InMemoryVectorStore`, `LexicalInMemoryVectorStore`) kullanır; kuyruk ve depo davranışı gerçek altyapıyla `Rag.IntegrationTests` (Testcontainers) ve `scripts/e2e.py` (gerçek Compose) ile doğrulanır.

## 2.8 Mülakatta nasıl anlatırım?

> "Çekirdek kütüphane hiçbir host'a bağımlı değil; host'lar sadece `AddRagCore` çağırır. İsteğe bağlı aşamaları (rewriting, reranking) `null` olabilen bağımlılık olarak enjekte ettim, böylece kapalıyken maliyetsiz ve kimlik bilgisi gerektirmez. Yanlış yapılandırma ilk sorguda değil, `ValidateOnStart` ile açılışta patlar; hata mesajı sırrı yazmaz. İptali hata saymam: zaman aşımı ile çağıran iptalini `when (ct.IsCancellationRequested)` ile ayırırım."

**Sık sorulan:** *Neden `IOptions` yerine doğrudan `Get<T>()`?* — `ValidateOnStart` ve yeniden yükleme (`IOptionsMonitor`) `IOptions` ile gelir; `Get<T>()` yalnızca kayıt anındaki karar için (hangi sağlayıcı kaydedilsin) kullanıldı.
