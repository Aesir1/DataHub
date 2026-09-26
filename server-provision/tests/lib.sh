# shellcheck shell=bash
# Shared output helpers for the security tests. Sourced, not executed.
#
# Every test script prints, for each section, WHAT it checks and WHY it matters, then one line per
# check:  [PASS] / [FAIL] / [WARN] / [INFO]. A summary at the end counts them. The exit code is the
# number of failures (capped at 125), so 0 means every check passed and CI can gate on it.

if [ -t 1 ]; then
    B=$'\033[1m'; DIM=$'\033[2m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; CYAN=$'\033[36m'; R=$'\033[0m'
else
    B=; DIM=; GREEN=; YELLOW=; RED=; CYAN=; R=
fi

PASS=0
FAIL=0
WARN=0
FAILED_CHECKS=()

# title "Script name" "one paragraph on what the whole script does"
title() {
    printf '%s========================================================================%s\n' "$B" "$R"
    printf '%s %s%s\n' "$B" "$1" "$R"
    printf '%s========================================================================%s\n' "$B" "$R"
    printf '%s\n' "$2" | fold -s -w 96
    printf '%sStarted %s on %s%s\n' "$DIM" "$(date -Iseconds)" "$(hostname)" "$R"
}

# section "Title" "What this checks and why it matters."
section() {
    printf '\n%s== %s ==%s\n' "$B$CYAN" "$1" "$R"
    printf '%s\n' "$2" | fold -s -w 96 | sed "s/^/${DIM}   /;s/\$/${R}/"
}

pass() { printf '  %s[PASS]%s %s\n' "$GREEN" "$R" "$1"; PASS=$((PASS + 1)); }
warn() { printf '  %s[WARN]%s %s\n' "$YELLOW" "$R" "$1"; [ -n "${2:-}" ] && printf '         %sfix: %s%s\n' "$DIM" "$2" "$R"; WARN=$((WARN + 1)); }
info() { printf '  %s[INFO]%s %s\n' "$CYAN" "$R" "$1"; }
fail() {
    printf '  %s[FAIL]%s %s\n' "$RED" "$R" "$1"
    [ -n "${2:-}" ] && printf '         %sfix: %s%s\n' "$DIM" "$2" "$R"
    FAIL=$((FAIL + 1))
    FAILED_CHECKS+=("$1")
}

# check "description" command...  -> PASS when the command succeeds, FAIL otherwise.
check() {
    local description="$1"; shift
    if "$@" >/dev/null 2>&1; then pass "$description"; else fail "$description"; fi
}

summary() {
    printf '\n%s------------------------------------------------------------------------%s\n' "$B" "$R"
    printf '%sResult:%s %s%d passed%s, %s%d failed%s, %s%d warnings%s\n' \
        "$B" "$R" "$GREEN" "$PASS" "$R" "$RED" "$FAIL" "$R" "$YELLOW" "$WARN" "$R"
    if [ "$FAIL" -gt 0 ]; then
        printf '%sFailed checks:%s\n' "$RED" "$R"
        printf '  - %s\n' "${FAILED_CHECKS[@]}"
        printf '%sThe server is NOT in the expected hardened state.%s\n' "$RED" "$R"
    elif [ "$WARN" -gt 0 ]; then
        printf '%sNo failures. Review the warnings above.%s\n' "$YELLOW" "$R"
    else
        printf '%sAll checks passed.%s\n' "$GREEN" "$R"
    fi
    exit $((FAIL > 125 ? 125 : FAIL))
}

# tcp_open host port [timeout]  -> succeeds when a TCP connection can be opened. No nc needed.
tcp_open() { timeout "${3:-3}" bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null; }
