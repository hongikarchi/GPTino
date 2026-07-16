# Pinned source references

`sources.lock.json` is the source of truth for repositories used while designing
GPTino. Run `scripts/fetch-references.ps1` to create ignored, detached checkouts
under `.references/`.

The checkouts are not Git remotes of GPTino. Updating a pin requires a focused
review, license check, replay tests, and a commit explaining what behavior is
being ported. Do not automatically merge an upstream branch.
