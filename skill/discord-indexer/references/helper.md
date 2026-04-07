# discord-indexer-search helper

## Purpose

Query the local MongoDB-backed Discord index from the repository checkout.

## Expected execution context

Run from the repository root containing `discord-indexer-search`.

## Typical usage

```bash
./discord-indexer-search Quincy
./discord-indexer-search "exact phrase"
```

## Behavior notes

- The helper reads `MONGODB_URI` if set; otherwise it defaults to `mongodb://127.0.0.1:27017`.
- If `mongosh` is installed, it uses that directly.
- Otherwise it can fall back to running `mongosh` inside the configured Mongo container.
- Results are intended for quick operator lookup, not polished end-user formatting.

## Good response pattern

When reporting results to a user:

- say whether the search worked
- summarize the number or character of hits
- quote only the most relevant excerpts
- include ids when names are unavailable
- mention permission limitations plainly when a channel cannot be resolved
