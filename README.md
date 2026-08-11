# sonarqube-mcp

A Model Context Protocol server for **SonarQube Cloud**, written in C# on .NET 10 and published as
a Native AOT single binary per RID.

> **Status: under construction.** This file is a placeholder so that the package and the release
> archives have the README they ship with. The full document — tool table, install instructions,
> token creation walkthrough, client configuration snippets, environment variable reference,
> troubleshooting and security notes — is written in Phase E, against the frozen tool table.

## Building

```sh
./build.sh Test          # restore, compile, run the tests
./build.sh SmokeTest     # publish the Native AOT binary and drive a real stdio JSON-RPC handshake
```

On Windows use `.\build.ps1` with the same arguments.

## Licence

MIT — see [LICENSE](LICENSE).
