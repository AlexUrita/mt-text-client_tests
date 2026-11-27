# PreToolUse(Bash|PowerShell) commit guard. Reads the hook's stdin JSON, extracts the
# command, and BLOCKS (exit 2) a `git commit` that violates either CLAUDE.md rule:
#   1) committing directly on main/master — create a feature branch off the right base first.
#   2) a multi-line commit message passed inline — a here-string (@'...'@ / @"..."@) or a
#      message flag (-m / -am / -qam... / --message) whose value contains a newline or the
#      PowerShell newline escape (`n / `r). Use `git commit -F <msgfile>`.
# Single-line `-m "msg"` and `git commit -F <file>` are allowed. Mirrors the shape of
# scripts/check-secrets.ps1. Fails OPEN (exit 0) on any error so a guard hiccup never
# bricks the Bash/PowerShell tools.
#
# SCOPE: Rule 1 checks the branch of the repo the commit targets via an explicit
# `git -C <path>` or, otherwise, the hook's current working directory. It deliberately
# does NOT parse `cd`/`pushd`/env-var retargets out of the command string — that was a
# fragile, leaky heuristic (spaces, flags, ordering, cmd-style `/d`). A commit that
# retargets another repo purely via a shell `cd` is covered only when the shell's cwd is
# that repo (the common case) or `-C` is used.
try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    $cmd = $null
    try { $cmd = [string]((ConvertFrom-Json $raw).tool_input.command) } catch { exit 0 }
    if ([string]::IsNullOrEmpty($cmd)) { exit 0 }

    # Only inspect actual `git commit` invocations: allow leading global flags
    # (-C <path>, -c k=v, --opt) between `git` and `commit`; don't match commit-graph/-tree.
    # Keep the Match (not just a bool) so its position can order the branch-create skip below.
    $commitM = [regex]::Match($cmd, '(?i)\bgit\s+(?:-{1,2}\S+\s+|-C\s+\S+\s+|-c\s+\S+\s+)*commit\b(?!-)')
    if (-not $commitM.Success) { exit 0 }

    # --- Rule 2: no multi-line inline message ---
    # A real here-string's opener (@' / @") is, by PowerShell syntax, immediately followed
    # by a line break — requiring that avoids false-positives on a single-line message that
    # merely contains the two characters (e.g. `-m "see @'x'"`).
    $hasHereString = $cmd -match "@['`"]\r?\n"
    $multilineM = $false
    # Message flag: -m, --message, or a short-flag cluster of real git-commit letters ending
    # in m (-am, -qam, ...). Restricted letter set [aqsvne] so an unrelated tool's flag like
    # `-xm "..."` on a chained command does NOT false-positive. A single- or double-quoted
    # value is multi-line if it holds a literal newline/CR or the PowerShell escape `n/`r.
    if (-not $hasHereString) {
        foreach ($m in [regex]::Matches($cmd, "(?<![\w-])(?:-[aqsvne]*m|--message)[=\s]*(?:`"([^`"]*)`"|'([^']*)')")) {
            $val = $m.Groups[1].Value + $m.Groups[2].Value   # only one quote branch populates
            if ($val -match "`n|`r|``n|``r") { $multilineM = $true; break }
        }
    }
    if ($hasHereString -or $multilineM) {
        [Console]::Error.WriteLine("BLOCKED by scripts/check-commit.ps1 (CLAUDE.md rule): never pass a multi-line commit message inline (no here-strings, no -m with newlines). Write the message to a temp file and run: git commit -F <msgfile>")
        exit 2
    }

    # --- Rule 1: never commit directly on main/master ---
    # The branch a commit LANDS on is set by the LAST branch-position-changing op in the
    # command before the commit — not merely by whether a feature branch was ever created.
    # An earlier version exited 0 as soon as it saw a non-main `checkout -b`/`switch -c`
    # BEFORE the commit, so `git checkout -b feat; git switch main; git commit` (create a
    # branch, then hop back to main) slipped through. Compute two positions instead:
    #   $lastFeatureCreate — latest `checkout -b/-B` / `switch -c/-C <name>` whose name is
    #                        not main/master (a real feature-branch create), before the commit.
    #   $lastToMain        — latest op that LANDS on main/master (`checkout`/`switch`, incl.
    #                        a `-b/-B/-c/-C main`), before the commit.
    # If a hop onto main/master is the last of the two, the commit lands on main -> BLOCK.
    # Else if a feature branch was created with no later hop back -> trust it (exit 0).
    # Else (no branch ops) fall through to the cwd/-C branch check below.
    # (A contrived `...; switch main; switch feat; commit` over-blocks - fail-SAFE; it never
    # false-ALLOWS an on-main commit, which is the invariant that matters. `checkout -- <file>`
    # is a restore, not a hop, and the (?:main|master)(?![\w./-]) guard won't match a branch
    # like `main-feature`.)
    $lastFeatureCreate = -1
    foreach ($cm in [regex]::Matches($cmd, '(?i)\bgit\s+(?:-{1,2}\S+\s+|-C\s+\S+\s+|-c\s+\S+\s+)*(?:checkout\s+-{1,2}[bB]|switch\s+-{1,2}[cC])\s+["'']?([^\s"'';&|]+)')) {
        $nm = $cm.Groups[1].Value
        if ($cm.Index -lt $commitM.Index -and $nm -ne 'main' -and $nm -ne 'master' -and $cm.Index -gt $lastFeatureCreate) {
            $lastFeatureCreate = $cm.Index
        }
    }
    $lastToMain = -1
    foreach ($cm in [regex]::Matches($cmd, '(?i)\bgit\s+(?:-{1,2}\S+\s+|-C\s+\S+\s+|-c\s+\S+\s+)*(?:checkout|switch)\s+(?:-{1,2}[bBcC]\s+)?["'']?(?:main|master)(?![\w./-])')) {
        if ($cm.Index -lt $commitM.Index -and $cm.Index -gt $lastToMain) {
            $lastToMain = $cm.Index
        }
    }
    if ($lastToMain -gt $lastFeatureCreate) {
        [Console]::Error.WriteLine("BLOCKED by scripts/check-commit.ps1 (CLAUDE.md rule): the commit lands on main/master - a 'git checkout/switch main' precedes it. Create or switch to a feature branch before committing.")
        exit 2
    }
    if ($lastFeatureCreate -ge 0) { exit 0 }

    # Determine the branch of the repo the commit TARGETS: an explicit `git -C <path>`
    # (the -C is matched case-SENSITIVELY so git's lowercase `-c key=val` isn't mistaken
    # for it), else the hook's cwd.
    $repoArg = $null
    if ($cmd -match '(?i)\bgit\s+(?-i:-C)\s+(?:"([^"]+)"|''([^'']+)''|(\S+))') {
        $repoArg = ($Matches[1], $Matches[2], $Matches[3]) -join ''   # only one alternative populates
    }

    # symbolic-ref --short HEAD returns the branch name even on an UNBORN branch (fresh
    # `git init` with no commits → 'main'), where rev-parse --abbrev-ref HEAD would return
    # 'HEAD' and let the first on-main commit slip through. Select -First 1 guarantees a
    # scalar so a stray extra line can't defeat the comparison. Detached HEAD → no symbolic
    # ref → empty → not blocked (a detached commit isn't "on main").
    $branch = $null
    try {
        if ($repoArg) { $branch = (& git -C $repoArg symbolic-ref --short HEAD 2>$null | Select-Object -First 1) }
        else          { $branch = (& git symbolic-ref --short HEAD 2>$null | Select-Object -First 1) }
    } catch { exit 0 }
    if ($branch) { $branch = ([string]$branch).Trim() }

    if ($branch -eq 'main' -or $branch -eq 'master') {
        [Console]::Error.WriteLine("BLOCKED by scripts/check-commit.ps1 (CLAUDE.md rule): never commit directly on '$branch'. Create a feature branch off the intended base first (pre-commit branch check), then commit.")
        exit 2
    }

    exit 0
}
catch {
    exit 0   # fail open — never block the tool because the guard itself errored
}
