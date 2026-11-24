<#
 Copies the code into an isolated folder, plants known bugs one at a time and checks that the tests that guard against
 each one fail. The working tree and the packages are never changed; the logs stay under artifacts/ for review.

 -Set Core (default): no window; rebuilds and runs the named test classes in the copy.
 -Set Window: publishes the mutated program from the copy and runs the window test class on it. Takes over the desktop.
 -Only <name>: just this mutation.
 Exit code 0 only when every mutation was caught. A mutation that does not compile or whose code has moved is an error, not a pass.
#>
[CmdletBinding()]
param([ValidateSet('Core', 'Window')][string]$Set = 'Core', [string]$Only)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
$unit = 'tests/VamSys.UnitTests/VamSys.UnitTests.csproj'
$integration = 'tests/VamSys.IntegrationTests/VamSys.IntegrationTests.csproj'
$failureHandler = 'src/VamSys.Infrastructure/Api/Pipeline/FailureHandler.cs'
$writeSafety = 'VamSys.IntegrationTests.Api.WriteSafetyTableTests'
$lostPost = 'VamSys.IntegrationTests.Api.AircraftLostPostTests'
$recoveryStage = 'src/VamSys.Infrastructure/Api/Pipeline/RecoveryStage.cs'
$finder = 'src/VamSys.Infrastructure/Api/CreatedRecordFinder.cs'

$core = @(
    # Never send twice, and what counts as unknown.
    @{ Name = '500-not-unknown'; File = $failureHandler; From = '|| phase == Phase.Writing && (int)e.Status >= 500 ? ItemState.Unknown'; To = '? ItemState.Unknown'; Project = $integration; Class = $writeSafety }
    @{ Name = 'timeout-to-pending'; File = $failureHandler; From = 'item.State = phase != Phase.Preflight ? ItemState.Unknown : ItemState.Pending;'; To = 'item.State = ItemState.Pending;'; Project = $integration; Class = $writeSafety }
    @{ Name = '429-not-pending'; File = $failureHandler; From = ': throttled || phase == Phase.Preflight && (int)e.Status >= 500 ? ItemState.Pending : ItemState.Failed;'; To = ': phase == Phase.Preflight && (int)e.Status >= 500 ? ItemState.Pending : ItemState.Failed;'; Project = $integration; Class = $writeSafety }
    @{ Name = 'unknown-not-recovered'; File = 'src/VamSys.Infrastructure/Api/Pipeline/ExecutionPipeline.cs'; From = 'var recovering = item.State is ItemState.Running or ItemState.Unknown || item.CreateCompleted || item.WriteAccepted || item.CreateFoundWithoutAnswer;'; To = 'var recovering = item.State is ItemState.Running || item.CreateCompleted || item.WriteAccepted || item.CreateFoundWithoutAnswer;'; Project = $integration; Class = $writeSafety }
    @{ Name = 'discard-after-sending'; File = 'src/VamSys.Domain/ApiEvidence.cs'; From = "`n        && item.ApiSteps.All(s => s.State == BatchStepState.Prepared);"; To = ';'; Project = $integration; Class = 'VamSys.IntegrationTests.Api.DiscardUnsentTests' }
    # A create whose POST answer was lost and whose later steps never left (an aircraft with internal remarks): its record
    # is found by the POST's values and bound, never resumed; only accepting the server's value settles it.
    @{ Name = 'lost-create-all-values'; File = 'src/VamSys.Infrastructure/Api/ReadbackVerifier.cs'; From = "var verdict = UnknownResolver.CanBindLostCreate(item)`n            ? Verify(item, current, item.After, PostValues(item)) : VerifyPlanned(item, current, item.After);"; To = 'var verdict = VerifyPlanned(item, current, item.After);'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-not-bound'; File = $recoveryStage; From = 'else if (UnknownResolver.CanBindLostCreate(item) && found != null && api.MatchesCreated(item, found))'; To = 'else if (UnknownResolver.CanBindLostCreate(item) && found != null && api.MatchesCreated(item, found) && item.CreateCompleted)'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-binds-any-record'; File = $recoveryStage; From = 'else if (UnknownResolver.CanBindLostCreate(item) && found != null && api.MatchesCreated(item, found))'; To = 'else if (UnknownResolver.CanBindLostCreate(item) && found != null)'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-bound-as-answered'; File = $recoveryStage; From = 'item.CreateFoundWithoutAnswer = true;'; To = 'item.CreateFoundWithoutAnswer = true; item.CreateCompleted = true; item.ApiSteps[0].State = BatchStepState.ResponseReceived;'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-not-acceptable'; File = 'src/VamSys.Infrastructure/OperationsServerValue.cs'; From = '(item.WriteAccepted || item.CreateCompleted || item.CreateFoundWithoutAnswer)'; To = '(item.WriteAccepted || item.CreateCompleted)'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-message-dropped'; File = 'src/VamSys.Infrastructure/Api/Pipeline/ExecutionPipeline.cs'; From = 'item.Description?.Code is not ("ApiCreateCandidates" or "ApiCreateFoundFollowUpsNotSent")'; To = 'item.Description?.Code != "ApiCreateCandidates"'; Project = $integration; Class = $lostPost }
    @{ Name = 'lost-create-not-applied-offered'; File = 'src/VamSys.Infrastructure/OperationsUnknownResolution.cs'; From = '&& !item.WriteAccepted && !item.CreateFoundWithoutAnswer'; To = '&& !item.WriteAccepted'; Project = $integration; Class = $lostPost }
    @{ Name = 'found-create-not-new'; File = $finder; From = 'if (item.PreCreateCandidateIds!.Contains(id) || found.Contains(id)'; To = 'if (found.Contains(id)'; Project = $integration; Class = $lostPost }
    @{ Name = 'found-create-any-time'; File = $finder; From = 'if (created >= start + shift.Value - margin && created <= end + shift.Value + margin)'; To = 'if (created != default)'; Project = $integration; Class = $lostPost }
    # Checkpoint order.
    @{ Name = 'no-inflight-save'; File = 'src/VamSys.Infrastructure/Api/StepWriter.cs'; From = "            record.FinishedAt = null;`n            await persist();"; To = '            record.FinishedAt = null;'; Project = $integration; Class = $writeSafety }
    @{ Name = 'unsaved-task-kept'; File = 'src/VamSys.Application/TaskSubmission.cs'; From = "if (!isDurable(job))`n                    owner.Jobs.Remove(job);"; To = '// MUTATION: the unsaved job lingers'; Project = $unit; Class = 'VamSys.UnitTests.Application.SubmissionServiceTests' }
    # Read back.
    @{ Name = 'readback-always-passes'; File = 'src/VamSys.Infrastructure/Api/ReadbackVerifier.cs'; From = 'return before ? Unchanged(item, current, expected) : VerifyPlanned(item, current, expected).Passed;'; To = 'return before ? Unchanged(item, current, expected) : true;'; Project = $unit; Class = 'VamSys.UnitTests.Api.ReadbackVerifierTests' }
    # Duplicates.
    @{ Name = 'duplicates-ignored'; File = 'src/VamSys.Infrastructure/Api/DuplicateFinder.cs'; From = 'return current.Get(field).Equals(item.After.Get(field), StringComparison.OrdinalIgnoreCase);'; To = 'return false;'; Project = $unit; Class = 'VamSys.UnitTests.Api.DuplicateFinderTests' }
    # Versions and stale overwrites.
    @{ Name = 'newer-database-accepted'; File = 'src/VamSys.Storage/Migrations/MigrationRunner.cs'; From = "if (version > SchemaV4.Version)`n            throw StorageErrors.Block(`"StorageNewer`");"; To = '// MUTATION: a newer database is opened'; Project = $integration; Class = 'VamSys.IntegrationTests.Storage.StorageMigrationTests' }
    @{ Name = 'stale-overwrite'; File = 'src/VamSys.Storage/UnitOfWork.cs'; From = 'if (revision == null ? !allowNew || expected != 0 : Convert.ToInt64(revision) != expected)'; To = 'if (revision == null && (!allowNew || expected != 0))'; Project = $integration; Class = 'VamSys.IntegrationTests.Storage.JournalWriteScopeTests' }
    @{ Name = 'journal-misses-metadata'; File = 'src/VamSys.Domain/ChangeJournal.cs'; From = 'var metadata = force || !ReferenceEquals(columns, old.Columns) || !ReferenceEquals(conflicts, old.Conflicts) || old.SnapshotAt != data.SnapshotAt;'; To = 'var metadata = force;'; Project = $integration; Class = 'VamSys.IntegrationTests.Storage.JournalEquivalenceSeed101' }
    # History.
    @{ Name = 'history-missing-after'; File = 'src/VamSys.Domain/EditHistory.cs'; From = "if (undo.Count > 0)`n            undo[^1] = Change(undo[^1], true);"; To = '// MUTATION: omit same-step After update'; Project = $unit; Class = 'VamSys.UnitTests.Domain.EditHistoryTests' }
    @{ Name = 'history-keeps-discarded-steps'; File = 'src/VamSys.Storage/Repositories/HistoryRepository.cs'; From = "foreach (var step in h.Deletes)`n            Execute(c, tx, `"DELETE FROM resource_history_steps WHERE workspace_id=`$w AND resource=`$r AND step_id=`$id`", [.. args, (`"`$id`", step.ToString())]);"; To = '// MUTATION: retain discarded redo and evicted steps'; Project = $integration; Class = 'VamSys.IntegrationTests.Storage.StorageHistoryTests' }
    # Fixed problems from the regression ledger.
    @{ Name = 'shared-alias-binds'; File = 'src/VamSys.Domain/SnapshotMerger.cs'; From = 'out var matched) && matched.Count == 1)'; To = 'out var matched) && matched.Count >= 1)'; Project = $unit; Class = 'VamSys.UnitTests.Domain.SnapshotMergerTests' }
    @{ Name = 'local-prefix-case'; File = 'src/VamSys.Domain/LocalReference.cs'; From = 'token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);'; To = 'token.StartsWith(Prefix, StringComparison.Ordinal);'; Project = $unit; Class = 'VamSys.UnitTests.LocalReferenceTests' }
    @{ Name = 'numeric-client-id-masked'; File = 'src/VamSys.Infrastructure/Transport/TokenProvider.cs'; From = 'credentials.ClientId.All(char.IsAsciiDigit) ? [credentials.Secret] : [credentials.ClientId, credentials.Secret]'; To = '[credentials.ClientId, credentials.Secret]'; Project = $integration; Class = 'VamSys.IntegrationTests.Api.ApiDiagnosticsTests' }
    @{ Name = 'path-masked'; File = 'src/VamSys.Infrastructure/Transport/ApiDiagnostics.cs'; From = 'Path = uri.AbsolutePath + Mask(uri.Query, secrets),'; To = 'Path = Mask(uri.AbsolutePath + uri.Query, secrets),'; Project = $integration; Class = 'VamSys.IntegrationTests.Api.ApiDiagnosticsTests' }
    @{ Name = 'recovery-after-every-review'; File = 'src/VamSys.Application/UnknownReview.cs'; From = 'recovery |= await review(item);'; To = 'if (await review(item)) await recover();'; Project = $unit; Class = 'VamSys.UnitTests.Application.UnknownReviewTests' }
    @{ Name = 'conflict-stop-goes-on'; File = 'src/VamSys.Application/ConflictService.cs'; From = "if (choice == ConflictChoice.Stop)`n                    return;"; To = 'if (choice == ConflictChoice.Stop) continue;'; Project = $unit; Class = 'VamSys.UnitTests.Application.ConflictServiceTests' }
    @{ Name = 'retire-toolbar-two-days'; File = 'src/VamSys.Application/BatchEditService.cs'; From = 'new(RuleKind.Retire, "End Date", "")'; To = 'new(RuleKind.Set, "End Date", DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"))'; Project = $unit; Class = 'VamSys.UnitTests.Application.BatchEditServiceTests' }
    @{ Name = 'retire-rule-two-days'; File = 'src/VamSys.Domain/ChangePlanner.cs'; From = 'DateTime.UtcNow.AddDays(-1).ToString(DateTimeFormat'; To = 'DateTime.UtcNow.AddDays(-2).ToString(DateTimeFormat'; Project = $unit; Class = 'VamSys.UnitTests.Domain.RuleEngineTests' }
    @{ Name = 'shift-days-bad-date-throws'; File = 'src/VamSys.Domain/ChangePlanner.cs'; From = '{ result.Add(row); skips.Add(new(row.LocalId, "BatchShiftSkippedInvalidDate")); continue; }'; To = 'throw new FormatException();'; Project = $unit; Class = 'VamSys.UnitTests.Domain.RuleEngineTests' }
    @{ Name = 'shift-days-range-unchecked'; File = 'src/VamSys.Domain/ChangePlanner.cs'; From = 'if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)'; To = 'if (ticks < 0)'; Project = $unit; Class = 'VamSys.UnitTests.Domain.RuleEngineTests' }
)

$window = @(
    @{ Name = 'tasks-not-selected'; File = 'src/VamSys.App/MainWindow.Submission.cs'; From = "shell.ShowTasksBeforeUnlock();`n        reviews.Invalidate(forget: false);`n        SelectPane(ShellPage.Tasks);"; To = 'reviews.Invalidate(forget: false);'; Package = 'Product'; Class = 'VamSys.UiTests.SubmissionHandoffTests' }
    @{ Name = 'conflict-buttons-swapped'; File = 'src/VamSys.App/MainWindow.Api.cs'; From = 'DialogResult.Primary => ConflictChoice.KeepLocal, DialogResult.Secondary => ConflictChoice.UseRemote'; To = 'DialogResult.Primary => ConflictChoice.UseRemote, DialogResult.Secondary => ConflictChoice.KeepLocal'; Package = 'Product'; Class = 'VamSys.UiTests.ConflictReviewTests' }
    @{ Name = 'import-mapping-ignored'; File = 'src/VamSys.App/MainWindow.ImportExport.cs'; From = 'importExport.ImportAsync(resource, parsed, form.Mapping, form.Baseline)'; To = 'importExport.ImportAsync(resource, parsed, parsed.Headers.ToDictionary(h => h, h => h), form.Baseline)'; Package = 'Product'; Class = 'VamSys.UiTests.CsvImportTests' }
    @{ Name = 'discard-without-asking'; File = 'src/VamSys.App/MainWindow.Tasks.cs'; From = 'if (ids.Count == 0 || !await ConfirmMessage(L("ApiDiscardPending"), L("ApiDiscardConfirm", ("count", ids.Count)), L("ApiDiscardPending")))'; To = 'if (ids.Count == 0)'; Package = 'Product'; Class = 'VamSys.UiTests.DialogTests' }
    @{ Name = 'retire-window-two-days'; File = 'src/VamSys.Domain/ChangePlanner.cs'; From = 'DateTime.UtcNow.AddDays(-1).ToString(DateTimeFormat'; To = 'DateTime.UtcNow.AddDays(-2).ToString(DateTimeFormat'; Package = 'Product'; Class = 'VamSys.UiTests.DialogTests' }
    @{ Name = 'shift-days-preview-hides-reason'; File = 'src/VamSys.App/MainWindow.Batch.cs'; From = '? [BatchScope(selection, changes.Count, ruleSkipped), .. skipReasons.Select(reason => L(reason))]'; To = '? [BatchScope(selection, changes.Count, ruleSkipped)]'; Package = 'Product'; Class = 'VamSys.UiTests.DialogTests' }
    @{ Name = 'batch-result-overwritten'; File = 'src/VamSys.App/MainWindow.Data.cs'; From = 'var announce = !keepStatusThroughRefresh;'; To = 'var announce = true;'; Package = 'Product'; Class = 'VamSys.UiTests.DialogTests' }
    @{ Name = 'refresh-stays-silent'; File = 'src/VamSys.App/MainWindow.Batch.cs'; From = "Render();`n            keepStatusThroughRefresh = false;"; To = 'Render();'; Package = 'Product'; Class = 'VamSys.UiTests.DialogTests' }
    @{ Name = 'parameter-not-assigned'; File = 'src/VamSys.App/MainWindow.Maintenance.cs'; From = 'await maintenance.AssignParameterAsync(resource, row, chosen.Value);'; To = '// MUTATION: the choice is dropped'; Package = 'Qa'; Class = 'VamSys.UiTests.ParameterPickerTests' }
    @{ Name = 'footer-label-stuck'; File = 'src/VamSys.Presentation/DataGridFooterViewModel.cs'; From = 'GoLabel = language.Get("TableGo");'; To = 'GoLabel = "Go";'; Package = 'Qa'; Class = 'VamSys.UiTests.DataFooterTests' }
    @{ Name = 'undo-button-redoes'; File = 'src/VamSys.App/MainWindow.xaml.cs'; From = '() => MoveHistory(undo: true)'; To = '() => MoveHistory(undo: false)'; Package = 'Qa'; Class = 'VamSys.UiTests.ClosingAndHistoryTests' }
)

$mutations = if ($Set -eq 'Core') { $core } else { $window }
if ($Only) {
    $mutations = @($mutations | Where-Object { $_.Name -eq $Only })
    if (!$mutations) { throw "No $Set mutation named '$Only'" }
}
if ($Set -eq 'Window' -and (Get-Process FlightOpsDesk -ErrorAction SilentlyContinue)) { throw 'An existing FlightOps Desk window is open; isolate QA before running' }

$root = Join-Path $repo ('artifacts/mutations-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$copy = Join-Path $root 'code'
foreach ($top in 'src', 'tests', 'tools', 'docs') {
    Get-ChildItem (Join-Path $repo $top) -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $target = Join-Path $copy ([IO.Path]::GetRelativePath($repo, $_.FullName))
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
foreach ($file in 'Directory.Build.props', 'global.json', 'VamSys.slnx') { Copy-Item (Join-Path $repo $file) (Join-Path $copy $file) }

function Failed([string]$log) {
    $text = [IO.File]::ReadAllText($log)
    # A compile error is not a behaviour failure: only failed test cases count.
    if ($text -match 'error (CS|MSB)\d+') { throw "Mutation did not build (see $log)" }
    if ($text -notmatch '(?m)^\s*total:\s*[1-9]') { throw "No test ran (see $log)" }
    if ($text -match '(?m)^\s*failed:\s*([1-9]\d*)') { return [int]$Matches[1] }
    return 0
}

$survived = [Collections.Generic.List[string]]::new()
foreach ($m in $mutations) {
    $path = Join-Path $copy $m.File
    $original = [IO.File]::ReadAllText($path)
    $source = $original.Replace("`r`n", "`n")
    if (!$source.Contains($m.From)) { throw "Mutation anchor missing: $($m.Name) in $($m.File)" }
    $log = Join-Path $root "$($m.Name).log"
    try {
        [IO.File]::WriteAllText($path, $source.Replace($m.From, $m.To))
        if ($Set -eq 'Core') {
            Push-Location $copy
            try { & $dotnet test --project $m.Project -c Release -p:RestoreLockedMode=true -- --filter-class $m.Class *> $log }
            finally { Pop-Location }
        }
        else {
            $qa = ($m.Package -eq 'Qa').ToString().ToLowerInvariant()
            $published = Join-Path $root "$($m.Name)-package"
            Push-Location $copy
            try { & $dotnet publish src/VamSys.App/VamSys.App.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:Platform=x64 -p:RestoreLockedMode=true "-p:UiVerification=$qa" "-p:VamSysTestSupport=$qa" -o $published *> "$log.publish" }
            finally { Pop-Location }
            if ($LASTEXITCODE -ne 0) { throw "Mutated program did not publish (see $log.publish)" }
            $variable = if ($m.Package -eq 'Qa') { 'VAMSYS_QA_EXE' } else { 'VAMSYS_APP_EXE' }
            [Environment]::SetEnvironmentVariable($variable, (Join-Path $published 'FlightOpsDesk.exe'))
            try { & $dotnet test --project (Join-Path $repo 'tests/VamSys.UiTests/VamSys.UiTests.csproj') -c Release --no-build -- --explicit only --filter-class $m.Class *> $log }
            finally { [Environment]::SetEnvironmentVariable($variable, $null) }
            [IO.Directory]::Delete($published, $true)
        }
        $failed = Failed $log
        if ($failed -gt 0) { Write-Output "PASS $($m.Name): caught by $($m.Class), $failed failed" }
        else { $survived.Add($m.Name); Write-Output "SURVIVED $($m.Name): $($m.Class) still passes (see $log)" }
    }
    finally { [IO.File]::WriteAllText($path, $original) }
}
[IO.Directory]::Delete($copy, $true)
Write-Output "$($mutations.Count - $survived.Count) of $($mutations.Count) caught; logs: $root"
if ($survived.Count -gt 0) { exit 1 }
