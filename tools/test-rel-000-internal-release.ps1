[CmdletBinding()]
param(
    [switch] $SelfTest,
    [switch] $PythonOnly,
    [switch] $CI,
    [string] $Ops001ArtifactDirectory = $env:REL000_OPS001_ARTIFACT_DIRECTORY,
    [string] $Ops002ArtifactDirectory = $env:REL000_OPS002_ARTIFACT_DIRECTORY,
    [string] $OutputDirectory = $env:REL000_OUTPUT_DIRECTORY,
    [string] $Issue5Path = $env:REL000_ISSUE5_PATH,
    [string] $SecurityTrackingIssuePath = $env:REL000_SECURITY_TRACKING_ISSUE_PATH,
    [string] $BaseAuditPath = $env:REL000_BASE_AUDIT_PATH,
    [string] $BranchAuditPath = $env:REL000_BRANCH_AUDIT_PATH,
    [string] $Mode = $env:REL000_MODE,
    [string] $SourceBranch = $env:REL000_SOURCE_BRANCH,
    [string] $RemediationId = $env:REL000_REMEDIATION_ID,
    [string] $RemediationPolicyPath = $env:REL000_REMEDIATION_POLICY_PATH,
    [string] $SharpRuntimeSmokePath = $env:REL000_SHARP_RUNTIME_SMOKE_PATH,
    [string] $SourceHeadSha = $env:REL000_SOURCE_HEAD_SHA,
    [string] $TestedGitSha = $env:REL000_TESTED_GIT_SHA,
    [string] $BaseMainSha = $env:REL000_BASE_MAIN_SHA,
    [string] $GitRelationship = $env:REL000_GIT_RELATIONSHIP,
    [string] $WorkflowRunId = $env:REL000_WORKFLOW_RUN_ID,
    [string] $WorkflowRunAttempt = $env:REL000_WORKFLOW_RUN_ATTEMPT,
    [string] $JobResultsJson = $env:REL000_JOB_RESULTS_JSON,
    [string] $Ops001ArtifactName = $env:REL000_OPS001_ARTIFACT_NAME,
    [string] $Ops001ArtifactId = $env:REL000_OPS001_ARTIFACT_ID,
    [string] $Ops001ArtifactDigest = $env:REL000_OPS001_ARTIFACT_DIGEST,
    [string] $Ops002ArtifactName = $env:REL000_OPS002_ARTIFACT_NAME,
    [string] $Ops002ArtifactId = $env:REL000_OPS002_ARTIFACT_ID,
    [string] $Ops002ArtifactDigest = $env:REL000_OPS002_ARTIFACT_DIGEST,
    [string] $ExecutionResultsDirectory = $env:REL000_EXECUTION_RESULTS_DIRECTORY,
    [string] $WorkflowProvenancePath = $env:REL000_WORKFLOW_PROVENANCE_PATH,
    [string] $SyntheticRollbackScenario,
    [string] $SyntheticRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($null -eq (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) {
    $script:IsWindows = $env:OS -eq "Windows_NT"
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

if (-not [string]::IsNullOrWhiteSpace($SyntheticRollbackScenario)) {
    if ($env:REL000_TEST_MODE -cne "true") {
        throw "REL000_SYNTHETIC_MODE_FORBIDDEN"
    }
    $syntheticRootPath = [System.IO.Path]::GetFullPath($SyntheticRoot)
    if ([System.IO.Path]::GetFileName($syntheticRootPath) -notlike "paquetenvia-rel000-rollback-*") {
        throw "REL000_SYNTHETIC_ROOT_INVALID"
    }
    $stagingPath = Join-Path $syntheticRootPath "staging"
    $outputPath = Join-Path $syntheticRootPath "output"
    $outputOwned = $false
    try {
        if ($SyntheticRollbackScenario -eq "cancel-before-output") {
            throw "REL000_SYNTHETIC_CANCEL_BEFORE_OUTPUT"
        }
        if ($SyntheticRollbackScenario -eq "preexisting-output") {
            [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $outputPath "preexisting.txt"), "preserve")
        }
        if (Test-Path -LiteralPath $outputPath) {
            if ($SyntheticRollbackScenario -eq "preexisting-output") {
                throw "REL000_STALE_REPORT_REUSED"
            }
            throw "REL000_SYNTHETIC_OUTPUT_UNEXPECTED"
        }
        [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
        if ($SyntheticRollbackScenario -eq "cancel-after-staging") {
            throw "REL000_SYNTHETIC_CANCEL_AFTER_STAGING"
        }
        if ($SyntheticRollbackScenario -eq "staging-link-rejected") {
            $foreignPath = Join-Path $syntheticRootPath "foreign"
            $linkPath = Join-Path $stagingPath "linked-foreign"
            $linkType = if ($IsWindows -or $env:OS -eq "Windows_NT") {
                "Junction"
            }
            else {
                "SymbolicLink"
            }
            New-Item -ItemType $linkType -Path $linkPath -Target $foreignPath | Out-Null
            throw "REL000_STAGING_LINK_REJECTED"
        }
        $generatorScenario = switch ($SyntheticRollbackScenario) {
            "artifact-other-sha" { "artifact-other-sha" }
            "artifact-other-run" { "artifact-other-run" }
            "manifest-incomplete" { "manifest-incomplete" }
            "error-after-final-output" { "success" }
            "no-success-after-failure" { "success" }
            "output-partial-removed" { "success" }
            default { "cancel-after-first-json" }
        }
        $generatedPath = Join-Path $stagingPath "generated"
        & python (Join-Path $repositoryRoot "tools/rel-000/rel000.py") `
            synthetic-generate `
            --scenario $generatorScenario `
            --output $generatedPath
        if ($LASTEXITCODE -ne 0) {
            throw "REL000_SYNTHETIC_GENERATION_FAILED"
        }
        if ($SyntheticRollbackScenario -eq "manifest-incomplete") {
            $generated = @(Get-ChildItem -LiteralPath $generatedPath -File)
            if ($generated.Count -ne 4) {
                throw "REL000_GENERATED_OUTPUT_INCOMPLETE"
            }
        }
        [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
        $outputOwned = $true
        $first = Get-ChildItem -LiteralPath $generatedPath -File | Select-Object -First 1
        [System.IO.File]::Copy($first.FullName, (Join-Path $outputPath $first.Name), $false)
        throw "REL000_SYNTHETIC_ERROR_AFTER_FINAL_OUTPUT"
    }
    finally {
        $link = Join-Path $stagingPath "linked-foreign"
        if (Test-Path -LiteralPath $link) {
            [System.IO.Directory]::Delete($link)
        }
        if ($outputOwned -and (Test-Path -LiteralPath $outputPath)) {
            [System.IO.Directory]::Delete($outputPath, $true)
        }
        if (Test-Path -LiteralPath $stagingPath) {
            [System.IO.Directory]::Delete($stagingPath, $true)
        }
    }
}

function Invoke-Rel000PythonTests {
    param([Parameter(Mandatory)] [string] $OutputPath)
    & python (Join-Path $repositoryRoot "tools/rel-000/run_focused_tests.py") `
        --output $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "REL000_PYTHON_FOCAL_TESTS_FAILED"
    }
}

$selfTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "paquetenvia-rel000-self-test-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($selfTestRoot) | Out-Null
$pythonTestResultsPath = Join-Path $selfTestRoot "python-tests.json"
$physicalTestResultsPath = Join-Path $selfTestRoot "physical-tests.json"
$rollbackExecutionPath = Join-Path $selfTestRoot "rollback-execution.json"

if ($PythonOnly) {
    Invoke-Rel000PythonTests -OutputPath $pythonTestResultsPath
    $pythonResult = Get-Content -LiteralPath $pythonTestResultsPath -Raw | ConvertFrom-Json
    Write-Output ([pscustomobject]@{
        format_version = "paquetenvia-rel000-local-self-test-v1"
        python_tests_expected = $pythonResult.python_tests_expected
        python_tests_discovered = $pythonResult.python_tests_discovered
        python_tests_executed = $pythonResult.python_tests_executed
        python_tests_passed = $pythonResult.python_tests_passed
        python_tests_failed = $pythonResult.python_tests_failed
        python_tests_skipped = $pythonResult.python_tests_skipped
        physical_path_tests_local = "NOT_EXECUTED"
        physical_path_tests_local_reason = "POWERSHELL_7_UNAVAILABLE"
        result = "REL000_LOCAL_PYTHON_TESTS_PASSED"
    } | ConvertTo-Json -Depth 4)
    [System.IO.Directory]::Delete($selfTestRoot, $true)
    return
}

. (Join-Path $repositoryRoot "tools/backup-restore.common.ps1")

function Test-Rel000PhysicalOutputGuard {
    $candidate = Join-Path $repositoryRoot "TestResults/rel000-forbidden-output"
    $reason = $null
    try {
        New-Ops002ValidatedExternalDirectory `
            -Path $candidate `
            -Purpose "REL-000 output" `
            -MustNotExist | Out-Null
    }
    catch {
        $reason = $_.Exception.Message
    }
    if ($reason -notmatch "OPS002_PATH_PHYSICAL_INSIDE_REPOSITORY") {
        throw "REL000_OUTPUT_PHYSICAL_GUARD_NOT_PROVEN"
    }
    [pscustomobject]@{
        test = "output_physical_inside_repository"
        passed = $true
        reason_code = "OUTPUT_PHYSICAL_INSIDE_REPOSITORY"
    }
}

function Test-Rel000CleanupLinkGuard {
    $suffix = [Guid]::NewGuid().ToString("N")
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $containerInfo = $null
    $targetInfo = $null
    $linkPath = $null
    try {
        $containerInfo = New-Ops002ValidatedExternalDirectory `
            -Path (Join-Path $temporaryRoot "paquetenvia-rel000-link-$suffix") `
            -Purpose "REL-000 cleanup test" `
            -MustNotExist
        $targetInfo = New-Ops002ValidatedExternalDirectory `
            -Path (Join-Path $temporaryRoot "paquetenvia-rel000-target-$suffix") `
            -Purpose "REL-000 cleanup target" `
            -MustNotExist
        $sentinel = Join-Path $targetInfo.PhysicalPath "sentinel.txt"
        [System.IO.File]::WriteAllText($sentinel, "preserve")
        $linkPath = Join-Path $containerInfo.PhysicalPath "linked-target"
        $linkType = if ($IsWindows -or $env:OS -eq "Windows_NT") {
            "Junction"
        }
        else {
            "SymbolicLink"
        }
        New-Item -ItemType $linkType -Path $linkPath -Target $targetInfo.PhysicalPath `
            -ErrorAction Stop | Out-Null

        $reason = $null
        try {
            Remove-Ops002ValidatedDirectory `
                -PathInfo $containerInfo `
                -Purpose "REL-000 cleanup test"
        }
        catch {
            $reason = $_.Exception.Message
        }
        if ($reason -notmatch "OPS002_CLEANUP_LINK_REJECTED") {
            throw "REL000_CLEANUP_LINK_GUARD_NOT_PROVEN"
        }
        if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
            throw "REL000_CLEANUP_FOLLOWED_LINK"
        }
        [pscustomobject]@{
            test = "cleanup_does_not_follow_symlink_or_junction"
            passed = $true
            reason_code = "CLEANUP_LINK_REJECTED"
        }
    }
    finally {
        if ($null -ne $linkPath -and (Test-Path -LiteralPath $linkPath)) {
            [System.IO.Directory]::Delete($linkPath)
        }
        if ($null -ne $containerInfo -and
            (Test-Path -LiteralPath $containerInfo.LexicalPath)) {
            Remove-Ops002ValidatedDirectory `
                -PathInfo $containerInfo `
                -Purpose "REL-000 cleanup test"
        }
        if ($null -ne $targetInfo -and
            (Test-Path -LiteralPath $targetInfo.LexicalPath)) {
            Remove-Ops002ValidatedDirectory `
                -PathInfo $targetInfo `
                -Purpose "REL-000 cleanup target"
        }
    }
}

function Invoke-Rel000SelfTests {
    if ($CI -and $PSVersionTable.PSVersion.Major -ne 7) {
        throw "REL000_POWERSHELL_7_REQUIRED_IN_CI"
    }
    Invoke-Rel000PythonTests -OutputPath $pythonTestResultsPath
    $physicalGuards = @(
        Test-Rel000PhysicalOutputGuard
        Test-Rel000CleanupLinkGuard
    )
    $physicalTestsExpected = 2
    if ($physicalGuards.Count -ne $physicalTestsExpected) {
        throw "REL000_FOCUSED_TEST_COUNT_MISMATCH"
    }
    $physicalPassed = @($physicalGuards | Where-Object passed).Count
    $physicalPayload = [pscustomobject]@{
        format_version = "paquetenvia-rel000-physical-tests-v2"
        physical_tests_expected = $physicalTestsExpected
        physical_tests_discovered = $physicalGuards.Count
        physical_tests_executed = $physicalGuards.Count
        physical_tests_passed = $physicalPassed
        physical_tests_failed = $physicalGuards.Count - $physicalPassed
        physical_tests_skipped = 0
        tests = $physicalGuards
    } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $physicalTestResultsPath,
        $physicalPayload + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    & python (Join-Path $repositoryRoot "tools/rel-000/run_rollback_e2e.py") `
        --wrapper $PSCommandPath `
        --output $rollbackExecutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "REL000_ROLLBACK_E2E_TESTS_FAILED"
    }
    $pythonResult = Get-Content -LiteralPath $pythonTestResultsPath -Raw | ConvertFrom-Json
    $physicalResult = Get-Content -LiteralPath $physicalTestResultsPath -Raw | ConvertFrom-Json
    $rollbackResult = Get-Content -LiteralPath $rollbackExecutionPath -Raw | ConvertFrom-Json
    Write-Output ([pscustomobject]@{
        format_version = "paquetenvia-rel000-self-test-v2"
        focused_tests_expected = (
            $pythonResult.python_tests_expected + $physicalResult.physical_tests_expected)
        focused_tests_discovered = (
            $pythonResult.python_tests_discovered + $physicalResult.physical_tests_discovered)
        focused_tests_executed = (
            $pythonResult.python_tests_executed + $physicalResult.physical_tests_executed)
        focused_tests_passed = (
            $pythonResult.python_tests_passed + $physicalResult.physical_tests_passed)
        focused_tests_failed = (
            $pythonResult.python_tests_failed + $physicalResult.physical_tests_failed)
        focused_tests_skipped = (
            $pythonResult.python_tests_skipped + $physicalResult.physical_tests_skipped)
        physical_path_tests_ci = "PASSED"
        powershell_ci_major_version = $PSVersionTable.PSVersion.Major
        physical_guards = $physicalGuards
        rollback_scenarios_expected = $rollbackResult.rollback_scenarios_expected
        rollback_scenarios_executed = $rollbackResult.rollback_scenarios_executed
        rollback_scenarios_passed = $rollbackResult.rollback_scenarios_passed
        rollback_scenarios_failed = $rollbackResult.rollback_scenarios_failed
        rollback_cleanup_verified = $rollbackResult.rollback_cleanup_verified
        result = "REL000_FOCUSED_TESTS_PASSED"
    } | ConvertTo-Json -Depth 6)
}

Invoke-Rel000SelfTests
if ($SelfTest) {
    [System.IO.Directory]::Delete($selfTestRoot, $true)
    return
}
if (-not $CI) {
    throw "REL000_FULL_GENERATION_REQUIRES_CI"
}

$required = [ordered]@{
    Ops001ArtifactDirectory = $Ops001ArtifactDirectory
    Ops002ArtifactDirectory = $Ops002ArtifactDirectory
    OutputDirectory = $OutputDirectory
    Issue5Path = $Issue5Path
    SecurityTrackingIssuePath = $SecurityTrackingIssuePath
    BaseAuditPath = $BaseAuditPath
    BranchAuditPath = $BranchAuditPath
    Mode = $Mode
    SourceBranch = $SourceBranch
    RemediationPolicyPath = $RemediationPolicyPath
    SourceHeadSha = $SourceHeadSha
    TestedGitSha = $TestedGitSha
    BaseMainSha = $BaseMainSha
    GitRelationship = $GitRelationship
    WorkflowRunId = $WorkflowRunId
    WorkflowRunAttempt = $WorkflowRunAttempt
    JobResultsJson = $JobResultsJson
    Ops001ArtifactName = $Ops001ArtifactName
    Ops001ArtifactId = $Ops001ArtifactId
    Ops001ArtifactDigest = $Ops001ArtifactDigest
    Ops002ArtifactName = $Ops002ArtifactName
    Ops002ArtifactId = $Ops002ArtifactId
    Ops002ArtifactDigest = $Ops002ArtifactDigest
    ExecutionResultsDirectory = $ExecutionResultsDirectory
    WorkflowProvenancePath = $WorkflowProvenancePath
}
if ($Mode -eq "SECURITY_REMEDIATION") {
    $required.SharpRuntimeSmokePath = $SharpRuntimeSmokePath
}
$missing = @(
    $required.GetEnumerator() |
        Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) } |
        ForEach-Object Key
)
if ($missing.Count -ne 0) {
    throw "REL000_REQUIRED_INPUT_MISSING: $($missing -join ', ')"
}

$validatedOps001 = Assert-Ops002ExternalPath `
    -Path $Ops001ArtifactDirectory `
    -Purpose "REL-000 OPS-001 artifact"
$validatedOps002 = Assert-Ops002ExternalPath `
    -Path $Ops002ArtifactDirectory `
    -Purpose "REL-000 OPS-002 artifact"
$validatedExecutionResults = Assert-Ops002ExternalPath `
    -Path $ExecutionResultsDirectory `
    -Purpose "REL-000 execution artifacts"
$validatedWorkflowProvenance = Assert-Ops002ExternalRegularFile `
    -Path $WorkflowProvenancePath `
    -Purpose "REL-000 workflow provenance manifest"
$validatedIssue = Assert-Ops002ExternalRegularFile `
    -Path $Issue5Path `
    -Purpose "REL-000 Issue #5 state"
$validatedSecurityTrackingIssue = Assert-Ops002ExternalRegularFile `
    -Path $SecurityTrackingIssuePath `
    -Purpose "REL-000 additional security tracking issue"
$validatedBaseAudit = Assert-Ops002ExternalRegularFile `
    -Path $BaseAuditPath `
    -Purpose "REL-000 base audit state"
$validatedBranchAudit = Assert-Ops002ExternalRegularFile `
    -Path $BranchAuditPath `
    -Purpose "REL-000 branch audit state"
$validatedSharpRuntimeSmoke = $null
if (-not [string]::IsNullOrWhiteSpace($SharpRuntimeSmokePath)) {
    $validatedSharpRuntimeSmoke = Assert-Ops002ExternalRegularFile `
        -Path $SharpRuntimeSmokePath `
        -Purpose "REL-000 Sharp runtime smoke"
}

$stagingInfo = New-Ops002StagingDirectory -Purpose "rel000"
$finalOutputInfo = $null
$generationSucceeded = $false
try {
    $generatedPath = Join-Path $stagingInfo.PhysicalPath "generated"
    $arguments = @(
        (Join-Path $repositoryRoot "tools/rel-000/rel000.py"),
        "validate",
        "--repository-root", $repositoryRoot,
        "--item-evidence", (Join-Path $repositoryRoot "tests/fixtures/rel-000/item-evidence.json"),
        "--cross-tenant-evidence", (Join-Path $repositoryRoot "tests/fixtures/rel-000/cross-tenant-evidence.json"),
        "--rollback-evidence", (Join-Path $repositoryRoot "tests/fixtures/rel-000/rollback-evidence.json"),
        "--rollback-execution", $rollbackExecutionPath,
        "--python-test-results", $pythonTestResultsPath,
        "--physical-test-results", $physicalTestResultsPath,
        "--execution-results-directory", $validatedExecutionResults,
        "--workflow-provenance", $validatedWorkflowProvenance,
        "--ops001-directory", $validatedOps001,
        "--ops002-directory", $validatedOps002,
        "--output-directory", $generatedPath,
        "--issue5", $validatedIssue,
        "--security-tracking-issue", $validatedSecurityTrackingIssue,
        "--base-audit", $validatedBaseAudit,
        "--branch-audit", $validatedBranchAudit,
        "--mode", $Mode,
        "--source-branch", $SourceBranch,
        "--remediation-id", $RemediationId,
        "--remediation-policy", $RemediationPolicyPath,
        "--source-head-sha", $SourceHeadSha,
        "--tested-git-sha", $TestedGitSha,
        "--base-main-sha", $BaseMainSha,
        "--git-relationship", $GitRelationship,
        "--workflow-run-id", $WorkflowRunId,
        "--workflow-run-attempt", $WorkflowRunAttempt,
        "--job-results-json", $JobResultsJson,
        "--ops001-artifact-name", $Ops001ArtifactName,
        "--ops001-artifact-id", $Ops001ArtifactId,
        "--ops001-artifact-digest", $Ops001ArtifactDigest,
        "--ops002-artifact-name", $Ops002ArtifactName,
        "--ops002-artifact-id", $Ops002ArtifactId,
        "--ops002-artifact-digest", $Ops002ArtifactDigest
    )
    if ($null -ne $validatedSharpRuntimeSmoke) {
        $arguments += @("--sharp-runtime-smoke", $validatedSharpRuntimeSmoke)
    }
    & python @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "REL000_TECHNICAL_VALIDATION_FAILED"
    }

    $finalOutputInfo = New-Ops002ValidatedExternalDirectory `
        -Path $OutputDirectory `
        -Purpose "REL-000 output" `
        -MustNotExist
    Assert-Ops002ValidatedDirectoryUnchanged `
        -PathInfo $finalOutputInfo `
        -Purpose "REL-000 output" | Out-Null
    $allowed = @(
        "rel000-internal-release-report.json",
        "rel000-p0-evidence.json",
        "rel000-cross-tenant-evidence.json",
        "rel000-rollback-evidence.json"
    )
    foreach ($name in $allowed) {
        $source = Join-Path $generatedPath $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "REL000_GENERATED_OUTPUT_INCOMPLETE"
        }
        [System.IO.File]::Copy(
            $source,
            (Join-Path $finalOutputInfo.PhysicalPath $name),
            $false)
    }
    $published = @(
        Get-ChildItem -LiteralPath $finalOutputInfo.PhysicalPath -File |
            ForEach-Object Name
    )
    if (@($published | Where-Object { $_ -notin $allowed }).Count -ne 0 -or
        $published.Count -ne $allowed.Count) {
        throw "REL000_OUTPUT_ALLOWLIST_VIOLATION"
    }
    $generationSucceeded = $true
    Write-Host "REL-000 technical evidence generated outside the repository."
    Write-Output ([pscustomobject]@{
        OutputDirectory = $finalOutputInfo.PhysicalPath
        Files = $published
        Result = "REL000_EVIDENCE_GENERATED"
    })
}
finally {
    if (-not $generationSucceeded -and $null -ne $finalOutputInfo -and
        (Test-Path -LiteralPath $finalOutputInfo.LexicalPath)) {
        Remove-Ops002ValidatedDirectory `
            -PathInfo $finalOutputInfo `
            -Purpose "REL-000 output"
    }
    if (Test-Path -LiteralPath $stagingInfo.LexicalPath) {
        Remove-Ops002StagingDirectory -PathInfo $stagingInfo
    }
    if (Test-Path -LiteralPath $selfTestRoot) {
        [System.IO.Directory]::Delete($selfTestRoot, $true)
    }
}
