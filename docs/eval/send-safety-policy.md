# Send-safety policy — what may be sent to an external model provider

Applies to the human-labeling pilot corpus and to any later paid reranking run that sends passages to a hosted provider (OpenAI Decisions, Jev). With a hosted provider the **query and
the passage text leave the process**; this policy decides what is allowed to.

## Principle: allow-list, not block-list
A file enters the corpus only if it is named in the explicit allow-list in `PilotCommands`. Everything else is excluded by default. The manifest records both lists and the reason for each exclusion.

## What is excluded, and why
| Excluded | Reason |
|---|---|
| `plan.md` | Company-specific Definition Seeder plan; may carry proprietary names and domain details |
| `.env`, `.env.template` | Credentials and default passwords |
| `docs/PLAN.md` | A living document that changes with the work under evaluation |
| `docs/eval/**` | Generated reports and protocols about the experiments themselves: circular, and they change |
| `datasets/**`, `models/**` | Evaluation data and downloaded model files |
| `src/**`, `tests/**` | Source code is out of scope for the pilot (a possible later growth of the corpus, with its own review) |
| `compose*.yaml`, `infra/**`, Dockerfiles | Deployment files with default credentials |

## Automated check (necessary, not sufficient)
`SafetyScanner` runs over every allow-listed file **before anything is copied** and blocks the whole build on a finding. It looks for: OpenAI-style keys, bearer tokens, JWTs, AWS access keys,
private-key headers, e-mail addresses, absolute user paths (`/Users/<name>`, `C:\Users\<name>`), IPv4 addresses (loopback and documentation ranges allowed), connection-string passwords,
`*_SECRET/_TOKEN/_PASSWORD=value` assignments and `api_key = value` assignments. Documented placeholders (`sk-REDACTED`, `sk-...`, `Bearer <token>`, empty values) are not findings.
A finding reports file, line and rule, **never the matched text** (at most three leading characters), so the scan output is not itself a leak.

**Known blind spots:** it recognises patterns, not meaning. It cannot tell that a paragraph describes a confidential design, names a customer, or reveals an internal process.
That is why the owner's review comes next.

## Owner review and sign-off
1. Read `corpus-manifest.json` (included files, excluded files and reasons) and skim the frozen copies in `corpus/`.
2. Decide that the content is safe to send to an external model provider.
3. Run `pilot-signoff --by <your-id> --confirm "I reviewed the corpus and it is safe to send to external model providers"`.

The sign-off is bound to a hash of the manifest (version, chunking, every included file hash). If a file, the chunking or the version changes, the sign-off no longer matches and pooling
is refused until the corpus is reviewed again. The corpus is a **frozen copy**: later edits to the repository cannot change what is sent.

## Provider-side controls
This policy does not replace provider settings: prefer a project with a budget/usage limit and, where the account is eligible, zero data retention (OpenAI's guide states it is available
for eligible customers; verify for this account). Jev's data handling is not documented in what was read; do not send anything to it that you would not send publicly.

## What the pilot itself sends
**Nothing.** The pilot is fully offline (BM25, a hashing embedder and an optional local ONNX model). The policy matters for the later phase, which would send the same frozen passages to a provider.
