# PreToolUse(Bash|PowerShell) secret guard. Reads the hook's stdin JSON, extracts the
# command, and BLOCKS (exit 2) only if the command text contains a probable secret:
# a 64-hex ClientToken or a JWT (same shapes as scripts/secret-scan.js). Fails OPEN
# (exit 0) on any error so a guard hiccup never bricks the Bash/PowerShell tools.
try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    $cmd = $null
    try { $cmd = [string]((ConvertFrom-Json $raw).tool_input.command) } catch { exit 0 }
    if ([string]::IsNullOrEmpty($cmd)) { exit 0 }

    $hex = [regex]'[0-9a-fA-F]{64}'                              # ClientToken (64-hex)
    $jwt = [regex]'eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}'    # JWT

    if ($hex.IsMatch($cmd) -or $jwt.IsMatch($cmd)) {
        [Console]::Error.WriteLine('BLOCKED by scripts/check-secrets.ps1: the command contains a probable secret (64-hex ClientToken or JWT). Remove it, or pass the value via a file / env var instead of inline.')
        exit 2
    }
    exit 0
}
catch {
    exit 0   # fail open — never block Bash because the guard itself errored
}
