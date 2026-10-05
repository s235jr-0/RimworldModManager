namespace RimModManager.Core;

public sealed class InstallJob
{
    public required WorkshopDetails Details { get; init; }
    public required string Destination { get; init; }
    public ModEntry? Entry { get; init; }     // grid row; null on the Install tab
    public bool Succeeded { get; set; }
    public string FailureReason { get; set; } = "";
}

public sealed record InstallListResult(List<InstallJob> Jobs, int Skipped);

// Downloading and installing mods, and everything that protects an existing
// install while doing so.
public sealed class ModInstaller
{
    public const string RimWorldAppId = "294100";
    public const string BackupFolderPrefix = "RWBackup_";

    // Mods per SteamCMD run. Each run pays startup + login once; 10 cuts that
    // overhead by 90% while keeping a failed group small to retry.
    public const int SteamBatchSize = 10;

    private readonly SteamCmd _steam;
    private readonly StateStore _state;
    private readonly ActivityLog _log;
    private readonly Cleanup _cleanup;

    // Forgets where a deleted mod came from (set by the app).
    public Sources.SourceRegistry? Sources { get; set; }

    public ModInstaller(SteamCmd steam, StateStore state, ActivityLog log, Cleanup cleanup)
    {
        _steam = steam;
        _state = state;
        _log = log;
        _cleanup = cleanup;
    }

    // ------------------------------------------------------------------
    // Install tab
    // ------------------------------------------------------------------

    // Resolves pasted IDs/collections, skips mods that are installed AND at
    // the saved Steam version, and installs the rest.
    public InstallListResult InstallFromIds(
        List<string> roots, string modsRoot, bool backupExisting, Action<string> status)
    {
        List<WorkshopDetails> items = SteamApi.ResolveItems(roots, status);

        int rejected = items.Count(d => d.AppId != RimWorldAppId);
        if (rejected > 0)
            throw new Exception(
                "This is the RimWorld-specific manager. " + rejected +
                " resolved Workshop item(s) belong to another Steam app.");

        status("Indexing installed RimWorld mods...");
        Dictionary<string, string> installedMap = ModMetadata.BuildInstalledWorkshopMap(modsRoot);

        List<InstallJob> jobs = new();
        int skipped = 0;
        int processed = 0;

        // Preflight the real Mods folder BEFORE invoking SteamCMD. A successful
        // install saves its Workshop time immediately, so rerunning an
        // interrupted list resumes without reprocessing completed items.
        foreach (WorkshopDetails d in items)
        {
            processed++;
            installedMap.TryGetValue(d.Id, out string? existing);
            long adoptedTime = _state.Get(d.Id);

            bool installedAndCurrent =
                !String.IsNullOrWhiteSpace(existing) &&
                Directory.Exists(existing) &&
                adoptedTime > 0 &&
                adoptedTime == d.TimeUpdated;

            if (installedAndCurrent)
            {
                skipped++;
                status("Already current - skipped " + processed + "/" + items.Count + ": " + d.Title);
                _log.Add("SKIP", d.Title + " [" + d.Id + "] already installed and current -> " + existing);
                continue;
            }

            // Updates keep an existing human-readable folder name.
            jobs.Add(new InstallJob
            {
                Details = d,
                Destination = String.IsNullOrWhiteSpace(existing) ? Path.Combine(modsRoot, d.Id) : existing,
            });
        }

        RunInstallJobs(jobs, backupExisting, modsRoot, status, (j, message) => status(j.Details.Title + ": " + message));

        _log.Add("SUCCESS",
            "Install list finished: " +
            jobs.Count(j => j.Succeeded) + " installed/updated, " +
            jobs.Count(j => !j.Succeeded) + " failed, " +
            skipped + " already-current skipped, " +
            items.Count + " total.");

        return new InstallListResult(jobs, skipped);
    }

    // ------------------------------------------------------------------
    // Installed / Updates tab
    // ------------------------------------------------------------------

    public void CheckForUpdates(List<ModEntry> known, IModEntrySink sink, Action<string> status)
    {
        const int batchSize = 20;
        int checkedCount = 0;

        for (int start = 0; start < known.Count; start += batchSize)
        {
            List<ModEntry> batch = known.Skip(start).Take(batchSize).ToList();
            foreach (ModEntry e in batch) sink.SetStatus(e, "Checking...");

            string label = "Checking Steam " + (checkedCount + 1) + "-" +
                           Math.Min(checkedCount + batch.Count, known.Count) + " / " + known.Count;
            status(label);

            Dictionary<string, WorkshopDetails> details;
            try
            {
                details = SteamApi.GetDetailsWithRetry(batch.Select(m => m.WorkshopId), label, 3, status);
            }
            catch
            {
                foreach (ModEntry e in batch) sink.SetStatus(e, "Check error");
                throw;
            }

            foreach (ModEntry e in batch)
            {
                checkedCount++;

                if (!details.TryGetValue(e.WorkshopId, out WorkshopDetails? d) || d.Result != 1)
                {
                    sink.SetStatus(e, "Missing / removed");
                    status("Checked " + checkedCount + "/" + known.Count + ": " + e.FolderName + " - missing/removed");
                    continue;
                }

                sink.SetDetails(e, d);

                long localKnownTime = _state.Get(e.WorkshopId);
                string result =
                    localKnownTime == 0 ? "Unknown (update once)" :
                    localKnownTime >= d.TimeUpdated ? "Current" :
                    "Update available";

                // Keep a failed update visible until it succeeds.
                if (result != "Current" && _state.GetFailed(e.WorkshopId) != null)
                    result = ModMetadata.FailedLastUpdateStatus;

                sink.SetStatus(e, result);
                status("Checked " + checkedCount + "/" + known.Count + ": " +
                       (String.IsNullOrWhiteSpace(d.Title) ? e.FolderName : d.Title) + " - " + result);
            }
        }
    }

    // Updates the given grid rows in place.
    public List<InstallJob> UpdateMods(
        List<ModEntry> targets, string modsRoot, bool backup, IModEntrySink sink, Action<string> status)
    {
        Dictionary<string, WorkshopDetails> details = new();
        const int batchSize = 20;
        int resolved = 0;

        // Resolve current Steam metadata in visible batches.
        for (int start = 0; start < targets.Count; start += batchSize)
        {
            List<ModEntry> batch = targets.Skip(start).Take(batchSize).ToList();
            foreach (ModEntry e in batch) sink.SetStatus(e, "Checking...");

            string label = "Preparing update " + (resolved + 1) + "-" +
                           Math.Min(resolved + batch.Count, targets.Count) + " / " + targets.Count;
            status(label);

            Dictionary<string, WorkshopDetails> got =
                SteamApi.GetDetailsWithRetry(batch.Select(m => m.WorkshopId), label, 3, status);

            foreach (KeyValuePair<string, WorkshopDetails> kv in got)
                details[kv.Key] = kv.Value;

            foreach (ModEntry e in batch)
            {
                resolved++;

                if (!got.TryGetValue(e.WorkshopId, out WorkshopDetails? d) || d.Result != 1)
                {
                    sink.SetStatus(e, "Missing / removed");
                    continue;
                }

                sink.SetDetails(e, d);
                sink.SetStatus(e, "Ready to download");
            }
        }

        List<InstallJob> jobs = targets
            .Where(m => details.TryGetValue(m.WorkshopId, out WorkshopDetails? d) && d.Result == 1)
            .Select(m => new InstallJob { Details = details[m.WorkshopId], Destination = m.FolderPath, Entry = m })
            .ToList();

        RunInstallJobs(jobs, backup, modsRoot, status, (j, message) => sink.SetStatus(j.Entry!, message));
        return jobs;
    }

    // Deletes installed mod folders (never anything outside modsRoot).
    public (List<ModEntry> Deleted, List<string> Failures) DeleteMods(
        List<ModEntry> selected, string modsRoot, Action<string> status)
    {
        List<ModEntry> deleted = new();
        List<string> failures = new();

        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRoot)) + Path.DirectorySeparatorChar;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = 0;

        foreach (ModEntry e in selected)
        {
            index++;
            status("Deleting " + index + "/" + selected.Count + ": " + e.FolderName);

            try
            {
                string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.FolderPath));

                if (!(candidate + Path.DirectorySeparatorChar).StartsWith(rootFull, cmp) ||
                    candidate.Length + 1 <= rootFull.Length)
                    throw new InvalidOperationException("Refusing to delete a folder outside the selected Mods directory.");

                bool wasLink = SafeFileSystem.IsLink(candidate);
                SafeFileSystem.DeleteDirectory(candidate);

                _log.Add("DELETE",
                    e.FolderName +
                    (wasLink ? " (link removed; the files it pointed to were not touched)" : "") +
                    " -> " + candidate);

                if (!String.IsNullOrWhiteSpace(e.WorkshopId))
                    _state.Remove(e.WorkshopId);
                Sources?.Remove(e.FolderName);

                deleted.Add(e);
            }
            catch (Exception ex)
            {
                failures.Add(e.FolderName + ": " + ex.Message);
            }
        }

        _state.Save();
        return (deleted, failures);
    }

    // ------------------------------------------------------------------
    // Shared pipeline
    // ------------------------------------------------------------------

    //   1. download up to SteamBatchSize mods in one SteamCMD run,
    //   2. retry the ones that failed once, as a smaller group,
    //   3. install one by one, saving state after each (resumable).
    // A failing mod never stops the run: its previous version is kept or
    // restored, it is flagged as failed (remembered in state.json), and the
    // run continues with the next mod.
    public void RunInstallJobs(
        List<InstallJob> jobs,
        bool backup,
        string modsRoot,
        Action<string> status,
        Action<InstallJob, string> setStatus)
    {
        int total = jobs.Count;
        int done = 0;

        for (int start = 0; start < total; start += SteamBatchSize)
        {
            List<InstallJob> batch = jobs.Skip(start).Take(SteamBatchSize).ToList();
            string range = (start + 1) + "-" + (start + batch.Count) + " of " + total;

            foreach (InstallJob j in batch) setStatus(j, "Downloading...");

            Dictionary<string, string> failures = DownloadGroup(batch, "Downloading " + range, status);

            List<InstallJob> retry = batch.Where(j => failures.ContainsKey(j.Details.Id)).ToList();
            if (retry.Count > 0)
            {
                foreach (InstallJob j in retry) setStatus(j, "Retrying download...");

                // Only items that fail twice remain failed.
                failures = DownloadGroup(retry, "Retrying " + retry.Count + " failed download(s) from " + range, status);
            }

            foreach (InstallJob j in batch)
            {
                done++;
                WorkshopDetails d = j.Details;
                bool hadPrevious = Directory.Exists(j.Destination);

                if (failures.TryGetValue(d.Id, out string? reason))
                {
                    FlagFailed(j, "Download failed: " + reason, hadPrevious ? "previous version kept" : "not installed", setStatus);
                    continue;
                }

                _log.Add("DOWNLOAD", d.Title + " [" + d.Id + "] -> " + _steam.CachePath(d.AppId, d.Id));

                string? backupPath = null;

                try
                {
                    if (backup && hadPrevious)
                    {
                        setStatus(j, "Backing up...");
                        status("Installing " + done + "/" + total + ": backing up " + d.Title);

                        backupPath = CreateDatedBackup(modsRoot, j.Destination, d.Id, Path.GetFileName(j.Destination));
                        _log.Add("BACKUP", d.Title + " [" + d.Id + "] -> " + backupPath);
                    }

                    setStatus(j, "Installing...");
                    status("Installing " + done + "/" + total + ": " + d.Title);

                    ReplaceModFromSource(_steam.CachePath(d.AppId, d.Id), j.Destination);

                    // Save immediately after each successful install. This is
                    // what makes a later rerun resumable.
                    _state.Set(d.Id, d.TimeUpdated);
                    _state.ClearFailed(d.Id);
                    _state.Save();

                    j.Succeeded = true;
                    _log.Add("INSTALL", d.Title + " [" + d.Id + "] -> " + j.Destination);
                    setStatus(j, hadPrevious ? "Updated" : "Installed");
                }
                catch (Exception ex)
                {
                    FlagFailed(j, "Install failed: " + ex.Message, RevertAfterFailedInstall(j.Destination, hadPrevious, backupPath), setStatus);
                }
            }
        }

        if (_state.Data.CleanupAutomatically)
        {
            status("Automatic cleanup...");
            _cleanup.RunAutomatic(modsRoot, status);
        }
    }

    // One SteamCMD group; a problem with the whole run (e.g. SteamCMD failing
    // to start) counts as a failure for every item in the group.
    private Dictionary<string, string> DownloadGroup(
        List<InstallJob> group, string label, Action<string> status)
    {
        status(label + "...");

        try
        {
            return _steam.Download(group.Select(j => j.Details).ToList(), m => status(label + ": " + m));
        }
        catch (Exception ex)
        {
            return group.ToDictionary(j => j.Details.Id, _ => ex.Message);
        }
    }

    // Called after ReplaceModFromSource threw. That method already moves the
    // previous version back when it can; this confirms the result and falls
    // back to the dated backup if the previous version is gone.
    internal static string RevertAfterFailedInstall(string destination, bool hadPrevious, string? backupPath)
    {
        if (!hadPrevious)
        {
            try { SafeFileSystem.DeleteDirectory(destination); }
            catch { }
            return "not installed";
        }

        if (ModMetadata.IsModFolder(destination))
            return "reverted to previous version";

        if (!String.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
        {
            try
            {
                SafeFileSystem.DeleteDirectory(destination);
                SafeFileSystem.CopyDirectory(backupPath, destination);
                EnsureValidModFolder(destination, "Restored mod");
                return "reverted to previous version from backup";
            }
            catch (Exception ex)
            {
                return "COULD NOT REVERT (" + ex.Message + "); backup is at " + backupPath;
            }
        }

        return "COULD NOT REVERT; see the error message for where the previous version is";
    }

    private void FlagFailed(InstallJob j, string reason, string outcome, Action<InstallJob, string> setStatus)
    {
        WorkshopDetails d = j.Details;
        j.Succeeded = false;
        j.FailureReason = reason + " -> " + outcome;

        _state.MarkFailed(d.Id, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ": " + j.FailureReason);
        try { _state.Save(); } catch { }

        _log.Add("ERROR", d.Title + " [" + d.Id + "] " + j.FailureReason);
        setStatus(j, "FAILED - " + outcome);
    }

    // ------------------------------------------------------------------
    // Staged replace + backups
    // ------------------------------------------------------------------

    internal static void EnsureValidModFolder(string folder, string description)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(description + " folder was not found: " + folder);

        if (!ModMetadata.IsModFolder(folder))
            throw new Exception(description + " is incomplete: About/About.xml is missing." + Environment.NewLine + folder);
    }

    // Copy into a staging folder OUTSIDE RimWorld's Mods folder, verify it,
    // move the old version aside, move the new one in, verify again. If
    // anything fails after the old version was moved, it is moved back.
    internal static void ReplaceModFromSource(string source, string destination)
    {
        EnsureValidModFolder(source, "Downloaded Workshop mod");

        string? modsRoot = Path.GetDirectoryName(destination);
        if (String.IsNullOrWhiteSpace(modsRoot))
            throw new Exception("Invalid destination: " + destination);

        string safeRoot = Directory.GetParent(modsRoot)?.FullName ?? modsRoot;
        string token = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        string stageRoot = Path.Combine(safeRoot, "_RWMM_STAGE");
        string oldRoot = Path.Combine(safeRoot, "_RWMM_OLD");
        string stage = Path.Combine(stageRoot, token);
        string old = Path.Combine(oldRoot, token);

        bool oldMoved = false;
        bool committed = false;

        Directory.CreateDirectory(stageRoot);
        Directory.CreateDirectory(oldRoot);

        try
        {
            SafeFileSystem.CopyDirectory(source, stage);
            EnsureValidModFolder(stage, "Staged mod");

            if (Directory.Exists(destination))
            {
                Directory.Move(destination, old);
                oldMoved = true;
            }

            try
            {
                Directory.Move(stage, destination);
                EnsureValidModFolder(destination, "Installed mod");
                committed = true;
            }
            catch (Exception installError)
            {
                try { SafeFileSystem.DeleteDirectory(destination); }
                catch { }

                if (oldMoved && Directory.Exists(old) && !Directory.Exists(destination))
                {
                    try
                    {
                        Directory.Move(old, destination);
                        oldMoved = false;
                    }
                    catch (Exception restoreError)
                    {
                        // Keep the original reason; the previous version stays
                        // safe in _RWMM_OLD (never deleted here).
                        throw new IOException(
                            installError.Message +
                            " The previous version could not be moved back (" + restoreError.Message +
                            "); it is kept at: " + old,
                            installError);
                    }
                }

                throw;
            }

            // The new mod is committed. Cleanup failure here must never turn a
            // successful install into a broken rollback; the old copy is
            // outside Mods, so RimWorld can't load it as a duplicate.
            if (committed && oldMoved && Directory.Exists(old))
            {
                try { SafeFileSystem.DeleteDirectory(old); }
                catch { }
            }
        }
        finally
        {
            try { SafeFileSystem.DeleteDirectory(stage); } catch { }
            TryRemoveIfEmpty(stageRoot);
            TryRemoveIfEmpty(oldRoot);
        }
    }

    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, false);
        }
        catch { }
    }

    // Backups live two levels above Mods (e.g. C:\GOG Games\RWBackup_yyyyMMdd
    // for C:\GOG Games\RimWorld\Mods), outside anything RimWorld loads.
    public static string BackupParentFor(string modsRoot)
    {
        string modsFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRoot));
        DirectoryInfo? rimWorldDir = new DirectoryInfo(modsFull).Parent;

        return rimWorldDir?.Parent != null ? rimWorldDir.Parent.FullName : modsFull;
    }

    internal static string CreateDatedBackup(string modsRoot, string sourceFolder, string workshopId, string folderName)
    {
        // Short backup root on purpose (v2 history: long paths).
        string backupRoot = Path.Combine(BackupParentFor(modsRoot), BackupFolderPrefix + DateTime.Now.ToString("yyyyMMdd"));

        string safeFolderName =
            !String.IsNullOrWhiteSpace(folderName) ? folderName :
            !String.IsNullOrWhiteSpace(workshopId) ? workshopId :
            "UnknownMod";

        string backupDir = Path.Combine(backupRoot, safeFolderName);
        if (Directory.Exists(backupDir))
            backupDir = Path.Combine(backupRoot, safeFolderName + "_" + DateTime.Now.ToString("HHmmss"));

        Directory.CreateDirectory(backupRoot);
        SafeFileSystem.CopyDirectory(sourceFolder, backupDir);
        return backupDir;
    }
}
