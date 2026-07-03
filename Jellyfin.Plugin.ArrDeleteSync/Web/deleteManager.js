// Web/deleteManager.js
(function () {
  var currentResolution = null;
  var currentItem = null;
  var currentGranularity = null;
  var breakerTripped = false; // best-effort, reactive only — see note below

  function fetchJson(path, options) {
    return ApiClient.ajax(Object.assign({ url: ApiClient.getUrl("ArrDeleteSync/" + path), dataType: "json" }, options || {}));
  }

  function needsTypedConfirmation(granularity, resolution) {
    return !resolution.hasUsableProviderId || granularity === "Series" || granularity === "Season";
  }

  // Jellyfin's native BaseItemDto.Type values ("Movie", "Series", "Season", "Episode", ...) line
  // up 1:1 with this plugin's DeleteGranularity enum names for every type this dashboard supports
  // deleting. Anything else (BoxSet, Folder, etc.) isn't a supported delete target here.
  function mapItemTypeToGranularity(itemType) {
    if (itemType === "Movie" || itemType === "Series" || itemType === "Season" || itemType === "Episode") {
      return itemType;
    }
    return null;
  }

  function renderLibraryResults(items) {
    var list = document.getElementById("LibraryResults");
    list.innerHTML = "";
    items.forEach(function (item) {
      var granularity = mapItemTypeToGranularity(item.Type);
      if (!granularity) {
        return;
      }
      var row = document.createElement("div");
      row.textContent = item.Name + " (" + item.Type + ")";
      row.style.cursor = "pointer";
      row.onclick = function () {
        showPreview(item, granularity);
      };
      list.appendChild(row);
    });
  }

  var searchDebounceHandle = null;
  function searchLibrary(term) {
    var resultsDiv = document.getElementById("LibraryResults");
    if (!term) {
      resultsDiv.innerHTML = "";
      return;
    }
    ApiClient.getItems(ApiClient.getCurrentUserId(), {
      SearchTerm: term,
      IncludeItemTypes: "Movie,Series,Season,Episode",
      Recursive: true,
      Limit: 25
    }).then(function (result) {
      renderLibraryResults((result && result.Items) || []);
    });
  }

  function showPreview(item, granularity) {
    if (breakerTripped) {
      document.getElementById("BreakerBanner").style.display = "block";
      Dashboard.alert("Deletes are disabled until an admin resets the circuit breaker in Settings.");
      return;
    }

    currentItem = item;
    currentGranularity = granularity;
    fetchJson("resolve?itemId=" + item.Id + "&granularity=" + granularity).then(function (resolution) {
      currentResolution = resolution;
      var page = document.getElementById("ArrDeleteSyncManagerPage");
      var content = page.querySelector("#PreviewContent");

      if (!resolution.hasUsableProviderId) {
        content.textContent = "Not identified in Jellyfin (no usable provider ID). Force-deleting " +
          item.Name + " will remove the file WITHOUT syncing arr/Seerr. If this item is actually " +
          "tracked by arr under a different identity, arr will not know it was deleted and is " +
          "likely to re-download it.";
      } else if (resolution.state === "Indeterminate") {
        content.textContent = "Couldn't verify arr/Seerr status right now — try again shortly.";
        page.querySelector("#ConfirmDeleteButton").disabled = true;
        page.querySelector("#PreviewSection").style.display = "block";
        return;
      } else {
        var label = resolution.arrTitle
          ? resolution.arrTitle + " (" + resolution.arrYear + ") — arr-side match"
          : "No arr match found";
        if (resolution.seerrMatchFromFallback) {
          label += " | Seerr fallback match found by title search";
        }
        content.textContent = "Jellyfin shows: " + item.Name + ". " + label +
          ". Deleting is irreversible — recovery requires re-requesting through Seerr.";
      }

      var confirmInput = page.querySelector("#ConfirmNameInput");
      if (needsTypedConfirmation(granularity, resolution)) {
        confirmInput.style.display = "block";
        confirmInput.value = "";
      } else {
        confirmInput.style.display = "none";
      }

      page.querySelector("#ConfirmDeleteButton").disabled = false;
      page.querySelector("#PreviewSection").style.display = "block";
    });
  }

  function confirmDelete(granularity) {
    var page = document.getElementById("ArrDeleteSyncManagerPage");
    var confirmInput = page.querySelector("#ConfirmNameInput");

    if (needsTypedConfirmation(granularity, currentResolution) &&
        confirmInput.value.trim() !== currentItem.Name.trim()) {
      Dashboard.alert("Type the exact item name to confirm this delete.");
      return;
    }

    fetchJson("delete", {
      type: "POST",
      contentType: "application/json",
      data: JSON.stringify({
        jellyfinItemId: currentItem.Id,
        granularity: granularity,
        force: !currentResolution.hasUsableProviderId
      })
    }).then(function (outcome) {
      // The API has no dedicated "circuit breaker tripped" flag/status endpoint — this is the
      // only signal available client-side, and it is necessarily reactive (learned only after an
      // attempt is rejected), not pre-emptive. A future task should add a
      // GET /ArrDeleteSync/circuit-breaker/status endpoint if true pre-attempt client-side
      // blocking is required; see the task report for detail.
      if (outcome.blockedReason && outcome.blockedReason.indexOf("Circuit breaker") !== -1) {
        breakerTripped = true;
        document.getElementById("BreakerBanner").style.display = "block";
      }

      var message = outcome.blockedReason ||
        (outcome.queuedForRetry ? "Some steps queued for retry — see Retry Queue below." : "Deleted.");
      if (outcome.requiresManualFileCleanup && outcome.filePath) {
        message += " The file was NOT deleted (arr doesn't track it) — remove it manually at: " + outcome.filePath;
      }
      Dashboard.alert(message);
      page.querySelector("#PreviewSection").style.display = "none";
      refreshRetryQueue();
      refreshAuditLog();
    });
  }

  function refreshRetryQueue() {
    fetchJson("retry-queue").then(function (entries) {
      var list = document.getElementById("RetryQueueList");
      list.innerHTML = "";
      entries.forEach(function (entry) {
        var row = document.createElement("div");
        row.textContent = entry.jellyfinItemId + " — attempts: " + entry.attemptCount + " — last error: " + (entry.lastError || "");
        var retryBtn = document.createElement("button");
        retryBtn.textContent = "Retry now";
        retryBtn.onclick = function () {
          fetchJson("retry-queue/" + entry.id + "/retry", { type: "POST" }).then(refreshRetryQueue);
        };
        var dismissBtn = document.createElement("button");
        dismissBtn.textContent = "Give up";
        dismissBtn.onclick = function () {
          // Field names reflect the corrected architecture: *arr now owns file deletion, so
          // "arrDeleteStatus === Succeeded" means the file is already gone; Jellyfin's role is
          // catalog-only cleanup ("jellyfinCleanupStatus"), not an "untrack" step.
          if (entry.arrDeleteStatus === "Succeeded" &&
              (entry.jellyfinCleanupStatus !== "Succeeded" || entry.seerrUpdateStatus !== "Succeeded")) {
            if (!confirm("The file is already deleted but arr/Seerr sync is incomplete — arr may re-download this. Give up anyway?")) {
              return;
            }
          }
          fetchJson("retry-queue/" + entry.id + "/dismiss", { type: "POST" }).then(refreshRetryQueue);
        };
        row.appendChild(retryBtn);
        row.appendChild(dismissBtn);
        list.appendChild(row);
      });
    });
  }

  function refreshAuditLog() {
    fetchJson("audit-log").then(function (entries) {
      var list = document.getElementById("AuditLogList");
      list.innerHTML = "";
      entries.slice().reverse().forEach(function (entry) {
        var row = document.createElement("div");
        row.textContent = entry.timestampUtc + " — " + entry.itemDisplayName + " — " + entry.action + " — " + entry.outcome;
        list.appendChild(row);
      });
    });
  }

  document.addEventListener("pageshow", function (e) {
    if (!e.target.id || e.target.id !== "ArrDeleteSyncManagerPage") return;
    refreshRetryQueue();
    refreshAuditLog();
  });

  document.addEventListener("input", function (e) {
    if (e.target.id !== "LibrarySearch") return;
    var term = e.target.value;
    if (searchDebounceHandle) {
      clearTimeout(searchDebounceHandle);
    }
    searchDebounceHandle = setTimeout(function () {
      searchLibrary(term);
    }, 300);
  });

  document.addEventListener("click", function (e) {
    if (e.target.id === "CancelDeleteButton") {
      document.getElementById("PreviewSection").style.display = "none";
    }
    if (e.target.id === "ConfirmDeleteButton") {
      if (breakerTripped) {
        document.getElementById("BreakerBanner").style.display = "block";
        Dashboard.alert("Deletes are disabled until an admin resets the circuit breaker in Settings.");
        return;
      }
      confirmDelete(currentGranularity);
    }
  });
})();
