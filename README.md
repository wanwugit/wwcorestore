# CoreShop（.NET 10 分支）

基于 ASP.NET Core 10 + Uni-App 的可视化布局小程序商城系统。前后端分离，支持微信小程序 / H5 / Android / iOS / 支付宝小程序等多端编译。本仓库为二次开发分支，基于上游 CoreUnion/CoreShop 改造。

## 快速上手

### 环境要求

- .NET 10 SDK（10.0.301 测试通过）
- Visual Studio 2022（17.14+）或纯 CLI
- HBuilderX（前端构建，非 dotnet）
- Redis 5.0+（运行时强制依赖，测试不需要）
- SQL Server 2012R2+ 或 MySQL 5.7+（二选一）
- 微信开发者工具（前端调试）

### 构建与测试

```bash
# 构建整个解决方案
dotnet build CoreShopCommunity.slnx -c Debug

# 构建单个项目
dotnet build CoreCms.Net.Web.Admin/CoreCms.Net.Web.Admin.csproj

# 运行全部测试（xUnit，SQLite 内存库，无需外部数据库/Redis）
dotnet test CoreCms.Net.Tests/CoreCms.Net.Tests.csproj

# 运行单个测试
dotnet test CoreCms.Net.Tests/CoreCms.Net.Tests.csproj --filter "FullyQualifiedName~ChangeAsync_Recharge_IncreasesBalance"
```

> 解决方案文件为 `CoreShopCommunity.slnx`（新 XML 格式，非 `.sln`）。22 个项目全部目标 `net10.0`。构建产 0 错误，警告均为已知的 NuGet 漏洞告警（`NU1903`）与缺 XML 注释（`CS1591`），非故障。

### Docker 部署

`docker-compose.yaml` 启动 MySQL 5.7 + Redis + 两个 Web 镜像。**必须先手动 publish 两个 Web 项目再 compose up**：

```bash
dotnet publish CoreCms.Net.Web.Admin/CoreCms.Net.Web.Admin.csproj -c Release -o ./front/publish
dotnet publish CoreCms.Net.Web.WebApi/CoreCms.Net.Web.WebApi.csproj -c Release -o ./api/publish
docker compose up -d
```

`front`/`api` 指后台/Api 两个 Web 宿主，与 uni-app 前端无关。详见本项目 `AGENTS.md`。

## 架构概览

| 解决方案文件夹 | 项目 | 职责 |
|---|---|---|
| `/1.Core/` | Auth, Caching, CodeGenerator, Configuration, Core, Filter, Loging, Mapping, Middlewares, RedisMQ, Swagger, Task, Utility | 基础设施库 |
| `/2.Entity/` | Model | 实体、DTO、视图模型 |
| `/3.Services/` | IServices, Services | 业务逻辑 |
| `/4.Repository/` | IRepository, Repository | 数据访问 |
| `/5.WeChat/` | WeChat.Service | 微信公众号/小程序 SDK + MediatR |
| `/9.App/` | Web.Admin, Web.WebApi, Uni-App | 入口 |
| `/Tests/` | Tests | xUnit |

两个独立 Web 宿主（均为 `Microsoft.NET.Sdk.Web`，`net10.0`）：
- **Web.Admin** — 管理后台，承载 `wwwroot/` 下的 LayUIAdmin 静态 UI。
- **Web.WebApi** — 面向前端 uni-app 的 API，Hangfire 仪表板挂在 `/job`。

非默认技术栈（与 EF + MS DI 不同）：
- **Autofac** 10.0.0 作 DI 容器，批量注册在 `CoreCms.Net.Core/AutoFac/`。
- **SqlSugar** 5.1.4.207 作 ORM（非 EF Core），仓储经 `IUnitOfWork.GetDbClient()` 取 `SqlSugarScope`。
- **AutoMapper** 用 OSS 12.0.1（不是商业版），详见下方"风险记录"。
- **Hangfire** + Redis 存储做后台任务（仅 WebApi）。

## 上线前必填配置

`appsettings.json`（两个 host 都有）。**Production 环境下，下列密钥为空会启动失败并抛 `InvalidOperationException` 列出缺失项**：

| 键 | 要求 |
|---|---|
| `JwtConfig:SecretKey` | ≥ 16 字符 |
| `JwtConfig:Issuer` | 非空 |
| `HangFire:Login` / `HangFire:PassWord` | 非空（仅 WebApi 校验） |
| `SwaggerConfig:UserName` / `SwaggerConfig:PassWord` | 非空 |
| `RedisConfig:ConnectionString` | 可达的 Redis 实例 |

数据库切换：`ConnectionStrings:DbType` ∈ {`SqlServer`, `MySql`}
- SqlServer 连接串必须包含 `MultipleActiveResultSets=true`
- MySql 须 5.7+，保留注释中的 charset/zerodate 标志位

数据库初始化：用 `数据库/` 目录下的 dump 还原
- `数据库/SqlServer/*.bak` — SSMS 还原
- `数据库/MySql/*.sql` — Navicat / SQLyog 导入

## 佣金结算（状态机已落地，待迁移后启用）

分销佣金状态机已实现并测试（`CoreCmsDistributionOrderServices` 三入口 + 14 个 SQLite 内存用例），但在 DB 迁移（`docs/design/05` 003/004）应用到生产前，以配置门禁默认禁用，避免新字段未就位即触发：

- 配置键：`Distribution:CommissionSettleEnabled`（默认 `"0"` = 禁用）
- 生效位置：`CoreCmsDistributionOrderServices.AddData` / `FinishOrder` / `CancleOrderByOrderId` 顶部早退
- 代理佣金 `CoreCmsAgentOrderServices` 不受门禁影响
- 状态机：`Pending → Frozen → Available → Cancelled | ClawedBack`（详见 `docs/design/04-commission-state-machine.md`）
- 启用步骤：①对生产库执行 doc 05 的 003/004 迁移；②staging 烟测；③将 `CommissionSettleEnabled` 改为 `"1"`。

## 已接受风险记录

**AutoMapper CVE-2026-32933**（DoS，深嵌套自引用对象图导致栈溢出）：已修复版本仅商业版 15.1.1/16.1.1。本仓库为规避第三方 license-key kill-switch，刻意使用 OSS AutoMapper 12.0.1 并接受该 CVE 理论风险—— mappings 仅限扁平内部 DTO（分类、页面布局参数），无自引用深嵌套对象图，实际可利用性极低。详见 `AGENTS.md`。

## 开发约定

- 注释与 XML 文档为**中文**。`Web.Admin`/`Web.WebApi` 公共成员应带 XML 注释（`CS1591` 已通过 `DocumentationFile=doc.xml` 启用）。
- 提交信息以中文前缀开头：`【修复】` / `【优化】` / `【新增】` / `【升级】`。
- 仓库 `AGENTS.md` 汇总了 AI 协作会话所需的高信号事实，修改前请先读。

## 前端（Uni-App）

`CoreCms.Net.Uni-App/CoreShop/` 下为实际 uni-app 源码（App.vue, pages.json, manifest.json, uView UI）。项目 csproj 仅占位以纳入解决方案树，**用 HBuilderX 构建，不要 `dotnet build` 期待真实输出**。`manifest.json` 中的微信小程序 AppId 必须与 WebApi `appsettings.json` 的 `WeChatOptions:WxOpenAppId` 一致。