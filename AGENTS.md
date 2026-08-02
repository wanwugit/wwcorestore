# AGENTS.md

Compact guidance for OpenCode sessions working in CoreShop. Verify before trusting.

## Solution & Toolchain

- Solution file is `CoreShopCommunity.slnx` (new XML-based SLNX format, not `.sln`). Open/build with `dotnet build CoreShopCommunity.slnx`.
- Target framework: **`net10.0`** on all 22 projects. README still says .NET 9 — stale; trust the csprojs. Local SDK in use: `10.0.301`.
- No `Directory.Build.props` / `Directory.Packages.props` / `global.json` / `nuget.config`. Package versions are pinned per csproj.
- No CI workflows configured (`.github/` empty). A standalone preflight script for human/CI use lives at `scripts/preflight-check.ps1` (validates `appsettings.json` mandatory keys + Redis/DB connectivity + Distribution gate state); a SQL verification pack at `scripts/migration-verify.sql` covers migrations 003/004/005 pre/post-checks and runtime conservation reconciliation.

## Build & Test Commands

- Build solution: `dotnet build CoreShopCommunity.slnx -c Debug`
- Build a single project: `dotnet build CoreCms.Net.Web.Admin/CoreCms.Net.Web.Admin.csproj`
- Run all tests: `dotnet test CoreCms.Net.Tests/CoreCms.Net.Tests.csproj`
- Run a single test: `dotnet test CoreCms.Net.Tests/CoreCms.Net.Tests.csproj --filter "FullyQualifiedName~ChangeAsync_Recharge_IncreasesBalance"`
- Build produces 0 errors and ~267 warnings. Warning breakdown (all pre-existing, none are build failures):
  - `NU1903` advisories on transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.10/2.1.11, `System.Security.Cryptography.Xml` 8.0.3, and **AutoMapper 12.0.1** (CVE-2026-32933, DoS via deeply-nested self-referential object graphs — see decision under "AutoMapper" below).
  - `CS1591` missing XML doc comments in `Web.Admin`/`Web.WebApi` (Chinese XML docs expected in Debug via `DocumentationFile=doc.xml`).
  - `NU1510` "package not removed" on a few redundant `Microsoft.Extensions.*` references already satisfied by the net10.0 shared framework.
  Do not treat these as failures. Do not suppress `NU1903` globally — it would hide new advisories.

## Testing Notes

- xUnit. `CoreCms.Net.Tests` is the only test project.
- Integration tests for `CoreCmsUserBalanceServices` use **SQLite in-memory** (`Microsoft.Data.Sqlite` + `SqlSugarScope` with `CodeFirst.InitTables`). No external database or Redis required to run tests — they are self-contained.
- Test base class `BalanceTestBase` (in `CoreCms.Net.Tests/User/CoreCmsUserBalanceServicesTests.cs`) wires a `TestUnitOfWork` + repositories + service manually (no Autofac container). Follow this pattern when adding integration tests for other services.
- Error codes asserted by numeric constants (e.g. `11007` = insufficient balance, `11005` = negative amount, `11008` = empty idempotency key, `11006` = zero amount). See `docs/design/02-financial-idempotency-design.md` for the code table.

## Architecture

Solution is organized into numbered solution folders (visible in `CoreShopCommunity.slnx`):

| Folder | Projects | Role |
|---|---|---|
| `/1.Core/` | Auth, Caching, CodeGenerator, Configuration, Core, Filter, Loging, Mapping, Middlewares, RedisMQ, Swagger, Task, Utility | Infrastructure libraries |
| `/2.Entity/` | Model | Entities, DTOs, view models |
| `/3.Services/` | IServices (interfaces), Services (impl) | Business logic |
| `/4.Repository/` | IRepository (interfaces), Repository (impl) | Data access |
| `/5.WeChat/` | WeChat.Service | WeChat MP/MiniProgram SDK wrapper + MediatR handlers |
| `/9.App/` | Web.Admin, Web.WebApi, Uni-App | Entry points |
| `/Tests/` | Tests | xUnit |

### Two independent web hosts (both `Microsoft.NET.Sdk.Web`, `net10.0`)

- **`CoreCms.Net.Web.Admin`** — admin panel. Hosts LayUIAdmin static UI under `wwwroot/` (not compiled; `MvcRazorCompileOnPublish=false`). Entry: `Program.cs`. Has `UserSecretsId`. Excludes `Controllers/WeChat/**` except two explicitly-included controllers.
- **`CoreCms.Net.Web.WebApi`** — public-facing API for the uni-app frontends. Hosts Hangfire dashboard at `/job`. Includes `RedisMQ` and `Model` references that Admin does not. Entry: `Program.cs`.

Both apps boot Autofac, SqlSugar, Redis cache, AutoMapper, MediatR (registered from `WeChat.Service` assembly), and paylink (Alipay + WeChatPay) in `Program.cs`.

### DI / ORM / Stack (non-default — agents familiar with EF+MS DI should note)

- **Autofac** (`Autofac.Extensions.DependencyInjection` 10.0.0) is the DI container, not the default `Microsoft.Extensions.DependencyInjection` purely. Bulk service registration lives in `CoreCms.Net.Core/AutoFac/`.
- **SqlSugar** (`sqlSugarCore` 5.1.4.207) is the ORM, not EF Core. Repositories take an `IUnitOfWork` exposing `SqlSugarScope` via `GetDbClient()`. CodeFirst is used for test schema init; runtime schema is managed via DB scripts.
- **AutoMapper** is the **OSS edition (12.0.1)**, the last free release before LuckyPennySoftware commercialized the package (v13+ requires a license key). The actual nuspec declares `<license type="expression">MIT</license>` — v12.0.1 is genuinely MIT-licensed, **not** an "accepted-risk" license decision; the only risk tracked for AutoMapper 12.0.1 is the security CVE-2026-32933. Registration is the plain `builder.Services.AddAutoMapper(typeof(AutoMapperConfiguration))` in both web hosts' `Program.cs` (the commercial `LicenseKey=` overload was deliberately removed to eliminate a third-party kill-switch on startup). The commercial patched versions (15.1.1+ / 16.1.1+) that fix **CVE-2026-32933** (DoS via deeply-nested self-referential object graphs) are intentionally NOT used. Do not re-introduce a `LicenseKey=` call or bump AutoMapper above 12.0.1 without an explicit decision — see `docs/design` notes and the accept-risk record below. The `AutoMapper.Extensions.Microsoft.DependencyInjection` 12.0.1 package is referenced in `CoreCms.Net.Mapping` to provide `AddAutoMapper`.
- **MediatR** is pinned to **12.4.1** (last Apache-2.0 release, 2024-09-09; nuspec confirms `<license type="expression">Apache-2.0</license>`). LuckyPennySoftware's 13.0.0 (2025-07) switched to **RPL-1.5 + commercial** dual license; RPL-1.5's "External Deployment" clause would force CoreShop (Apache 2.0) to re-license under RPL-1.5 once the WebApi serves any external request, which is incompatible with Apache 2.0. Registration in `Web.WebApi/Program.cs` uses `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))`, the same API in 12.x and 13.x. Do not bump MediatR above 12.4.1 (nor 12.5.0 without re-checking) without verifying the license status.
- **Hangfire** + `Hangfire.Redis.StackExchange` for background jobs (WebApi only). Dashboard gated by `HangFire:Login`/`PassWord` in `appsettings.json`.
- **Redis** is mandatory at runtime (`UseCache:true`, `UseTimedTask:true`) for cache and Hangfire storage. Tests do not require Redis.

### Configuration (`appsettings.json`, both web hosts)

Switch database with `ConnectionStrings:DbType` ∈ {`SqlServer`, `MySql`}.
- SqlServer connection string **must** include `MultipleActiveResultSets=true`.
- MySql string must keep the trailing charset/zerodate flags shown in the comment — versions below 5.7 are unsupported.

Keys an agent must fill before the apps start in a fresh env: `JwtConfig:SecretKey` (16+ chars), `JwtConfig:Issuer`, `HangFire:Login`/`PassWord`, `SwaggerConfig:UserName`/`PassWord`, `RedisConfig:ConnectionString`. WeChat/Alipay payment sections can stay empty if those flows are unused.

### Frontend (`CoreCms.Net.Uni-App`)

- The csproj is a placeholder — it excludes `CoreShop/.hbuilderx/**` and `CoreShop/unpackage/**` and compiles nothing. The actual uni-app source lives under `CoreCms.Net.Uni-App/CoreShop/` (App.vue, pages.json, manifest.json, uView UI).
- Built with **HBuilderX**, not `dotnet`. Do not attempt `dotnet build` of the uni-app project expecting real output; it exists only so the solution tree includes the folder.
- `manifest.json` keys the WeChat MiniProgram AppId — must match `WeChatOptions:WxOpenAppId` in the WebApi `appsettings.json`.

## Database & Migrations

- No EF migrations, no codegen scripts committed. Schema is bootstrapped from dumps in the **`数据库/`** (Chinese for "database") directory:
  - `数据库/SqlServer/*.bak` — SQL Server backups (restore in SSMS).
  - `数据库/MySql/*.sql` — MySQL dumps (Navicat / SQLyog / DMS variants).
- When adding an entity field, update the SqlSugar model, the dump scripts, and the Design docs under `docs/design/` (the financial design notes, e.g. `06-financial-test-plan.md`, track expected schema).

## Docker

`docker-compose.yaml` builds two images from `./front/publish` and `./api/publish` with their own `Dockerfile`s (each web project has its own `Dockerfile`, `CopyToOutputDirectory=PreserveNewest`). **You must `dotnet publish` Admin and WebApi into `front/publish` and `api/publish` respectively before `docker compose up`** — the compose file does not run the publish step. The `front`/`api` split refers to Admin(front)/WebApi(api) naming, not the uni-app frontend.

Spins up MySQL 5.7 (`MYSQL_ROOT_PASSWORD=admin`) and Redis on default ports.

## Conventions

- Comments and XML docs are in **Chinese**. Public API members in `Web.Admin`/`Web.WebApi` are expected to carry XML doc comments (CS1591 is enabled via `DocumentationFile=doc.xml` in Debug). Match the surrounding style.
- README states PRs should target the **`develop`** branch (default branch in the repo is currently `master`). Ask the user if branch policy is unclear for a given change.
- Commit messages are prefixed in Chinese with `【修复】`/`【优化】`/etc. Follow the existing log style when committing.
- The `docs/design/NN-*.md` files describe an in-progress financial refactor (balance transactions, idempotency, commission accounts). Consult them before modifying `CoreCmsUserBalanceServices`, commission state machine, or financial migration code.
- **Refactor status (verified 2026-08-01):** the balance `ChangeAsync` idempotent/transactional core plus the **distribution commission state machine** are both implemented and tested (`CoreCmsUserBalanceServicesTests` + `CommissionStateMachineFundsTests`, 14 passing, SQLite in-memory). `CommissionStatus` enum (Pending/Frozen/Available/Cancelled/ClawedBack/Exception) and `CoreCmsDistributionOrder.status` (+ frozenAmount/availableAmount/timestamps/idempotencyKey) are landed. `AddData`/`FinishOrder`/`CancleOrderByOrderId` rewritten: single-level commission + idempotency key + state-guard `WHERE status=...` + two-step ChangeAsync (commissionFrozen→commissionAvailable) + full clawback with commissionDebt for shortfalls. **L3 concurrency tests (`BalanceConcurrencyTests`/`CommissionConcurrencyTests`, 8 cases) and L4 end-to-end lifecycle tests (`EndToEndCommissionFlowTests`, 5 cases) are implemented and passing — total 27 tests green.** L3 verifies concurrent idempotency-key uniqueness, balance invariant under mixed deposit/pay, insufficient-balance contention, and state-guard Frozen↔Available↔ClawedBack race outcomes (终态+资金守恒). L4 orchestrates full freeze→settle→clawback / freeze→cancel / refund-after-withdrawal→debt / debt-offset-against-new-settlement / idempotent-replay flows by复刻 `SettleSingleCommission`/`CancelFrozenCommission`/`ClawbackAvailableCommission` 的资金序列（因真实 `CoreCmsDistributionOrderServices` 依赖 30+ 服务无法在 SQLite 内存库装配，故仅验证资金+状态侧；真实服务编排仍需 staging 手动 smoke）。**`CommissionSettlementJob`（`CoreCms.Net.Task`）已落地**：每小时 RecurringJob，定时扫 `status=Frozen && expectedSettleTime<=now` 调 `SettleDueCommissions`，含售后守卫；`FinishOrder` 增加保护期门控 `Distribution:CommissionProtectionPeriodDays`（默认 `"0"`=立刻结算旧行为不变，`>0` 则仅设 `expectedSettleTime` 留待 Job 结算）。`CoreCmsDistributionOrder` 实体已补 `expectedSettleTime` 字段。**Migrations in `docs/design/05-financial-data-migration.md` (003 / 004) must be applied to production DBs before enabling** — they add the new columns and backfill `status` from legacy `isSettlement`. The `Distribution:CommissionSettleEnabled` gate remains in place (default `"0"` = disabled) as a runtime kill-switch; flip to `"1"` only after applying the migrations and smoke-testing on staging. Group-buy concurrency (doc 06 §4.3) is **out of scope for the financial refactor** — it requires group-buy service infrastructure unrelated to balance/commission and is not covered by these tests.
- **Accepted-risk record — AutoMapper CVE-2026-32933:** the OSS AutoMapper 12.0.1 in use is listed as affected by CVE-2026-32933 (DoS via uncontrolled recursion on self-referential object graphs ≥25000 levels deep; patched only in commercial 15.1.1/16.1.1). CoreShop maps flat internal DTOs (categories, page-layout params) with no self-referential deep graphs, so practical exploit risk is judged low. Decision: keep OSS 12.0.1 to avoid reintroducing a third-party license-key kill-switch. Revisit only if user-supplied/external object graphs start being mapped through `AutoMapperConfiguration`.
- **MediatR 协议合规记录:** v13.0.0+ 由 LuckyPennySoftware 改为 RPL-1.5 + 商业双协议，RPL-1.5 §3.5 "External Deployment" 把 Web 对外服务计入分发，§3.4 强制传染整体作品改用 RPL-1.5，与 CoreShop Apache 2.0 不兼容。本仓库已下调到 12.4.1 (Apache-2.0) 消除该法律风险，业务 API `IRequest`/`IRequestHandler`/`IMediator.Send`/`AddMediatR` 在 12.x 全部兼容，零代码改动。自动化许可证扫描：2026-08-01 全量核查 63 个 nuget 依赖，除 MediatR 已处理外，MySql.Data 9.5.0 (GPL-2.0 + FOSS-exception-1.0) 在私有部署场景免费合规，若未来对外分发二进制建议迁移到 MySqlConnector (MIT)。其余 61 包均为 MIT/Apache-2.0/BSD-3-Clause/LGPL-3.0/Apache-OR-MS-PL 等合规协议，无商业付费要求。

## Gotchas

- Both `Web.Admin` and `Web.WebApi` `Program.cs` include a **Production-only** fast-fail guard: when `ASPNETCORE_ENVIRONMENT=Production`, startup throws `InvalidOperationException` listing any empty critical keys. Fill these via `appsettings.json` or environment variables before deploying; dev runs are unaffected.
  - **WebApi** 校验：`JwtConfig:SecretKey` ≥16 chars、`JwtConfig:Issuer`、`HangFire:Login/PassWord`、`SwaggerConfig:UserName/PassWord`（WebApi 启用了 `SwaggerBasicAuthMiddleware`，缺这段会挡住 Swagger UI 返回 401，缺密钥则 UI 不可用但不崩）。
  - **Web.Admin** 校验：`JwtConfig:SecretKey` ≥16 chars、`JwtConfig:Issuer`。Admin 不启用 SwaggerBasicAuth 中间件（`Program.cs` 硬编码 `RoutePrefix="doc"`），故不校验 `SwaggerConfig`。
- `Doc.xml`（`DocumentationFile=doc.xml` 在 Debug 生成的 XML 文档产物）已被 `.gitignore` 排除，勿提交。
- Redis must be reachable before either web host starts cleanly; otherwise startup throws during `AddRedisCacheSetup` / `AddRedisMessageQueueSetup` / Hangfire storage init. Tests are unaffected.
- `SqlSugar.IOC` and `Autofac.Extras.DynamicInterceptor` (AOP) are in `CoreCms.Net.Core` — some services may be intercepted (logging, caching attributes). Check for interceptor attributes before refactoring service constructors.
- `CoreCms.Net.CodeGenerator` is a runtime code-generation helper, not part of the app startup. Do not wire it into the web hosts unless intentionally regenerating scaffolds.
- Paths under `CoreCms.Net.Web.Admin/wwwroot/upload` are git-ignored (user uploads) — do not commit generated posters/QR codes placed there at runtime.