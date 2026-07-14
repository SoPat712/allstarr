# Branch Strategy & Workflow

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only - the user will document themselves. This keeps the repository clean and focused.

## Branch Overview

This project uses a three-tier branch strategy:

- **`dev`** - Development playground (no CI/CD)
- **`beta`** - Testing/preview environment (CI/CD enabled)
- **`main`** - Stable production (CI/CD enabled)

## Branch Rules

### `dev` Branch
- Your personal playground for development
- Make as many commits as you want
- No CI/CD pipelines run on this branch
- Individual commit history is preserved here
- **Never push `dev` directly to `beta` or `main`**

### `beta` Branch
- Testing and preview environment
- **Only receives squashed commits from `dev`**
- CI/CD builds and pushes Docker image tagged as `:beta`
- Clean commit history (one commit per feature/fix)
- Used for testing before production release

### `main` Branch
- Stable production environment
- **Only receives squashed commits from `beta`**
- CI/CD builds and pushes Docker image tagged as `:latest`
- Cleanest commit history (one commit per release)
- What users see and use

## Workflow

### 1. Development (dev → beta)

Work in `dev` with individual commits:

```bash
git checkout dev
# Make changes, commit as many times as needed
git add .
git commit -m "Work in progress"
git commit -m "Fix bug"
git commit -m "Add feature"
```

When ready to test, squash merge to `beta`:

```bash
git checkout beta
git pull origin beta
git merge --squash dev
git commit -m "v1.2.0-beta.1: Add feature X with bug fixes"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Add feature X with bug fixes"
git push origin beta --tags
```

This triggers CI/CD and pushes `ghcr.io/sopat712/allstarr:beta`

Sync `dev` with `beta`:

```bash
git checkout dev
git pull origin dev
git merge beta
git push origin dev
```

### 2. Release (beta → main)

When `beta` is stable and ready for production:

```bash
git checkout main
git pull origin main
git merge --squash beta
git commit -m "v1.2.0: Feature X and bug fixes"
git tag -a v1.2.0 -m "v1.2.0: Feature X and bug fixes"
git push origin main --tags
```

This triggers CI/CD and pushes `ghcr.io/sopat712/allstarr:latest`

Sync `beta` and `dev` with `main`:

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

## Docker Image Tags

- `ghcr.io/sopat712/allstarr:latest` - Built from `main` (stable)
- `ghcr.io/sopat712/allstarr:beta` - Built from `beta` (testing)
- `ghcr.io/sopat712/allstarr:main` - Built from `main` (branch name tag)
- `ghcr.io/sopat712/allstarr:<sha>` - Built from commit SHA
- `ghcr.io/sopat712/allstarr:v*` - Built from version tags

## Important Notes

1. **Always use `--squash` when promoting release changes**
   - `dev` → `beta`: `git merge --squash dev`
   - `beta` → `main`: `git merge --squash beta`

2. **Use regular merges only for required back-syncs**
   - After releasing `beta`, merge `beta` back into `dev`
   - After releasing `main`, merge `main` back into `beta` and `dev`
   - After hotfixing `beta`, merge `beta` back into `dev`
   - After hotfixing `main`, merge `main` back into `beta` and `dev`

3. **Always sync branches after merging**
   - After merging to `beta`, sync `dev` with `beta`
   - After merging to `main`, sync both `beta` and `dev` with `main`
   - Resolve conflicts intentionally; do not automatically use `--theirs`

4. **CI/CD only runs on `beta` and `main`**
   - Push to `dev` = no build
   - Push to `beta` = build + push `:beta` tag
   - Push to `main` = build + push `:latest` tag

## Current State

All three branches are currently in sync at the same commit.

## Example: Full Feature Development Cycle

```bash
# 1. Work in dev
git checkout dev
git add .
git commit -m "Start feature X"
git commit -m "WIP: feature X"
git commit -m "Fix typo"
git commit -m "Complete feature X"

# 2. Squash to beta for testing
git checkout beta
git pull origin beta
git merge --squash dev
git commit -m "v1.2.0-beta.1: Add feature X"
git tag -a v1.2.0-beta.1 -m "v1.2.0-beta.1: Add feature X"
git push origin beta --tags  # Triggers CI/CD, builds :beta

# 3. Sync dev
git checkout dev
git pull origin dev
git merge beta
git push origin dev

# 4. Test beta, when stable, release to main
git checkout main
git pull origin main
git merge --squash beta
git commit -m "v1.2.0: Add feature X"
git tag -a v1.2.0 -m "v1.2.0: Add feature X"
git push origin main --tags  # Triggers CI/CD, builds :latest

# 5. Sync everything
git checkout beta
git pull origin beta
git merge main
git push origin beta

git checkout dev
git pull origin dev
git merge main
git push origin dev
```

## Recovery: If You Accidentally Regular Merge During Promotion

If you accidentally do a regular merge instead of squash while promoting `dev` to `beta` or `beta` to `main`:

```bash
# Reset to before the merge
git reset --hard HEAD~1

# Do it correctly with squash
git merge --squash <source-branch>
git commit -m "Proper squashed commit message"
```

If already pushed, you'll need to force push:

```bash
git push origin <branch> --force
```

**Note:** Only force push if you're sure no one else is working on that branch!
