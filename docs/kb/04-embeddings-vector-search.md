# Bölüm 4 — Embedding'ler ve Vektör Arama

**Durum:** ✅ Implemented (OpenAI/Azure/Hashing embedder'ları; Qdrant ve PgVector) · 🟡 Partial (PgVector'da **ANN indeksi yok**, tam tarama; ayrıntı §4.4)
**Ana dosyalar:** `src/Rag.Core/Providers/Embeddings/*`, `src/Rag.Core/Providers/VectorStores/{QdrantVectorStore,PgVectorStore}.cs`, `src/Rag.Core/Abstractions/IVectorStore.cs`
**Ölçüm kanıtı:** `docs/eval/real-dense.md` (gerçek `text-embedding-3-small`), `docs/eval/baseline-dense.md` (Hashing — yalnızca pipeline doğrulaması)

## 4.1 Nedir?

Bir **embedding**, metni sabit uzunluklu bir `float[]` vektörüne çeviren modelin çıktısıdır; anlamca yakın metinler uzayda yakındır. **Vektör arama**, sorgunun vektörüne en yakın `K` chunk vektörünü bulmaktır. Yakınlık ölçüsü burada **cosine** benzerliğidir: `cos(a,b) = a·b / (|a||b|)`; vektörler L2-normalize ise nokta çarpımına eşittir.

## 4.2 Hangi problemi çözer? Neyi çözmez?

Çözer: "kapının kilidi açılmıyor" sorgusunun "anahtar deliği arızası" geçen pasajı bulması (kelime örtüşmesi olmadan). **Çözmez:** tam eşleşmeler (hata kodu `E4012`, sürüm numarası, tanımlayıcı) — bunlar için lexical arama gerekir (Bölüm 6). Gerçek ölçümümüzde de hybrid, **keyword** sorgularında yardımcı oldu ama toplamda dense'i geçmedi (Bölüm 7).

## 4.3 İçeride nasıl çalışır?

### Embedding sağlayıcıları (`IEmbeddingProvider`)

| `Rag:EmbeddingProvider` | Sınıf | Not |
|---|---|---|
| `OpenAI` | `OpenAiEmbeddingProvider` | `Microsoft.Extensions.AI` üzerinden; **ücretli**; boyut `OpenAI:Dimensions` |
| `AzureOpenAI` | `AzureOpenAiEmbeddingProvider` | Dağıtım adı `AzureOpenAI:Deployment` |
| `Hashing` | `HashingEmbeddingProvider` | **Yerel, deterministik, ağ yok.** Feature hashing + sublinear tf + L2. *Semantik değildir*: yalnızca ortak yüzey kelimeleri görür; geliştirme, test ve Docker E2E içindir |

Sarmalayıcı: `CachingEmbeddingProvider` (Redis, Bölüm 8). `Hashing` önbelleğe sarılmaz (zaten ucuz ve deterministik).

**Tutarlılık kuralı:** bir koleksiyon belirli bir `(provider, model, vectorSize)` ile indexlenir ve bu **koleksiyon metadata'sına** yazılır. Sorgu veya yeni indexleme farklı bir üçlüyle gelirse `IsCompatible` reddeder ("... was indexed with X, but current configuration is Y"). Çünkü farklı modellerin vektörleri aynı uzayda değildir; karıştırmak sessizce anlamsız sonuç üretir. Bu kontrol E2E'de ve birim testlerde sabittir.

### Qdrant (`QdrantVectorStore`)

- gRPC (`Qdrant:Port` 6334) ile; sağlık için HTTP (`HttpPort` 6333).
- Koleksiyon `Distance.Cosine` ile oluşur. Qdrant, ANN için **HNSW** grafı kullanır (varsayılan parametrelerle; repo bunları ayarlamaz).
- Nokta kimliği `CreatePointId("{collection}:{chunkId}")` — deterministik, yani tekrar `Upsert` aynı noktayı günceller.
- Payload: `documentId`, `chunkId`, `text`, `metadata` (iç içe). **Metadata filtresi** `metadata.{key}` üzerinde `MatchKeyword` ile; anahtar alfabesi `FilterValidator` ile sınırlıdır (Bölüm 13).
- Hibrit için ayrıca **adlandırılmış sparse vektör** (`IDF` modifier) saklanır. Qdrant var olan bir koleksiyona sonradan vektör ekleyemez; sparse'tan önce oluşan koleksiyonlar dense-only kalır ve lexical istek `LexicalSearchUnavailableException` fırlatır (yeniden indexleme gerekir) — bu varsayım **entegrasyon testiyle çürütülüp** tasarım düzeltildi.

### PgVector (`PgVectorStore`)

- Tablo `rag_vectors(collection, document_id, chunk_id, text, metadata jsonb, embedding vector, PK(collection, chunk_id))`.
- Skor: `1 - (embedding <=> @embedding)` (cosine uzaklığı → benzerlik), `ORDER BY embedding <=> @embedding`.
- Yalnızca `(collection, document_id)` üzerinde B-tree indeksi vardır. **Vektör sütununda HNSW/IVFFlat yoktur**: arama koleksiyon içinde **tam (exact) taramadır**. Küçük/orta koleksiyonlarda doğru ve basit; büyük koleksiyonda gecikme doğrusal artar. Bu bir **bilinen sınırdır**, ANN indeksi eklemek `CREATE INDEX ... USING hnsw (embedding vector_cosine_ops)` ve sabit boyutlu `vector(N)` sütunu gerektirir (şu an boyutsuz `vector`).
- `ILexicalSearchStore` **uygulamaz** → PgVector'da `Hybrid` seçilirse sessizce dense'e düşer ve uyarı döner (Bölüm 6, capability modeli).

## 4.4 ANN vs tam tarama — trade-off

| | Tam tarama (PgVector burada) | ANN/HNSW (Qdrant) |
|---|---|---|
| Doğruluk | %100 (tam en yakın komşu) | Yaklaşık; recall < 1 olabilir |
| Gecikme | O(N·d) | ~O(log N) |
| Yazma maliyeti | Düşük | Grafı güncellemek |
| Ayar | Yok | `m`, `ef_construct`, `ef` |

Bu repoda ANN recall'ı **ölçülmedi**; Qdrant varsayılanları kullanılıyor.

## 4.5 Ne ters gidebilir?

- **Boyut uyuşmazlığı:** `Rag:VectorSize` ≠ modelin gerçek boyutu → indexleme `ValidateEmbeddingSize` ile durur.
- **Model değişince eski koleksiyon:** uyumsuzluk hatası alırsınız; çözüm yeni koleksiyon + yeniden indexleme.
- **Hashing ile "kalite" iddiası:** Hashing skorları kaliteyi değil pipeline'ı ölçer; raporlarda `embedderKind` ile etiketlenir (`real` / `hashing`).
- **Çok dilli:** `text-embedding-3-small` çok dillidir ama Türkçe için ayrıca ölçülmedi.

## 4.6 Nasıl test/gözlem edilir?

`QdrantMetadataTests`, `QdrantLexicalSearchTests`, `PgVectorMetadataTests`, `ProviderTests` (Testcontainers; gerçek Qdrant/PostgreSQL). `scripts/e2e.py`: indeksleme sonrası Qdrant'taki nokta sayısı, metadata filtresi ve sonuç alanları. Metrik: `rag.embedding.duration`.

## 4.7 Mülakatta nasıl anlatırım?

> "Dense arama anlam yakınlığını bulur, tam eşleşmeyi kaçırabilir. Koleksiyon metadata'sı (provider/model/boyut) ile modeller karışmaz. Qdrant'ta HNSW ile yaklaşık arama, PgVector'da bu repoda indeks olmadığı için tam tarama var — bunu bilinen sınır olarak kayda geçirdim. Hashing embedder semantik değildir; sadece ağsız deterministik test için ve raporlarda açıkça etiketli."
