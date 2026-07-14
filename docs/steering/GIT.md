# Git Workflow

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only - the user will document themselves. This keeps the repository clean and focused.

## Branch Strategy

Allstarr uses a three-tier branch pyramid for clean commit history:

- **dev**: Development branch - all individual commits (messy, detailed)
- **beta**: Testing branch - squashed commits from dev (cleaner)
- **main**: Production branch - squashed commits from beta (cleanest)

### Commit Pyramid

```
dev:  100+ commits (all the work, detailed messages)
       ↓ squash merge
beta: ~10 commits (major features, humanized messages)
       ↓ squash merge
main: ~3 commits (releases, concise messages)
```

## Release Workflow

For release tasks, follow this runbook exactly. Promotions are squash merges downward through the release pyramid. After each release, back-merge the released branch upward so hotfixes, release commits, and tags remain part of the shared branch ancestry.

### dev → beta Release

Use this when the user asks to push a beta release from `dev`.

```bash
git checkout beta
git pull origin beta
git merge dev --squash
git status
```

If conflicts occur, resolve them intentionally. Do not automatically use `--theirs`.

```bash
git commit -m "v1.2.0-beta.1: Short, humanized message"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Short description of changes"
git push origin beta --tags
```

After the beta release, back-merge `beta` into `dev`:

```bash
git checkout dev
git pull origin dev
git merge beta
git push origin dev
```

### beta → main Stable Release

Use this when the user asks to push a production release to `main` from `beta`.

```bash
git checkout main
git pull origin main
git merge beta --squash
git status
```

If conflicts occur, resolve them intentionally. Do not automatically use `--theirs`.

```bash
git commit -m "v1.2.0: Short, humanized message"
git tag -a v1.2.0 -m "v1.2.0: Short description of changes"
git push origin main --tags
```

Stable releases do not use the `-beta.N` suffix. The stable version must match the beta version without the suffix.

After the stable release, back-merge `main` into both `beta` and `dev`:

```bash
git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

Then leave the repository on the active development branch:

```bash
git checkout dev
```

### Hotfix Back-Merges

If a hotfix is made on `beta`, merge it upward into `dev`:

```bash
git checkout dev
git pull origin dev
git merge beta
git push origin dev
```

If a hotfix is made on `main`, merge it upward into both `beta` and `dev`:

```bash
git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

### Flow Summary

```
dev  -> squash -> beta -> tag vX.Y.Z-beta.N
beta -> merge  -> dev

beta tested
beta -> squash -> main -> tag vX.Y.Z
main -> merge  -> beta
main -> merge  -> dev
```

**IMPORTANT: Always tag releases with annotated tags (`git tag -a`).** Tags help track versions and make it easy to reference specific releases.

## Commit Message Style

Allstarr uses Conventional Commits for development commits. Use the format `<type>[optional scope]: <description>` and keep the message focused on the behavioral change.

### Good Examples

```
fix: deduplicate cache mode downloads
feat: add Spotify playlist injection
refactor: simplify search merging logic
docs: update architecture documentation
```

### Bad Examples

```
fix: enable deduplication for cache mode

- Cache mode now registers downloads in mappings file
- Prevents duplicate downloads of same track
- Improves storage efficiency
```

**NEVER use bullet points in commit messages!** Keep it to one or two sentences maximum.

```
fix: use unified download structure for cache and permanent files

- Cache mode now uses downloads/cache/ instead of cache/Music/
- Permanent mode now uses downloads/permanent/ instead of downloads/
- Kept files already use downloads/kept/
- All download paths now unified under downloads/ base directory
```

This is TOO VERBOSE! Instead:

```
fix: use unified download structure for all files

Cache and permanent files now go to downloads/cache/ and downloads/permanent/.
```

### Rules

- Short and humanized, not verbose
- No bullet point lists in commit messages
- Just the essential info
- Follow Conventional Commits structure: `<type>[optional scope]: <description>`
- Use conventional commit prefixes:
  - `feat:` - New feature
  - `fix:` - Bug fix
  - `refactor:` - Code refactoring
  - `docs:` - Documentation changes
  - `test:` - Test changes
  - `chore:` - Build/tooling changes

## Development Workflow

### Starting New Work

```bash
# Make sure you're on dev
git checkout dev
git pull origin dev

# Create feature branch (optional)
git checkout -b feature/my-feature

# Make changes and commit frequently
git add .
git commit -m "feat: add new feature"

# Push to dev (or merge feature branch to dev)
git checkout dev
git merge feature/my-feature
git push origin dev
```

### Preparing for Release

```bash
# 1. Merge dev to beta (squash)
git checkout beta
git pull origin beta
git merge dev --squash
git commit -m "v1.2.0-beta.1: Spotify integration and lyrics support"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Spotify playlists and lyrics"
git push origin beta --tags

# 2. Back-merge beta to dev
git checkout dev
git pull origin dev
git merge beta
git push origin dev

# 3. Test beta thoroughly
# - Deploy to staging
# - Run integration tests
# - Manual testing

# 4. If tests pass, merge beta to main (squash)
git checkout main
git pull origin main
git merge beta --squash
git commit -m "v1.2.0: Spotify playlists and lyrics"
git tag -a v1.2.0 -m "v1.2.0: Spotify playlists and lyrics"
git push origin main --tags

# 5. Back-merge main to beta and dev
git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

**Note:** Always use annotated tags (`-a`) for releases, not lightweight tags. Annotated tags include metadata like tagger name, date, and message.

## Commit Frequency

### On dev branch
- Commit often (every logical change)
- Detailed commit messages are fine
- Don't worry about commit history cleanliness
- Push frequently to backup work

### On beta branch
- Squash multiple dev commits into one
- Humanized, concise messages
- One commit per major feature or fix
- Test before pushing

### On main branch
- Squash multiple beta commits into one
- Very concise, release-focused messages
- One commit per release
- Always tag with version number

## Handling Conflicts

### During Squash Merge

```bash
# If conflicts occur during squash merge
git checkout beta
git merge dev --squash

# Resolve conflicts intentionally; do not automatically use --theirs
# Edit conflicting files
git add .
git commit -m "feat: merged feature X from dev"
```

### Avoiding Conflicts

- Keep dev up to date with beta/main features
- Don't make changes directly on beta/main
- All work happens on dev
- Squash merges reduce conflict likelihood

## Branch Protection

### Recommended Settings

**main branch:**
- Require pull request reviews
- Require status checks to pass
- Require branches to be up to date
- No direct pushes

**beta branch:**
- Require status checks to pass
- Allow squash merges only
- No direct pushes (except from dev)

**dev branch:**
- Allow direct pushes
- No restrictions (development freedom)

## Release Process

### Version Numbering

Follow Semantic Versioning (semver):
- **Major** (1.0.0): Breaking changes
- **Minor** (1.1.0): New features, backwards compatible
- **Patch** (1.1.1): Bug fixes, backwards compatible

### Release Checklist

1. ✅ All tests pass on dev
2. ✅ Squash merge dev → beta
3. ✅ Deploy beta to staging
4. ✅ Run integration tests
5. ✅ Manual testing with clients
6. ✅ Update CHANGELOG.md
7. ✅ Tag beta release with `vX.Y.Z-beta.N`
8. ✅ Back-merge beta into dev
9. ✅ Squash merge beta → main
10. ✅ Tag stable release with `vX.Y.Z`
11. ✅ Back-merge main into beta and dev
12. ✅ Deploy main to production
13. ✅ Create GitHub release with notes

## Hotfix Workflow

For urgent fixes, merge the fixed branch upward after the release so all long-lived branches share ancestry.

```bash
# 1. Create hotfix branch from main for production fixes
git checkout main
git checkout -b hotfix/critical-bug

# 2. Fix the bug
git add .
git commit -m "fix: critical bug in streaming"

# 3. Release to main
git checkout main
git merge hotfix/critical-bug --squash
git commit -m "v1.2.1: Critical streaming bug fix"
git tag -a v1.2.1 -m "v1.2.1: Critical streaming bug fix"
git push origin main --tags

# 4. Back-merge main to beta
git checkout beta
git pull origin beta
git merge main
git push origin beta

# 5. Back-merge main to dev
git checkout dev
git pull origin dev
git merge main
git push origin dev

# 6. Delete hotfix branch
git branch -d hotfix/critical-bug
```

## Common Scenarios

### Scenario 1: Feature Development

```bash
# Work on dev
git checkout dev
# ... make changes ...
git commit -m "feat: add feature X"
git commit -m "fix: bug in feature X"
git commit -m "refactor: improve feature X"
git push origin dev

# When ready for testing
git checkout beta
git pull origin beta
git merge dev --squash
git commit -m "v1.2.0-beta.1: Feature X with improvements"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Feature X with improvements"
git push origin beta --tags

git checkout dev
git pull origin dev
git merge beta
git push origin dev
```

### Scenario 2: Bug Fix

```bash
# Fix on dev
git checkout dev
git commit -m "fix: resolve issue #123"
git push origin dev

# Merge to beta for testing
git checkout beta
git pull origin beta
git merge dev --squash
git commit -m "v1.2.1-beta.1: Resolve streaming issue"
git tag -a v1.2.1-beta.1 -m "v1.2.1-beta.1: Fix streaming issue"
git push origin beta --tags

# Back-merge beta to dev
git checkout dev
git pull origin dev
git merge beta
git push origin dev

# If stable, release to main
git checkout main
git pull origin main
git merge beta --squash
git commit -m "v1.2.1: Streaming issue fix"
git tag -a v1.2.1 -m "v1.2.1: Fix streaming issue"
git push origin main --tags

git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

### Scenario 3: Documentation Update

```bash
# Update on dev
git checkout dev
git commit -m "docs: update architecture guide"
git push origin dev

# Merge to beta
git checkout beta
git pull origin beta
git merge dev --squash
git commit -m "v1.2.0-beta.1: Documentation updates"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Documentation updates"
git push origin beta --tags

git checkout dev
git pull origin dev
git merge beta
git push origin dev

# Merge to main
git checkout main
git pull origin main
git merge beta --squash
git commit -m "v1.2.0: Documentation updates"
git tag -a v1.2.0 -m "v1.2.0: Documentation updates"
git push origin main --tags

git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

## Tips

1. **Commit often on dev** - Don't worry about messy history
2. **Squash when promoting releases** - Keep beta and main clean
3. **Always back-merge after releases and hotfixes** - Keep long-lived branches aligned
4. **Test before merging to main** - Beta is your staging branch
5. **Always tag releases** - Use annotated tags (`-a`) with version numbers (`v1.2.1-beta.1` for beta, `v1.2.1` for main)
6. **Push tags with commits** - Use `git push origin beta --tags` to push tags
7. **Write good squash messages** - They become your release notes
8. **Use conventional commits** - Makes changelog generation easier
9. **Include version in release commit messages** - e.g., `v1.2.1-beta.1: Feature description` or `v1.2.1: Feature description`

## Tools

### Recommended Git Aliases

```bash
# Add to ~/.gitconfig
[alias]
    squash = merge --squash
    dev = checkout dev
    beta = checkout beta
    prod = checkout main
    lg = log --oneline --graph --decorate
```

### Usage

```bash
git dev              # Switch to dev
git squash dev       # Squash merge dev into current branch
git lg               # View commit graph
```

## References

- [Conventional Commits](https://www.conventionalcommits.org/)
- [Semantic Versioning](https://semver.org/)
- [Git Squash Merge](https://git-scm.com/docs/git-merge#Documentation/git-merge.txt---squash)
