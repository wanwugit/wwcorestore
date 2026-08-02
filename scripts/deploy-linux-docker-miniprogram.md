# CoreShop Linux + Docker + MySQL + 微信小程序 部署指南

**场景**：云服务器 Linux + 新起 MySQL 5.7 容器 + Redis 容器 + 微信小程序前端。
**不适用**：Windows Server / 已有外部数据库 / H5 端首次部署（请另行参考 `pre-deployment-checklist.md`）。

---

## 部署时间线概览

```
┌─ Phase 0: 服务器准备 ──────────────── Day 1
├─ Phase 1: 本地编译发布产物 ────────── Day 2     (可以与 Phase 0 并行)
├─ Phase 2: 域名 + ICP 备案 ──────────── Day 1-20  (并行进行，国内云服务商必须)
├─ Phase 3: 上传 + 启动整套 ─────────── Day 21
├─ Phase 4: HTTPS 证书 + 反向代理 ──── Day 21-22
├─ Phase 5: 后端联调验证 ────────────── Day 22
└─ Phase 6: 小程序前端上线 ─────────── Day 23
```

⚠️ **关键约束**：微信小程序后台「request 合法域名」必须 HTTPS + 备案域名，所以 Phase 2 备案必须先完成，前端才能上线。期间可以用「微信开发者工具勾选不校验合法域名」做联调。

---

## Phase 0: 服务器准备（Day 1，1 小时）

### 0.1 推荐配置

| 项 | 配置 |
|---|---|
| CPU | 2 核起步（4 核推荐，对标阿里云 ecs.g6.large） |
| 内存 | 4 GB 起步（8 GB 推荐，MySQL+Redis+两个 dotnet 容器） |
| 磁盘 | 40 GB SSD 起（MySQL 数据 + 日志） |
| 带宽 | 5 Mbps 起（支付回调响应慢会失败） |
| OS | Ubuntu 22.04 LTS / CentOS 7+ / Debian 12 |

### 0.2 安装 Docker + Docker Compose

SSH 登录服务器后执行：

```bash
# Ubuntu/Debian
curl -fsSL https://get.docker.com | sudo bash
sudo systemctl enable --now docker
sudo apt install -y docker-compose-plugin
# 或安装旧版 docker-compose：
# sudo apt install -y docker-compose

# CentOS
# curl -fsSL https://get.docker.com | sudo bash
# sudo systemctl enable --now docker
# sudo yum install -y docker-compose

# 验证
docker --version
docker compose version  # 或 docker-compose --version
```

### 0.3 防火墙端口开放

```bash
# 阿里云/腾讯云控制台「安全组」放行这些端口
# SSH（22）已开
# HTTP/HTTPS（80/443）—— 域名解析后用
# 自定义调试端口（8088/8089）—— 备案前用 IP 调试

# Ubuntu 系统防火墙
sudo ufw allow 22
sudo ufw allow 80
sudo ufw allow 443
sudo ufw allow 8088
sudo ufw allow 8089
sudo ufw allow 3306  # 生产强烈建议只放内网，不暴露公网
sudo ufw allow 6379  # 同上，生产严禁公网
sudo ufw --force enable
```

⚠️ **重要**：`3306`（MySQL）和 `6379`（Redis）**强烈建议只允许内网访问，不暴露到公网**。下面 compose 文件后面会改成只 expose 不 ports。

### 0.4 准备目录

```bash
sudo mkdir -p /opt/coreshop/{api,front,mysql-data,mysql-conf,redis-data}
sudo chown -R $USER:$USER /opt/coreshop
```

---

## Phase 1: 本地编译发布产物（Day 2，可并行）

在你的 **Windows 开发机**仓库根目录执行：

```powershell
# 进仓库根目录
cd D:\Proj\CoreShop

# 1. 还原依赖（确保已 dotnet restore）
dotnet restore CoreShopCommunity.slnx

# 2. 发布 WebApi 到 api/publish
dotnet publish CoreCms.Net.Web.WebApi/CoreCms.Net.Web.WebApi.csproj `
    -c Release -o api/publish

# 3. 发布 Admin 到 front/publish
dotnet publish CoreCms.Net.Web.Admin/CoreCms.Net.Web.Admin.csproj `
    -c Release -o front/publish
```

**检查产物**：
- `api/publish/CoreCms.Net.Web.WebApi.dll` 存在
- `api/publish/appsettings.json` + `appsettings.Development.json` + `NLog.config` 存在
- `front/publish/CoreCms.Net.Web.Admin.dll` + `wwwroot/`（前端静态资源）存在
- `api/publish/Dockerfile` + `front/publish/Dockerfile`（pyi[0]自动复制）

### 1.1 准备生产配置

```powershell
# 复制模板为生产配置（注意 appsettings.Production.json 已被 .gitignore 排除）
Copy-Item .\scripts\appsettings.Production.WebApi.template.json `
          .\CoreCms.Net.Web.WebApi\appsettings.Production.json

Copy-Item .\scripts\appsettings.Production.Admin.template.json `
          .\CoreCms.Net.Web.Admin\appsettings.Production.json

# 用文本编辑器打开填值（逐个搜 <FILL_...> 替换）
notepad .\CoreCms.Net.Web.WebApi\appsettings.Production.json
notepad .\CoreCms.Net.Web.Admin\appsettings.Production.json
```

### 1.2 ⚠️ 修改 NLog.config（易遗漏）

两个 host 的 `NLog.config` 第 17 行**硬编码了 dev 数据库**，必须改：

```xml
<!-- 改前：-->
<target name="log_database" xsi:type="Database" 
    dbProvider="MySql.Data.MySqlClient.MySqlConnection, MySql.Data"
    connectionString="Server=127.0.0.1;uid=CoreShop;pwd=CoreShop;Database=CoreShop;...">

<!-- 改后（用你的生产 MySQL 容器连接信息）：-->
<target name="log_database" xsi:type="Database" 
    dbProvider="MySql.Data.MySqlClient.MySqlConnection, MySql.Data"
    connectionString="Server=mysql;Port=3306;Database=CoreShop;Uid=root;Pwd=你的MySQLRoot密码;CharSet=utf8;SslMode=None;Allow User Variables=true;">
```

注意：
- `Server=mysql` 用 docker-compose 服务名（不是 IP），docker 内部 DNS 自动解析
- dbProvider 切到 `MySql.Data.MySqlClient.MySqlConnection, MySql.Data`（不是默认的 SqlServer）

### 1.3 重新发布确认配置进产物

⚠️ 重要：**改完 appsettings.Production.json + NLog.config 后要重新 `dotnet publish`**，确保发布目录包含新配置：

```powershell
# 删除旧的发布目录（避免残留）
Remove-Item -Recurse -Force api\publish, front\publish

# 重新发布
dotnet publish CoreCms.Net.Web.WebApi/CoreCms.Net.Web.WebApi.csproj -c Release -o api/publish
dotnet publish CoreCms.Net.Web.Admin/CoreCms.Net.Web.Admin.csproj -c Release -o front/publish
```

验证 `api/publish/appsettings.Production.json` 和 `api/publish/NLog.config` 都已更新。

### 1.4 打包上传到服务器

```powershell
# Windows 开发机
Compress-Archive -Path api\publish -DestinationPath api.publish.zip
Compress-Archive -Path front\publish -DestinationPath front.publish.zip

# 同时打包 docker-compose.yaml 与 数据库脚本
Compress-Archive -Path docker-compose.yaml -DestinationPath docker-compose.zip -Force

# 用 scp 上传到服务器（假设 IP = your.server.ip）
scp api.publish.zip front.publish.zip docker-compose.yaml youruser@your.server.ip:/opt/coreshop/
```

或者用 WinSCP / 宝塔面板手动上传。

---

## Phase 2: 域名 + ICP 备案（Day 1-20，并行进行）

国内云服务器必须备案，否则微信小程序后台不会接受你的域名。推荐：

### 2.1 选域名商

阿里云/腾讯云万网域名注册，价格：
- .com 60-80 元/年
- .cn 30 元/年

### 2.2 备案

登录阿里云/腾讯云控制台 → 「ICP 备案」→ 提交资料：
- 法人/个人身份证
- 服务器备案号（云服务商有）
- 网站信息（写入你的项目名 / 简介）
- 一般 7-20 个工作日

### 2.3 域名解析

备案通过后，在域名商控制台加 A 记录：
```
api    → 服务器公网 IP
admin  → 服务器公网 IP
```

得到 `https://api.yourshop.com` 和 `https://admin.yourshop.com`。

### 2.4 申请 SSL 证书

- 阿里云：SSL 证书 → 免费证书 → 配置为 `api.yourshop.com`, `admin.yourshop.com`
- Let's Encrypt：用 certbot 自动签发（需要 80 端口可达）

---

## Phase 3: 服务器上启动整套（Day 21，1-2 小时）

### 3.1 SSH 登录服务器

```bash
ssh youruser@your.server.ip
cd /opt/coreshop
```

### 3.2 解压发布包

```bash
unzip api.publish.zip -d api/
unzip front.publish.zip -d front/

# 验证结构
ls api/publish/CoreCms.Net.Web.WebApi.dll  # 应存在
ls front/publish/CoreCms.Net.Web.Admin.dll  # 应存在
ls docker-compose.yaml  # 在当前目录
```

⚠️ 注意 把 `api/publish/` 内的内容放到 `/opt/coreshop/api/publish/` 路径下：
```bash
# Compress-Archive 打的包解压后是 api/publish/
# 移到 docker-compose.yaml 期望的 ./api/publish 路径
mkdir -p api/publish front/publish
# 解压时注意层级
```

### 3.3 修改 docker-compose.yaml

注意：仓库自带的 docker-compose.yaml **不适合生产**（MySQL/Redis 暴露公网、密码是 admin、没有 volume 隔离）。改成下面这样：

```yaml
# /opt/coreshop/docker-compose.yaml
version: '3'
services:
  frontservice:
    container_name: front-backend
    build:
      context: ./front/publish
      dockerfile: Dockerfile
    ports:
      - "8088:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    depends_on:
      - redis
      - mysql
    restart: always

  webapiservice:
    container_name: web-api
    build:
      context: ./api/publish
      dockerfile: Dockerfile
    ports:
      - "8089:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    depends_on:
      - redis
      - mysql
    restart: always

  redis:
    image: redis:7-alpine
    container_name: coreshop-redis
    restart: always
    expose:
      - "6379"                    # 只在 docker 内部网络暴露，不暴露公网
    command: redis-server --requirepass 你的Redis强密码
    volumes:
      - ./redis-data:/data

  mysql:
    image: mysql:5.7.19
    container_name: coreshop-mysql
    restart: always
    expose:
      - "3306"                    # 只在 docker 内部网络暴露
    volumes:
      - ./mysql-data:/var/lib/mysql
      - ./mysql-conf:/etc/mysql/conf.d
      - ./数据库/MySql:/docker-entrypoint-initdb.d  # 把初始化 SQL 挂进去
    command: mysqld --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci --lower_case_table_names=2
    environment:
      - MYSQL_ROOT_PASSWORD=你的MySQL强密码
      - MYSQL_DATABASE=CoreShop
``` 

**关键修改**：
1. Redis 加 `--requirepass 你的Redis强密码`，appsettings 连接串对应加 `password=你的Redis强密码`
2. MySQL Root 密码改成强密码
3. Redis/MySQL 用 `expose` 不用 `ports`，保持内网
4. 加 `mysql-conf`、`redis-data`、`mysql-data` 卷做持久化
5. 加 `MYSQL_DATABASE=CoreShop` 自动创建库
6. 把 `数据库/MySql/*.sql` 挂到 `/docker-entrypoint-initdb.d` 首次启动自动导入

### 3.4 上传数据库初始化脚本

```bash
# 在 Windows 开发机 scp 上传
scp -r "数据库" youruser@your.server.ip:/opt/coreshop/数据库
```

— 或者直接复制其中一个 SQL 文件做初始化（更简单）：

```bash
# 服务器上
mkdir -p /opt/coreshop/sql-init
# Windows 端选一个 Navicat 版的 SQL（更兼容）上传：
# 数据库\MySql\20211025\coreshopmysql20211025带演示数据（Navicat导出）.sql
# 重命名为 /opt/coreshop/sql-init/init.sql
```

挂到 compose 的 `./数据库/MySql:/docker-entrypoint-initdb.d`（脚本自动执行首启）。

### 3.5 启动整套

```bash
cd /opt/coreshop
docker compose up -d                   # 或 docker-compose up -d (旧版)
docker compose ps                      # 看运行状态
```

期望输出：
```
NAME                  STATUS   PORTS
coreshop-mysql        Up       3306/tcp
coreshop-redis         Up       6379/tcp
front-backend         Up       0.0.0.0:8088->80/tcp
web-api               Up       0.0.0.0:8089->80/tcp
```

### 3.6 查看启动日志

```bash
docker compose logs -f webapiservice
```

首次启动会看到：
- 「Now listening on: http://[::]:80」
- 「Application started」
- Hangfire Dashboard 启动

按 Ctrl+C 退出实时查看。

### 3.7 用 IP + 端口做备案前联调

```bash
# 服务器内测试
curl http://localhost:8089/
# 期望：401/404，不是 502/500

# 外网测试（防火墙已放行 8089）
curl http://your.server.ip:8089/
```

---

## Phase 4: HTTPS 证书 + Nginx 反向代理（备案通过后做）

备案通过、域名解析生效后，给两个 web 容器加层 Nginx 做 HTTPS。

### 4.1 安装 Nginx

```bash
sudo apt install -y nginx
```

### 4.2 申请 SSL 证书（Let's Encrypt 路径）

```bash
sudo apt install -y certbot python3-certbot-nginx

# 先把 Nginx 配 80 走响应 Let's Verify
sudo certbot --nginx -d api.yourshop.com -d admin.yourshop.com
```

证书自动续期：
```bash
sudo crontab -e
# 加入：
0 3 * * * certbot renew --quiet
```

### 4.3 配置 Nginx 反向代理

`/etc/nginx/sites-available/coreshop.conf`:

```nginx
# API 后端
server {
    listen 443 ssl http2;
    server_name api.yourshop.com;

    ssl_certificate     /etc/letsencrypt/live/api.yourshop.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/api.yourshop.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8089;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Hangfire / SignalR 长连接需要
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        client_max_body_size 20m;     # 文件上传
    }
}

# Admin 后台
server {
    listen 443 ssl http2;
    server_name admin.yourshop.com;

    ssl_certificate     /etc/letsencrypt/live/admin.yourshop.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/admin.yourshop.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8088;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        client_max_body_size 20m;
    }
}

# HTTP 强制跳转 HTTPS
server {
    listen 80;
    server_name api.yourshop.com admin.yourshop.com;
    return 301 https://$host$request_uri;
}
```

启用：
```bash
sudo ln -s /etc/nginx/sites-available/coreshop.conf /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

### 4.4 改 appsettings.Production.json 用正式域名

SSH 上服务器：

```bash
cd /opt/coreshop
nano api/publish/appsettings.Production.json
# 把 AppInterFaceUrl 改为 https://api.yourshop.com/
# 把 AppUrl 改为 https://admin.yourshop.com/
# RedisConfig:ConnectionString 改为 api.yourshop.com 同 redis 服务名密码

nano front/publish/appsettings.Production.json
# 同上，两个 host 必须用同一套 JwtConfig:SecretKey + Issuer
```

重启容器使配置生效：

```bash
docker compose restart
```

### 4.5 验证 HTTPS

```bash
curl -I https://api.yourshop.com/
# 期望：HTTP/2 401 或 404（已经 HTTPS 走通）
```

浏览器访问：
- `https://api.yourshop.com/doc` → SwaggerBasicAuth 弹窗
- `https://api.yourshop.com/job` → Hangfire Dashboard 弹窗
- `https://admin.yourshop.com/` → Admin 后台登录页

---

## Phase 5: 后端冒烟测试（Phase 4 通过后做）

按 `scripts/pre-deployment-checklist.md` 第六章逐项勾选：

- [ ] 浏览器访问 `https://api.yourshop.com/doc` 能进 Swagger UI
- [ ] 浏览器访问 `https://api.yourshop.com/job` 能进 Hangfire Dashboard，看到 8 个 RecurringJob（含 `CommissionSettlementJob`）
- [ ] 浏览器访问 `https://admin.yourshop.com/` 能进 Admin 登录页
- [ ] 用 Postman 调一个不需要 token 的公共接口（如 `/api/Home/GetNav`），返回 200
- [ ] 调一个需要 token 的接口（如 `/api/User/GetUserInfo`），返回 401

至此**后端上线完成**。

---

## Phase 6: 微信小程序前端上线

### 6.1 修改前端配置（Dev 机执行）

在仓库 `CoreCms.Net.Uni-App/CoreShop/` 下改三个文件：

**`common/setting/constVarsHelper.js`**：
```js
export const apiBaseUrl = 'https://api.yourshop.com';     // 后端 API 域名
export const apiFilesUrl = 'https://api.yourshop.com';   // 静态资源同 API 域名（也可用 CDN）
```

**`manifest.json`**：找到 `"mp-weixin"` 节，改 `"appid"` 为你的真实 AppId：
```json
"mp-weixin" : {
    "appid" : "你的小程序AppId",
    ...
}
```

### 6.2 微信小程序后台配域名白名单

登录 [mp.weixin.qq.com](https://mp.weixin.qq.com) → 开发 → 开发管理 → 服务器域名：

| 类型 | 填入 |
|---|---|
| request 合法域名 | `https://api.yourshop.com` |
| uploadFile 合法域名 | `https://api.yourshop.com` |
| downloadFile 合法域名 | `https://api.yourshop.com` |

提示：每月只能改 50 次，确认无误再提交。

### 6.3 HBuilderX 编译发行包

1. HBuilderX 打开 `CoreCms.Net.Uni-App/CoreShop/` 目录（注意是 CoreShop 子目录，不是根）
2. 菜单：发行 → 小程序-微信
3. 弹窗：小程序名填你的，AppID 选 manifest 已配的
4. **取消勾选「发行到微信平台」**（手动传更安全）
5. 点击「发行」
6. 编译完成后输出在 `CoreShop/unpackage/dist/build/mp-weixin/`

### 6.4 微信开发者工具上传

1. 下载安装「微信开发者工具」
2. 导入项目 → 目录选 `CoreShop/unpackage/dist/build/mp-weixin/`，填入你的 AppId
3. **预览/真机调试**：先用「预览」生成二维码扫一扫，验证能否打开小程序、能否调通后端接口
4. **上传发行**：右上角点「上传」→ 填版本号（如 `1.0.0`）和备注 → 上传
5. 登录 mp.weixin.qq.com → 管理 → 版本管理 → 看到你刚才上传的版本 → 点击「提交审核」
6. 等审核通过（1-3 天）→ 发布为线上版本

---

## 备案前的临时联调方案（如果等待备案期间想先测）

微信开发者工具在「详情」面板里勾选：
- ☑ 不校验合法域名、web-view（业务域名）、TLS 版本以及 HTTPS 证书

这样可以用非 HTTPS + IP + 端口联调。**但发布正式小程序时不能勾选**，仅本地调试用。

后端开发机也可以用 ngrok / cpolar 内网穿透工具做联调：
```bash
# 本机后端 dotnet run 起来后
ngrok http 5000
# 拿到 https://xxx.ngrok.io 临时域名
# 配到 constVarsHelper.apiBaseUrl
# 微信开发者工具勾选「不校验合法域名」即可调通
```

---

## 维护命令速查

```bash
# 查看容器状态
docker compose ps

# 看实时日志
docker compose logs -f webapiservice

# 重启某个服务
docker compose restart webapiservice

# 更新发布包（重新发布后）
# Windows 端 → scp 上传新 zip 到 /opt/coreshop/
cd /opt/coreshop
unzip -o api.publish.zip -d api/
docker compose up -d --build webapiservice

# 进入容器
docker exec -it web-api bash

# MySQL 备份
docker exec coreshop-mysql mysqldump -uroot -p你的密码 CoreShop > backup_$(date +%Y%m%d).sql

# Redis 备份（RDB）
docker exec coreshop-redis redis-cli -a 你的密码 BGSAVE
docker cp coreshop-redis:/data/dump.rdb ./redis-backup-$(date +%Y%m%d).rdb
```

---

## 问题排查

### Q1: docker compose up 后容器总是重启
查看 `docker compose logs <服务名>`，常见原因：
- `appsettings.Production.json` 缺少必填密钥 → Guard 抛异常
- Redis 起来慢于 web → 用 `depends_on` 但注意这只是启动顺序不等于「等就绪」，需要在 web容器里加重试逻辑或者先手动启动 redis
- MySQL 初始化 SQL 文件大、启动时间久，web 起来时还连不上 → 临时 `docker compose stop webapiservice frontservice`，等 30 秒再 `docker compose start`

### Q2: Swagger 返 401
检查 `SwaggerConfig:UserName/PassWord` 是否填了，浏览器 BasicAuth 弹窗输的就是这两个值。

### Q3: Hangfire Dashboard 一直 502
检查 Nginx 反代「Upgrade」「Connection」头是否设置好；如果只是单访问应该是 HTTP，不需要 WebSocket，但 Hangfire 部分页面用了 WebSocket。

### Q4: 微信支付回调失败
检查 `PayCallBack:WeChatPayUrl` 必须是 `https://api.yourshop.com/Notify/WeChatPay/Unifiedorder`，且此 URL 必须可被公网访问、SSL 证书有效。

### Q5: 小程序打开后报「不在以下 request 合法域名列表中」
说明小程序前端 apiBaseUrl 与微信后台「服务器域名」白名单不一致，要么改前端，要么改后台白名单，必须二边对齐。

---

## 下一步

1. ✅ 服务器准备就绪 (Phase 0)
2. → 现在就做：**开始备案**（耗时最久的步骤）
3. → 同步进行：本地编译 + 填配置 + 上传 (Phase 1)
4. → 备案通过后：启动整套 + HTTPS + 冒烟 (Phase 3-5)
5. → 最后：前端编译上传审核 (Phase 6)

有任何阶段报错，把错误日志发给我，我帮你定位。