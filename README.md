# ArrDeleteSync

A Jellyfin plugin that keeps media deletions in sync across your arr stack and request platform.

When you delete a movie or TV show from Jellyfin, **ArrDeleteSync** coordinates the deletion with Radarr, Sonarr, and Seerr — removing the file from disk, cleaning up the catalog metadata, and updating request approvals. Built for homelab operators who want one-click deletion without orphaning files or leaving stale entries.

## Features

- **Multi-level granularity** — Delete individual episodes, entire seasons, whole series, or movies
- **Arr integration** — Confirms tracking in Radarr/Sonarr before deletion and coordinates file removal
- **Seerr sync** — Updates media availability in Seerr when requests are completed
- **Retry queue** — Failed deletions are automatically retried with exponential backoff; view and manage retries via the UI
- **Circuit breaker** — Stops making API calls if repeated failures are detected, preventing cascading issues
- **Force delete** — Remove untracked content from Jellyfin even without provider IDs (file cleanup is manual)
- **Comprehensive validation** — Checks file boundaries (e.g., won't delete an episode file that contains multiple episodes), verifies arr tracking before proceeding
- **Audit log** — Full history of all delete actions with 15-day retention; searchable via the UI
- **Secure API keys** — Keys are encrypted and never stored in plaintext on disk
- **Admin-only access** — All operations require Jellyfin admin elevation

## How It Works

### Deletion Flow

1. **Admin initiates delete** from Jellyfin (via dashboard or client)
2. **Provider ID resolution** — Plugin looks up the item's TVDB/TMDB ID
3. **Arr verification** — Queries Radarr/Sonarr to confirm the item is tracked and verify file boundaries
4. **File deletion** — If tracked, arr deletes the file from disk (arr owns the filesystem)
5. **Catalog cleanup** — Jellyfin removes metadata from its database
6. **Seerr sync** — Updates availability if the item is tracked in Seerr
7. **Audit log** — Records the action for history and troubleshooting

If any step fails, the entry is queued for automatic retry.

### Retry Mechanism

- Failed deletions are stored in a persistent queue
- A scheduled task retries queued items every 5 minutes
- Retry backoff increases exponentially, starting at the configured base delay (default: 300 seconds)
- Retries stop after the configured maximum attempts (default: 5)
- Manual retry or dismissal available from the Delete Manager UI
- Both thresholds are configurable in **ArrDeleteSync Settings**

### Circuit Breaker

If the configured failure threshold is reached within the configured window (defaults: 5 failures / 15 minutes):
- The circuit breaker **trips** and blocks all deletion attempts
- Protects against cascading failures (e.g., Radarr down, network issue)
- Admin must manually reset the breaker from the plugin settings
- Breaker state survives a Jellyfin restart
- Threshold and window are configurable in **ArrDeleteSync Settings**

## Configuration

Navigate to **Dashboard → Plugins → ArrDeleteSync Settings** to configure:

| Setting | Default | Description |
|---------|---------|-------------|
| **Radarr URL** | (required) | Base URL of your Radarr instance, e.g., `http://radarr:7878` |
| **Radarr API Key** | (required) | API key from Radarr settings (Settings → General → API Key) |
| **Sonarr URL** | (required) | Base URL of your Sonarr instance, e.g., `http://sonarr:8989` |
| **Sonarr API Key** | (required) | API key from Sonarr settings (Settings → General → API Key) |
| **Seerr URL** | (optional) | Base URL of your Seerr instance; omit to skip Seerr sync |
| **Seerr API Key** | (optional) | API key from Seerr settings (Settings → General → API Key) |
| **Retry Backoff Base (seconds)** | 300 | Initial delay before first retry; subsequent retries use exponential backoff |
| **Max Retry Attempts** | 5 | How many times to retry a failed deletion before giving up |
| **Circuit Breaker Threshold** | 5 | Consecutive failures within the window before tripping |
| **Circuit Breaker Window (minutes)** | 15 | Time window for consecutive failure tracking |
| **Audit Log Retention (days)** | 15 | How long to keep deletion audit logs |

**API key security:** Keys are encrypted using Jellyfin's data protection API and never stored in plaintext. The encryption keyring is kept separate from the configuration file for additional security.

## Requirements

- **Jellyfin 10.11.x** or compatible
- **Radarr** and/or **Sonarr** (at least one required)
- **Seerr** (optional; omit configuration to skip request approval updates)
- API keys with read/write permissions in your arr/Seerr instances

## Installation

### Plugin Catalog (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **+** and add:
   ```
   https://raw.githubusercontent.com/perrin-g/arr-delete-sync/main/manifest.json
   ```
3. Go to **Dashboard → Plugins → Catalog**, find **ArrDeleteSync**, and click **Install**
4. Restart Jellyfin when prompted
5. Go to **ArrDeleteSync Settings** and configure your arr/Seerr URLs and API keys

Future updates will appear in **Dashboard → Plugins** and can be applied in-app.

### Manual Installation

1. Download the latest release `.zip` from [GitHub Releases](https://github.com/perrin-g/arr-delete-sync/releases)
2. Extract and copy `Jellyfin.Plugin.ArrDeleteSync.dll` to your Jellyfin plugins directory:
   - **Bare metal:** `~/.config/jellyfin/plugins/`
   - **Docker:** inside the `/config/plugins/` mount
   - **Other:** check your Jellyfin data path at Dashboard → About
3. Restart Jellyfin
4. Navigate to **Dashboard → Plugins** and verify **ArrDeleteSync** appears in the list
5. Go to **ArrDeleteSync Settings** and configure your arr/Seerr URLs and API keys

### Testing Your Configuration

Before trusting the plugin with real deletions:

1. In **ArrDeleteSync Settings**, use the "Test Connection" button for each service
2. Ensure all tests pass (green checkmarks)
3. Start with a dummy/test item to verify the flow end-to-end

## UI

Both pages are in the Jellyfin **admin dashboard** (`/web/#/dashboard`), not the user-facing media interface. They appear in the server sidebar under the plugin's menu entries and are only visible to admin accounts.

### Settings Page

Navigate to **Dashboard → ArrDeleteSync Settings**:

- Configure Radarr, Sonarr, and Seerr connection details
- Test connections to verify API access
- Adjust retry policy and circuit breaker thresholds
- View and reset the circuit breaker status
- Clear audit logs (or let them auto-expire after 15 days)

### Delete Manager

Navigate to **Dashboard → Delete Manager**:

- Manually retry failed deletions
- Dismiss items from the retry queue (marks as abandoned, logs the action)
- View detailed status of each retry entry:
  - Which step failed (arr delete, Jellyfin cleanup, or Seerr sync)
  - The error message
  - Next retry time
  - Whether manual file cleanup is required

### Audit Log

Accessible from the Delete Manager page:

- Complete history of all deletion attempts
- Search by item name or filter by success/failure
- Timestamps and error details for troubleshooting

## Limitations

### Untracked Content

- If an item has no TVDB/TMDB ID in Jellyfin, the plugin can't verify arr tracking
- **Force delete** option removes the item from Jellyfin anyway, but **the file remains on disk** — you must clean it up manually
- Useful for orphaned catalog entries that never made it to arr

### Episode-Level Granularity

- Only works for tracked content (requires arr confirmation)
- Blocks deletion if a single file contains multiple episodes (not supported)
- Prefer season-level or series-level deletion for complex multi-episode files

### Virtual Seasons

- Some Jellyfin layouts don't have physical per-season folders (e.g., all episodes in one folder)
- Season-level deletion blocked for these layouts
- Use series-level delete instead

### Seerr TVDB-Only Content

- If an item has a TVDB ID but no TMDB ID, Seerr updates are best-effort
- Fallback is skipped if no matching TMDB ID found in Seerr
- The deletion still succeeds; Seerr sync is simply unavailable for this item

## Troubleshooting

### Circuit Breaker Is Tripped

**Symptom:** All deletions blocked with "Circuit breaker is tripped" message.

**Fix:**
1. Check the Jellyfin logs for errors from the retry task
2. Verify Radarr, Sonarr, and Seerr are online and responding
3. Confirm API keys are correct and haven't expired
4. Once the issue is resolved, go to **ArrDeleteSync Settings** → **Reset Circuit Breaker**

### Item Won't Delete

1. Check the **Delete Manager** → **Retry Queue** for detailed error messages
2. **No provider ID error:** The item has no TVDB/TMDB ID in Jellyfin — either add it manually, or use force-delete (file stays on disk)
3. **Indeterminate arr status:** Radarr/Sonarr query timed out — the plugin will retry automatically
4. **Episode file covers multiple episodes:** Not supported — delete the season or series instead
5. **Structural failure (Jellyfin cleanup):** Metadata removal failed due to database lock or permission issue — retry once Jellyfin is less busy, or check system permissions

### Manual File Cleanup Required

**When:** Force-deleted an untracked item from Jellyfin, and the audit log says "requires manual file cleanup."

**Fix:**
1. Note the file path from the **Delete Manager** entry
2. Manually delete the file from your storage
3. Run **Library Scan** in Jellyfin to verify it's no longer visible

## Logs

Plugin logs are written to Jellyfin's main logs:
- **Bare metal:** `~/.config/jellyfin/logs/`
- **Docker:** Check container logs with `docker logs jellyfin`

Search for `ArrDeleteSync` to isolate plugin messages.

## Development & Contributing

The plugin is written in C# / .NET 9 and compiled against Jellyfin 10.11.x APIs. See the repository for unit tests and architecture details.

---

**Built with Claude Code** | [GitHub](https://github.com/perrin-g/arr-delete-sync)
