# SemCompare

A cloud-deployed C# semantic diff tool. Sign in with GitHub to access your public and private repositories, pick branches and commits from dropdowns, and get plain-English AI explanations of what actually changed — methods, fields, logic, and signatures.

## Features

- **GitHub OAuth** — sign in, access public + private repos, token stored securely in cookie
- **Repository browser** — searchable list of all repos you have access to
- **Branch picker** — dropdown populated from GitHub API, no typing required
- **Commit history browser** — click any commit as the From or To anchor
- **Semantic diff engine** — 3-pass: exact match → fuzzy rename → cross-class move detection
- **Body hashing** — detects logic changes even when signature is unchanged
- **Parameter-level diffs** — Added / Removed / TypeChanged per parameter
- **Gemini AI** — plain-English summary of each run + per-breaking-change explanations
- **Run history** — scoped per user, stored in SQLite
- **Code churn dashboard** — which methods/fields change most often

## Setup

### 1. Create a GitHub OAuth App

1. Go to https://github.com/settings/applications/new
2. Set **Homepage URL** to your deployed URL (e.g. `https://yourdomain.com`)
3. Set **Authorization callback URL** to `https://yourdomain.com/signin-github`
4. Copy the **Client ID** and generate a **Client Secret**

### 2. Configure secrets

Edit `SemCompare/appsettings.json`:

```json
{
  "GitHub": {
    "ClientId":     "your_client_id_here",
    "ClientSecret": "your_client_secret_here"
  },
  "Gemini": {
    "ApiKey": "sk-ant-..."
  }
}
```

For production, use environment variables or a secrets manager instead:

```bash
export GitHub__ClientId=...
export GitHub__ClientSecret=...
export Gemini__ApiKey=sk-ant-...
```

### 3. Run locally

```bash
cd SemanticDiff
dotnet run
```

For local testing, set the GitHub OAuth callback to `https://localhost:5001/signin-github`.

### 4. Deploy to cloud

The app is a standard ASP.NET Core 8 Blazor Server app. Deploy to:
- **Azure App Service** — `dotnet publish` then deploy the output folder
- **Railway / Render** — point at the repo, set env vars, done
- **Docker** — add a standard `dotnet/aspnet:8.0` Dockerfile

The SQLite database (`diff.db`) is created automatically on first run. For production, consider switching to PostgreSQL by changing the EF Core provider.

## How the diff engine works

**Pass 1 — Exact match**: Same class + same method name → Unchanged / SignatureChanged / BodyModified

**Pass 2 — Fuzzy rename**: Jaccard similarity on parameter types within the same class → Renamed

**Pass 3 — Move detection**: Removed methods with identical full signature appearing in a different class → Moved

**Body hash**: MD5 of `NormalizeWhitespace()` body text. Reformatting = no change. Logic change = `BodyModified`.

**Parameter diffs**: For SignatureChanged/Renamed, a `ParamDiff[]` records exactly which parameters were Added, Removed, or had their type changed. This feeds into the Claude prompt for more precise explanations.
