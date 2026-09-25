<#
.SYNOPSIS
    Checks comments against the conventions in CLAUDE.md, and holds the debt to a baseline.

.DESCRIPTION
    Three of the comment conventions can be read off the text: a comment says what the code does
    rather than when it was written, it does not name a decision or milestone number, and it does
    not carry an issue number unless that issue is open work the reader has to tolerate. Every
    other convention needs a person.

    The debt is per file, in comment-baseline.tsv. A file over its recorded count fails; a file
    under it fails too, asking for the baseline to be lowered, because a number nobody lowers is a
    number nobody believes. A file absent from the baseline must be clean. So the debt can only
    shrink, and a rewrite that fixes one site cannot pay for a new one somewhere else.

    Only comments are scanned. The same words in a string literal are a message to a user, and a
    format specifier like {session:D2} is not a milestone number.

.PARAMETER Update
    Rewrites the baseline from what is found now. For lowering it after a cleanup -- and the diff
    is the record of what the cleanup covered.

.NOTES
    Four phrases that look like they belong here and do not, measured over this repository before
    the rules were chosen:

    - "no longer" -- 55 sites, of which one is history. The rest describe a state the code has to
      handle: a target that is no longer running, a frame that is no longer selected, a binding
      that no longer resolves. Those are timeless descriptions of the thing the code is for.
    - "lands in" -- 12 sites, none of them a schedule. A write lands in a file, a step lands in a
      new stop, and "islands in a type" matches on a substring.
    - "today" -- 4 sites, three of them present-tense fact ("call sites that compile today"),
      which is what a comment is supposed to say.
    - "used to" bare -- 37 sites, 13 of them history. "The handle used to read it back" is the
      passive sense, so the rule takes a narrative subject with it and refuses the auxiliary that
      marks the passive one: "this is used to explain a failure" is a description, while
      "everything below used to ask Window::Current" is a commit message.

    A rule that fires mostly on correct comments teaches people to write around the rule.
#>

[CmdletBinding()]
param([switch] $Update)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$baselinePath = Join-Path $PSScriptRoot 'comment-baseline.tsv'

<#
    What each rule catches, and the convention it comes from.

    The issue rule looks behind the # for a word character or a slash, so a cross-repository
    reference like dotnet/roslyn#41640 is left alone: it names somebody else's tracker, which no
    amount of tidying this repository will close.
#>
$script:Rules = [ordered]@{
    history   = '(?i)\b(?:this|it|which|that|they|there|we|nothing|everything)\b[^.;]{0,24}(?<!\b(?:is|are|was|were|be|been|being|not|also)\s)\bused to\b|\bpreviously\b|\buntil now\b|\bfor now\b|\ba later slice\b|\b(?:comes|lands) in a later\b'
    milestone = '\b[DM][0-9]{1,2}\b|§[0-9]'
    issue     = '(?<![\w/])#[0-9]+\b'
}

<#
    The issues a comment may name, because each is open work a reader has to tolerate rather than a
    tag. An issue closing takes its number off this list, and the check then asks for the sentence
    to be rewritten to stand on its own -- which is the moment the explanation is worth keeping and
    the number is not.
#>
$script:OpenIssues = @(
    39  # The Roslyn fixtures are copied per test rather than shared, and the wait is the reason.
    195 # A match drops the comments between its tokens; a test pins the damage until it stops.
    217 # An insertion or an anchored replacement lays out lines it was not asked to; pinned the same way.
    218 # With no .editorconfig a write takes Roslyn's four spaces and re-indents a neighbour; pinned too.
)

$script:Advice = @{
    history   = 'Say the failure the code prevents, which is timeless, rather than what the code was before.'
    milestone = 'Restate the reason in a sentence, or link the decision page.'
    issue     = 'A closed issue number is a tag: drop it and keep the explanation.'
}

<#
    The comment text of one line, or nothing where the line has none.

    Everything after the first // -- which takes /// and a trailing comment with it -- plus the
    continuation lines of a block comment, which this repository writes as a leading *. A string
    holding // would be read as a comment, and does not appear in the files scanned.
#>
function Get-CommentText
{
    param([string] $Line, [string] $Extension)

    $trimmed = $Line.TrimStart()

    if ($Extension -in '.yml', '.yaml')
    {
        if ($trimmed.StartsWith('#')) { return $trimmed }
        return $null
    }

    if ($Extension -in '.csproj', '.props', '.targets', '.slnx')
    {
        if ($trimmed.StartsWith('<!--') -or $trimmed.StartsWith('-->')) { return $trimmed }
        # A comment body inside <!-- --> is an ordinary line, so anything not obviously markup counts.
        if (-not $trimmed.StartsWith('<') -and $trimmed.Length -gt 0) { return $trimmed }
        return $null
    }

    $slashes = $Line.IndexOf('//')
    if ($slashes -ge 0) { return $Line.Substring($slashes) }
    if ($trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')) { return $trimmed }

    return $null
}

<#
    The files the conventions apply to: the repository's own, which is what git tracks plus whatever
    is new and not ignored.

    Asked of git rather than of the file system, because the file system also holds what a build
    leaves inside the source tree. A provider build writes a C++/WinRT projection under
    src/RoseMcp.Xaml.WinUi.Tap/generated -- thousands of headers nobody here wrote and nothing here
    can change, which would fail the check on any machine that has built a provider -- and bin and
    obj are the best-known cases of the same thing. The ignore rules already say which paths those are, so
    they decide here too. A file not yet added is still read, since the check is run before a commit
    as often as after one.
#>
function Get-ScannedFile
{
    $patterns = @(
        @{ Path = 'src/'; Include = @('.cs', '.h', '.cpp', '.csproj') },
        @{ Path = 'tests/'; Include = @('.cs', '.csproj') },
        @{ Path = '.github/'; Include = @('.yml') }
    )

    # Separated by NUL, so no path comes back quoted or escaped.
    $listed = (git -C $root ls-files -z --cached --others --exclude-standard) -split "`0"

    if ($LASTEXITCODE -ne 0)
    {
        throw "git ls-files failed in $root, so there is no telling which files are the repository's own."
    }

    foreach ($path in $listed)
    {
        if ($path.Length -eq 0) { continue }

        $pattern = $patterns
            | Where-Object { $path.StartsWith($_.Path, [StringComparison]::Ordinal) }
            | Select-Object -First 1

        if ($null -eq $pattern) { continue }
        if ([IO.Path]::GetExtension($path).ToLowerInvariant() -notin $pattern.Include) { continue }

        # A file deleted from the working tree stays in the index until the deletion is staged.
        $full = Join-Path $root $path
        if (Test-Path -LiteralPath $full -PathType Leaf) { Get-Item -LiteralPath $full }
    }

    foreach ($name in 'Directory.Build.props', 'Directory.Packages.props')
    {
        $file = Join-Path $root $name
        if (Test-Path $file) { Get-Item $file }
    }
}

<#
    Every violation, as the relative path, the rule it broke, and the line. Sorted, so the baseline
    is stable whatever order the file system hands the files back in.
#>
function Get-Violation
{
    foreach ($file in Get-ScannedFile)
    {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        $extension = $file.Extension.ToLowerInvariant()
        $number = 0

        foreach ($line in [IO.File]::ReadAllLines($file.FullName))
        {
            $number++
            $comment = Get-CommentText -Line $line -Extension $extension
            if ($null -eq $comment) { continue }

            foreach ($rule in $script:Rules.Keys)
            {
                $matched = [regex]::Matches($comment, $script:Rules[$rule])
                if ($matched.Count -eq 0) { continue }

                if ($rule -eq 'issue')
                {
                    $matched = @($matched | Where-Object { [int] $_.Value.Substring(1) -notin $script:OpenIssues })
                    if ($matched.Count -eq 0) { continue }
                }

                [pscustomobject]@{
                    File  = $relative
                    Rule  = $rule
                    Line  = $number
                    Text  = $line.Trim()
                    Match = $matched[0].Value
                }
            }
        }
    }
}

function Read-Baseline
{
    $counts = @{}
    if (-not (Test-Path $baselinePath)) { return $counts }

    foreach ($line in [IO.File]::ReadAllLines($baselinePath))
    {
        if ($line.StartsWith('#') -or $line.Trim().Length -eq 0) { continue }

        $fields = $line -split "`t"
        $counts["$($fields[0])`t$($fields[1])"] = [int] $fields[2]
    }

    return $counts
}

$violations = @(Get-Violation)
$found = @{}
foreach ($violation in $violations)
{
    $key = "$($violation.File)`t$($violation.Rule)"
    $found[$key] = 1 + ($found[$key] ?? 0)
}

if ($Update)
{
    $lines = @(
        '# Comment-convention debt, per file and rule, written by tools/Check-Comments.ps1 -Update.'
        '# The counts may only go down. See CLAUDE.md, Conventions, for what each rule is about.'
        foreach ($key in $found.Keys | Sort-Object) { "$key`t$($found[$key])" }
    )

    Set-Content -Path $baselinePath -Value $lines -Encoding utf8NoBOM
    Write-Host "Baseline written: $($violations.Count) across $($found.Count) file-and-rule pairs."
    exit 0
}

$baseline = Read-Baseline
$failed = $false

foreach ($key in ($found.Keys + $baseline.Keys | Sort-Object -Unique))
{
    $now = $found[$key] ?? 0
    $allowed = $baseline[$key] ?? 0
    if ($now -eq $allowed) { continue }

    $file, $rule = $key -split "`t"
    $failed = $true

    if ($now -gt $allowed)
    {
        Write-Host "$file`: $rule is $now, over the $allowed allowed. $($script:Advice[$rule])"

        $violations
            | Where-Object { $_.File -eq $file -and $_.Rule -eq $rule }
            | ForEach-Object { Write-Host "  $($_.File):$($_.Line): $($_.Text)" }
    }
    else
    {
        Write-Host "$file`: $rule is $now, under the $allowed allowed. Run tools/Check-Comments.ps1 -Update and commit the baseline."
    }
}

if ($failed)
{
    Write-Host ''
    Write-Host 'Comment conventions: failed.'
    exit 1
}

Write-Host "Comment conventions: $($violations.Count) known, none new."
