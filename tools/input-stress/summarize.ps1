param([string]$ReportDirectory = 'tests\stress-3000-final')
$ErrorActionPreference = 'Stop'
$scDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
$scExpected = [regex]::Matches((Get-Content -LiteralPath (Join-Path $scDirectory 'expected.txt') -Raw), '\S+') | ForEach-Object Value
$scNoisy = [regex]::Matches((Get-Content -LiteralPath (Join-Path $scDirectory 'wrong-layout-input.txt') -Raw), '\S+') | ForEach-Object Value
$scRuns = Get-Content -LiteralPath (Join-Path $scDirectory 'runs.json') -Raw | ConvertFrom-Json
$scSummary = foreach ($scRun in $scRuns) {
    $scActual = @([regex]::Matches((Get-Content -LiteralPath (Join-Path $scDirectory ($scRun.name + '-actual.txt')) -Raw), '\S+') | ForEach-Object Value)
    $scInput = if ($scRun.name -like '*wrong-layout*') { $scNoisy } else { $scExpected }
    $scAligned = $scActual.Count -eq $scExpected.Count
    $scFixed = 0; $scMissed = 0; $scWrong = 0; $scFalse = 0; $scProtected = 0
    $scDetails = @()
    if ($scAligned) {
        for ($scIndex = 0; $scIndex -lt $scExpected.Count; $scIndex++) {
            $scKind = $null
            if ($scInput[$scIndex] -cne $scExpected[$scIndex]) {
                if ($scInput[$scIndex] -match '^https?://' -and $scActual[$scIndex] -ceq $scInput[$scIndex]) { $scProtected++; $scKind = 'protected_url_preserved' }
                elseif ($scActual[$scIndex] -ceq $scExpected[$scIndex]) { $scFixed++; $scKind = 'corrected' }
                elseif ($scActual[$scIndex] -ceq $scInput[$scIndex]) { $scMissed++; $scKind = 'missed' }
                else { $scWrong++; $scKind = 'wrong_correction_or_input_damage' }
            } elseif ($scActual[$scIndex] -cne $scExpected[$scIndex]) { $scFalse++; $scKind = 'unwanted_change_or_input_damage' }
            if ($scKind) { $scDetails += [ordered]@{ index = $scIndex; kind = $scKind; input = $scInput[$scIndex]; expected = $scExpected[$scIndex]; actual = $scActual[$scIndex] } }
        }
    }
    [ordered]@{
        run = $scRun.name
        sent_characters = $scRun.sentCharacters
        actual_characters = $scRun.actualCharacters
        exact_expected_text = $scRun.exactExpectedText
        expected_edit_distance = $scRun.expectedEditDistance
        token_counts_match = $scAligned
        comparison_note = 'Raw edit distance compares to the original prose. The mutation generator also changed hello inside a URL; preserving that input URL is expected protection and is counted separately, not as a missed word correction.'
        corrected_words = $scFixed
        missed_words = $scMissed
        protected_urls_preserved = $scProtected
        wrongly_changed_error_words = $scWrong
        changed_correct_words = $scFalse
        hook_healthy = $scRun.hookStillRunning
        seconds = [Math]::Round($scRun.typingSeconds, 2)
        characters_per_second = [Math]::Round($scRun.actualCharactersPerSecond, 2)
        ui_heartbeat_max_gap_ms = [Math]::Round($scRun.maxUiHeartbeatGapMs, 2)
        messages = $scRun.messages
        details = $scDetails
    }
}
$scSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scDirectory 'summary.json') -Encoding utf8
$scSummary | ForEach-Object { [pscustomobject]$_ } | Select-Object run,actual_characters,exact_expected_text,corrected_words,missed_words,wrongly_changed_error_words,changed_correct_words,hook_healthy,characters_per_second | Format-Table -AutoSize
