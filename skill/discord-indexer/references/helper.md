# discord-indexer-search helper

## Purpose

Query the local MongoDB-backed Discord index from the repository checkout.

## Expected execution context

Run from the repository root containing `discord-indexer-search` and `discord-indexer-delta`.

Agent note: do not assume there is a sibling Python entrypoint for delta retrieval unless you have actually verified one exists. Prefer the executable wrapper in the repo (`./discord-indexer-delta`) or the installed binary (`discord-indexer-delta`) when choosing your own command.

## Typical usage

```bash
./discord-indexer-search Quincy
./discord-indexer-search "exact phrase"
./discord-indexer-delta --since 2026-04-09T00:00:00Z
./discord-indexer-delta --since 1712624400000 --guild 1466068838440370258
```

## Behavior notes

- The helpers read `MONGODB_URI` if set; otherwise they default to `mongodb://127.0.0.1:27017`.
- If `mongosh` is installed, they use that directly.
- Otherwise they can fall back to running `mongosh` inside the configured Mongo container.
- `discord-indexer-delta` retrieves messages since a timestamp across all indexed/readable channels by default, with optional `--guild` / `--channel` narrowing.
- Search and delta results include top-level message attachment metadata when present, including Discord attachment URLs.
- `discord-indexer-migrate-attachments` backfills `attachments`, `attachment_count`, and `has_attachments` from existing `raw.attachments` payloads.
- Results are intended for quick operator lookup, not polished end-user formatting.

## Good response pattern

When reporting results to a user:

- say whether the search worked
- summarize the number or character of hits
- quote only the most relevant excerpts
- include ids when names are unavailable
- mention permission limitations plainly when a channel cannot be resolved
