# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Read `README.md` first. It is the design document, and it stays current with the code.

## The open questions are the point

The five "Open questions" in the README are places where the code works around a missing framework hook, and each
workaround is reproduced deliberately so a Critter Stack maintainer can see it. They are not a TODO list. Do not
"clean up" or "fix" them unless asked to.

## Commands

Tests are TUnit on Microsoft.Testing.Platform, so `dotnet test --filter` matches nothing — it reports "Zero tests
ran" rather than an error. Pass a tree-node filter to the test host instead:

```sh
dotnet test -- --treenode-filter "/*/*/IdempotencyStoreTests/*"   # one class
dotnet test -- --treenode-filter "/*/*/*/Validate_accepts_a_uuid" # one test
```

Bring Postgres up with `docker compose up -d` before any test that touches the database. The test host's
connection string is a hardcoded const with no configuration override, so without it those tests fail on
connection rather than on an assertion.

To read a generated chain, prefer `dotnet run --project src/Api -- codegen preview`. It answers the same question
as `codegen write` without leaving a snapshot behind for the next build to compile.

## Style

Members are separated by a blank line, including fields and single-line properties.

The `<remarks>` blocks throughout `src/Api/Idempotency/` document *failure modes* — what breaks, and why a
tempting alternative does not work. They are the artifact's main content, not noise. Do not compress or strip
them; when changing the behaviour they describe, update them.
