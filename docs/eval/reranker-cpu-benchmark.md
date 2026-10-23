# Local cross-encoder CPU benchmark

- Machine: macOS 26.1.0, Arm64, 8 logical cores, .NET 10.0.3
- Input: 20 corpus chunks per query, 44 golden-v1.1.0 queries, 3 warm-up queries discarded
- Latency includes tokenisation + inference; excludes any network (there is none).

| model file | batch | intra-op threads | load ms | p50 ms | p95 ms | max ms |
|---|---:|---:|---:|---:|---:|---:|
| fp32 | 20 | default | 185 | 188.6 | 202.8 | 220.8 |
| fp32 | 10 | default | 73 | 172.4 | 214.7 | 218.2 |
| fp32 | 5 | default | 74 | 176.7 | 206.6 | 211.0 |
| fp32 | 20 | 4 | 72 | 214.8 | 228.2 | 250.4 |
| fp32 | 20 | 1 | 76 | 633.3 | 677.1 | 681.2 |
| qint8 | 20 | default | 69 | 92.1 | 142.6 | 241.4 |
| qint8 | 10 | default | 67 | 81.1 | 95.8 | 126.1 |
| qint8 | 5 | default | 65 | 79.6 | 90.5 | 146.5 |
| qint8 | 20 | 4 | 66 | 98.1 | 131.1 | 135.9 |
| qint8 | 20 | 1 | 72 | 262.2 | 290.3 | 300.3 |

Memory is measured separately, one fresh process per model file (`--mode bench-memory`), because the working set of a process that loads several models in a row is not interpretable.

## Memory (fresh process per model file, working set in MB)

| model file | baseline | after load | after 20 reranks |
|---|---:|---:|---:|
| model.onnx | 49 | 232 (+182) | 267 (+218) |
| model_qint8_arm64.onnx | 49 | 124 (+74) | 174 (+124) |
