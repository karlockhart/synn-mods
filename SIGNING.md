# Code signing

The Synn Mods installer and app are Authenticode-signed with **Azure Artifact Signing** (formerly
Trusted Signing). It's the same account and certificate profile as
[simple-crosshair](https://github.com/karlockhart/simple-crosshair):

| Setting | Value (in [signing/metadata.json](signing/metadata.json)) |
|---|---|
| Endpoint | `https://wus2.codesigning.azure.net` (West US 2) |
| Account | `karlockhart` |
| Certificate profile | `karlockhartpublic` |

None of these are secret. Credentials come only from your own `az login` (locally) or from GitHub's
OIDC sign-in (in Actions). No password, key or client secret is stored in this repo.

- [tools/sign.ps1](tools/sign.ps1) signs files with signtool and Microsoft's signing client. It's
  the same script as simple-crosshair.
- [build.ps1](build.ps1) `-Sign` passes it to Velopack, which signs `SynnMods.exe`, Velopack's
  `Squirrel.exe` (Update.exe) and the shortcut stub, and then `SynnMods-win-Setup.exe`. That's
  4 signatures per build, against the 5,000/month quota.

## Signing locally

Needs the Azure CLI (`winget install -e --id Microsoft.AzureCLI`), Windows SDK signtool 10.0.22621+
(already installed here) and an account with the **Artifact Signing Certificate Profile Signer** role.

```powershell
az login
.\build.ps1 -Version 0.1.0 -Sign
```

## Signing in GitHub Actions (one-time setup for this repo)

The Entra app from simple-crosshair (`github-signing`) can sign for this repo too. It needs one more
federated credential and the repo needs a `signing` environment:

1. **Federated credential**: Portal → *Microsoft Entra ID* → *App registrations* → `github-signing`
   → *Certificates & secrets* → *Federated credentials* → *Add* → *GitHub Actions deploying Azure
   resources*: organization `karlockhart`, repository `synn-mods`, entity type **Environment**, name
   **signing**.
2. **GitHub environment**: repo *Settings* → *Environments* → *New environment* `signing`. Under
   *Deployment branches and tags*, allow only tags matching `v*`. Then add the same two values as
   simple-crosshair:

   ```powershell
   gh secret set AZURE_CLIENT_ID --env signing -R karlockhart/synn-mods
   gh secret set AZURE_TENANT_ID --env signing -R karlockhart/synn-mods
   ```

3. **Release**: `git tag v0.1.0; git push origin v0.1.0`. See [README.md](README.md#releasing-the-app).

The signer role is already on the app from simple-crosshair, so nothing changes in the signing
account itself.
