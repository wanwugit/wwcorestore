# 前端小程序上线配置指引

本仓库前端是 uni-app 工程，目录在 `CoreCms.Net.Uni-App/CoreShop/`。需要用 **HBuilderX** 编译发行。上线前需要改 4 处配置，然后通过 HBuilderX 发布到对应端。

---

## 一、必改配置清单（4 项）

| 序号 | 文件 | 行 | 字段 | 用途 |
|---|---|---|---|---|
| 1 | `common/setting/constVarsHelper.js` | 7 | `apiBaseUrl` | 后端 API 域名 |
| 2 | `common/setting/constVarsHelper.js` | 9 | `apiFilesUrl` | 静态资源域名 |
| 3 | `manifest.json` | 105 | `mp-weixin.appid` | 微信小程序 AppId |
| 4 | `manifest.json` | 125 | `h5.domain` | H5 端正式域名（仅发 H5 时必填） |

---

## 二、配置 1：API 域名（**最关键**）

**文件**：`CoreCms.Net.Uni-App/CoreShop/common/setting/constVarsHelper.js`

```js
// 改前（演示值）：
export const apiBaseUrl = 'https://api.demo.coreshop.cn';
export const apiFilesUrl = 'https://files.cdn.coreshop.cn';

// 改后（你的生产值）：
export const apiBaseUrl = 'https://api.yourshop.com';      // 你的 WebApi 域名
export const apiFilesUrl = 'https://files.yourshop.com';   // 静态资源域名（可与 api 同域）
```

**说明**：
- `apiBaseUrl` 是小程序所有 HTTP 请求的目标域名，必须与后端 `WebApi` 部署的 HTTPS 域名 **完全一致**（含 https://、不带尾斜杆）
- `apiFilesUrl` 用于图片 / 海报 / 二维码下载，可以是同域、CDN、或 OSS / COS 域名
- **H5 端会自动从 `apiBaseUrl` 派生 `baseUrl`**，不需要单独再改

---

## 三、配置 2：微信小程序 AppId

**文件**：`CoreCms.Net.Uni-App/CoreShop/manifest.json` 第 105 行

```json
"mp-weixin" : {
    "appid" : "wx9ffab147a56e9424",   ← 改成你自己的小程序 AppId
    ...
}
```

**获取 AppId**:
1. 去 [mp.weixin.qq.com](https://mp.weixin.qq.com) 注册微信小程序账号
2. 开发 → 开发管理 → 开发设置 → AppID(小程序ID)

**同步到后端**：
后端 `WebApi/appsettings.Production.json` 的 `WeChatOptions:WxOpenAppId` 必须与这里的 `mp-weixin.appid` **完全相同**，否则微信登录会失败。

---

## 四、配置 3（可选）：H5 域名

**文件**：`CoreCms.Net.Uni-App/CoreShop/manifest.json` 第 125 行

```json
"h5" : {
    "domain" : "https://h5.demo.coreshop.com.cn/",   ← 改成你的 H5 域名
    ...
}
```

仅当你要发行 H5 版本时需要改。微信小程序不受此字段影响。

---

## 五、可选配置：5+App（Android/iOS）

**文件**：`manifest.json` 第 52 行

```json
"sdkConfigs" : {
    "payment" : {
        "weixin" : {
            "appid" : "wxd56f71964a318e5d"   ← 改成你的微信开放平台 AppId（与小程序不同）
        }
    }
}
```

仅当你要打 Android/iOS 原生 App + 接入微信支付时需要改。纯小程序部署可忽略。

---

## 六、发布流程（微信小程序）

### 步骤 1：用 HBuilderX 打开项目

```
HBuilderX → 文件 → 打开目录 → 选择 D:\Proj\CoreShop\CoreCms.Net.Uni-App\CoreShop\
```

⚠️ 注意：**打开的是 `CoreShop` 子目录**，不是 `CoreCms.Net.Uni-App` 根目录。`CoreCms.Net.Uni-App` 下的 csproj 只是占位，实际 uni-app 源在 `CoreShop/`。

### 步骤 2：发行 → 小程序-微信

```
HBuilderX 菜单：发行 → 小程序-微信
弹出对话框：
  - 小程序应用名：你的小程序名
  - AppID：选 manifest.json 中配置的（自动填充）
  - ★ 取消勾选「发行到微信平台」（手动传更可控）
  - 点击「发行」
```

编译完成后，发行包在：

```
CoreCms.Net.Uni-App/CoreShop/unpackage/dist/build/mp-weixin/
```

### 步骤 3：用微信开发者工具上传

1. 打开「微信开发者工具」
2. 导入项目 → 目录选 `unpackage/dist/build/mp-weixin/` → AppID 填你在 manifest.json 配的
3. 右上角点「上传」→ 填版本号 + 备注 → 上传
4. 登录 [mp.weixin.qq.com](https://mp.weixin.qq.com) → 管理 → 版本管理 → 提交审核 → 发布

---

## 七、微信小程序后台必做配置

登录 [mp.weixin.qq.com](https://mp.weixin.qq.com) → 开发 → 开发管理 → 服务器域名：

| 域名类型 | 填什么 | 示例 |
|---|---|---|
| **request 合法域名** | 后端 WebApi HTTPS 域名 | `https://api.yourshop.com` |
| **uploadFile 合法域名** | 后端上传接口域名（通常同 API） | `https://api.yourshop.com` |
| **downloadFile 合法域名** | 图片/海报/二维码下载来源域名 | `https://files.yourshop.com` 或 OSS/COS 域名 |
| **socket 合法域名** | 通常不需要 | 留空 |

⚠️ 关键要求：
- **必须 HTTPS**，不支持 HTTP
- **必须备案域名**，不能用 IP
- **必须是 443 端口**（或默认 https 端口），不能带端口号
- 每月只能改 50 次，谨慎操作

---

## 八、发布流程（H5）

```
HBuilderX 菜单：发行 → 网站-PC Web 或手机 H5
弹出对话框：网站标题填你的项目名 → 点发行
编译输出：CoreCms.Net.Uni-App/CoreShop/unpackage/dist/build/h5/
```

部署到 Nginx / IIS / 任意静态服务器：

```nginx
server {
    listen 443 ssl http2;
    server_name h5.yourshop.com;
    root /var/www/coreshop-h5;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;   # uni-app H5 是 hash 路由，必须 fall back
    }
}
```

---

## 九、发布流程（5+App）

```
HBuilderX 菜单：发行 → 原生 App-云打包
选择 Android/iOS → 填证书 → 提交云端打包 → 下载 apk/ipa
```

仅当你要做 Android/iOS 原生 App 时使用。

---

## 十、核对清单

部署前端前请逐项打勾：

- [ ] `constVarsHelper.js` 的 `apiBaseUrl` 已改为生产 API 域名
- [ ] `constVarsHelper.js` 的 `apiFilesUrl` 已改为生产静态资源域名
- [ ] `manifest.json` 的 `mp-weixin.appid` 已改为你的小程序 AppId
- [ ] 后端 `WebApi/appsettings.Production.json` 的 `WeChatOptions:WxOpenAppId` 与前端 `mp-weixin.appid` 完全一致
- [ ] 后端 `WebApi/appsettings.Production.json` 的 `WeChatOptions:WxOpenAppSecret` 已填正确 AppSecret
- [ ] 微信小程序后台 → 服务器域名已配 request / uploadFile / downloadFile 白名单
- [ ] HBuilderX 编译微信小程序发行包成功，输出在 `unpackage/dist/build/mp-weixin/`
- [ ] 微信开发者工具上传发行包成功
- [ ] 微信小程序后台提交审核
- [ ] （H5 端）`manifest.json` 的 `h5.domain` 已改 + 静态服务器配置 try_files