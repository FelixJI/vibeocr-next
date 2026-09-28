# Contributing

Open an issue before large changes. Keep generated files reproducible, run the repository CI commands, and never commit local path or editable dependencies.

## Commits and changelog

Use Conventional Commit titles and squash merge pull requests so the final
commit is the release-note source of truth.

- Release notes include `feat`, `fix`, `perf`, `revert`, and `deps`
  types, breaking changes, and commits whose scope is `deps`.
- All other types, including `security`, `build`, `docs`, `refactor`,
  `test`, `ci`, `style`, and `chore`, are hidden by default; use a
  `Changelog: include` or `Changelog: skip` trailer only when an
  override is genuinely required.
- Choose the type that describes the actual change. Do not relabel maintenance
  work merely to make it appear in the changelog.
