# Security Policy

TicketFlow handles customer conversations, contact data and access-control decisions. Please report suspected vulnerabilities privately before opening a public issue.

## Supported Versions

| Version | Security fixes |
| --- | --- |
| `0.1.x` | Supported while `0.1.x` is the active pre-1.0 line. |
| `< 0.1.0` | Not supported. |

Pre-1.0 releases may change public APIs while the package set stabilizes. Security fixes target the active package line unless a later release note says otherwise.

## Reporting A Vulnerability

Use GitHub private vulnerability reporting when it is available for this repository: <https://github.com/kcrandall/TicketFlow/security/advisories/new>.

If GitHub private vulnerability reporting is not available to you, email the maintainers privately at [security@meetrobyn.com](mailto:security@meetrobyn.com). This mailbox is monitored and is the private fallback channel for reporters who reach the project through package metadata or mirrors and cannot use GitHub.

In either case, include:

- affected package and version
- vulnerable flow or endpoint
- reproduction steps
- expected impact
- any known workaround

Do not include live credentials, tokens, private keys, production user data, or app-specific database exports in the report.

## Scope

In scope:

- authentication bypasses
- credential verification flaws
- token, session, or DataProtection misuse
- passkey/WebAuthn ceremony flaws
- OAuth/OIDC callback and token handling flaws
- rate limit or abuse-protection bypasses
- sensitive data exposure in logs, audit events, or persistence

Out of scope:

- vulnerabilities in consuming applications outside TicketFlow integration points
- reports that require compromised host infrastructure
- unsupported package versions
