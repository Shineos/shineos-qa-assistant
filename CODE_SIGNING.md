# Code Signing Policy / コード署名ポリシー

> **日本語サマリー**: 本ページは SignPath Foundation 利用規約に基づく署名ポリシーです。署名対象、ビルドと署名の流れ、チーム役割（コミッター／レビュアー／承認者）、リリース承認の運用を定めます。現在はテスト証明書での署名であり、SignPath Foundation 承認後に正式証明書へ移行します。

This document is the code signing policy of **ShineosQA (社内知恵袋)** as required by the [SignPath Foundation](https://signpath.org/) terms of use.

## Project

- **Product**: ShineosQA — an offline, local-AI internal Q&A tool for small businesses
- **Repository / homepage**: https://github.com/Shineos/shineos-qa-assistant
- **License**: MIT (repository code). See [THIRD-PARTY-NOTICES](vendor/THIRD-PARTY-NOTICES.txt) for bundled/downloaded components.

## What is signed

| Artifact | Description |
|---|---|
| `ShineosQA-Setup-<version>.exe` | The Windows installer (Inno Setup) published on [GitHub Releases](https://github.com/Shineos/shineos-qa-assistant/releases) |

No other binaries are distributed by this project.

## Build and signing process

1. **Build**: Installers are built exclusively by [GitHub Actions](.github/workflows/release.yml) from a tagged commit (`v*`) of the public repository. No local builds are published.
2. **Origin verification**: The CI build is linked to the tagged source revision; SignPath.io verifies that the artifact was produced from the noted source repository before signing.
3. **Signing**: Code signing is performed by **[SignPath.io](https://signpath.io)** — the free code signing service for open source projects, provided by the **SignPath Foundation**. Private keys are generated and held in hardware security modules (HSM) operated by SignPath; the project team never has access to the private key.
4. **Release approval**: *Every* signing request is manually approved by the designated Approver (see Team roles) before the signature is applied.

> **Current status**: Releases are currently signed with a **test (self-signed) certificate**. Upon SignPath Foundation approval, releases will be signed with the Foundation's certificate and this page will be updated.

## Team roles

| Role | Members (GitHub) | Responsibility |
|---|---|---|
| Committer | @GuangxiZheng | Code commits to the repository |
| Reviewer | @GuangxiZheng | Code review before merge |
| Approver (signing) | @GuangxiZheng | Manual approval of each release signing request |

- Multi-factor authentication (MFA) is **required and enabled** on the GitHub accounts of all team members and on the SignPath account.
- Role changes are made by the repository owner and reflected in this table.

## Components and licensing of the signed artifact

The signed installer contains only project-owned code (MIT) and the following bundled components:

- **NSSM** (nssm.exe) — public domain
- **WebView2Loader.dll** — Microsoft redistributable (Microsoft Software License Terms)
- Project assets and scripts (MIT)

All major runtime components (**Ollama**, **Open WebUI**, **Python**, **PyTorch**, and AI models **Qwen2.5 / bge-m3**) are **not bundled** in the signed artifact; they are downloaded at installation time from their official distribution sources (github.com, pypi.org, nuget.org, ollama.com registry). License details: [vendor/THIRD-PARTY-NOTICES.txt](vendor/THIRD-PARTY-NOTICES.txt).

## Credits

Code signing for this project is provided by **[SignPath.io](https://signpath.io)** — the free code signing service for open source projects, and its certificate is provided by the **[SignPath Foundation](https://signpath.org/)**.

## Privacy

See [PRIVACY.md](PRIVACY.md).

## Contact

- Security / signing related inquiries: https://shineos.com/contact/
- Repository issues: https://github.com/Shineos/shineos-qa-assistant/issues
