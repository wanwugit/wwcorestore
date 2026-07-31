# 01 余额事务设计

## 一、现有 Change() 的问题

**位置**：`CoreCms.Net.Services\User\CoreCmsUserBalanceServices.cs:58-126`

### 问题清单

| # | 问题 | 代码位置 | 后果 |
|---|------|---------|------|
| 1 | 流水插入和余额更新不在同一事务 | 第115行 vs 第118行 | 流水存在但余额未变，或反之 |
| 2 | 读-改-写并发覆盖 | 第68行读 → 第99行算 → 第118行写 | 两个并发变更丢失其中一个 |
| 3 | 无幂等键 | 整个方法 | 重复消息重复加钱 |
| 4 | Change() 失败无法回滚 | 第115行成功后第118行失败 | 孤立流水 |
| 5 | 余额不足检查与更新非原子 | 第99-104行检查 → 第118行写入 | 并发下可能透支 |
| 6 | 佣金和充值混入同一 balance | 所有调用 | 无法区分资金来源 |

---

## 二、新事务模型

### 2.1 核心原则

1. **余额变更必须是数据库表达式**，禁止读-改-写
2. **流水和余额必须在同一事务中**
3. **每个资金操作必须有幂等键**
4. **余额不足检查在 UPDATE 的 WHERE 中完成**，与写入原子化
5. **Change() 签名向后兼容**，新增参数全部可选

### 2.2 新 Change() 流程

```
输入: userId, type, money, sourceId, cateMoney, idempotencyKey, accountType

1. 幂等检查
   → 查询 CoreCmsUserBalance 是否存在相同 idempotencyKey
   → 如果存在，直接返回之前的结果（幂等返回）

2. 确定目标账户和变更方向
   → 根据 accountType 确定更新哪个余额字段
   → 根据 type 确定正负方向

3. 开启数据库事务

4. 原子更新余额（核心）
   → UPDATE CoreCmsUser
     SET {targetColumn} = {targetColumn} + @changeAmount
     WHERE id = @userId
       AND {targetColumn} + @changeAmount >= 0
   → 检查 affected rows:
     = 0 → 余额不足或用户不存在，回滚返回错误
     = 1 → 继续

5. 读取更新后余额
   → SELECT {targetColumn} FROM CoreCmsUser WHERE id = @userId

6. 插入资金流水
   → INSERT CoreCmsUserBalance (userId, type, money, balance, sourceId, memo,
       idempotencyKey, accountType, createTime)

7. 提交事务

8. 返回结果
```

### 2.3 原子更新 SQL

```sql
-- 普通余额变更
UPDATE CoreCmsUser
SET balance = balance + @changeAmount
WHERE id = @userId
  AND balance + @changeAmount >= 0;

-- 佣金可提现余额变更
UPDATE CoreCmsUser
SET commissionAvailable = commissionAvailable + @changeAmount
WHERE id = @userId
  AND commissionAvailable + @changeAmount >= 0;

-- 佣金冻结变更（只允许增加或减少到非负）
UPDATE CoreCmsUser
SET commissionFrozen = commissionFrozen + @changeAmount
WHERE id = @userId
  AND commissionFrozen + @changeAmount >= 0;
```

### 2.4 SqlSugar 实现

```csharp
// 原子更新余额，返回受影响行数
var affected = await _userDal.DbClient.Updateable<CoreCmsUser>()
    .SetColumns(it => new CoreCmsUser
    {
        balance = it.balance + changeAmount  // 数据库表达式
    })
    .Where(it => it.id == userId)
    .Where(it => it.balance + changeAmount >= 0)  // 余额非负约束
    .ExecuteCommandAsync();

if (affected == 0)
{
    // 余额不足或用户不存在
    _unitOfWork.RollbackTran();
    return WebApiCallBack.Error(11007, "余额不足");
}

// 读取更新后余额（事务内）
var newBalance = await _userDal.DbClient.Queryable<CoreCmsUser>()
    .Where(it => it.id == userId)
    .Select(it => it.balance)
    .SingleAsync();
```

---

## 三、账户类型设计

### 3.1 账户类型枚举

```csharp
public enum AccountType
{
    /// <summary>普通余额（充值、退款、后台调整）</summary>
    Balance = 0,

    /// <summary>佣金可提现余额</summary>
    CommissionAvailable = 1,

    /// <summary>佣金冻结余额</summary>
    CommissionFrozen = 2,

    /// <summary>佣金负债（已提现但需追回）</summary>
    CommissionDebt = 3
}
```

### 3.2 CoreCmsUser 新增字段

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `balance` | decimal | 0 | 保留，普通余额（充值/退款/后台调整/余额支付） |
| `commissionAvailable` | decimal | 0 | 新增，佣金可提现余额 |
| `commissionFrozen` | decimal | 0 | 新增，佣金冻结余额 |
| `commissionDebt` | decimal | 0 | 新增，佣金负债（需追回的金额） |

### 3.3 余额来源与账户的映射

| SourceType | 目标账户 | 方向 | 说明 |
|-----------|---------|------|------|
| Pay=1 | Balance | 扣减 | 余额支付下单 |
| Refund=2 | Balance | 增加 | 余额支付退款返还 |
| Recharge=3 | Balance | 增加 | 在线充值 |
| Tocash=4 | CommissionAvailable | 扣减 | 从佣金可提现余额提现 |
| Distribution=5 | CommissionAvailable | 增加 | 佣金解冻入账 |
| Admin=6 | Balance | 可加可减 | 后台调整普通余额 |
| Prize=7 | Balance | 增加 | 奖励 |
| Service=8 | Balance | 扣减 | 服务订单支付 |

### 3.4 新增 SourceType

| SourceType | 目标账户 | 方向 | 说明 |
|-----------|---------|------|------|
| CommissionFreeze=10 | CommissionFrozen | 增加 | 佣金冻结 |
| CommissionUnfreeze=11 | CommissionFrozen → CommissionAvailable | 冻结减少，可提现增加 | 佣金解冻 |
| CommissionCancel=12 | CommissionFrozen | 扣减 | 佣金取消（冻结中退款） |
| CommissionClawback=13 | CommissionAvailable | 扣减 | 佣金追回（已解冻退款） |
| CommissionDebtRecord=14 | CommissionDebt | 增加 | 记录佣金负债（已提现退款） |
| CommissionDebtOffset=15 | CommissionDebt | 扣减 | 负债抵扣（新佣金优先还债） |

---

## 四、向后兼容的 Change() 签名

```csharp
public async Task<WebApiCallBack> Change(
    int userId,
    int type,
    decimal money,
    string sourceId = "",
    decimal cateMoney = 0,
    // ===== 新增参数，全部可选，向后兼容 =====
    string idempotencyKey = null,
    AccountType accountType = AccountType.Balance)
```

现有 6 个调用点无需修改：
- 不传 `idempotencyKey` → 不做幂等检查（旧行为）
- 不传 `accountType` → 默认 `AccountType.Balance`（旧行为）

新调用方：
- 传入 `idempotencyKey` → 启用幂等
- 传入 `accountType` → 指定目标账户

---

## 五、现有 6 个 Change() 调用点的迁移计划

| # | 调用位置 | 当前参数 | 迁移步骤 | 新增幂等键 |
|---|---------|---------|---------|-----------|
| 1 | `BillPaymentsServices:666` 充值 | userId, Recharge, money, paymentId | 无需改，默认 Balance | `Recharge:{paymentId}` |
| 2 | `BalancePayServices:52` 余额支付 | userId, Pay, money, paymentId | 无需改，默认 Balance | `Pay:{paymentId}` |
| 3 | `BalancePayServices:109` 余额退款 | userId, Refund, money, paymentId | 无需改，默认 Balance | `Refund:{paymentId}` |
| 4 | `DistributionOrderServices:330` 佣金结算 | userId, Distribution, amount, orderId | **必须改**：accountType=CommissionAvailable | `CommissionSettle:{orderId}:{userId}` |
| 5 | `AgentOrderServices:203` 代理结算 | userId, Agent, amount, orderId | 无需改，默认 Balance（代理佣金走普通余额） | `AgentSettle:{orderId}:{userId}` |
| 6 | `UserTocashServices:144` 提现 | userId, Tocash, money, tocashId, cateMoney | **必须改**：accountType=CommissionAvailable | `Tocash:{tocashId}` |

### 迁移策略

**分两步走**：

**第一步（M4）**：只修复事务和原子更新，不改签名，不改账户拆分。
- 所有调用点保持不变
- Change() 内部改用事务 + 原子更新
- 幂等键参数可选，不传则跳过检查

**第二步（M7）**：引入账户拆分，修改调用点 4 和 6。
- 调用点 4：`Change(userId, Distribution, amount, orderId, accountType: AccountType.CommissionAvailable, idempotencyKey: $"CommissionSettle:{orderId}:{userId}")`
- 调用点 6：`Change(userId, Tocash, money, tocashId, cateMoney, accountType: AccountType.CommissionAvailable, idempotencyKey: $"Tocash:{tocashId}")`

---

## 六、事务边界图

```
Change() 事务边界
├── BEGIN TRAN
│   ├── 1. 幂等检查（SELECT ... WHERE idempotencyKey = @key）
│   ├── 2. 原子更新余额（UPDATE ... SET balance = balance + @change WHERE ... AND balance + @change >= 0）
│   ├── 3. 读取新余额（SELECT balance FROM User WHERE id = @userId）
│   ├── 4. 插入流水（INSERT UserBalance ...）
│   └── COMMIT TRAN
│
└── 如果任何步骤失败 → ROLLBACK TRAN
```

**事务隔离级别**：READ COMMITTED（MySQL 默认），足够保证原子更新。

**锁分析**：
- 步骤 2 的 UPDATE 会对 User 行加 X 锁
- 同一用户的并发 Change() 会串行执行
- 不同用户的 Change() 互不阻塞
