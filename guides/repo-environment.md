# Repository test environment

This repository is developed and tested from Windows, with its local services
split across Windows and WSL:

- **MySQL** runs as the Windows `MySQL80` service. The EF-backed event-box tests
  connect to `127.0.0.1:3306` as a MySQL user that can create databases. They
  use a disposable database named `trueparsercoverage`.
- **NATS with JetStream** runs in WSL. Windows can reach the WSL service through
  `localhost` (for example, `nats://localhost:4222`) when WSL localhost
  forwarding is enabled. Otherwise use the WSL IP from `wsl.exe -e hostname -I`.
- **Redis** runs in WSL and is normally reachable from Windows at
  `localhost:6379` under the same port-forwarding condition.

The NATS integration tests use a primary JetStream server, a second independent
server, and an authenticated server. Start these in separate WSL terminals when
running every environment-gated test:

```bash
nats-server -js -p 4222
nats-server -js -p 4223
nats-server -js -p 4224 --user testuser --pass testpass
```

The live test project uses `RUN_NATS_TESTS=true`. EF-backed event-box tests
also require `TRUEPARSER_TEST_MYSQL_CONNECTION`, for example:

```powershell
$env:RUN_NATS_TESTS = 'true'
$wslIp = (wsl.exe -e hostname -I).Trim().Split(' ')[0]
$env:NATS_TEST_URL = "nats://$wslIp`:4222"
$env:NATS_SECONDARY_TEST_URL = "nats://$wslIp`:4223"
$env:NATS_AUTH_TEST_URL = "nats://$wslIp`:4224"
$env:NATS_AUTH_TEST_USERNAME = 'testuser'
$env:NATS_AUTH_TEST_PASSWORD = 'testpass'
$env:TRUEPARSER_TEST_MYSQL_CONNECTION = 'Server=127.0.0.1;Port=3306;User ID=root;Password=<local-password>;'
dotnet test TrueParser.Abp.Nats.slnx
```

Do not commit local passwords or other credentials. `NATS_TEST_URL` otherwise
defaults to `nats://localhost:4222`. Redis is available for local development
at `localhost:6379`; the current test project does not require Redis.
