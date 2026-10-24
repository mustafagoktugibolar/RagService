# Bölüm 12 — Test ve Kalite

**Durum:** ✅ Implemented (4 test projesi, regresyon kapısı, CI iş akışı) · 🟡 Partial (CI iş akışı yazıldı ve YAML olarak doğrulandı; **GitHub Actions üzerinde henüz çalıştırılmadı**)
**Ana dosyalar:** `tests/*`, `.github/workflows/ci.yml`, `scripts/e2e.py`

## 12.1 Test piramidi

| Katman | Proje | Ne sınar | Altyapı |
|---|---|---|---|
| Birim | `Rag.Core.Tests` (330) | Servisler, retriever'lar, fusion, cache anahtarı, resilience, rerank adaptörleri, rewriting, doğrulayıcılar | **Bellek içi sahte nesneler**, betikli HTTP sunucusu |
| Birim + ölçüm | `Rag.Evaluation.Tests` (289) | Metrikler, harness, bootstrap, insan-etiketleme araçları, **regresyon kapısı** | Yok (Hashing embedder) |
| Yerel model | `Rag.Reranking.Onnx.Tests` (30) | Tokenizer referans uyumu (23 çift), **gerçek ONNX çıkarımı** | Yerel model dosyası |
| Entegrasyon | `Rag.IntegrationTests` (23) | Qdrant/PgVector/S3/Redis/RabbitMQ davranışı | **Testcontainers** (gerçek kapsayıcılar) |
| Uçtan uca | `scripts/e2e.py` (49 kontrol) | Gerçek Compose: S3 → RabbitMQ → worker'lar → Qdrant → API | Gerçek yığın |

*Sayılar V1 doğrulama koşusundandır; güncel sayı için `dotnet test`.*

## 12.2 Mock değil fake

`InMemoryVectorStore` ve `LexicalInMemoryVectorStore` gerçek davranışı (filtre, sıralama, BM25) uygular. Mock "çağrıldı mı" sınar, fake **sonucu** sınar; retrieval gibi davranışsal kodda fake çok daha az kırılgandır. Dış HTTP sağlayıcıları `ScriptedHandler` ile betiklenir (Jev ve OpenAI Decisions sözleşmeleri **çevrimdışı** böyle doğrulanır; canlı Jev yok).

## 12.3 Retrieval testi: deterministik

Retrieval kalitesi "çalışıyor gibi" değil **sayıyla** sınanır: `golden-v1` (44 sorgu, el yazımı, kategorili), Hashing embedder (deterministik), bellek içi indeks. `RegressionGateTests`, dense/lexical/hybrid için doküman-düzeyi nDCG/Recall/MRR @1,5,10 değerlerinin **commit'li baseline'ın** (`docs/eval/baseline-dense.json`, `offline-lexical.json`, `offline-hybrid.json`) altına düşmemesini ister (tolerans 0.005). **Mutasyonla doğrulandı:** füzyon sıralamasını bozunca kapı kırıldı (hybrid nDCG@1 0.73 → 0.00). Bilinçli bir davranış değişikliği baseline'ı aynı commit'te yeniler; fark gözden geçirilir.

**Sınır:** kapı Hashing (sözcüksel) embedder'la çalışır — boru hattının gerilemediğini kanıtlar, gerçek embedding kalitesini değil (o için `docs/eval/real-*.md`, ücretli ve onaylı koşular).

## 12.4 Yanlış-negatif/yanlış-pozitif disiplini

Bu repoda sonradan bulunan hataların hepsi testle sabitlendi: stale sorgu cache'i (kırmızı→yeşil), bilinmeyen tiktoken modeli, tokenizer sapması (23 referans çifti), Qdrant'ın sparse vektör ekleyememesi (entegrasyon testi tasarımı çürüttü), kayan nokta sınırı, CSV formül enjeksiyonu. Kural: **bir testin başarısız olabildiğini görmeden ona güvenme** (regresyon kapısında mutasyon, `ReverseReranker` testi).

## 12.5 CI (`.github/workflows/ci.yml`)

İşler: `build-and-unit` (derleme + Core + Evaluation/regresyon kapısı), `onnx` (model indir + SHA doğrula + gerçek çıkarım), `integration` (Testcontainers), `compose-e2e` (yalnızca `main`'e push; 49 kontrol; rapor artifact), `secrets-scan`. **Hiçbir iş sır kullanmaz**; ücretli sağlayıcılar yalnızca betikli sunucuya karşı sınanır. Durum: iş akışı yerelde YAML olarak ayrıştırıldı; bileşenlerinin her biri yerelde koştu. **GitHub'da koşturulmadı** — ilk çalıştırmada ortam farkları (ör. Ubuntu runner'da Docker/ONNX) çıkabilir.

## 12.6 Çalıştırma

```bash
dotnet test tests/Rag.Core.Tests
dotnet test tests/Rag.Evaluation.Tests              # regresyon kapısı dahil
dotnet test tests/Rag.Reranking.Onnx.Tests          # önce: bash scripts/download-reranker-model.sh
dotnet test tests/Rag.IntegrationTests              # Docker gerekir
python3 scripts/e2e.py                              # gerçek Compose (Docker + ONNX modeli)
```
