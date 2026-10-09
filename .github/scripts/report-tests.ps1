# Turns the CTRF test reports under a folder into GitHub annotations: one notice with the totals, one error per failed
# test. Annotations are shown on the workflow run and in pull requests, so failures can be read without the full log.
param(
    [Parameter(Mandatory)] [string] $ResultsFolder,
    [Parameter(Mandatory)] [string] $Platform
)

function Escape-Data([string] $text) {
    return $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

function Escape-Property([string] $text) {
    return (Escape-Data $text).Replace(':', '%3A').Replace(',', '%2C')
}

$reports = @(Get-ChildItem -Path $ResultsFolder -Recurse -Filter '*.ctrf.json' -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    Write-Output "::error title=$(Escape-Property "Tests on $Platform")::No test report was produced (the build or the test run failed before reporting)."
    exit 0
}

foreach ($report in $reports) {
    $results = (Get-Content -Raw -Path $report.FullName | ConvertFrom-Json).results
    $summary = $results.summary
    Write-Output "::notice title=$(Escape-Property "Tests on $Platform")::$($summary.passed) passed, $($summary.failed) failed, $($summary.skipped) skipped ($($summary.tests) in total)."

    foreach ($test in @($results.tests | Where-Object { $_.status -eq 'failed' })) {
        $message = "$($test.message)"
        if ($test.trace) {
            $message += "`n" + (($test.trace -split "`n" | Select-Object -First 6) -join "`n")
        }

        if ($message.Length -gt 1500) {
            $message = $message.Substring(0, 1500) + '...'
        }

        Write-Output "::error title=$(Escape-Property "$Platform - $($test.name)")::$(Escape-Data $message)"
    }
}
