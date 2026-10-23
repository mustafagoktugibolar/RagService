namespace Rag.Reranking.Onnx;

public sealed class OnnxRerankerOptions
{
    /// <summary>Path to the cross-encoder ONNX file (inputs: input_ids, attention_mask, token_type_ids; output: one logit per pair).</summary>
    public string ModelPath { get; set; } = "models/ms-marco-MiniLM-L6-v2/model.onnx";

    /// <summary>WordPiece vocabulary of the model's tokenizer (uncased BERT vocab for MiniLM cross-encoders).</summary>
    public string VocabPath { get; set; } = "models/ms-marco-MiniLM-L6-v2/vocab.txt";

    /// <summary>Label that identifies model and precision; it becomes part of <c>IReranker.Name</c> and so of the query-cache key.</summary>
    public string ModelId { get; set; } = "ms-marco-MiniLM-L6-v2:fp32";

    /// <summary>Longest (query + document) input in tokens, including [CLS]/[SEP]. The model was trained with 512.</summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>Pairs per inference call. Larger batches amortise overhead but pad more and raise peak memory. 10 measured marginally fastest for 20 candidates on the benchmark machine (docs/eval/reranker-cpu-benchmark.md).</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>Inference calls allowed in flight across the whole process; bounds CPU use under concurrent queries.</summary>
    public int MaxConcurrentInferences { get; set; } = 1;

    /// <summary>ONNX Runtime intra-op threads per inference. 0 lets the runtime decide (all cores).</summary>
    public int IntraOpThreads { get; set; } = 0;
}
