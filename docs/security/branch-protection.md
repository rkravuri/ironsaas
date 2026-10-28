# Main's protection, proved once against GitHub

TF-077 AC1 asks that a change without its review cannot merge, and the brief that main change only through a pull
request with every check green (ADR-0055). The settings are `.github/branch-protection.json`; the tests of
`tools/branch_protection.py` prove that any setting letting a change through without them is named. This page records
the proof against GitHub itself, made once when the protection was first applied in S3 T10a.

## The protection as applied

`make protect-main` on 2026-10-28 (`tools/branch_protection.py apply`), then the check of GitHub's live settings
against the file:

```
applied main's protection to rkravuri/ironsaas: a pull request with every check green; merge commits only
main's protection on rkravuri/ironsaas: as the settings say
{"mergeCommitAllowed":true,"rebaseMergeAllowed":false,"squashMergeAllowed":false}
```

## A push straight to main, and a force push, refused

A commit made from main's own tree with main as its parent, so that it would change nothing but the history, pushed to
main as the repository's owner, an administrator. Neither push reached main.

```
date: 2026-10-28T14:25:30Z
main before: e3d98b1f4fd6a77310cac962dd390047af8a630d
the commit pushed: 7d6e33f449b99fbf7285573b067e2943390b1c28 (origin/main's tree, origin/main as its parent)

$ git push origin 7d6e33f449b99fbf7285573b067e2943390b1c28:refs/heads/main
remote: error: GH006: Protected branch update failed for refs/heads/main.
remote:
remote: - Changes must be made through a pull request.
remote:
remote: - 9 of 9 required status checks are expected.
To https://github.com/rkravuri/ironsaas.git
 ! [remote rejected] 7d6e33f449b99fbf7285573b067e2943390b1c28 -> main (protected branch hook declined)
error: failed to push some refs to 'https://github.com/rkravuri/ironsaas.git'
exit: 1

$ git push --force origin 7d6e33f449b99fbf7285573b067e2943390b1c28:refs/heads/main
remote: error: GH006: Protected branch update failed for refs/heads/main.
remote:
remote: - Changes must be made through a pull request.
remote:
remote: - 9 of 9 required status checks are expected.
To https://github.com/rkravuri/ironsaas.git
 ! [remote rejected] 7d6e33f449b99fbf7285573b067e2943390b1c28 -> main (protected branch hook declined)
error: failed to push some refs to 'https://github.com/rkravuri/ironsaas.git'
exit: 1

main after: e3d98b1f4fd6a77310cac962dd390047af8a630d
main is unchanged
```

## Checking it again

`make protection-check` compares GitHub's settings with the file at any time and names each difference. Run it after
a change of the file, and when a second developer joins and PE-1 ends (`docs/security/exceptions.md`), after setting
`required_approving_review_count` to 1 and applying it with `make protect-main`.
