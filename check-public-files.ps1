param([string]$Revision, [switch]$ListFiles)
$ErrorActionPreference = 'Stop'
$allowed = @(
    '.gitattributes','.gitignore','.githooks/pre-commit','.githooks/pre-push',
    '.github/ISSUE_TEMPLATE/bug_report.yml','README.md','ASSETS.md','CHANGELOG.md',
    'AssemblyInfo.cs','GameUpdateService.cs','LaunchEngine.cs','LauncherGameUpdates.cs',
    'LauncherRegion.cs','LauncherUpdates.cs','LauncherWindow.cs','Models.cs','Program.cs',
    'RegionManagement.cs','UpdateInstaller.cs','UpdateService.cs','WindowsPlatform.cs','MainWindow.xaml','app.manifest',
    'build.ps1','build-icon.ps1','check-public-files.ps1','prepare-update.ps1','sign-document.ps1',
    'test.ps1','test-ui.ps1','test-updates.ps1','test-portable.ps1','test-published-update.ps1',
    'test-update-install.ps1','test-region-live.ps1',
    'Tests/EngineTests.cs','Tests/GameUpdateTests.cs','Tests/PublishedUpdateTests.cs','Tests/RegionLiveTests.cs',
    'Tests/RegionTests.cs','Tests/RegionUiTests.cs','Tests/UpdateFixture.cs','Tests/UpdateInstallTests.cs',
    'Tests/UpdateTests.cs','Tests/UpdateUiTests.cs',
    'Assets/hero.png','Assets/launch-button.png','Assets/app-icon-source.png','Assets/app-icon.png','Assets/app.ico','Assets/update-public-key.xml',
    'docs/RELEASING.md','docs/launcher-preview.png','docs/releases/v1.1.5.md',
    'updates/updates.source.json','updates/updates.json','updates/announcements.source.json','updates/announcements.json'
)
if ($ListFiles) { $allowed; return }
$rules = [ordered]@{
    PersonalPath = '(?i)\b[A-Z]:[\\/]+(?:Users[\\/]+[^\\/\s]+|Game[\\/]+MangoT5\b)'
    Email = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    Credential = '(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{24,}|AKIA[0-9A-Z]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)'
    PrivateRsa = '<(?:D|P|Q|DP|DQ|InverseQ)>[A-Za-z0-9+/=]+</(?:D|P|Q|DP|DQ|InverseQ)>'
}
function Check-Text([string]$file, [string]$content) {
    foreach ($rule in $rules.Keys) {
        $matches = @([regex]::Matches($content, $rules[$rule]))
        if ($rule -eq 'Email' -and $file -eq 'Tests/UpdateTests.cs') {
            # This synthetic URL user is used to test rejection of embedded credentials.
            $matches = @($matches | Where-Object { $_.Value -ne ('user' + '@github.com') })
        }
        if ($matches.Count -gt 0) { throw ('Publication rejected: ' + $file + ' (' + $rule + '). Remove private values before committing.') }
    }
}
function Check-Identity([string]$identity) {
    $mail = [regex]::Match($identity, '<([^<>]+)>')
    if (-not $mail.Success -or $mail.Groups[1].Value -notmatch '\A(?:[0-9]+\+)?[A-Za-z0-9-]+@users\.noreply\.github\.com\z') {
        throw 'Publication rejected: use a GitHub noreply author and committer address.'
    }
}
$commits = @('')
if ($Revision) {
    if ($Revision -notmatch '\A[0-9a-fA-F]{40}\z' -and $Revision -ne 'HEAD') { throw 'Use HEAD or a full commit ID.' }
    $commits = @(git rev-list $Revision)
    if ($LASTEXITCODE -ne 0 -or $commits.Count -eq 0) { throw 'Cannot read publication history.' }
}
else {
    Check-Identity (git var GIT_AUTHOR_IDENT)
    Check-Identity (git var GIT_COMMITTER_IDENT)
}
$checked = @{}
foreach ($commit in $commits) {
    if ($commit) {
        Check-Identity (git show -s --format='%an <%ae>' $commit)
        Check-Identity (git show -s --format='%cn <%ce>' $commit)
    }
    $paths = if ($commit) { @(git ls-tree -r --name-only $commit) } else { @(git ls-files --cached) }
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read tracked file list.' }
    foreach ($file in $paths) {
        if ($allowed -cnotcontains $file) { throw ('Publication rejected: unapproved file ' + $file) }
        $spec = if ($commit) { $commit + ':' + $file } else { ':' + $file }
        $objectId = git rev-parse $spec
        if ($LASTEXITCODE -ne 0) { throw ('Cannot inspect ' + $file) }
        if ($checked.ContainsKey($objectId)) { continue }
        $checked[$objectId] = $true
        if ($file -match '\.(png|ico)$') { continue } # Images require a separate visual review.
        $content = (git show $spec | Out-String)
        if ($LASTEXITCODE -ne 0) { throw ('Cannot read ' + $file) }
        Check-Text $file $content
        if ($file -match '^updates/(updates|announcements)\.json$') {
            $envelope = $content | ConvertFrom-Json
            Check-Text $file ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)))
        }
    }
}
Write-Output ('PASS publication allowlist and privacy scan: ' + $checked.Count + ' unique file(s).')
