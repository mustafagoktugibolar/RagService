# Bölüm 7 — Retrieval Evaluation

**Durum:** ✅ Implemented — metrikler, dataset, evaluator, offline harness, CLI (Faz 3) · 🔵 Planned — regression karşılaştırma ve quality gate (Faz 8) · ✅ gerçek embedding (`text-embedding-3-small`) ile baseline koşuldu (§7.7b)
**Kod:** `src/Rag.Evaluation/` (`Metrics/RankingMetrics.cs`, `Dataset/`, `Evaluation/`, `Harness/`), `src/Rag.Evaluation.Cli/`, veri: `datasets/golden-v1/dataset.json`, çıktı: `docs/eval/`
**Testler:** `tests/Rag.Evaluation.Tests/` (45 test)

## 7.1 Nedir ve neden en yüksek öncelik?

Retrieval kalitesi ölçülmeden yapılan her "iyileştirme" tahmindir. Hybrid search, reranking, query rewriting, chunk boyutu — hepsi bazı sorgularda yardım edip bazılarında zarar verebilir. **Evaluation**, bir sorgu kümesi için "doğru cevaplar neydi" bilgisini (ground truth) saklayıp sistemin döndürdüğü sıralamayı sayıya çevirir. Bu sayı olmadan bir değişikliğin regresyon mu iyileştirme mi olduğu bilinemez.

Bu bölümün tek cümlelik tezi: **Önce ölç, sonra optimize et; ölçmeden hiçbir retrieval aşamasını varsayılan yapma.**

## 7.2 Temel kavramlar

| Terim | Anlamı |
|---|---|
| **Golden dataset** | Elle etiketlenmiş (query → ilgili doküman) çiftleri + indekslenecek corpus. Sürümlüdür. |
| **Ground truth / relevance judgment** | "Bu sorgu için bu doküman ne kadar ilgili?" etiketi. |
| **Graded relevance** | İkili (ilgili/değil) yerine 0–3 gibi derece. nDCG bunu kullanır. |
| **Document-level vs chunk-level** | Doküman seviyesi: "doğru doküman bulundu mu?". Chunk seviyesi: "cevabı taşıyan chunk bulundu mu?" Chunking stratejisini yalnızca chunk seviyesi ayırt eder. |

### Bu repodaki dataset formatı

`datasets/golden-v1/dataset.json`: `datasetVersion`, `corpus[]` (documentId, text, metadata), `queries[]` (id, query, category, `relevantDocuments[]` {documentId, grade 1–3, **evidence**}).

**`evidence` tasarımı (önemli):** Chunk-level yargılar chunk ID'leriyle değil, *kanıt cümlesiyle* verilir. Değerlendirme sırasında "ilgili dokümanın, kanıt cümlesini içeren chunk'ları" ilgili sayılır (`RetrievalEvaluator.ChunkJudgments`). Neden? Chunk ID'leri (`doc:000003`) chunk boyutu değişince anlam değiştirir; kanıt cümlesi ise **herhangi bir chunking stratejisinde** geçerlidir. Bu, Faz 7'deki chunking deneylerini mümkün kılar. Açık chunk ID yargısı (`relevantChunkIds`) opsiyonel olarak desteklenir ama kırılgandır.

`DatasetLoader.Validate` tüm hataları tek seferde raporlar: tekrar eden ID, bilinmeyen doküman, grade ∉ [1,3], **corpus'ta bulunmayan evidence** (dataset çürümesine karşı koruma).

## 7.3 Metrikler: tanım, formül, kenar durumlar

Tek sorgu için sıralama `r₁ … r_K`, `rel(i)` = i. sıradaki öğenin derecesi (0 = ilgisiz), `R` = ilgili öğe sayısı.

| Metrik | Formül | Soru |
|---|---|---|
| **Hit@K** | 1 eğer ilk K içinde ilgili varsa, yoksa 0 | "En az bir doğru sonuç var mı?" |
| **Precision@K** | (ilk K'daki ilgili sayısı) / **K** | "Dönenlerin ne kadarı doğru?" |
| **Recall@K** | (ilk K'daki ilgili sayısı) / **R** | "Doğruların ne kadarı bulundu?" |
| **MRR@K** | 1 / (ilk ilgili sonucun sırası), yoksa 0 | "İlk doğru ne kadar yukarıda?" |
| **nDCG@K** | DCG@K / IDCG@K, DCG = Σ (2^{rel(i)} − 1) / log₂(i+1) | "Sıralama, dereceleri de hesaba katarak ne kadar ideal?" |
| **AP@K** | Σ P@i·[r_i ilgili] / min(R, K) | Sıra-duyarlı precision ortalaması; MAP = AP'nin sorgular üzerinden ortalaması |

**Neden 2^rel − 1?** Yüksek dereceli bir sonucun ödülünü üstel artırır: derece 3 bir sonuç, derece 1'in 7 katı değerlidir (7 vs 1), "çok daha önemli" sezgisini yansıtır. Alternatif lineer gain'dir (rel); iki biçim de literatürde var, **seçim bir kez dokümante edilip sabit tutulmalıdır** (karşılaştırılabilirlik için).

**Neden log₂(i+1)?** Sıra 1'de indirim log₂2 = 1 (indirim yok), sıra 3'te log₂4 = 2 (yarı değer). Kullanıcı üst sonuçlara daha çok bakar.

### Kenar durum kararları (kodda uygulanan)

| Durum | Karar | Neden |
|---|---|---|
| İlgili öğesi **olmayan** sorgu (no-answer) | Metrik **`null`** (tanımsız); ortalamaya girmez, `SkippedQueries` olarak raporlanır | 0 yazmak, "cevabı olmayan soru" sayısı arttıkça skoru yapay düşürür |
| Sıralamada tekrar eden ID | Yalnız ilk konumda sayılır | Aynı dokümanın birden çok chunk'ı Precision'ı şişirmesin |
| Sıralama K'dan kısa | Precision yine **K'ya** bölünür | Az sonuç döndürmek ödüllendirilmesin |
| İlgili öğe K dışında | Recall paydasında yine sayılır | Recall = bulunan / var olan |
| Grade 0 kayıt | İlgisiz (yargılanmış ama ilgisiz) | Açıkça "ilgisiz" demek mümkün olsun |
| nDCG ideali | Tüm yargılı öğelerden, K ile kesilir | Standart tanım |
| AP paydası | min(R, K) | R > K iken mükemmel top-K'nın 1 alması için |
| K ≤ 0 | `ArgumentOutOfRangeException` | Sessiz yanlış sonuç yok |

## 7.4 Elle çözülmüş örnek (testlerle birebir aynı)

Sıralama: `d3, d1, d7, d2, d9`. Yargılar: `d1=3, d2=2, d4=1` → **R = 3** (d4 hiç getirilmedi).

Sıra bazında derece: `d3:0, d1:3, d7:0, d2:2, d9:0`.

- **Hit@1** = 0 (ilk sonuç ilgisiz), **Hit@2** = 1.
- **Precision@2** = 1/2 = 0.5; **@3** = 1/3; **@5** = 2/5 = 0.4.
- **Recall@2** = 1/3; **@4** = 2/3; **@5** = 2/3 (d4 asla bulunamadı).
- **MRR@5**: ilk ilgili sıra 2 → 1/2 = 0.5.
- **nDCG@5**:
  - DCG = (2³−1)/log₂3 + (2²−1)/log₂5 = 7/1.58496 + 3/2.32193 = 4.41640 + 1.29203 = **5.70854**
  - Ideal sıra (derece azalan): 3, 2, 1 → IDCG = 7/log₂2 + 3/log₂3 + 1/log₂4 = 7 + 1.89279 + 0.5 = **9.39279**
  - nDCG@5 = 5.70854 / 9.39279 = **0.60776**
  - nDCG@3: DCG = yalnızca d1 → 4.41640 → 4.41640 / 9.39279 = **0.47020**
- **AP@5**: ilgili sıralar 2 ve 4 → P@2 = 1/2, P@4 = 2/4 → toplam 1.0; ÷ min(3,5)=3 → **0.3333**.

Bu değerlerin hepsi `RankingMetricsTests` içinde assert edilir (tolerans 1e-12). Metrik kodunun doğruluğu böylece elle hesapla çapraz doğrulanmıştır.

**Alıştırma:** Aynı yargılarla `d1, d2, d4` sıralamasının nDCG@3 ve AP@3 değerlerini hesapla (ikisi de 1.0 çıkmalı — neden?).

## 7.5 Evaluator nasıl çalışır?

`RetrievalEvaluator.EvaluateAsync(dataset, retrieve, options, chunkCatalog, ct)`:

1. Her sorgu için `retrieve` delegate'i `max(K)` derinliğinde çağrılır (süre `Stopwatch` ile ölçülür).
2. **Document-level:** dönen chunk listesi doküman ID'sine indirgenir, tekrarlar **en iyi (ilk) sıraya** çökertilir.
3. **Chunk-level** (catalog verildiyse): yargılar evidence'dan türetilir (§7.2). Chunk yargısı olmayan sorgular `SkippedQueries`'e düşer.
4. Her K için metrikler hesaplanır; ortalamalar yalnız yargısı olan sorgular üzerinden alınır; kategori kırılımı (keyword/paraphrase/…) ayrıca raporlanır.
5. Gecikme: mean/p50/p95/max (nearest-rank yüzdelik).

Retriever exception fırlatırsa **evaluation başarısız olur** (yutulmaz): eksik sorgulu bir koşu başka koşularla karşılaştırılamaz.

## 7.6 Offline harness: "mevcut dense pipeline"i ölçmek

`DenseBaselineHarness`, repodaki **gerçek** `IndexDocumentService`, `DataIngestionTokenChunker` ve `VectorSearchService`'i çalıştırır; yalnızca ağ kenarlarını değiştirir:

| Bileşen | Harness'te | Üretimde |
|---|---|---|
| Embedding | `HashingEmbeddingProvider` (deterministik bag-of-words, feature hashing, 256 boyut) | OpenAI/Azure |
| Vector store | `InMemoryCosineVectorStore` (exact/brute-force cosine, eşitlikte chunkId ile sıralama) | Qdrant/PgVector |
| Metin kaynağı | `CorpusTextExtractor` | `FileExtensionTextExtractor` + blob storage |
| Chunker, indeksleme/sorgu servisleri | **gerçek** | gerçek |

> ⚠️ **Dürüstlük notu:** `HashingEmbeddingProvider` **anlamsal bir model değildir**; yalnızca ortak yüzey token'larını görür. Bu yüzden aşağıdaki sayılar *harness'in ve datasetin* ölçümüdür, OpenAI embedding'lerinin kalitesi değildir. Gerçek-embedder koşusu ayrıca yapıldı (§7.7b).

Neden böyle? (1) CI'da ağsız ve tekrarlanabilir olması; (2) exact search, ANN yaklaşıklığını değişken olmaktan çıkarır; (3) stratejileri (hybrid, rerank) karşılaştırırken gürültüyü azaltır. Determinizm testi (`TwoRunsOverTheSameDatasetProduceIdenticalRankingsAndMetrics`) bunu doğrular. `string.GetHashCode` kullanılmaz (process başına rastgele); FNV-1a ile sabit.

## 7.7 İlk ölçüm: sentetik dense baseline

Komut: `dotnet run --project src/Rag.Evaluation.Cli` → `docs/eval/baseline-dense.{md,json}`. Dataset `golden-v1.0.0`: 35 doküman, 44 sorgu (40 puanlanan + 4 no-answer), chunk 1000/200.

Document-level (sentetik embedder):

| K | Hit | Precision | Recall | MRR | nDCG |
|---:|---:|---:|---:|---:|---:|
| 1 | 0.675 | 0.675 | 0.517 | 0.675 | 0.646 |
| 3 | 0.800 | 0.317 | 0.688 | 0.729 | 0.678 |
| 5 | 0.850 | 0.210 | 0.721 | 0.739 | 0.694 |
| 10 | 0.900 | 0.123 | 0.796 | 0.746 | 0.717 |

Kategoriye göre K=10 (document-level): keyword Recall **1.000**, paraphrase Recall **0.625**, nDCG 0.938 vs 0.568.

**Bundan ne öğrenilir (ve ne öğrenilmez)?**
- ✔ Harness ve dataset çalışıyor; kategori kırılımı beklenen deseni gösteriyor: yüzey token eşleşmesi keyword sorgularında kusursuz, paraphrase'de zayıf. Bu, "lexical eşleşme ile semantik eşleşmenin farklı hata profilleri vardır" fikrinin somut bir gösterimi — ve hybrid search'ün neden iki sinyali birleştirdiğini hatırlatır.
- ✔ Precision@K, K arttıkça düşüyor: ilgili doküman sayısı sorgu başına 1–3 olduğundan beklenen davranış (Precision@10 üst sınırı R/10).
- ✘ **Hiçbir "iyileştirme" iddiası yoktur.** Baseline, ileride karşılaştırılacak referanstır.
- ✘ Dataset tek anotatör (yazar) etiketli ve küçük (40 puanlanan sorgu). Küçük farklar gürültüdür; anlamlılık için sorgu sayısı artırılmalı ve koşular arası varyans ölçülmelidir (Faz 8).

## 7.7b Gerçek embedding ile baseline

Komut: `dotnet run --project src/Rag.Evaluation.Cli -- --embedder openai --label real-dense` (anahtar git'e girmeyen `.env`'den okunur; embedding'ler `.eval-cache/`'e yazılır, böylece strateji karşılaştırmaları aynı vektörleri kullanır). Çıktı: `docs/eval/real-dense.{md,json}`.

Document-level, `text-embedding-3-small`, chunk 1000/200:

| K | Hit | Precision | Recall | MRR | nDCG |
|---:|---:|---:|---:|---:|---:|
| 1 | 0.875 | 0.875 | 0.671 | 0.875 | 0.832 |
| 3 | 0.975 | 0.417 | 0.833 | 0.917 | 0.877 |
| 5 | 0.975 | 0.275 | 0.887 | 0.917 | 0.887 |
| 10 | 1.000 | 0.148 | 0.942 | 0.921 | 0.903 |

Sentetik baseline ile (K=10 nDCG 0.717) kıyaslayınca gerçek model belirgin biçimde daha iyi, özellikle paraphrase kategorisinde (nDCG 0.568 → 0.871). Bu, sentetik koşunun neden sadece "harness testi" olduğunun kanıtıdır. **Bu, ileride karşılaştırılacak gerçek referanstır.** Aynı sınırlar geçerli: 40 sorgu, tek anotatör.

## 7.7c Tuning disiplini: tune / held-out ayrımı, önceden kayıt, bootstrap

Bir konfigürasyonu aynı sorgularda hem seçip hem "kanıtlamak" yanıltıcıdır. Bu repoda hybrid ayarı şu sırayla yapıldı (`docs/eval/protocol-hybrid-tuning.md`):

1. **Split, sonuçlara bakmadan sabitlendi:** kategori içinde id sırasına göre çift pozisyon → `tune`, tek → `heldout` (puanlanan sorgu: 21 / 19).
2. **Protokol, kod ve sonuçtan önce commit'lendi:** arama uzayı (36 konfigürasyon), seçim sırası, istatistik ve **kabul kuralı** yazılıp commit'lendi. Sonuca bakıp kuralı oynamak mümkün olmasın diye.
3. **Seçim yalnız tune setinde** yapıldı (nDCG@10 ↓, nDCG@5 ↓, daha küçük aday sayısı…).
4. **Held-out bir kez** okundu: dense vs seçilen hybrid, **paired bootstrap** (10 000 yeniden örnekleme, sabit seed, %95 aralık).
5. **Kabul kuralı:** (a) aralığın alt sınırı > 0, (b) nDCG@1 farkı ≥ 0, (c) ≥3 sorgulu hiçbir kategori 0.05'ten fazla kaybetmesin.

**Neden paired bootstrap?** Aynı sorgular iki sistemde de koştuğu için her sorgunun farkı (hybrid − dense) alınır; sorgular yerine konarak çekilip ortalama fark yeniden hesaplanır. Aralık, "başka benzer sorgular gelseydi bu fark ne kadar oynardı" sorusunu yanıtlar. Küçük örneklemde tek sorguluk şans eseri farkları eler.

**Kabul kuralındaki bir hata testle yakalandı:** `0.75 − 0.80` kayan noktada `−0.05000000000000004` verir; tam sınırdaki kayıp yanlışlıkla reddediliyordu. Karşılaştırma 6 basamağa yuvarlandı. Ders: eşik karşılaştırmaları kayan nokta hatasına açıktır, sınır değerini ayrı test et.

**Sonuç (`docs/eval/hybrid-tuning.md`):**

| | tune nDCG@10 | held-out nDCG@10 |
|---|---:|---:|
| dense | 0.940 | **0.861** |
| tuned hybrid (ağırlık 0.5, k=30, 20 aday) | 0.946 | 0.785 |

- Tune setinde seçilen konfigürasyon dense'i yalnız **0.006** geçti; 36 deneme arasından en iyiyi seçmenin **winner's curse**'ü (iyimser sapma) tam bu.
- Held-out farkı **−0.076**, %95 aralık **[−0.155, −0.010]**: sıfırı dışarıda bırakan *negatif* aralık, yani sadece "kanıt yok" değil, **zarar kanıtı**. En çok paraphrase kaybetti (−0.155). Üç kabul koşulu da başarısız → hybrid kabul edilmedi.
- Dense'in kendi skoru bile iki set arasında 0.940 → 0.861 oynadı: bu, 19–21 sorguluk setlerin gürültüsünün somut ölçüsü.

**Sınırlar:** held-out 19 sorgu, tek anotatör; aralık geniş. Sonuç "hybrid bu corpus'ta işe yaramıyor" demek için yeterince tutarlı ama "hybrid asla işe yaramaz" demek için yetersiz. Reranking sonrası yeniden test planlı.

## 7.7d İnsan etiketli doğrulama pilotu (hazırlık tamamlandı, etiketleme bekleniyor)

**Durum:** ✅ araçlar ve belgeler uygulandı ve test edildi · 🔵 insan etiketleme henüz yapılmadı · ❌ pilot bulguları henüz yok (insan girdisi olmadan üretilemez). Protokol: `docs/eval/protocol-human-pilot.md`.

**Neden gerekli?** golden-v1'deki ilgi yargıları, corpus ve sorgularla aynı LLM destekli süreçte yazıldı. Bir LLM hakem (reranker) ile bir LLM etiketleyicinin **ortak önyargıları** örtüşebilir ve kazancı abartabilir. Bağımsız insan etiketi bu riski ölçer. Ama 160 sorguluk etiketleme çok pahalı (insan saati); önce **20–30 sorguluk bir pilot**: corpus ne kadar zor, anotatörler ne kadar anlaşıyor, gerçek çaba ne kadar?

**Corpus kararı.** RagService'in kendi teknik dokümanları, yalnızca dış sağlayıcıya göndermek için **güvenli** olanlar; şirkete özgü veya gizli malzeme dışarıda (`plan.md`, `.env`, deployment dosyaları…). Seçim **allow-list** ile yapılır: listede olmayan hiçbir dosya girmez. Bir **güvenlik tarayıcısı** kopyalamadan *önce* tüm dosyaları tarar (anahtar, e-posta, kullanıcı yolu, IP, parola…) ve bulgu varsa hiçbir şey yazmaz; bulgu çıktısı eşleşen metni **göstermez** (tarayıcı sızıntı kaynağı olmasın). Tarayıcı kalıp tanır, anlam tanımaz; bu yüzden sahibin incelemesi ve **onayı** gerekir. Onay manifestin içerik özetine **bağlıdır**: dosya veya chunk'lama değişirse onay geçersizdir.

**Bir bulgu kararı etkiledi.** Bu dokümanlar üretim chunk'lamasıyla (1000/200) golden-v1 kadar küçük bir corpus verir ve top-20 corpus'un yarısından fazlasını kaplar. Pilotta **250/40** chunk'lama kullanılır (daha ince ayrıntı, daha hızlı yargı); bu bir pilot parametresidir ve manifestte kayıtlıdır. Kesin sayılar manifest ve plan belgesindedir.

**Kör aday havuzu (pooling).** Etiketlenecek pasajlar tek bir sistemin değil, **birden çok sistemin** üst sonuçlarının birleşimidir: BM25, hashing-dense ve (varsa) BM25 + yerel ONNX cross-encoder; artı sorgu başına birkaç **rastgele** pasaj. Birleşim, yargıların tek bir sistemi kayırmasını önler; rastgele pasajlar net "ilgisiz" örnekler verir ve havuzun kaçırdığı ilgiyi tahmin ettirir. Anotatör **yalnızca sorgu ve pasajı** görür: sistem adı, sıra veya skor yoktur; sıra her anotatör için ayrı karıştırılır (aksi hâlde konum sıra hakkında bilgi sızdırır). Hangi sistemin neyi getirdiğini gösteren **kör olmayan anahtar** ayrı bir dizinde tutulur ve etiketleme bitene kadar açılmaz. *Pooling yanlılığı:* havuzda olmayan pasajlar "ilgisiz" sayılır; havuzlayan sistemlere benzemeyen bir sistemi (örn. OpenAI embedding'li dense) olduğundan düşük gösterebilir. Bu yüzden böyle bir sistem skorlanmadan önce **ek yargı turu** gerekir; pilot yalnızca ücretsiz çevrimdışı sistemlerle havuzlar (ücretli çağrı yapılmaz).

**Ölçek (0–3).** 3 cevaplar · 2 büyük ölçüde yardımcı · 1 yalnızca ilgili · 0 ilgisiz. Kılavuz (`annotation-guidelines.md`) sağlayıcı prompt'undan **bağımsız** yazıldı; çünkü etiketler "bir kişi ilgiyi nasıl görür"ü ölçmeli, belirli bir reranker'ın ifadesiyle uyumu değil.

**Anotatörler arası uyuşma: Cohen'in κ'sı.** İki kişinin tesadüfen de uyuşacağı düşünülerek düzeltilmiş uyuşma: `κ = (p₀ − pₑ) / (1 − pₑ)`; `p₀` gözlenen uyuşma, `pₑ` marjinallerden beklenen tesadüfi uyuşma. Derecelendirmede 1 ile 3 arası fark 1 ile 2 arası farktan çok daha ciddidir; bu yüzden **ikinci dereceden ağırlıklı κ**: ağırlık `w(i,j) = 1 − ((i−j)/(k−1))²`.

*Elle çözülmüş örnek (testle aynı, bağımsız bir Python hesabıyla doğrulandı):* A = 0 0 1 1 2 2 3 3 2 1 0 3, B = 0 1 1 2 2 3 3 3 2 0 0 2 (12 öğe, 4 derece). Ağırlıklı: `p₀ = 0.9537`, `pₑ = 0.7222` → **κ_w = 0.833**. Ağırlıksız (yalnızca tam uyuşma): **κ = 0.444**. İkili (derece ≥ 2): **0.833**. Tam uyuşma 7/12 = 0.583, bir basamak içinde uyuşma 12/12. Fark şunu gösterir: anotatörler hep **bir basamak** ayrışıyor; ağırlıklı κ bunu hafif, ağırlıksız κ ağır cezalandırır. Hangisinin doğru olduğu, hatanın maliyetine bağlıdır; ikisi de raporlanır.

*Tanımsız κ:* iki kişi de her öğeye aynı dereceyi verirse `pₑ = 1`, payda sıfır olur. Bu "mükemmel uyuşma" değil, **bilgi yokluğudur**; araç `null` döndürür ve kriteri başarısız sayar. κ'nın güven aralığı **sorgu bazlı bootstrap** ile hesaplanır (bir sorgunun öğeleri bağımsız değildir); 24 sorguda aralık geniştir ve rapor bunu söyler.

**Uzlaşma (adjudication).** Notların **herhangi bir farkı** veya bir bayrak (`unclear-query`, `unjudgeable`) bir insan hakemine gider; hakem iki notu görür, **sistemi/sırayı asla**, nihai notu ve gerekçeyi yazar. Hiçbir şey ortalama alınarak veya araçla çözülmez. Nihai etiket ya "iki insan aynı notu verdi" ya da "insan karar verdi + gerekçe"dir; **hiçbir etiketi LLM üretmez** (anotatör kaydı `kind: human` beyanı ister; araçlar hiçbir model çağırmaz). Bu beyan araçla doğrulanamaz, sürece dayanır.

**Pilotun önceden kayıtlı kabul kriterleri** (hepsi kodda tek yerde; protokol belgesiyle uyum testlidir):

| | Kriter | Eşik |
|---|---|---|
| P1 | Göndermeye güvenli | tarayıcı temiz + sahip onayı manifeste bağlı |
| P2 | Sorgular | 20–30, kategori asgarileri, hepsi kayıtlı insanlardan |
| P3 | İzolasyon | golden-v1 / nihai kümeyle 0 çakışma |
| P4 | Tamlık | havuzdaki öğelerin ≥ %98'i iki anotatörce notlanmış |
| P5 | Uyuşma | ağırlıklı κ ≥ 0.60 **ve** ikili κ ≥ 0.60 (0.40–0.60: gözden geçir; <0.40 veya tanımsız: başarısız) |
| P6 | Cevaplanabilirlik | cevaplanabilir sorguların ≥ %85'inde derece ≥ 2 pasaj; cevapsızlarda yok |
| P7 | **Baş boşluğu** | en iyi ücretsiz taban çizgi: ortalama nDCG@10 ≤ 0.90 ve sorguların ≥ %30'u 0.90 altında; ≥ 0.95 → başarısız (tavan) |
| P8 | Çaba | medyan ≤ 45 sn/yargı (45–60 gözden geçir, >60 başarısız) |
| P9 | Havuz boyutu | sorgu başına medyan 15–30 benzersiz aday |
| P10 | İnsan etiketi | her nihai etiket iki insan uyuşması veya kayıtlı insan kararı |

**Karar:** herhangi biri *başarısız* → **NO-GO**; başarısız yok ama *gözden geçir* var → **REVISE**; hepsi geçti → **GO**. **P7 neden önemli:** corpus öyle kolaysa ki ücretsiz taban çizgi zaten ~1.0 alıyorsa, δ = 0.05'lik bir kazancı **ölçmek mümkün değildir** (tavan etkisi); golden-v1'de gördüğümüz tam bu sorundu.

**Pilot neyi gösteremez:** hiçbir reranker'ı ölçmez (ücretli çağrı yok); yalnızca havuzlanan pasajlar etiketlidir; iki anotatörle κ küçük örneklemde geniş aralıklı bir tahmindir; tahmin edilen varyans bir **vekildir** (gerçek karşılaştırma — dense ve dense+reranker — ölçülmemiştir).

**İzolasyon.** Pilot sorguları `pilot-` önekli ayrı dizinde yaşar; `SplitIsolation`, normalleştirmeden sonra aynı veya çok benzer (Jaccard ≥ 0.8) sorguları reddeder; **pilot etiketleri nihai etiket olarak asla yeniden kullanılmaz** ve kılavuz pilottan sonra değişebilir (`guidelines-v2`).

## 7.8 Ne ters gidebilir?

| Risk | Açıklama / önlem |
|---|---|
| **Etiket gürültüsü** | Tek anotatör, belirsiz sorgular. Önlem: ikinci anotatör, uyuşma oranı; zor sorguları işaretle |
| **Dataset sızıntısı** | Sistemi golden set'e göre ayarlayıp aynı set ile "kanıtlamak" aşırı uyum (overfitting) yaratır. Önlem: dev/held-out ayrımı (planlı) |
| **Küçük örneklem** | 40 sorguda ±0.05 fark istatistiksel olarak anlamsız olabilir. Önlem: bootstrap güven aralığı (Faz 8 adayı) |
| **Metrik yanlış okuma** | Recall@K'yı R > K iken 1'e ulaşamaz diye yorumlamak. Önlem: AP/nDCG ile birlikte oku |
| **Dataset çürümesi** | Corpus değişir, evidence artık yok. Önlem: validator her yüklemede kontrol eder |
| **Sentetik embedder yanılgısı** | Sayıları "kalite" diye raporlamak. Önlem: `EmbedderKind=synthetic` her raporda |

## 7.9 Test ve gözlem

- `RankingMetricsTests` (28): elle hesaplanmış değerler, kenar durumlar (boş sıralama, ilgisiz sorgu, tekrar, grade 0, K ≤ 0).
- `DatasetLoaderTests`, `GoldenDatasetTests`: doğrulama ve gerçek dataset kontrolü.
- `RetrievalEvaluatorTests`: ortalamaların yalnız puanlanan sorgulardan alındığı, doküman çökertme, evidence-temelli chunk yargıları, derinlik, iptal, yüzdelik hesabı.
- `DenseBaselineHarnessTests`: determinizm, chunk konfigürasyonunun uygulandığı.

## 7.10 Mülakatta nasıl anlatırım?

> "Retrieval'ı değiştirmeden önce bir golden dataset ve metrik seti kurdum. Doküman ve chunk seviyesini ayırdım; chunk yargılarını ID yerine kanıt cümlesiyle verdim ki chunking stratejisi değişince etiketler geçersizleşmesin. nDCG için üstel gain ve log₂ indirim kullandım, ilgili öğesi olmayan sorguları 0 sayıp ortalamayı bozmak yerine tanımsız bıraktım. Metrikleri elle hesaplanmış örneklerle test ettim. İlk baseline'ı sentetik, deterministik bir embedder ile aldım, çünkü amaç CI'da tekrarlanabilirlik; bu sayıları gerçek model kalitesi olarak sunmuyorum ve gerçek-embedder koşusunu ayrı bir adım olarak bıraktım."

## Alıştırmalar

1. `HashingEmbeddingProvider`'a küçük bir eş anlamlı sözlüğü ekle; paraphrase kategorisi nasıl değişir? Bu sonucu "semantik model kalitesi" olarak yorumlamak neden yanlış?
2. nDCG'de lineer gain (`rel`) kullansaydık, `d3 d1 d7 d2 d9` örneğinde değer ne olurdu? Hesapla.
3. Aynı sorguya iki anotatör farklı yargı verdiyse nDCG nasıl etkilenir? Uyuşma için hangi istatistik (Cohen's κ) kullanılır?
4. Chunk boyutunu 40/8'e düşür (CLI: `--chunk-size 40 --chunk-overlap 8 --label small-chunks`), chunk-level Recall nasıl değişir? Neden document-level'dan farklı?
