#!/usr/bin/env python3
"""End-to-end verification of RagService against the REAL Docker Compose stack (no mocks of the infrastructure).

    bash scripts/download-reranker-model.sh          # once: local ONNX model
    python3 scripts/e2e.py                           # builds, starts, verifies, writes docs/release/e2e-report.md

Safety: compose.e2e.yaml replaces the .env file of the application containers, so no OpenAI key can reach them; embeddings are the local
Hashing provider. Nothing here can make a paid call. The script never reads .env.

Every check is recorded as PASS/FAIL with the evidence; the report contains only what actually ran. Python standard library only.
"""
import base64, json, os, re, subprocess, sys, time, urllib.error, urllib.request, uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COMPOSE = ["docker", "compose", "-f", "compose.yaml", "-f", "compose.e2e.yaml"]
API = "http://localhost:8080"
RMQ = "http://localhost:15672/api"
S3 = "http://localhost:8333"
BUCKET = "rag-e2e"
RESULTS = []
RUN = uuid.uuid4().hex[:8]
COLLECTION = f"e2e-{RUN}"

DOC_RESILIENCE = """Circuit breaker behaviour in RagService.
The circuit breaker opens after consecutive failures of an external dependency such as Qdrant or the embedding provider.
While the breaker is open, calls fail fast and the worker requeues the message instead of waiting for a timeout.
After the break duration the breaker half-opens and lets a single probe request through to decide whether to close again.
Retries use exponential backoff with jitter so that many workers do not hammer a recovering service at the same moment.
""" * 8

DOC_FUSION = """Reciprocal rank fusion merges dense and lexical rankings.
Each retriever contributes one divided by the sum of a constant k and the rank of the document in that list.
The constant k defaults to sixty and dampens the influence of the very top ranks.
Hybrid retrieval therefore needs no score calibration between the cosine similarity of embeddings and the BM25 score.
""" * 8

DOC_V2 = """Circuit breaker behaviour in RagService, revised.
The breaker now also records slow calls as failures. A zebra-striped sentinel phrase marks this revision: quokka marmalade.
""" * 2


def run(cmd, check=True, capture=True, timeout=900):
    p = subprocess.run(cmd, cwd=ROOT, capture_output=capture, text=True, timeout=timeout)
    if check and p.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd)} failed ({p.returncode}): {(p.stderr or p.stdout)[-800:]}")
    return p


def http(method, url, body=None, headers=None, auth=None, timeout=60, raw=False):
    data = None if body is None else (body if isinstance(body, (bytes, bytearray)) else json.dumps(body).encode())
    req = urllib.request.Request(url, data=data, method=method, headers=dict(headers or {}))
    if body is not None and not isinstance(body, (bytes, bytearray)):
        req.add_header("Content-Type", "application/json")
    if auth:
        req.add_header("Authorization", "Basic " + base64.b64encode(f"{auth[0]}:{auth[1]}".encode()).decode())
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            payload = r.read()
            return r.status, (payload if raw else (json.loads(payload) if payload else None))
    except urllib.error.HTTPError as e:
        payload = e.read()
        try:
            return e.code, (payload if raw else json.loads(payload))
        except Exception:
            return e.code, payload.decode(errors="replace")


def check(name, ok, evidence=""):
    RESULTS.append((name, bool(ok), str(evidence)[:400]))
    print(("PASS " if ok else "FAIL ") + name + (f"  [{str(evidence)[:160]}]" if evidence else ""), flush=True)
    return ok


def wait_for(fn, timeout=120, interval=2, what=""):
    end = time.time() + timeout
    last = None
    while time.time() < end:
        try:
            last = fn()
            if last:
                return last
        except Exception as e:  # noqa: BLE001
            last = e
        time.sleep(interval)
    raise TimeoutError(f"timed out waiting for {what}: {last}")


# ---------------------------------------------------------------- infrastructure helpers
def s3_put(key, content=None):
    url = f"{S3}/{BUCKET}" + (f"/{key}" if key else "")
    args = ["curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "--aws-sigv4", "aws:amz:us-east-1:s3", "--user", "ragaccess:ragsecret", "-X", "PUT"]
    if content is not None:
        args += ["--data-binary", content, "-H", "Content-Type: text/plain"]
    return run(args + [url]).stdout.strip()


def rmq(method, path, body=None):
    return http(method, f"{RMQ}{path}", body, auth=("guest", "guest"))


def declare_reply_queue(name):
    rmq("PUT", f"/queues/%2F/{name}", {"durable": False, "auto_delete": False})


def publish(queue, payload, reply_to=None, correlation_id=None, raw_body=None):
    props = {"delivery_mode": 2, "content_type": "application/json"}
    if reply_to:
        props["reply_to"] = reply_to
    if correlation_id:
        props["correlation_id"] = correlation_id
    body = raw_body if raw_body is not None else json.dumps(payload)
    status, resp = rmq("POST", "/exchanges/%2F/amq.default/publish",
                       {"properties": props, "routing_key": queue, "payload": body, "payload_encoding": "string"})
    return status == 200 and resp.get("routed")


def fetch_reply(queue, timeout=60):
    def attempt():
        status, msgs = rmq("POST", f"/queues/%2F/{queue}/get", {"count": 1, "ackmode": "ack_requeue_false", "encoding": "auto"})
        return msgs[0] if status == 200 and msgs else None
    msg = wait_for(attempt, timeout=timeout, interval=0.5, what=f"reply on {queue}")
    return json.loads(msg["payload"]), msg["properties"]


def queue_depth(name):
    status, q = rmq("GET", f"/queues/%2F/{name}")
    return q.get("messages", 0) if status == 200 else None


def via_worker_query(query, top_k=3, filters=None, timeout=60):
    reply = f"e2e.reply.{uuid.uuid4().hex[:8]}"
    declare_reply_queue(reply)
    cid = uuid.uuid4().hex
    body = {"requestId": cid, "collection": COLLECTION, "query": query, "topK": top_k}
    if filters:
        body["filters"] = filters
    publish("rag.query", body, reply_to=reply, correlation_id=cid)
    response, props = fetch_reply(reply, timeout)
    rmq("DELETE", f"/queues/%2F/{reply}")
    return response, props, cid


def api_query(query, top_k=3, filters=None):
    body = {"collection": COLLECTION, "query": query, "topK": top_k}
    if filters:
        body["filters"] = filters
    t = time.time()
    status, resp = http("POST", f"{API}/query", body)
    return status, resp, (time.time() - t) * 1000


def metric(name, label_filter=""):
    time.sleep(1.2)   # the Prometheus exporter caches each scrape briefly; read fresh values
    status, text = http("GET", f"{API}/metrics", raw=True)
    total = 0.0
    for line in text.decode().splitlines():
        if line.startswith(name + "{") or line.startswith(name + " "):
            if label_filter in line:
                total += float(line.rsplit(" ", 1)[1].split()[0])
    return total


def redis_keys():
    out = run(COMPOSE + ["exec", "-T", "redis", "redis-cli", "--scan", "--pattern", "*"]).stdout.split()
    return out


def up(env=None):
    e = dict(os.environ)
    e.update({k: str(v) for k, v in (env or {}).items()})
    subprocess.run(COMPOSE + ["up", "-d", "--no-build", "--force-recreate", "rag.api", "rag.queryworker", "rag.indexworker"],
                   cwd=ROOT, env=e, check=True, capture_output=True, text=True)
    wait_for(lambda: http("GET", f"{API}/health/ready")[0] == 200, 120, 2, "api readiness")


def index_via_worker(doc_id, text, metadata=None, key=None):
    key = key or f"{COLLECTION}/{doc_id}.txt"
    code = s3_put(key, text)
    reply = f"e2e.reply.{uuid.uuid4().hex[:8]}"
    declare_reply_queue(reply)
    cid = uuid.uuid4().hex
    body = {"requestId": cid, "collection": COLLECTION, "documentId": doc_id, "filePath": key}
    if metadata:
        body["metadata"] = metadata
    publish("rag.index", body, reply_to=reply, correlation_id=cid)
    response, props = fetch_reply(reply, 90)
    rmq("DELETE", f"/queues/%2F/{reply}")
    return code, response, props, cid


def write_report(strategy_notes):
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    lines = ["# V1 end-to-end verification report", "",
             f"Generated by `scripts/e2e.py` on {time.strftime('%Y-%m-%d %H:%M:%S %Z')} against the real Docker Compose stack "
             "(RabbitMQ, Qdrant, SeaweedFS S3, Redis, Tempo, Prometheus, Grafana, Rag.Api, Rag.IndexWorker, Rag.QueryWorker).", "",
             "**No paid call is possible in this run:** `compose.e2e.yaml` replaces the `.env` file of the application containers, embeddings come from the "
             "local deterministic Hashing provider, rewriting uses the local rules rewriter and reranking the local ONNX model.",
             "", f"**Result: {passed} of {len(RESULTS)} checks passed.**", "", "| # | check | result | evidence |", "|---:|---|:-:|---|"]
    for i, (name, ok, ev) in enumerate(RESULTS, 1):
        lines.append(f"| {i} | {name} | {'PASS' if ok else '**FAIL**'} | {ev.replace('|', '/').replace(chr(10), ' ')} |")
    if strategy_notes:
        lines += ["", "## Notes", ""] + [f"- {n}" for n in strategy_notes]
    os.makedirs(os.path.join(ROOT, "docs", "release"), exist_ok=True)
    with open(os.path.join(ROOT, "docs", "release", "e2e-report.md"), "w") as f:
        f.write("\n".join(lines) + "\n")
    return passed == len(RESULTS)


def main():
    notes = []
    if not os.path.exists(os.path.join(ROOT, "models", "ms-marco-MiniLM-L6-v2", "model.onnx")):
        print("ONNX model missing: run bash scripts/download-reranker-model.sh first", file=sys.stderr)
        return 2

    # ------------------------------------------------------------ 1. infrastructure
    run(COMPOSE + ["up", "-d", "--no-build", "rabbitmq", "qdrant", "seaweedfs", "redis", "tempo", "prometheus", "grafana"])
    wait_for(lambda: all(h == "healthy" for h in [
        run(["docker", "inspect", "-f", "{{.State.Health.Status}}", c]).stdout.strip()
        for c in run(COMPOSE + ["ps", "-q", "rabbitmq", "seaweedfs", "redis"]).stdout.split()]), 180, 3, "infra health")
    check("1. infrastructure containers healthy (rabbitmq, seaweedfs, redis)", True)
    wait_for(lambda: http("GET", "http://localhost:6333/healthz", raw=True)[0] == 200, 60, 2, "qdrant")
    check("1. qdrant answers /healthz", True)
    check("1. SeaweedFS bucket created", s3_put("") in ("200", "409"), "PUT bucket")

    up()
    live = http("GET", f"{API}/health/live")
    ready = http("GET", f"{API}/health/ready")
    check("1. api /health/live", live[0] == 200, live[1])
    check("1. api /health/ready reports qdrant + object-storage healthy", ready[0] == 200 and all(c["status"] == "Healthy" for c in ready[1]["checks"]), ready[1])
    wait_for(lambda: queue_depth("rag.index") is not None and queue_depth("rag.query") is not None, 60, 2, "worker queues")
    check("1. workers declared rag.index / rag.query with dead-letter queues", all(queue_depth(q) is not None for q in ["rag.index.dlq", "rag.query.dlq"]))

    # ------------------------------------------------------------ 2-5. indexing through RabbitMQ and IndexWorker
    code, resp, props, cid = index_via_worker("doc-resilience", DOC_RESILIENCE, {"topic": "resilience", "lang": "en"})
    check("2. document uploaded to object storage (S3 PUT)", code == "200", f"http {code}")
    check("3. IndexWorker processed the message from rag.index and replied", resp.get("success") is True, resp)
    check("3. reply keeps the correlation id", props.get("correlation_id") == cid)
    chunks = resp.get("chunkCount") or resp.get("chunks") or resp.get("indexedChunks")
    check("4-5. text extracted, chunked, embedded and indexed (>1 chunk)", isinstance(chunks, int) and chunks > 1, f"chunks={chunks}; response={resp}")
    _, resp2, _, _ = index_via_worker("doc-fusion", DOC_FUSION, {"topic": "fusion", "lang": "en"})
    check("3. second document indexed", resp2.get("success") is True)
    status, points = http("GET", f"http://localhost:6333/collections/{COLLECTION}")
    check("5. vectors are in Qdrant", status == 200 and points["result"]["points_count"] >= 4, points.get("result", {}).get("points_count") if status == 200 else status)

    # ------------------------------------------------------------ 6/13. dense retrieval through the Query API
    status, r, _ = api_query("how does the circuit breaker open after failures")
    top = r["results"][0] if r and r.get("results") else {}
    check("6. dense retrieval through the Query API returns results", status == 200 and r["success"] and top, f"http {status}")
    check("13. top result is the expected document with attribution", top.get("documentId") == "doc-resilience", top.get("documentId"))
    check("13. result carries chunk id, text, score and the indexed metadata", all(k in top for k in ("chunkId", "text", "score")) and top.get("metadata", {}).get("topic") == "resilience", {k: top.get(k) for k in ("chunkId", "score", "metadata")})
    status, r2, _ = api_query("how does the circuit breaker open after failures", filters={"topic": "fusion"})
    check("13. metadata filter restricts results to the filtered documents", status == 200 and r2["results"] and all(x["documentId"] == "doc-fusion" for x in r2["results"]), [x["documentId"] for x in r2["results"]])
    bad_status, bad, _ = api_query("anything", filters={"bad key!": "x"})
    check("13. invalid filter key is rejected (400)", bad_status == 400, bad_status)
    miss_status, miss = http("POST", f"{API}/query", {"collection": "does-not-exist", "query": "x"})
    check("13. unknown collection returns a clear error, not a 500", miss_status == 400 and "not been indexed" in json.dumps(miss), miss)

    # ------------------------------------------------------------ 7. query worker through RabbitMQ
    resp, props, cid = via_worker_query("how does the circuit breaker open after failures")
    check("7. QueryWorker processed the message from rag.query and replied", resp.get("success") is True and resp["results"], resp.get("errorMessage"))
    check("7. worker result equals the API result (same retrieval components)", [x["chunkId"] for x in resp["results"]] == [x["chunkId"] for x in r["results"]])
    check("7. reply keeps the correlation id", props.get("correlation_id") == cid)

    # ------------------------------------------------------------ 11. cache hits, invalidation, index changes
    keys_before = len(redis_keys())
    hits_before = metric("rag_queries_executed_total", 'cache_hit="true"')
    status, c1, t1 = api_query("how does the circuit breaker open after failures")
    status, c2, t2 = api_query("how does the circuit breaker open after failures")
    hits_after = metric("rag_queries_executed_total", 'cache_hit="true"')
    check("11. repeated query is served from the Redis query cache", hits_after > hits_before, f"cache-hit counter {hits_before}->{hits_after}")
    check("11. cached result equals the computed result", c1["results"] == c2["results"])
    check("11. query cache entries exist in Redis", len(redis_keys()) > 0, f"{len(redis_keys())} keys (before: {keys_before})")
    _, resp_v2, _, _ = index_via_worker("doc-resilience", DOC_V2, {"topic": "resilience", "lang": "en"})
    status, c3, _ = api_query("quokka marmalade sentinel phrase")
    check("11. re-indexing a document invalidates cached results (index version changed)",
          resp_v2.get("success") is True and any("quokka" in x["text"] for x in c3["results"]), [x["text"][:40] for x in c3["results"]])
    status, c4, _ = api_query("how does the circuit breaker open after failures")
    check("11. the old text is no longer returned after the update", all("quokka" in x["text"] or x["documentId"] != "doc-resilience" for x in c4["results"]))

    # ------------------------------------------------------------ 12. failure handling
    dlq_before = queue_depth("rag.query.dlq") or 0
    publish("rag.query", None, raw_body="{this is not json")
    wait_for(lambda: (queue_depth("rag.query.dlq") or 0) > dlq_before, 30, 1, "poison message in DLQ")
    check("12. a malformed query message is dead-lettered, not retried forever", True, f"rag.query.dlq {dlq_before}->{queue_depth('rag.query.dlq')}")
    check("12. the main queue is empty afterwards (no redelivery loop)", wait_for(lambda: queue_depth("rag.query") == 0, 20, 1, "empty queue") is True)
    idx_dlq = queue_depth("rag.index.dlq") or 0
    reply = f"e2e.reply.{uuid.uuid4().hex[:8]}"
    declare_reply_queue(reply)
    publish("rag.index", {"requestId": "x", "collection": COLLECTION, "documentId": "ghost", "filePath": "no/such/object.txt"}, reply_to=reply, correlation_id="ghost")
    err, _ = fetch_reply(reply, 60)
    check("12. indexing a missing object replies with a failure and does not crash the worker", err.get("success") is False and err.get("errorMessage"), err.get("errorMessage"))
    check("12. the failed index message is dead-lettered", wait_for(lambda: (queue_depth("rag.index.dlq") or 0) > idx_dlq, 30, 1, "index DLQ") is True)
    rmq("DELETE", f"/queues/%2F/{reply}")

    run(COMPOSE + ["stop", "qdrant"])
    time.sleep(2)
    t = time.time()
    status, down, _ = api_query("circuit breaker failures in the uncached outage probe")
    check("12. with Qdrant down the API fails the request cleanly (no hang, no crash)", status in (400, 500, 503) and time.time() - t < 60, f"http {status} in {time.time() - t:.1f}s")
    run(COMPOSE + ["start", "qdrant"])
    wait_for(lambda: http("GET", "http://localhost:6333/healthz", raw=True)[0] == 200, 60, 2, "qdrant restart")
    recovered = wait_for(lambda: api_query("how does the circuit breaker open after failures")[0] == 200, 90, 3, "recovery")
    check("12. after Qdrant returns the service recovers without a restart", recovered is True)
    resp, _, _ = via_worker_query("how does the circuit breaker open after failures")
    check("12. the worker recovers too", resp.get("success") is True)
    ready = http("GET", f"{API}/health/ready")
    check("12. readiness reflects the recovered dependency", ready[0] == 200)

    # ------------------------------------------------------------ 8-10. strategies through configuration
    lex_probe = "reciprocal rank fusion constant"
    up({"E2E_STRATEGY": "Lexical"})
    status, lx, _ = api_query(lex_probe)
    check("8. Lexical strategy (configuration only) works through the API", status == 200 and lx["success"] and lx["results"][0]["documentId"] == "doc-fusion", [x["documentId"] for x in lx["results"]] if lx and lx.get("results") else lx)
    check("8. lexical results report their source", any(s["source"] == "lexical" for s in (lx["results"][0].get("sources") or [])), lx["results"][0].get("sources"))
    up({"E2E_STRATEGY": "Hybrid"})
    status, hy, _ = api_query(lex_probe)
    srcs = {s["source"] for x in hy["results"] for s in (x.get("sources") or [])}
    check("8. Hybrid strategy fuses dense and lexical legs with RRF (both sources present, no warning)", status == 200 and {"dense", "lexical"} <= srcs and not hy.get("warnings"), f"sources={sorted(srcs)} warnings={hy.get('warnings')}")
    resp, _, _ = via_worker_query(lex_probe)
    check("8. Hybrid works through the QueryWorker as well", resp.get("success") and resp["results"][0]["documentId"] == "doc-fusion")

    up({"E2E_STRATEGY": "Hybrid", "E2E_REWRITE": "true"})
    def rewrites():
        return metric("rag_rewrite_requests_total", 'outcome="rewritten"') + metric("rag_rewrite_requests_total", 'outcome="cached"')
    before = rewrites()
    status, rw, _ = api_query("could you please tell me how reciprocal rank fusion works?")
    after = rewrites()
    check("9. query rewriting (configuration only) rewrote the query and retrieval still succeeded", status == 200 and rw["success"] and after > before and rw["results"][0]["documentId"] == "doc-fusion", f"rewritten+cached counter {before}->{after}")
    status, short, _ = api_query("RRF k")
    check("9. a very short query is searched as written (skipped)", status == 200 and metric("rag_rewrite_requests_total", 'outcome="skipped"') >= 1)

    up({"E2E_STRATEGY": "Dense", "E2E_RERANK": "true", "E2E_RERANK_PROVIDER": "Onnx"})
    status, rr, _ = api_query("how does the circuit breaker open after failures", top_k=3)
    srcs = {s["source"] for x in (rr.get("results") or []) for s in (x.get("sources") or [])}
    check("10. optional reranking with the local ONNX model runs inside the Docker container", status == 200 and rr["success"] and "rerank" in srcs and not rr.get("warnings"), f"http {status}; sources={sorted(srcs)}; warnings={rr.get('warnings')}")
    scores = [x["score"] for x in rr["results"]] if rr and rr.get("results") else []
    check("10. reranked results are ordered by descending score (ONNX scores are raw logits, not 0..1)", len(scores) >= 2 and scores == sorted(scores, reverse=True), scores)
    resp, _, _ = via_worker_query("how does the circuit breaker open after failures")
    check("10. reranking works through the QueryWorker too", resp.get("success") and any(s["source"] == "rerank" for x in resp["results"] for s in (x.get("sources") or [])))
    check("10. the same query cached under Dense is NOT served stale after reranking is enabled", "rerank" in srcs, "this exact query was answered and cached (5 min TTL) under Dense earlier in the run")

    # observability: Prometheus scrapes the API and Tempo received traces
    def up_targets():
        status, targets = http("GET", "http://localhost:9090/api/v1/targets")
        return [t for t in (targets or {}).get("data", {}).get("activeTargets", []) if t.get("health") == "up"] if status == 200 else []
    try:
        healthy = wait_for(up_targets, 45, 3, "prometheus target up")   # scrape interval is 15 s and the API was just restarted
    except TimeoutError:
        healthy = []
    check("12. Prometheus scrapes the API metrics endpoint (target up)", len(healthy) >= 1, [t["scrapeUrl"] for t in healthy])
    status, found = http("GET", "http://localhost:3200/api/search?tags=service.name%3DRag.Service&limit=5")
    check("12. Tempo received OpenTelemetry traces from the services", status == 200 and bool((found or {}).get("traces")), f"http {status}; {len((found or {}).get('traces', []))} traces")

    # misconfiguration stops the host at startup (ValidateOnStart)
    e = dict(os.environ, E2E_RERANK="true", E2E_RERANK_PROVIDER="OpenAiDecisions")
    subprocess.run(COMPOSE + ["up", "-d", "--no-build", "--force-recreate", "rag.api"], cwd=ROOT, env=e, capture_output=True, text=True)
    time.sleep(8)
    logs = run(COMPOSE + ["logs", "--no-color", "rag.api"], check=False).stdout
    check("12. reranking enabled without a credential refuses to start (startup validation), and logs never contain a key",
          "OpenAI:ApiKey" in logs and "sk-" not in logs, "validator message found in rag.api logs" if "OpenAI:ApiKey" in logs else logs[-300:])
    try:
        serving = http("GET", f"{API}/health/live", timeout=5)[0] == 200
    except OSError:
        serving = False   # connection refused: nothing is listening, which is the expected outcome
    check("12. ...and the host is not serving", not serving)

    up({"E2E_STRATEGY": "Dense"})
    check("final. stack returns to the production defaults (Dense, no rewriting, no reranking)", api_query("how does the circuit breaker open after failures")[0] == 200)

    ok = write_report(notes)
    print(f"\n{sum(1 for _, o, _ in RESULTS if o)}/{len(RESULTS)} checks passed; report: docs/release/e2e-report.md")
    return 0 if ok else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # noqa: BLE001
        check("script completed without an unexpected error", False, repr(exc))
        write_report([])
        raise
