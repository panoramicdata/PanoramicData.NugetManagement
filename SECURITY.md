# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| Latest  | :white_check_mark: |

## Reporting a Vulnerability

If you discover a security vulnerability in this project, please report it responsibly.

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, please email security@intodayshighlight.com with:

- A description of the vulnerability
- Steps to reproduce the issue
- Any relevant logs or screenshots

We will acknowledge your email within 48 hours and provide an estimated timeline for a fix.

## Security Best Practices

This library interacts with the GitHub API using authentication tokens. When using this library:

- **Never commit API tokens** to source control
- Store tokens in environment variables or secure vaults
- Use the minimum required permissions for GitHub tokens
- Rotate tokens regularly
