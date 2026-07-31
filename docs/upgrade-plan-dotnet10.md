# CoreShop .NET 10 升级计划

> 状态：草案 | 创建日期：2026-07-11 | 目标版本：.NET 10.0 LTS

---

## 一、背景与目标

### 1.1 当前状态

- **当前 .NET 版本**：.NET 9.0
- **项目结构**：22 个 .csproj 项目，全部使用 `net9.0` TargetFramework
- **关键依赖**：SqlSugar、Autofac、Hangfire、Redis、JWT、Swagger
- **开发环境**：Visual Studio 2022 +、.NET 9 SDK

### 1.2 升级目标

- 将项目从 .NET 9.0 升级到 **.NET 10.0**
- 利用 .NET 10 的新特性提升性能和安全性
- 保持向后兼容性，确保现有功能不受影响
- 更新所有 NuGet 包到兼容 .NET 10 的版本

---

## 二、.NET 10 关键变更分析

### 2.1 发布信息

| 项目 | 信息 |
|------|------|
| 预计正式发布 | 2025 年 11 月（根据微软发布周期） |
| 当前状态 | Preview 阶段（截至 2026-07） |
| LTS 支持 | .NET 10 是 LTS 版本，支持 3 年 |
| VS 最低版本 | Visual Studio 2022 17.14+（预计） |

### 2.2 对 CoreShop 有影响的新特性

#### ✅ 推荐利用的特性

| 特性 | 影响模块 | 收益 |
|------|---------|------|
| **数组接口方法去虚拟化** | 所有使用 LINQ/集合的地方 | 性能提升 5-15% |
| **值类型数组栈分配** | 高频数据处理 | 减少 GC 压力 |
| **ZipArchive 性能改进** | 商品导入导出（Excel/Zip） | 内存占用降低 |
| **OrderedDictionary 增强** | 缓存系统 | 更高效的缓存操作 |
| **框架包引用修剪** | 所有项目 | 更小的部署包 |

#### ⚠️ 需要关注的变更

| 变更 | 影响 | 处理方案 |
|------|------|---------|
| **C# 14 新语法** | 编译器升级 | 逐步采用，不影响现有代码 |
| **OpenAPI 3.1 支持** | Swagger 文档 | 评估升级 Swashbuckle |
| **容器镜像基础变更** | Docker 部署 | 更新 Dockerfile 基础镜像 |

### 2.3 潜在破坏性变更

| 领域 | 风险等级 | 说明 |
|------|---------|------|
| Windows Forms 剪贴板 API | 🟡 低 | CoreShop 未使用 WinForms |
| iOS/Mac Catalyst 修剪器 | 🟢 无影响 | 不涉及移动端原生开发 |
| Android API 最低版本 | 🟢 无影响 | 不涉及 Android 原生开发 |
| 字符串比较数字排序 | 🟡 低 | 可能影响排序逻辑，需测试 |

---

## 三、升级范围

### 3.1 需要修改的文件清单

```
📁 项目文件（22 个 .csproj）
├── CoreCms.Net.Auth.csproj
├── CoreCms.Net.Caching.csproj
├── CoreCms.Net.CodeGenerator.csproj
├── CoreCms.Net.Configuration.csproj
├── CoreCms.Net.Core.csproj
├── CoreCms.Net.Filter.csproj
├── CoreCms.Net.IRepository.csproj
├── CoreCms.Net.IServices.csproj
├── CoreCms.Net.Loging.csproj
├── CoreCms.Net.Mapping.csproj
├── CoreCms.Net.Middlewares.csproj
├── CoreCms.Net.Model.csproj
├── CoreCms.Net.RedisMQ.csproj
├── CoreCms.Net.Repository.csproj
├── CoreCms.Net.Services.csproj
├── CoreCms.Net.Swagger.csproj
├── CoreCms.Net.Task.csproj
├── CoreCms.Net.Utility.csproj
├── CoreCms.Net.WeChat.Service.csproj
├── CoreCms.Net.Web.Admin.csproj
├── CoreCms.Net.Web.WebApi.csproj
└── CoreCms.Net.Uni-App.csproj

📁 配置文件
├── global.json（新增/更新 SDK 版本）
├── Directory.Build.props（可选：统一管理 TargetFramework）
└── Dockerfile（更新基础镜像）
```

### 3.2 NuGet 包兼容性评估

| 包名 | 当前版本 | .NET 10 兼容性 | 备注 |
|------|---------|---------------|------|
| SqlSugarCore | 5.1.4.207 | ✅ 预计兼容 | 关注官方更新 |
| Autofac | 10.0.0 | ✅ 预计兼容 | 已支持 .NET 9 |
| Autofac.Extras.DynamicProxy | 7.1.0 | ✅ 预计兼容 | |
| Hangfire | 1.8.21 | ✅ 预计兼容 | |
| StackExchange.Redis | 2.9.32 | ✅ 预计兼容 | |
| NLog | 6.0.5 | ✅ 预计兼容 | |
| AutoMapper | 最新 | ✅ 预计兼容 | |
| Swashbuckle.AspNetCore | 最新 | ⚠️ 需验证 | OpenAPI 3.1 支持 |
| Microsoft.AspNetCore.* | 9.0.10 | ⚠️ 需升级到 10.x | |
| Microsoft.Extensions.* | 9.0.10 | ⚠️ 需升级到 10.x | |

---

## 四、升级步骤

### 阶段 1：环境准备（1-2 天）

```
□ 安装 .NET 10 SDK（正式发布后）
□ 安装/更新 Visual Studio 2022 到支持 .NET 10 的版本
□ 在测试环境验证 SDK 安装
□ 备份当前代码分支
```

### 阶段 2：项目文件升级（1-2 天）

```
□ 创建升级分支：feature/upgrade-to-dotnet10
□ 更新所有 .csproj 的 <TargetFramework>net9.0</TargetFramework> → net10.0
□ 更新 global.json 指定 .NET 10 SDK
□ 更新 NuGet 包到 .NET 10 兼容版本
□ 解决编译错误
```

### 阶段 3：代码适配（3-5 天）

```
□ 修复编译警告（C# 14 新语法警告等）
□ 检查并修复任何破坏性变更影响
□ 更新 Dockerfile 基础镜像到 .NET 10
□ 更新 CI/CD 配置文件
```

### 阶段 4：测试验证（3-5 天）

```
□ 运行单元测试套件
□ 运行集成测试
□ 执行冒烟测试（核心业务流程）
□ 性能基准测试对比
□ 部署到测试环境验证
```

### 阶段 5：文档更新与发布（1 天）

```
□ 更新 README.md 开发环境要求
□ 更新部署文档
□ 合并到主分支
□ 打标签：v10.0.0
```

---

## 五、风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| SqlSugar 不兼容 .NET 10 | 低 | 高 | 提前联系作者确认兼容性；准备替代方案 |
| 第三方包未更新 | 中 | 中 | 使用 `--prerelease` 版本测试；或等待正式版 |
| 性能回归 | 低 | 中 | 升级前后做性能基准测试对比 |
| 构建环境不支持 | 低 | 高 | 使用 Docker 构建；或升级构建服务器 |

---

## 六、回滚方案

```
1. 回滚代码分支到升级前
2. 恢复 global.json 到 .NET 9 SDK
3. 恢复 NuGet 包到 .NET 9 版本
4. 重新构建并部署
```

---

## 七、时间线（预估）

| 阶段 | 预计工时 | 负责人 |
|------|---------|--------|
| 环境准备 | 1-2 天 | 运维 |
| 项目文件升级 | 1-2 天 | 开发 |
| 代码适配 | 3-5 天 | 开发 |
| 测试验证 | 3-5 天 | 测试/开发 |
| 文档更新与发布 | 1 天 | 开发 |
| **总计** | **9-15 天** | |

---

## 八、相关文档

- [.NET 10 Release Notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/README.md)
- [.NET 10 Breaking Changes](https://learn.microsoft.com/dotnet/core/compatibility/10.0)
- [Migrate from .NET 9 to .NET 10](https://learn.microsoft.com/dotnet/core/migration/)（正式发布后）

---

## 九、决策记录

| 日期 | 决策 | 原因 |
|------|------|------|
| 2026-07-11 | 暂不升级，等待 .NET 10 正式发布 | .NET 10 仍在 Preview 阶段，不适合生产环境 |
| 待定 | 正式启动升级 | 待 .NET 10 正式发布后 |

---

> 💡 **建议**：.NET 10 预计在 2025 年 11 月正式发布。建议在正式发布后 1-2 个月（即 2026 年初）再开始升级，等待生态系统（特别是 SqlSugar 等关键依赖）完成适配。
