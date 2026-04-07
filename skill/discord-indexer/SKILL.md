---
name: discord-indexer
description: Search and inspect a local Discord message index backed by MongoDB using the repository's discord-indexer-search helper. Use when a user asks to find prior Discord messages, test whether indexing works, look up mentions of a person, phrase, or topic, inspect results by channel or time, or troubleshoot whether a Discord archive/index contains expected content.
---

# Discord Indexer

Use the local helper script from the repository root:

```bash
./discord-indexer-search <query>
```

## Workflow

1. Run searches from the repo root so the helper script is available.
2. Start with a narrow literal query when possible: a name, exact phrase, channel idea, or identifier.
3. If the first query is noisy, refine with more distinctive terms instead of repeating the same broad search.
4. Return a short summary plus a few representative hits with timestamp, guild/channel ids, author, and message excerpt.
5. If a search fails, check whether the indexer service and MongoDB are running before assuming the data is missing.

## Output handling

The helper prints tab-separated rows in this shape:

- ISO timestamp
- guild id
- channel id
- author
- message text

Preserve ids when reporting uncertain/private channels. Do not guess channel names you cannot resolve.

## Troubleshooting

If search output is empty or errors:

- check `systemctl status discord-indexer.service`
- check whether MongoDB is reachable at the configured `MONGODB_URI`
- inspect recent indexer logs in `/var/log/discord-indexer/`
- confirm the target channel is actually accessible to the bot; private channels may produce partial metadata and 403s

## References

- For helper behavior and Mongo execution details, read `references/helper.md`.
