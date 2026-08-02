# 上线核查清单 (Pre-Deployment Checklist)

**仓库**: CoreShop
**生成日期**: 2026-08-01
**代码状态**: 已就绪（0 错误 / 27 测试全绿 / 许可证合规）

---

## 一、启动阻塞项（不解决无法上线）

### 1.1 生产配置密钥（必填，密钥不可入 git）

| 配置项 | 在哪里填 | 填什么示例 |
|---|---|---|
| `JwtConfig:SecretKey` | WebApi + Admin appsettings.Production.json | >=16 位随机英数字串，建议 32+ 位 |
| `JwtConfig:Issuer` | 同上 | 如 `CoreShop.Professional`，两个 host 必须相同 |
| `HangFire:Login` | 同上 | 自定义账户名 |
| `HangFire:PassWord` | 同上 | 自定义强密码 |
| `SwaggerConfig:UserName` | **仅 WebApi** | 自定义账户名 |
| `SwaggerConfig:PassWord` | **仅 WebApi** | 自定义强密码 |

**模板**：`scripts/appsettings.Production.WebApi.template.json` / `appsettings.Production.Admin.template.json`
**用法**：复制为 `appsettings.Production.json`，搜 `<FILL_...>` 占位符逐个替换

### 1.2 Redis / 数据库连接（必填）

| 配置项 | 必填值 |
|---|---|
| `RedisConfig:ConnectionString` | 生产 Redis 连接串（如 `redis-prod:6379,password=YourPwd,DefaultDatabase=10`）|
| `ConnectionStrings:SqlConnection` | 生产数据库连接串（SqlServer 必含 `MultipleActiveResultSets=true`） |

**注意**：两个 host 共用同一 Redis 实例可以，但建议 DefaultDatabase 用不同 number 防止键冲突

### 1.3 环境变量（必填）

```
ASPNETCORE_ENVIRONMENT=Production
```

不设置 → Production Guard 不触发 → 空 JWT 密钥裸跑 / SwaggerBasicAuth 不生效

### 1.4 NLog.config 连接串（必填，易遗漏）

**两个 host 的 `NLog.config` 第 17 行硬编码了 dev 数据库连接**：
```xml
connectionString="Server=127.0.0.1;uid=CoreShop;pwd=CoreShop;Database=CoreShop;..."
```
**部署前必须改成生产数据库连接串**，否则日志写入失败（`throwExceptions=false` 故不崩，但日志全丢，且启动时报连接异常）。

### 1.5 HTTPS + 域名（必填）

- 后端 WebApi 域名 `https://api.yourshop.com`（443 端口）
- 后端 Admin 域名 `https://admin.yourshop.com`
- 必须 HTTPS + 备案域名（微信小程序硬性要求）
- 不能用 IP、不能用 HTTP、不能带端口号

### 1.6 跨域 CORS 配置

`appsettings.Production.json` 的 `Cors:IPs` 必须填白名单域名（逗号分隔，不带斜杆）：
```
"IPs": "https://h5.yourshop.com,https://admin.yourshop.com"
```
否则前端无法访问后端 API。

---

## 二、运行预检（部署前最后一道关）

```powershell
# 在部署目录执行（必须 powershell 而非 cmd）
.\scripts\preflight-check.ps1
```

期望输出：`>>> 预检通过 <<<`

如果失败，按 FAIL 项逐条修复。脚本会校验：
- 6 个 WebApi + 2 个 Admin 必填密钥
- Redis 连通性（Tcp 探测）
- 数据库连通性（SqlServer open+close / MySql 端口探测）
- Distribution 门控状态报告

---

## 三、发布 + 部署

### 3.1 发布产物

```bash
# 在仓库根目录执行
dotnet publish CoreCms.Net.Web.Admin -c Release -o front/publish
dotnet publish CoreCms.Net.Web.WebApi -c Release -o api/publish
```

### 3.2 部署方式 A：Docker

```bash
docker compose up -d
```

**前置**：必须先完成「3.1 发布产物」，docker-compose.yaml 不会自动 publish。

**容器映射**：
| 容器名 | 端口 | 角色 |
|---|---|---|
| front-backend | 8088:80 | Admin 后台 |
| web-api | 8089:80 | WebApi |
| redis | 6379 | Redis |
| mysql | 3306 | MySQL 5.7 |

**注意**：docker-compose.yaml 默认用 MySQL。若用 SqlServer，修改 compose 文件移除 mysql 服务并改 appsettings。

### 3.3 部署方式 B：IIS / systemd

- IIS：发布产物复制到 `C:\inetpub\CoreShop.Api` 等目录，建站点绑 HTTPS 域名
- Linux：`dotnet CoreCms.Net.Web.WebApi.dll --urls http://0.0.0.0:5000` + Nginx 反向代理 443

### 3.4 Nginx 反向代理示例

```nginx
server {
    listen 443 ssl http2;
    server_name api.yourshop.com;

    ssl_certificate     /etc/ssl/api.yourshop.com.crt;
    ssl_certificate_key  /etc/ssl/api.yourshop.com.key;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        # Hangfire 长连接需 WebSocket 透传（Hangfire 默认轮询可不开）
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

**关键**：`UseForwardedHeaders` 已在 Program.cs:160 启用，必须让反向代理传递 `X-Forwarded-Proto`，否则 ASP.NET Core 会以为是非 HTTPS 而重定向失败。

---

## 四、启动验证

### 4.1 后端启动

```bash
curl https://api.yourshop.com/        # 期望：401 或 404（非 502/500 即 OK）
curl https://admin.yourshop.com/       # 期望：Admin 登录页（302/200）
```

### 4.2 Swagger 可访问

```
浏览器访问 https://api.yourshop.com/doc
→ BasicAuth 弹窗 → 输入 SwaggerConfig 账密 → 进得去 → Swagger UI 正常加载
```

### 4.3 Hangfire Dashboard

```
浏览器访问 https://api.yourshop.com/job
→ BasicAuth 弹窗 → 输入 HangFire 账密
→ 看到 8 个 RecurringJob：
  AutoCancelOrderJob / CompleteOrderJob / EvaluateOrderJob / AutoSignOrderJob
  RemindOrderPayJob / AutoCanclePinTuanJob / RemoveOperationLogJob
  RefreshWeChatAccessTokenJob
  CommissionSettlementJob   ← 本次新增
```

### 4.4 数据库连接 + Redis 连接

观察 startup log，无 `Redis connection failed` / `SqlSugar open fail` / `NLog database connection failed` 报错。

---

## 五、小程序上线（前端）

详见 `scripts/frontend-deploy-guide.md`，关键 4 项：

- [ ] `common/setting/constVarsHelper.js` → `apiBaseUrl` 改成生产 API 域名
- [ ] `common/setting/constVarsHelper.js` → `apiFilesUrl` 改成生产静态资源域名
- [ ] `manifest.json` → `mp-weixin.appid` 改成你的小程序 AppId
- [ ] 后端 `appsettings.Production.json` → `WeChatOptions:WxOpenAppId` 与前端 `mp-weixin.appid` 完全一致

### 5.1 微信小程序后台必做

登录 [mp.weixin.qq.com](https://mp.weixin.qq.com) → 开发 → 开发管理 → 服务器域名：

- [ ] **request 合法域名**：填后端 API 域名（必须 HTTPS）
- [ ] **uploadFile 合法域名**：填上传接口域名（通常同 API）
- [ ] **downloadFile 合法域名**：填图片下载域名（API 同域 / CDN / OSS）

### 5.2 编译发行

```
HBuilderX → 打开目录 CoreCms.Net.Uni-App/CoreShop/
菜单：发行 → 小程序-微信
取消勾选「发行到微信平台」→ 发行
```

输出在 `CoreShop/unpackage/dist/build/mp-weixin/`，用「微信开发者工具」打开此目录 → 上传 → 提交审核。

---

## 六、业务冒烟（启服务后）

### 6.1 不涉及微信/支付的最小可用路径
- [ ] 小程序打开 → 不闪退
- [ ] 用户走手机号验证码登录 → 拿 token
- [ ] 浏览首页 → 商品列表加载
- [ ] 加入购物车 → 下单 → 库存扣减

### 6.2 涉及微信/支付（若启用）
- [ ] 配置 `WeChatOptions:WxOpenAppId/Secret` → 微信小程序登录
- [ ] 配置 `WeChatPay` 全套 → 微信支付
- [ ] 配置 `WeChatPay:APIKey, MchId, APIv3Key`
- [ ] 上传 `WxPayCert/apiclient_cert.p12` 证书文件到发布目录

### 6.3 分销佣金（**默认禁用**，启门控时才做）
- [ ] 生产 DB 执行迁移 003 → 004 → 005（`docs/design/05-financial-data-migration.md`）
- [ ] 运行 `scripts/migration-verify.sql` 后验段全部 0 行异常
- [ ] staging 冒烟完整链：下单 → 支付 → 收货 → 解冻 → 退款追回
- [ ] 翻 `Distribution:CommissionSettleEnabled = "1"`
- [ ] Hangfire Dashboard 确认 `CommissionSettlementJob` 按 cron `0 0 0/1 * * ?` 每小时跑

---

## 七、安全 / 合规

### 7.1 许可证合规（已验证）

- ✅ MediatR 12.4.1 Apache-2.0（已降级消除 RPL-1.5 法律风险）
- ✅ AutoMapper 12.0.1 MIT（CVE 接受风险记录在 AGENTS.md）
- ✅ 其余 61 包：MIT/Apache-2.0/BSD-3-Clause/LGPL-3.0/Apache-OR-MS-PL 合规
- ⚠️ MySql.Data 9.5.0 GPL-2.0 + FOSS-exception-1.0 私有部署免费，对外分发二进制则建议迁移 MySqlConnector (MIT)

### 7.2 已知 CVE 接受风险

- **AutoMapper 12.0.1** CVE-2026-32933（DoS via 自引用深图 ≥25000 层）→ CoreShop 只映射平铺 DTO，风险低，接受保持 v12.0.1 避免引入商业 license kill-switch
- **SQLitePCLRaw 2.1.11** / **System.Security.Cryptography.Xml 8.0.3**：传递依赖，运行时无影响，建议跟踪上游补丁（勿全局禁 NU1903 警告）

### 7.3 密钥不泄露

- [ ] `appsettings.Production.json` 已加入 `.gitignore`（**确认本地不提交**）
- [ ] 所有密钥使用强随机生成（不出现 `123456` `admin` `core` 等弱口令）
- [ ] 数据库 `pwd=` 不用 demo 默认值 `CoreShop`

---

## 八、上线后运维

### 8.1 日志监控

- NLog 同时写文件 + 数据库
  - 文件：`{发布目录}/App_Data/nlog/{yyyy-MM}/{level}-{shortdate}.csv`
  - 数据库：`SysNLogRecords` 表
- 异常告警：监控 `Error` 级日志，特别是 `LogType=Other` + `LogTitle=佣金结算` / `佣金取消` / `佣金追回` / `资金变更` 几类
- Hangfire Dashboard 每天检查 Job 失败率

### 8.2 数据库对账（启用佣金功能后）

定期运行 `scripts/migration-verify.sql` 的「运行时对账」段（建议每周一次）：
- User 佣金账户守恒
- DistributionOrder 金额守恒
- User.balance ↔ 最新流水一致
- 幂等键唯一性自检

### 8.3 备份

- 每日备份 MySQL / SqlServer 数据库
- 每周备份 Redis 持久化文件（RDB / AOF）

---

## 九、上线就绪状态总览

| 项目 | 状态 |
|---|---|
| 代码构建 | ✅ 0 错误 |
| 单元/集成/并发/E2E 测试 | ✅ 27/27 全绿 |
| Dockerfile 镜像版本 | ✅ aspnet:10.0 |
| 许可证合规 | ✅ MediatR 已降级 Apache 2.0 |
| 佣金定时结算 Job | ✅ 已落地 |
| 预检脚本 | ✅ scripts/preflight-check.ps1 |
| 迁移校验脚本 | ✅ scripts/migration-verify.sql |
| 生产配置模板 | ✅ scripts/appsettings.Production.*.template.json |
| 前端部署指引 | ✅ scripts/frontend-deploy-guide.md |
| **本清单** | ✅ scripts/pre-deployment-checklist.md |
| **生产配置密钥填写** | ⛔ 阻塞（运维/开发者手工） |
| **NLog.config 连接串** | ⛔ 阻塞（运维/开发者手工） |
| **HTTPS 备案域名** | ⛔ 阻塞（运维/运维服务商） |
| **微信小程序后台白名单** | ⛔ 阻塞（开发者/mp.weixin.qq.com） |
| **前端 apiBaseUrl + AppId** | ⛔ 阻塞（开发者/HBuilderX） |
| **数据库迁移 003/004/005** | 🟡 默认禁用佣金无影响；启门控前必做 |
| **staging 真实服务冒烟** | 🟡 启用佣金功能前必做 |

---

## 十、文件清单

本仓库为上线准备提供的脚本/文档：

| 文件 | 用途 |
|---|---|
| `scripts/preflight-check.ps1` | 启动前配置预检（PowerShell） |
| `scripts/migration-verify.sql` | DB 迁移前后验证 + 运行时对账 |
| `scripts/appsettings.Production.WebApi.template.json` | WebApi 生产配置模板 |
| `scripts/appsettings.Production.Admin.template.json` | Admin 生产配置模板 |
| `scripts/frontend-deploy-guide.md` | 前端小程序部署指引 |
| `scripts/pre-deployment-checklist.md` | 本清单 |
| `docs/design/02-financial-idempotency-design.md` | 余额幂等设计 |
| `docs/design/04-commission-state-machine.md` | 佣金状态机设计（含 Hangfire Job §7） |
| `docs/design/05-financial-data-migration.md` | 001-006 迁移脚本 |
| `docs/design/06-financial-test-plan.md` | L1-L4 测试计划 |