Real API responses captured read-only from a live Sonarr 4.0.20 instance on 2026-11-07.
Tests that parse Sonarr responses must use these (or newly captured ones), never hand-written JSON:
a hand-written fake once invented an `episodeIds` field Sonarr doesn't return, so tests passed while
every live episode delete was blocked.

- `*-from.*`   FROM (series 20, continuing): S1-S4 unmonitored, no files; S5 has one monitored placeholder episode.
- `*-fawlty.*` Fawlty Towers (series 8, ended): S1+S2 monitored with files; 30 unmonitored specials.
- `series-list.json` real `GET /api/v3/series` response trimmed to 4 of its entries (entries verbatim):
  FROM, Lioness, Rick and Morty (monitored, 0 files after keep-series deletes) and Fawlty Towers (has files).
